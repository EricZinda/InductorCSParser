# LiteralIgnoreAsciiCase records a mid-grapheme failure position when the pattern contains CRLF

The failure branches in `LiteralIgnoreAsciiCaseRule.TryParseRule` (src/InductorParser/LiteralIgnoreAsciiCaseRule.cs, the comment at lines 53-59 and the two `RecordFailure(tokenStart, ...)` calls below it) record at the pre-read offset of the failing token, skipping the whole-grapheme flooring that `LiteralRule.FailurePosition` does. The comment justifies the skip with "the pattern is ASCII-only, and every ASCII char is one whole grapheme in every normalization form." That premise has exactly one counterexample: CRLF. `"\r\n"` is a single grapheme cluster under UAX #29 rule GB3 (CR × LF), and both chars are ASCII, so the constructor accepts a pattern containing it.

Concrete divergence: pattern `"\r\nX"` against input `"\rY"`. The lexer reads `"\r"` as its own cluster (GB4, no LF follows in the input), it matches `expected[0..1)`, `consumed` becomes 1, then `"Y"` vs `"\n"` fails and the rule records at `startPosition + 1`, one char inside the partially matched expected grapheme `"\r\n"`. `Literal("\r\nX")` on the same input floors via `GraphemeHelpers.FloorToClusterStart("\r\nX", 1)` and records at `startPosition + 0`. The EOF branch has the same problem (pattern `"\r\n"`, input `"\r"` records at +1).

User-visible consequences: the reported `ErrorCharIndex` sits mid-grapheme in expected-text terms, violating the whole-grapheme error-position principle in docs/ErrorArchitecture.md (line 49, "a partially matched grapheme isn't progress"), and the inflated position can win `Or` error ranking over a sibling that legitimately failed at the cluster start, changing which error message the user sees. docs/ErrorArchitecture.md line 44 currently lists `LiteralIgnoreAsciiCase` in the whole-grapheme-flooring row, so the doc promises behavior the code doesn't deliver. Line 49's "For ASCII text every char is its own grapheme" repeats the false premise, and the commit message of eac9501 does too (leave the commit alone, fix the live doc).

Found during the 2026-07-28 Unicode claims audit. CRLF is the only all-ASCII multi-char cluster, so the change is small and the blast radius is patterns containing `"\r\n"`.

## Verify the Bug (Write Test First)

Add to src/InductorParser.Tests/Rules/LiteralIgnoreAsciiCaseRuleTests.cs (the existing CRLF test at line 235 covers only the success path):

```csharp
[Test]
public void Failure_inside_a_crlf_pattern_reports_the_start_of_the_crlf_grapheme()
{
    // "\r\n" is one grapheme cluster (UAX #29 GB3), so matching only the
    // "\r" isn't whole-grapheme progress. Literal("\r\nX") floors this
    // failure to the cluster start and LiteralIgnoreAsciiCase must agree.
    var result = LiteralIgnoreAsciiCase("\r\nX").Parse("\rY");
    Assert.That(result.Success, Is.False);
    AssertErrorPosition(result, charIndex: 0, line: 0, column: 0, TokenIndex: 0);

    var literalResult = Literal("\r\nX").Parse("\rY");
    Assert.That(result.ErrorCharIndex, Is.EqualTo(literalResult.ErrorCharIndex),
        "the two literal rules should report the same position for the same partial match");
}

[Test]
public void Eof_inside_a_crlf_pattern_reports_the_start_of_the_crlf_grapheme()
{
    var result = LiteralIgnoreAsciiCase("\r\n").Parse("\r");
    Assert.That(result.Success, Is.False);
    AssertErrorPosition(result, charIndex: 0, line: 0, column: 0, TokenIndex: 0);
}
```

Run: `./test.sh --filter "FullyQualifiedName~LiteralIgnoreAsciiCaseRuleTests"`. Both new tests fail today (ErrorCharIndex is 1).

## Fix

Give `LiteralIgnoreAsciiCaseRule` the same treatment `LiteralRule` has: record at `startPosition + GraphemeHelpers.FloorToClusterStart(_expected, consumed)` in both failure branches (matched input chars equal the matched section of `_expected` char for char under ASCII case-insensitive compare, so an expected-text cluster boundary at or below `consumed` is a valid input offset, the same argument as LiteralRule's `FailurePosition` comment). Rewrite the lines 53-59 comment: the pattern is ASCII-only but CRLF is still one cluster, so the flooring is needed for exactly that case. While there, fix docs/ErrorArchitecture.md line 49's "For ASCII text every char is its own grapheme" to name the CRLF exception (line 44's table row becomes true once the code floors).

## Verify the Fix

Rerun the filter above, both new tests pass, and the whole suite stays green: `./test.sh`.
