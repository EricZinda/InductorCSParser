# ScanUntil(Rule) never tests the stopper at end of input

`ScanUntilRule.TryParseRule` scans the body in a `while (!lexer.IsEof)` loop ([ScanUntilRule.cs:282](../src/InductorParser/ScanUntilRule.cs#L282)) and tests the stopper at the top of each iteration. When the loop reaches end of input the condition goes false and the loop exits *before* running the body once more, so the stopper is only ever tested at non-EOF positions. For the TokenSet-stopper path that's fine: a TokenSet is matched against a real token and EOF produces none. But a *Rule*-stopper can be EOF-sensitive: `Eof()` matches only at end of input, `Not(AnyToken())` matches only at end of input, and `Or(Literal("END"), Eof())` is the natural way to write "stop at END or at end of input." None of those is ever given a chance to match at the EOF position, so `ScanUntil` with such a stopper falls into the strict-failure branch (`!stopperMatched && !_eofIsTerminator`) and fails instead of stopping at end of input.

User-visible consequence: `ScanUntil(Eof())` — which reads as "scan until end of input" and should behave exactly like the library's own `ScanUntilEof()` helper — always fails. So does `ScanUntil(Or(Literal("END"), Eof()))` on any input that has no `END`, even though the author put `Eof()` in the stopper specifically so the body could legitimately end at EOF. The `Eof()` alternative is silently dead. The author has no way to know the stopper rule won't be consulted at EOF; nothing in the API or docs says so, and `ScanUntil(Rule)` accepts any rule. The result is a parse that fails with "Unexpected end of input." where it should have succeeded with the whole input as the body.

## Verify the Bug (Write Test First)

Two tests in `src/InductorParser.Tests/Rules/ScanUntilRuleTests.cs` (added at the end of the fixture):

```csharp
[Test]
public void ScanUntil_with_Eof_rule_stopper_stops_at_end_of_input()
{
    // ScanUntil(Eof()) reads as "scan until end of input": Eof() is
    // a rule that matches only at EOF, so ScanUntil should stop the
    // body there and match the whole input, exactly like the
    // library's own ScanUntilEof() helper. The Rule-stopper loop
    // only tests the stopper at non-EOF positions, so a stopper
    // that matches at EOF is never recognised and the rule fails.
    var rule = ScanUntil(Eof());

    var result = rule.Parse("abc");

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
}

[Test]
public void ScanUntil_with_rule_stopper_that_admits_Eof_stops_when_input_runs_out()
{
    // Or(Literal("END"), Eof()) is the natural way to write
    // "stop at END or at end of input." On input with no "END",
    // the body should run to EOF where the Eof() alternative
    // matches. Instead ScanUntil fails because the stopper rule
    // is never tried at the EOF position.
    var stopper = Or(Literal("END"), Eof());
    var rule = ScanUntil(stopper);

    var result = rule.Parse("abc");

    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
}
```

Run them:

```
cd src/InductorParser.Tests
dotnet test --filter "FullyQualifiedName~ScanUntil_with_Eof_rule_stopper_stops_at_end_of_input|FullyQualifiedName~ScanUntil_with_rule_stopper_that_admits_Eof"
```

Before the fix both fail: `ScanUntil(Eof()).Parse("abc")` returns `Success == false` with `ErrorMessage == "Unexpected end of input."`.

## Fix

In `ScanUntilRule.TryParseRule`, after the `while (!lexer.IsEof)` scan loop and before the `if (!stopperMatched && !_eofIsTerminator)` strict-failure check, give a Rule-stopper one final test at the EOF position:

```csharp
if (!stopperMatched && !_eofIsTerminator && _stopperRule != null && lexer.IsEof)
{
    using var peek = lexer.BeginTransaction();
    // No Commit: the `using` rolls the position back, same as
    // the in-loop Rule-stopper check, so the stopper is never
    // consumed by ScanUntil.
    if (_stopperRule.TryParse(lexer, outputSymbols: null) != null)
        stopperMatched = true;
}
```

The check is gated on `lexer.IsEof` so it only fires when the loop exited because input ran out (not via the escape zero-width `break` or the body-bounds `break`), and on `_stopperRule != null` so the TokenSet path is untouched. A normal Rule-stopper such as `Literal("X")` still fails at EOF (it reads the EOF token and returns null), so strict `ScanUntil` over a stopper that genuinely never matches keeps failing — no regression.

## Verify the Fix

```
cd src/InductorParser.Tests
dotnet test --filter "FullyQualifiedName~ScanUntilRuleTests"
```

Both new tests pass, and the rest of the `ScanUntilRuleTests` fixture (431 tests) stays green. Full suite: 5648 pass / 2 skipped under both the recursive and the state-machine engine.
