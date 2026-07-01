# Recipes

Small patterns that come up often when writing grammars with InductorParser. Each recipe leads with the problem, shows the natural-but-wrong translation, and walks through what actually works.

## Numbers with no leading zeros

Lots of grammars want "a number with no leading zeros." SemVer's major, minor, and patch positions. JSON numbers. IPv4 octets. The spec usually reads "0 or [1-9][0-9]*", meaning a bare 0 is fine but `007` is not. The natural translation into Inductor parser is one line:

```csharp
var numericCore = Or(
    Token('0'),
    And(OneOf(TokenSet.Range('1','9')), ZeroOrMore(OneOf(TokenSet.Ascii.Digits)))
);
```

Drop that into a SemVer-shaped grammar and feed it the bad input `01.2.3`:

```csharp
var semver = And(
    numericCore.AliasedAs("major"),
    Token('.'),
    numericCore.AliasedAs("minor"),
    Token('.'),
    numericCore.AliasedAs("patch")
);

// Result on "01.2.3":
//   fail at column 1: the default "Unexpected '1'" message
```

The position is wrong. The actual problem is the leading zero at column 0 (the `0`), but the parser reports a missing `.` at column 1 (the `1`). The reason is how `Or` works. It tries `Token('0')` first, that succeeds on the lone `0`, and the parser commits to it. The outer `And` then tries `Token('.')`, sees `1`, and fails there. Once an `Or` branch matches, the parser doesn't go back and try the others later. Committed is committed.

### Fix that gets the message: a lookahead inside the Or

The shape that looks right is a negative lookahead inside the first branch: only accept the `0` if it isn't followed by another digit.

```csharp
var peekCore = Or(
    And(Token('0'), Not(OneOf(TokenSet.Ascii.Digits))),
    And(OneOf(TokenSet.Range('1','9')), ZeroOrMore(OneOf(TokenSet.Ascii.Digits)))
).WithError("Number with no leading zeros expected");

// Result on "01.2.3":
//   fail at column 1: Number with no leading zeros expected
```

This gets the friendly message. The caret lands at column 1, the digit *after* the `0`: that's the spot the `Not` lookahead got stuck, where it found a digit it didn't want. Depth-primary ranking anchors the `Or`'s `WithError` at the deepest position its branches reached, and that's it. The message is right and the caret points one past the zero. If you want the caret *on* the zero, use the next pattern.

(The `Result on ...` comments here show just the `WithError` text and the caret column. By default the parser also appends ` at line 1, column N` to the message, counting the column from 1. Set `ParseOptions.WithErrorTemplate` to `"{message}"` for the bare text, or reshape it. See the [parsing-errors primer](primerFailure.md) for the template.)

### Fix that puts the caret on the zero: reject the bad prefix first

Put the lookahead before any consumption, as a "reject this prefix" check, and then accept any digit run. Putting `WithError` on the `Not` (instead of on the outer `And`) means the leading-zero message fires only when the leading-zero case is what tripped the parse. Other failures (a non-digit first character like the `v` in `v1.2.3`) fall through to the default unexpected-token message from the `OneOrMore`.

```csharp
static Rule NumericCore(string name) => And(
    Not(And(Token('0'), OneOf(TokenSet.Ascii.Digits)))
        .WithError($"{name} version must not have leading zeros"),
    OneOrMore(OneOf(TokenSet.Ascii.Digits))
).As(name);

var semver = And(
    NumericCore("major"),
    Token('.'),
    NumericCore("minor"),
    Token('.'),
    NumericCore("patch")
);

// Result on "01.2.3":     fail at column 0: major version must not have leading zeros
// Result on "1.02.3":     fail at column 2: minor version must not have leading zeros
// Result on "1.2.03":     fail at column 4: patch version must not have leading zeros
// Result on "v1.2.3":     fail at column 0: the default "Unexpected 'v'" message
// Result on "0.0.0":      success
// Result on "10.20.30":   success
```

Why this version puts the caret on the zero: the `Not` is the first thing the rule does, so it runs at the offset the rule starts at. On a bad input the inner *succeeds* (it consumed `0` followed by a digit), which means `Not` fails, and `Not` records its failure at its own start, the rule's start, with the `WithError` message attached. So the caret lands on the `0` itself rather than one past it.

The grammar accepts a touch more than the spec (a single `0` could in principle match the `OneOrMore(Digit)` branch followed by anything), but the `Not` prefix rejects exactly the bad shape `0` + digit, so the net set of accepted strings matches the spec.

The cost: one extra two-token lookahead at each numeric position. `Not` consumes nothing, so for typical inputs this is cheap. The grammar is a hair more verbose than the natural translation, but it's still self-contained and the spec rule is visible on the page.

The working SemVer sample under [E2ESamples/SemVer/Rewrite/SemVerGrammar.cs](../E2ESamples/SemVer/Rewrite/SemVerGrammar.cs) uses exactly this pattern for major, minor, and patch. The regression tests live in [RecipesExamples.cs](../src/InductorParser.Tests/DocExamples/RecipesExamples.cs) ("No_leading_zero_..." tests) and [SemVerTests.cs](../E2ESamples/SemVer/Tests/SemVerTests.cs) (the per-position tests in `SemVerErrorPositionTests`).

### Alternative: validate after parsing

Sometimes the no-leading-zero check is one of several things to validate on a number. SemVer is a good example. The spec also requires major, minor, and patch to fit in a 32-bit integer, which isn't a syntactic property at all. In that case it's cleaner to accept any digit run in the grammar and do all the validation in the consumer:

```csharp
var numericCore = OneOrMore(OneOf(TokenSet.Ascii.Digits)).As("major");
```

After parsing, walk to the node and check it yourself. `node.SourceRange` gives the position of the original text, so the error message can point at the right place:

```csharp
var node = result.Tree!.Find(numericCore)!;
var text = node.ToString();
if (text.Length > 1 && text[0] == '0')
{
    var range = node.SourceRange!;
    throw new FormatException(
        $"line {range.Value.Start.Line + 1}, column {range.Value.Start.CharColumn + 1}: " +
        $"Major version '{text}' must not have leading zeros");
}
if (!int.TryParse(text, out var major))
    throw new FormatException($"Major version '{text}' is out of range");
```

That's the pattern the SemVer sample under [E2ESamples/SemVer/Rewrite/](../E2ESamples/SemVer/Rewrite/) uses. See [SemVerParser.cs](../E2ESamples/SemVer/Rewrite/SemVerParser.cs) for the full version, including the same check on numeric pre-release identifiers.

### Which to pick

Use the reject-first grammar version when the no-leading-zero rule is the main check you need on the number. Parameterizing the factory by name (as above) gives per-field messages too, so the choice isn't grammar-version-equals-generic-message anymore. The spec rule lives in one place, the error is positioned correctly, and the failure happens before any post-parse code has to run.

Use the post-parse-validation version when you also want checks that don't fit in the grammar (Int32 range, cross-field consistency, etc.), or when the per-field message needs to include the actual bad value ("Major version '01' must not have leading zeros" with the offending text quoted, which the grammar message can't produce). The grammar accepts more than the spec, the consumer narrows it back, and the two layers split the work in a way that scales when validations grow.

The SemVer sample uses both: the grammar handles leading zeros for major, minor, and patch via reject-first, and the consumer handles the Int32 range check and the leading-zero check for numeric pre-release identifiers (which has a more complex rule shape).

The trap to avoid in either case is the natural Or-first translation. It looks like it should work, and it does for happy paths, but it positions the error one column past the real problem.
