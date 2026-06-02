# Pattern_White_Space and Pattern_Syntax TokenSet factories are missing

Real-world grammars repeatedly need two Unicode "pattern" properties from UAX #31 / PropList.txt, and today a grammar author has to spell them out by hand. The UTS #35 plural-rule spec, for example, says "Whitespace (defined as Unicode Pattern_White_Space) can occur between or around any of the above tokens", which forces a hand-rolled eleven-line union like:

```csharp
var patternWhiteSpace =
      TokenSet.Range(0x0009, 0x000D)
    | TokenSet.Single(0x0020)
    | TokenSet.Single(0x0085)
    | TokenSet.Single(0x200E)
    | TokenSet.Single(0x200F)
    | TokenSet.Single(0x2028)
    | TokenSet.Single(0x2029);
```

`Pattern_White_Space` is eleven specific code points (U+0009..U+000D, U+0020, U+0085, U+200E, U+200F, U+2028, U+2029). `Pattern_Syntax` is the larger set of characters used as syntax (brackets, operators, punctuation, math/currency symbols). Both are standard Unicode character classes in PropList.txt, both are stable (the consortium guarantees they're immutable across versions), and both show up in every CLDR LDML grammar, every UTS #18 regex character-class parser, and every UAX #31 identifier-syntax grammar. They'd fit the same shelf as the existing pre-built sets (`TokenSet.Letters`, `TokenSet.LineTerminators`, `TokenSet.Ascii.Digits`, `TokenSet.XidStart`, etc.).

Two related sets the original item suggested are already done. `IdentifierStart` (XID_Start) and `IdentifierContinue` (XID_Continue) already ship as public `TokenSet.XidStart` and `TokenSet.XidContinue` in TokenSet.Xid.cs. Don't add duplicate aliases.

## Design

The codebase already has an established pattern for "a Unicode property published in a UCD file, hand-typed as a private range table, built into a TokenSet, and verified against the live UCD by an `[Explicit]` test." That's what TokenSet.Xid.cs does for the XID properties, and the test helpers to verify a property against PropList.txt already exist in XidIdentifierTests.cs (`FetchPropListAsync`, `CodePointsForProperty`, `ReadPrivateRangeTable`, `ExpandRanges`, `AssertSetsEqual`). Both new sets follow that same shape, so they slot into existing machinery with no new infrastructure.

Both properties are reachable by the internal `FromRanges((int Low, int High)[])` helper in TokenSet.cs, the same one the XID builds use. Store each as a private range table so the existing `ReadPrivateRangeTable("...")` reflection-based UCD test can verify it.

Both `Pattern_White_Space` and `Pattern_Syntax` are code-point properties, not grapheme sets, so they contain CR and LF as separate single code points and carry no CRLF cluster. This is deliberately different from `TokenSet.LineTerminators`, which glues CRLF into one multi-rune entry. Worth a sentence in the doc comment so a reader doesn't expect cluster behavior.

New file `src/InductorParser/TokenSet.Pattern.cs`, a partial of `TokenSet`, mirroring the provenance style of TokenSet.Xid.cs (header citing UAX #31 and PropList.txt, a "recipe to regenerate" comment on each table):

- `private static readonly (int Low, int High)[] PatternWhiteSpaceRanges` as five ranges: (0x0009,0x000D), (0x0020,0x0020), (0x0085,0x0085), (0x200E,0x200F), (0x2028,0x2029).
- `private static readonly (int Low, int High)[] PatternSyntaxRanges`, every `Pattern_Syntax` range from PropList.txt at Unicode 17.0 copied verbatim with a per-line name comment (the same style as `OtherIdStart`). Roughly 30 ranges across U+0021..U+0040, U+005B..U+0060, U+007B..U+007E, scattered Latin-1 punctuation and symbols, U+2010..U+2027, U+2030..U+205E, U+2190..U+2BFF, U+2E00..U+2E7F, U+3001..U+3003, U+3008..U+3020, U+3030, U+FD3E..U+FD3F, U+FE45..U+FE46. Don't transcribe from memory, copy from the actual PropList.txt at unicode.org/Public/17.0.0. The matches_UCD test is what proves it's right.
- `public static readonly TokenSet PatternWhiteSpace = FromRanges(PatternWhiteSpaceRanges);` and `public static readonly TokenSet PatternSyntax = FromRanges(PatternSyntaxRanges);`, each with a `///` XML doc summary naming the Unicode property and linking the spec, so IntelliSense surfaces the property name. The XID accessors next door use plain `//`, but use `///` here since the whole point is IntelliSense surfacing and these are public API members.

Both are eager `static readonly` (cheap table expansion, like `LineTerminators` and `Replacement` on the main shelf), not `Lazy`.

## Tests

Fast, always-run tests in TokenSetTests.cs next to the `LineTerminators` / `AnyWhitespace` tests, using `ContainsRune`:

- `PatternWhiteSpace_contains_all_eleven_code_points_and_excludes_neighbors`: all eleven in (0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20, 0x85, 0x200E, 0x200F, 0x2028, 0x2029) and nearby points out (0x08, 0x0E, 0x1F, 0x84, 0x86, 0x200D, 0x2010, 0x202A, plus 'a' and '0'). Note: U+200E (LEFT-TO-RIGHT MARK) is in the set, so use 0x200D and 0x2010 as the out-points near the marks.
- `PatternSyntax_contains_representative_syntax_and_excludes_identifiers`: a sample in ('!', '#', '+', '<', '?', '~', '[', U+00AB, U+2010, U+2190) and out (letters 'a'/'A', digit '0', space ' ', underscore '_', which is Pc and deliberately not Pattern_Syntax).

Two `[Test, Explicit(...)]` UCD-verification tests in XidIdentifierTests.cs alongside the existing `*_matches_UCD_test` methods, reusing the existing helpers:

- `PatternWhiteSpace_matches_UCD_test`: `ExpandRanges(ReadPrivateRangeTable("PatternWhiteSpaceRanges"))` against `CodePointsForProperty(propList, "Pattern_White_Space")`.
- `PatternSyntax_matches_UCD_test`: same against `"Pattern_Syntax"`.

These lock the hand-typed tables to the published property and catch transcription errors and future version drift.

## Done when

`TokenSet.PatternWhiteSpace` and `TokenSet.PatternSyntax` are pre-built statics with `///` doc comments that link the Unicode spec and name the property. The fast tests and the two `[Explicit]` UCD tests are in place and green. Verify with `dotnet test` for the fast tests, and run the UCD tests on demand (they fetch PropList.txt) with `dotnet test --filter "FullyQualifiedName~PatternSyntax_matches_UCD_test|FullyQualifiedName~PatternWhiteSpace_matches_UCD_test"`.

Note: an earlier draft of this item said the fix should delete a hand-rolled definition in an `E2ESamples/CldrPlural` sample. There's no CLDR sample in the repo, so there's nothing to delete. The hand-rolled snippet above is just an illustration of the pain.
