- Verify the per-grapheme normalization shortcut against whole-string normalization for defective combining sequences

`NormalizedPositionMap.TranslateViaPerGraphemeNormalize` (src/InductorParser/Lexing/NormalizedPositionMap.cs lines 100-166) walks the caller's original input one UAX #29 grapheme at a time and normalizes each grapheme on its own to map a normalized-side index back to an original-side index. The comment at lines 100-142 lays out the safety argument: UAX #15 says normalization isn't closed under concatenation (canonical reordering of combining marks reaches across split points), but UAX #29 GB9 keeps combining marks glued to their base inside one grapheme, so splitting by grapheme avoids the reordering problem for "real text" (text where combining marks always follow a base). The comment then names the edge case the argument doesn't cover: a defective combining sequence (a combining mark with no preceding base at the start of input or immediately after a control character) forms its own grapheme cluster, and per-grapheme normalization "can differ from whole-string normalization by one grapheme's worth of char offset."

That's a documented engineering trade-off, but there's no test that pins it. The matrix in `NormalizationExamples` has a `DefectiveCombiningMarkAlone` row covering identity behavior of a lone combining mark, but it doesn't exercise the position-translation drift the comment is worried about: a defective sequence followed by other content where the per-grapheme math diverges from whole-string math. A grammar that compiles with a compatibility form (FormKC or FormKD) and has a parse failure inside or just after a defective combining sequence would surface the drift through `ParseResult.ErrorCharIndex`.

## Verify (write tests for the documented edge case)

Add tests to `src/InductorParser.Tests/Core/NormalizationTests.cs` that:

1. Construct an input string starting with a defective combining sequence (e.g., `"́"` followed by content the grammar will reject) and a grammar compiled with FormKC.
2. Compute the expected `ErrorCharIndex` against the ORIGINAL input by running the lexer on the WHOLE-STRING normalized form and translating the deepest-failure position back via a `string.Normalize`-based one-shot mapping (the spec-blessed operation).
3. Assert `ParseResult.ErrorCharIndex` matches that expected value.
4. Repeat for a defective sequence at non-zero offsets (immediately after a control character is the documented other case) and for FormKD.

If the per-grapheme shortcut drifts in any of these cases, the test fails and surfaces the offset error. If it doesn't, the test confirms the engineering shortcut holds in practice for the cases UAX #15 names. Either outcome is useful: a passing test pins the current behavior, a failing test surfaces a real bug to fix.

```csharp
[Test]
public void Per_grapheme_position_translation_matches_whole_string_for_defective_leading_combining_mark()
{
    // Defective combining sequence at the very start of the input,
    // followed by content the grammar will reject. The error
    // position has to be translated through TranslateViaPerGraphemeNormalize
    // because FormKC is a compatibility form. We check the per-grapheme
    // answer matches what whole-string Normalize would produce.
    string original = "́Z";   // lone combining acute, then Z
    var grammar = AllOf(Token('a'), Eof()).Compile(NormalizationForm.FormKC);
    var result = grammar.Parse(original);

    Assert.That(result.Success, Is.False);

    // Compute the spec-blessed expected position: normalize the WHOLE
    // string at once, run the same parse against the normalized form,
    // and walk back to the original via a single string.Normalize call.
    string wholeNormalized = original.Normalize(NormalizationForm.FormKC);
    // ... compute expectedOriginalIndex by re-running the parse against
    //     wholeNormalized and finding the equivalent position in the
    //     un-normalized original via whole-string substring matching ...

    Assert.That(result.ErrorCharIndex, Is.EqualTo(expectedOriginalIndex),
        "per-grapheme position translation drifted from whole-string normalization on a defective leading combining sequence");
}
```

(The test sketch is incomplete because computing the spec-blessed expected position requires its own helper. Part of the work is writing that helper.)

## Fix

Two possible outcomes:

1. **Tests pass on the existing implementation**: pin them as regression tests. The engineering shortcut is documented and verified, and we've added confidence that the documented edge case doesn't actually drift in practice. Update the comment in `NormalizedPositionMap.cs` to reference the test as evidence.

2. **Tests fail**: replace `TranslateViaPerGraphemeNormalize` with a whole-string normalization. The per-grapheme walk was an engineering speedup; falling back to one `string.Normalize` call per failure is one allocation per parse failure, which isn't on any hot path. The accuracy is worth the cost. Re-run the matrix tests in `NormalizationMatrixTests` to confirm no regressions.

Either way the comment in `NormalizedPositionMap.cs` lines 135-142 should be updated to either say "the edge case is verified by `<test name>`" or "the edge case used to drift but the implementation now uses whole-string normalization, see `<test name>`".

## Verify the Fix

Re-run the same tests:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~Per_grapheme_position_translation"
```

Expected: passes. Run the full suite to confirm no regressions, particularly the matrix tests and the existing `NormalizationTests` position-translation cases.
