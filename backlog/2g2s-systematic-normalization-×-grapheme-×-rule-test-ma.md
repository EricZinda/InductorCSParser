- Systematic normalization × grapheme × rule test matrix to close the FormC/D/KC/KD test gap

The nrmf bug (FirstConsumedTokens cached against pre-normalization data) only got found because someone happened to look at Compile's pass ordering by hand. The grammar shape that exposes it (`OneOrMore(Token("Å"))` compiled with FormC, with the canonical-singleton input) was nowhere in the existing tests. The same shape applies to every literal-bearing rule and every TokenSet-bearing rule, and to every combination of the four `NormalizationForm` values with every distinct grapheme behavior: graphemes that stay (already normalized), graphemes that compose (e + combining acute → é under FormC), graphemes that decompose (é → e + combining acute under FormD), canonical singletons (U+212B → U+00C5), compatibility singletons (Ω U+2126 → Ω U+03A9), compatibility ligatures (ﬁ → fi, two graphemes), fullwidth/halfwidth (Ａ → A under FormKC), CJK compatibility, Hangul syllable composition / jamo decomposition, ZWJ emoji sequences (multi-rune clusters that should stay regardless of form), CRLF clusters, and lone surrogates under `Compile(null)`. The matrix is too big to enumerate by hand for every test, but small enough that a shared test fixture with a curated set of (grapheme, form, expected post-normalization shape) tuples covers it once and benefits every rule.

The risk pattern is the same shape every normalization-form bug has surfaced under: the lexer and the rule's static analysis or runtime check disagree about what a token IS after the input is normalized. The lexer always sees post-normalization input, so any rule field that's still in the user-supplied form (or any cache derived from it) is stale by the time matching happens. Until we have a single fixture that can be parameterized across rules and forms, every new feature touching either Compile or the runtime needs hand-written normalization tests, and the gaps don't surface until someone trips over them in production.

## What "systematic" means here

Build one canonical test data table in `src/InductorParser.Tests/NormalizationExamples.cs` (analogous to the existing `UnicodeExamples.cs`) that lists, for each interesting grapheme behavior, the source text and what it normalizes to under each of the four forms. Each row tags itself with the behavioral category (StaysSingleRune, ComposesToSingleRune, DecomposesToMultiRune, CanonicalSingletonRune, CompatibilitySingletonRune, CompatibilityLigatureMultiGrapheme, FullwidthToHalfwidth, HangulSyllableToJamo, MultiRuneClusterStays, CRLFCluster, LoneSurrogate). The table is the source of truth: a row gets added once and every parameterized test that wants "all interesting graphemes" pulls from it. Concretely the row is something like:

```csharp
public readonly record struct NormalizationCase(
    string Source,
    string FormC,
    string FormD,
    string FormKC,
    string FormKD,
    NormalizationCategory Category,
    string Description);
```

Then add a parameterized test base / helper that, given a grammar factory `Func<Rule>`, enumerates every (case, form) pair where the case's behavior under that form is meaningful for the rule under test, and asserts the rule's parse-time behavior matches the lexer's post-normalization view. The helper covers at least these wrapping shapes for each leaf rule: bare leaf (no shortcut path), `OneOrMore(leaf)` (BetweenInclusive shortcut), `Or(leaf, AnyToken().As("fallback"))` (Or shortcut), `And(leaf, Eof())` (no shortcut, but with trailing-input checking). Each wrapping shape exercises a different code path, and the FirstConsumedTokens-related bugs only show up under the shortcut shapes. For OneOf / NoneOf / ScanWhile / ScanUntil, the helper also varies the set construction (`TokenSet.Runes(s)`, `TokenSet.Single(rune)` when the case is a single rune, `TokenSet.Letters | ...`).

The matrix's expected outcome is computable from the table: under form F, the rule's literal text gets projected to row[F], and the input the test feeds is also projected to row[F]. If both end up matching the same lexer-produced token, the test asserts success. If row[F] is multi-grapheme (the compatibility-ligature case under FormKC), the test asserts a Compile-time offender error (since OneOf / Token can't match a multi-grapheme entry).

## Verify (test-first)

Add `src/InductorParser.Tests/NormalizationExamples.cs` with the data table and at least these rows: ASCII letter, U+00E9 (precomposed é), "e + combining acute" decomposed text, U+212B (Angstrom), U+2126 (Ohm), U+FB01 (ﬁ ligature), U+FF21 (fullwidth Ａ), U+AC00 (Hangul 가), the US flag emoji, the family-emoji ZWJ cluster, "\r\n" (CRLF), and a lone high surrogate.

Add `src/InductorParser.Tests/NormalizationMatrixTests.cs` with parameterized tests over the existing leaf rules: Token, Literal, OneOf, NoneOf, ScanWhile, ScanUntil. Each test runs the helper for one rule and one wrapping shape, and the helper iterates the table × four forms. Use NUnit's `[TestCaseSource]` so each case shows up as its own test name in the runner output.

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~NormalizationMatrix"
```

Expected: every case passes (after this backlog item lands and the systematic tests are written, no other rule should silently fail under any form / grapheme combination).

## Fix

There's no single source-code fix for the matrix itself; the matrix IS the fix. The implementation is:

1. Create `src/InductorParser.Tests/NormalizationExamples.cs` with the data table and the `NormalizationCategory` enum.
2. Create `src/InductorParser.Tests/NormalizationMatrixTests.cs` with the parameterized test fixtures, one per leaf rule × wrapping shape.
3. As each new case surfaces a real bug, file it as its own backlog item (with the matrix entry that exposed it as the regression test) and fix the source.
4. Add a guideline to `docs/UnicodeGotchas.md` (or wherever testing guidance lives) saying "any new test that involves a normalization form should pull cases from `NormalizationExamples` rather than hand-rolling a single grapheme example."

The matrix should also live with a `[Test]` self-check that asserts each table row's `FormC` / `FormD` / `FormKC` / `FormKD` columns equal `string.Normalize(...)` of the source. That way a typo in the table is caught at test time rather than producing a passing-but-wrong assertion.

## Verify the Fix

Re-run the same filter:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~NormalizationMatrix"
```

Expected: all rows pass for every rule × wrapping shape × form combination. Baseline before this backlog item landed is whatever the suite reports today (1905 / 1907 with 2 timing skips). Each new bug surfaced gets its own backlog item and fix; this one stays open as the framework / convention.
