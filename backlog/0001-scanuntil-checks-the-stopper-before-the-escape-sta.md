# ScanUntil checks the stopper before the escape start, so escapes are unreachable when they share a prefix with a stopper

`ScanUntilRule.TryParseRule` scans the input one token at a time, and on
each iteration it ran the stopper check before the escape-start check.
When the escape-start rune (or the first rune of a rule-valued escape
start) is also a member of the stopper set, the stopper always wins:
the scan terminates at the escape character and the escape sequence is
never consumed. The escape becomes dead code.

This is exactly the grammar shape the `Rules.cs` XML-doc examples for
the escape-bearing `ScanUntil` overloads show. The single-rune-escape
example builds `ScanUntil(TokenSet.Runes("\"\\"), new Rune('\\'),
OneOf("ntr\"\\"))` — the escape rune `\` is listed in the `stopAt` set
right next to the closing quote. The rule-valued-escape example builds
`ScanUntil(TokenSet.Runes("\"$"), Literal("${"), ...)` — the escape
start `${` shares its first rune `$` with the `$` stopper, and the doc
text explicitly promises that `${name}` is consumed as an interpolation
escape while a bare `$` stops the body. Under the old check order
neither example worked: a user copying the documented JSON-string-body
grammar got a parser whose string body terminates at the first
backslash, so `"a\nb"` parses with body `a` and the outer closing-quote
rule then fails at the `\`.

The bug is in the per-token loop of `ScanUntilRule.TryParseRule` in
[src/InductorParser/ScanUntilRule.cs](../src/InductorParser/ScanUntilRule.cs):
the stopper-check block (the `if (_stopperRule == null) { ... } else
{ ... }`) sat before the escape-start block (`if (_hasEscape) { ... }`).
The header comment at the top of the file also stated the wrong order
("The order of checks is: stopper first, then escape start").

## Verify the Bug (Write Test First)

Add these tests to `src/InductorParser.Tests/Rules/ScanUntilRuleTests.cs`:

```csharp
[Test]
public void ScanUntil_single_rune_escape_fires_when_the_escape_rune_is_also_a_stopper()
{
    var body = ScanUntil(
        stopAt: TokenSet.Runes("\"\\"),
        escapeStart: new Rune('\\'),
        escapeEnd: OneOf("ntr\"\\"));

    var result = body.Parse("\\n\"", new ParseOptions { AllowTrailingInput = true });

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Tree!.ToString(), Is.EqualTo("\\n"),
        "the '\\' must trigger the escape, not terminate the body at offset 0");
}

[Test]
public void ScanUntil_rule_escape_start_fires_when_its_first_rune_is_also_a_stopper()
{
    var body = ScanUntil(
        stopAt: TokenSet.Runes("\"$"),
        escapeStart: Literal("${"),
        escapeEnd: And(OneOrMore(NoneOf("}")), Token('}')));

    var withInterpolation = body.Parse("ab${name}cd\"",
        new ParseOptions { AllowTrailingInput = true });
    Assert.That(withInterpolation.Success, Is.True, withInterpolation.ErrorMessage);
    Assert.That(withInterpolation.Tree!.ToString(), Is.EqualTo("ab${name}cd"),
        "${name} must be consumed as an escape, not stop the body at the '$'");

    var bareDollar = body.Parse("ab$cd\"",
        new ParseOptions { AllowTrailingInput = true });
    Assert.That(bareDollar.Success, Is.True, bareDollar.ErrorMessage);
    Assert.That(bareDollar.Tree!.ToString(), Is.EqualTo("ab"),
        "a bare '$' falls through to the stopper and ends the body");
}
```

Run them:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~ScanUntil_single_rune_escape_fires|FullyQualifiedName~ScanUntil_rule_escape_start_fires"
```

Before the fix both tests fail: the single-rune test sees an empty body
(the scan stopped at offset 0), and the rule-escape test sees `ab`
instead of `ab${name}cd`.

## Fix

In `ScanUntilRule.TryParseRule`, move the escape-start check so it runs
before the stopper check. Inside the `while (!lexer.IsEof)` loop:

1. Compute `int tokenLen = lexer.PeekTokenLength(pos);` once, up front,
   since both the escape fast path and the stopper check need it.
2. Run the `if (_hasEscape) { ... }` block (both the `_escapeStartRule`
   and the single-rune fast-path branches) first. When the escape start
   doesn't match, fall through.
3. Run the stopper block (`if (_stopperRule == null) { ... } else
   { ... }`) after the escape block.

The escape-start checks already fail cleanly and roll back when they
don't match (the single-rune path is a plain rune compare; the rule
path's `TryParse` rolls its own transaction back), so a position that
is a stopper but not an escape start still falls through to the stopper
check exactly as before. This makes the escape strictly more
expressive: a grammar can now have an escape-start share a prefix with
a stopper, and no grammar that kept them disjoint changes behavior.

Update the file header comment ("The order of checks is: stopper first,
then escape start") to describe the new order.

## Verify the Fix

Run the two tests above plus the full `ScanUntil` fixture and the
string-literal E2E grammars:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~ScanUntilRuleTests"
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~StringLiteral"
```

All pass after the fix. The escape-first order doesn't disturb any
existing grammar, because every existing `ScanUntil` grammar in the
test suite and the E2E examples keeps its escape start out of the
stopper set, so the two checks never compete.
