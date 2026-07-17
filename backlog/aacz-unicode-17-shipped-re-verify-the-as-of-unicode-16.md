# Unicode 17 shipped: re-verify the "as of Unicode 16" normalization claims

While verifying citations for docs/MappingPositionsAfterNormalization.md (2026-07-10), unicode.org's "latest" core-spec link redirected to Unicode 17.0.0, so a new Unicode version is out. The doc makes version-scoped claims that were audited against Unicode 16:

- Korean jamo (U+3131, U+314F, U+FFA1, U+FFC2) are the only characters whose compatibility conversions merge adjacent original graphemes.
- The Unicode 16 context-sensitive composites (Tulu-Tigalari, Kirat Rai, Gurung Khema) all decompose within a single grapheme, so they never reach a boundary the position walker tests.

The doc's "Unicode 16 and the future" section names the code points to check when this happens: U+1138E TULU-TIGALARI LETTER AI, U+113C7 TULU-TIGALARI VOWEL SIGN OO, U+16D69 KIRAT RAI VOWEL SIGN O, U+16121 GURUNG KHEMA VOWEL SIGN U.

The work: sweep the Unicode 17 UCD (https://www.unicode.org/Public/17.0.0/ucd/) for new characters whose normalization merges adjacent graphemes or adds context-sensitive composites, reread UAX #15 §9.2 for new warnings, and update the doc's "In Unicode 16..." wording to whatever the sweep finds. If a new merge case exists, add it to NormalizationExamples and the walker tests in DocExamples/MappingPositionsAfterNormalizationExamples.cs. The walker itself needs no change either way (the per-boundary check handles new merge cases by design), so this is about keeping the doc's version-scoped claims current.

Related but separate: the UAX #29 conformance suite still runs GraphemeBreakTest-15.1.0.txt. Dropping in the Unicode 17 grapheme-break test data is the refresh path the conformance fixture's header comment describes.
