using System;
using System.Collections.Generic;
using System.Text;
using InductorParser.Lexing;
using NUnit.Framework;

namespace InductorParser.Tests;

// Curated table of grapheme behaviors across the four NormalizationForms.
// The lexer always sees post-normalization input, so any rule field
// that's still in the user-supplied form (or any cache derived from it)
// is stale by the time matching happens. Rather than hand-writing a
// one-off Unicode example per test, the parameterized fixtures in
// per-rule test fixture pulls from this table so every interesting
// (rule × form × grapheme) combination gets exercised once and benefits
// every leaf rule uniformly.
//
// Each row records what the source text normalizes to under each of the
// four forms. FormC and FormKC are the "compose" forms. FormD and FormKD
// are the "decompose" forms. The compatibility forms (FormKC and FormKD)
// additionally rewrite ligatures, fullwidth letters, and other
// presentation variants into their plain equivalents.
//
// A row's "post-form" text is what the lexer actually hands to a rule's
// matching loop after Parse(input).Compile(form) ran. If the post-form
// text is multi-grapheme (the compatibility-ligature case under FormKC,
// where ﬁ → "fi" is two graphemes), OneOf / Token can't match it as one
// token by definition, and Compile reports the entry as a normalization
// offender. The category tag below distinguishes that case so matrix
// tests can assert the right outcome.
//
// Convention: every Unicode value is built from explicit hex codepoints
//, either `(char)0xHHHH` casts concatenated into a string, or `\uHHHH`
// escapes for surrogates, with a same-line "// looks like X" comment
// showing the rendered character. The hex form fixes the bytes regardless
// of editor or text-processing layer behavior, and the comment tells a
// reader what they're looking at without having to decode the codepoints
// by eye. Single ASCII characters ("a", "K", "fi") stay as plain string
// literals since ASCII is unambiguous.
public static class NormalizationExamples
{
    public enum NormalizationCategory
    {
        // Source is already in every form. ASCII letters, ASCII digits,
        // most punctuation. The "no-op" case the matrix needs as a control.
        AlreadyNormalized,

        // Source is a precomposed single-char rune (one UTF-16 code unit)
        // that decomposes under FormD/FormKD into a base rune plus one
        // or more combining marks. Source = "é" (U+00E9).
        // FormD = "e" + U+0301. The textbook canonical-equivalence case.
        PrecomposedDecomposesUnderD,

        // Source is a base rune followed by combining marks. FormC and FormKC
        // compose them into a single precomposed rune. Source = "e" + U+0301.
        // FormC = "é".
        DecomposedComposesUnderC,

        // Source is a base rune followed by two or more combining marks
        // whose canonical combining classes (CCC) differ AND whose
        // declared order isn't the canonical one (lower CCC first).
        // FormD reorders the marks into canonical order. FormC reorders
        // and may then compose into a precomposed rune. The textbook
        // case is Vietnamese ậ written as base + circumflex (CCC=230)
        // + dot-below (CCC=220): the canonical order is dot-below then
        // circumflex. Source "a" + U+0302 + U+0323 reorders to
        // "a" + U+0323 + U+0302 under FormD, and composes to U+1EAD
        // under FormC. The PRI #29 corrigendum specifically tightened
        // the normalization spec around sequences of this shape.
        // Distinct from DecomposedComposesUnderC because the FormD
        // output has a DIFFERENT rune order than the source, not just
        // a different segmentation.
        NonCanonicalCombiningMarkOrderReordersUnderD,

        // Source is a base rune plus combining marks in non-canonical
        // order, but NO precomposed form exists for any base+mark
        // combination involved. NFD/NFC reorder the marks into
        // canonical order without composing. Example: "q" + U+0307
        // (dot above, CCC=230) + U+0323 (dot below, CCC=220) reorders
        // to "q" + U+0323 + U+0307 under every form (no precomposed
        // q-with-dot-above or q-with-dot-below to compose to).
        // Distinct from NonCanonicalCombiningMarkOrderReordersUnderD
        // because the FormC result is multi-rune, not a single
        // precomposed rune. Tests whether reordering happens
        // independently of composition.
        CombiningMarksReorderWithoutComposing,

        // Source is a precomposed character that ALREADY has a
        // combining mark in its decomposition. Appending an additional
        // mark in non-canonical order causes NFC to recompose with a
        // DIFFERENT base+mark pair than the source. UAX #15's worked
        // example: U+1E0A (Ḋ, "D with dot above") + U+0323 (combining
        // dot below) normalizes to U+1E0C (Ḍ, "D with dot below") +
        // U+0307 (combining dot above) under FormC/FormKC. Under
        // FormD the source decomposes fully to D + U+0323 + U+0307
        // (canonical order: dot-below CCC=220 before dot-above
        // CCC=230). The composite "won" by NFC is U+1E0C, not U+1E0A:
        // canonical reordering shuffled the marks and the FIRST mark
        // (dot-below) found a different precomposed pairing.
        // Distinct because the precomposed character in the FormC
        // result isn't the same as the precomposed character in the
        // source: the composite SHIFTED.
        CanonicalCompositeShiftsUnderRecomposition,

        // Source is a single combining mark whose canonical
        // decomposition is MULTIPLE combining marks. The textbook case
        // is U+0344 COMBINING GREEK DIALYTIKA TONOS, which decomposes
        // to U+0308 (combining diaeresis) + U+0301 (combining acute)
        // under every form. Unicode explicitly lists U+0344 as the
        // example of a non-starter decomposition (the source is a
        // non-starter and so are its decomposition pieces). Distinct
        // from DefectiveCombiningMarkAlone because the source does
        // have a decomposition (it's not normalization-identity), and
        // distinct from CompositionExcludedDecomposesUnderBoth because
        // the source is itself a non-starter rather than a base+mark
        // precomposed letter. The decomposed result is two adjacent
        // defective combining marks with no preceding base.
        NonStarterDecomposesToNonStarters,

        // Source is a precomposed character whose canonical decomposition
        // is excluded from recomposition by the script-specific
        // composition exclusions list. The standard example is
        // Devanagari U+0958 क़ DEVANAGARI LETTER QA, whose canonical
        // decomposition U+0915 + U+093C (KA + NUKTA) is on the
        // composition exclusions list. NFC won't recompose
        // KA + NUKTA back to QA, so NFC of the PRECOMPOSED source
        // returns the DECOMPOSED multi-rune form. Both FormC and FormD
        // produce the multi-rune sequence, which is the opposite of
        // the typical PrecomposedDecomposesUnderD shape (where FormC
        // keeps the source intact). Same script-specific exclusion
        // shape applies to certain Hebrew and Tibetan characters and
        // to Bengali, Gurmukhi, and Oriya nukta letters.
        CompositionExcludedDecomposesUnderBoth,

        // Source is a canonical singleton whose target rune is itself
        // stable under further decomposition: substitutes to a different
        // single rune under both FormC and FormD, and that's where the
        // story ends. U+2126 OHM → U+03A9 GREEK CAPITAL OMEGA (no
        // further decomposition). U+212A KELVIN → U+004B ASCII K (no
        // further decomposition). FormC and FormD give the same result.
        CanonicalSingletonToStableRune,

        // Source is a canonical singleton whose target rune ITSELF has
        // a further canonical decomposition: substitutes under FormC to
        // a precomposed rune, but under FormD that precomposed rune
        // decomposes again into base + combining marks. U+212B ANGSTROM
        // → U+00C5 LATIN A WITH RING under FormC, and U+212B → "A" + U+030A
        // combining ring above under FormD. Two-step substitution makes
        // this the most interesting singleton shape: a grammar literal
        // for the Angstrom compiled with FormD ends up matching a
        // multi-rune cluster, while the same literal compiled with FormC
        // matches a single rune.
        CanonicalSingletonToDecomposableRune,

        // Source is a compatibility singleton: FormC leaves it alone,
        // FormKC substitutes a different rune. U+2102 DOUBLE-STRUCK
        // CAPITAL C → U+0043 'C' under FormKC. Distinguishes itself from
        // the canonical singletons by being stable under FormC.
        CompatibilitySingletonRune,

        // Source is a compatibility character whose FormKC conversion
        // is more than one grapheme. The count varies: ﬁ → "fi" is two,
        // ﬃ → "ffi" is three, U+2167 ROMAN NUMERAL EIGHT → "VIII" is
        // four, U+33A8 → "m/s²" is also four. Token / OneOf can't match
        // a multi-grapheme entry, so Compile is supposed to surface
        // this as a normalization offender regardless of the count.
        // FormC and FormD leave these source characters alone.
        CompatibilityMultiGraphemeExpansion,

        // Source is a fullwidth Latin letter (U+FF21..U+FF5A). FormKC
        // converts to the ASCII equivalent. FormC and FormD leave it alone.
        FullwidthToHalfwidth,

        // Source is a sequence of two halfwidth Katakana code units
        // that together represent one logical kana plus a voicing
        // mark. Under FormC/FormD: identity the halfwidth chars don't
        // canonically compose. Under FormKC: composes to a single
        // precomposed full-width kana with the voicing baked in.
        // Under FormKD: maps to two full-width chars (base + combining
        // voicing mark). UAX #15 has a specific Japanese compatibility
        // table for this. Example: U+FF76 (HALFWIDTH KATAKANA LETTER
        // KA) + U+FF9E (HALFWIDTH KATAKANA VOICED SOUND MARK) →
        // identity under C/D, U+30AC (KATAKANA LETTER GA) under KC,
        // U+30AB (KATAKANA LETTER KA) + U+3099 (COMBINING KATAKANA-
        // HIRAGANA VOICED SOUND MARK) under KD. Distinct shape: a
        // multi-rune source whose K-form output is a single different
        // rune (versus the existing K-form categories that go from
        // single rune to single rune or single rune to multi-grapheme).
        HalfwidthKatakanaComposesUnderKForms,

        // Unicode 16+ context-sensitive NFC composites for Kirat Rai,
        // Tulu-Tigalari, and Gurung Khema scripts: a character can
        // be NFC-stable by itself but change when preceded by certain
        // other characters in the same script. UAX #15 calls these
        // out as a new normalization category in Unicode 16. Not yet
        // testable here: .NET 8's string.Normalize ships with whatever
        // Unicode version the runtime ICU has (Unicode 15 in .NET 8),
        // so adding rows now would either silently behave as identity
        // (misleading the matrix's self-check) or fail differently
        // across runtimes. Adding the category as a placeholder so
        // future runtime upgrades surface this gap.
        // TODO: add concrete rows once the .NET runtime supports
        // Unicode 16. Inferred concrete shape: U+1138B U+113C7 in
        // Tulu-Tigalari should normalize across the boundary because
        // U+113C7 has decomposition U+113C2 U+113B8 and U+1138E has
        // decomposition U+1138B U+113C2.
        Unicode16ContextSensitiveCompositionPlaceholder,

        // Source is a precomposed Hangul syllable. FormD decomposes it
        // into its constituent conjoining jamo. The decomposition is
        // either two jamo (initial consonant + vowel, e.g., U+AC00 →
        // U+1100 + U+1161) or three (initial consonant + vowel + final
        // consonant, e.g., U+AC01 → U+1100 + U+1161 + U+11A8). Per the
        // Unicode text-segmentation rules, conjoining jamo sequences
        // cluster as one grapheme regardless of whether they came from
        // a precomposed syllable or were typed as jamo directly. So a
        // row's FormD column is multi-rune but still single-grapheme.
        //
        // Hangul has other normalization behaviors that fit elsewhere:
        // a typed jamo sequence composing back into a syllable under
        // FormC is the DecomposedComposesUnderC shape. A compatibility
        // jamo (U+3131 HANGUL LETTER KIYEOK → U+1100 conjoining jamo
        // only under FormKC, stable under FormC) is the
        // CompatibilitySingletonRune shape.
        // This category is for the syllable-decomposition direction
        // specifically.
        HangulSyllableDecomposesUnderD,

        // Source is a multi-rune grapheme cluster that's identity under
        // every form (zero-width-joiner-built emoji like the family
        // emoji, regional-indicator flag pairs, skin-toned emoji,
        // base+combining-mark sequences whose precomposed form doesn't
        // exist). Sibling category CarriageReturnLineFeedCluster below
        // shares the "stays under every form" behavior but is split
        // out because of cluster-boundary morphology, see its comment.
        MultiRuneClusterStaysAnyForm,

        // Source is a base rune followed by enough combining marks
        // that the grapheme exceeds the Unicode "Stream-Safe Format"
        // limit (more than 30 non-starters in a row). Stream-Safe
        // Format is a Unicode-defined transformation that inserts a
        // U+034F COMBINING GRAPHEME JOINER into long runs so no
        // grapheme has more than 30 combining marks. Standard NFC/NFD
        // don't do this insertion (they just decompose/compose without
        // truncating), so the sequence stays at its original length
        // under all four forms. The matrix exercise here is whether
        // the parser correctly handles an extra-long single grapheme
        // cluster when a rule walks one rune at a time inside a token
        // (WithinToken sub-lexer mode), one cluster at a time, or
        // when a leaf rule's _expected text is the long cluster.
        // Distinct from MultiRuneClusterStaysAnyForm because the run
        // length matters for parser correctness even though the
        // normalization behavior is the same.
        StreamSafeBoundaryLongCombinerSequence,

        // Source is a single combining mark with no preceding base
        // rune. Per the Unicode text-segmentation rules a combining
        // mark normally attaches to the previous cluster, but with
        // no previous cluster the combining mark forms its own
        // "defective" grapheme cluster of one rune. Behaves as
        // identity under every normalization form (no composition
        // partner is available). Distinct from AlreadyNormalized
        // because the source is morphologically dependent (a
        // combining mark) rather than a complete base rune: a rule
        // that expects "starts with a base letter" can't accept this
        // even though the cluster is a single grapheme.
        DefectiveCombiningMarkAlone,

        // Source is a lone surrogate code unit (a UTF-16 high surrogate
        // U+D800..U+DBFF or low surrogate U+DC00..U+DFFF that isn't
        // paired with its other half). Lone surrogates don't represent
        // a Unicode scalar value, so string.Normalize throws
        // ArgumentException for all four normalization forms. The only
        // way to use a lone surrogate in a grammar is Compile(null)
        // (skip normalization entirely). Under Compile(null) the lexer
        // surfaces the lone surrogate as a one-char token with
        // RuneValue == -1. Rules that use TryPeekRune get false back
        // and have to handle it explicitly.
        //
        // What works for matching a lone surrogate (Compile(null)):
        //
        //   Token("\uD800")    , Token(string) goes through the
        //                          segmenter, which treats the surrogate
        //                          as one one-char cluster. (Token(char)
        //                          refuses surrogates at the factory.)
        //   Literal("a\uD800b"), bit-exact char-by-char compare, no
        //                          rune validation.
        //   AnyToken()         , matches any cluster including a lone
        //                          surrogate.
        //   ScanUntil(...)     , surrogates flow through the body
        //                          (the stopper set is validated, but
        //                          body content isn't).
        //
        // What doesn't work (rejects lone surrogates at construction
        // time, before any compile):
        //
        //   Token('\uD800')               , the char factory refuses
        //                                     surrogates explicitly.
        //   OneOf(TokenSet.Runes("\uD800")), TokenSet.Runes validates
        //                                     each rune.
        //   OneOf(TokenSet.Single(0xD800)), TokenSet.Single validates
        //                                     the codepoint.
        //
        // The matrix exercises each rule type's "throws under every
        // form" path separately because the throw happens at different
        // layers per rule.
        LoneSurrogateNotNormalizable,

        // Source is "\r\n". Single grapheme cluster, unchanged under
        // any normalization form. Distinct from
        // MultiRuneClusterStaysAnyForm above only by cluster morphology:
        // CRLF is the only Unicode-segmentation cluster built from two
        // unrelated runes that are EACH individually meaningful single
        // graphemes (CR alone is a grapheme, LF alone is a grapheme,
        // both are common line terminators). ZWJ-emoji components are
        // joiners or modifiers. Regional-indicator components only have
        // flag meaning when paired. Combining marks are dependent by
        // definition. CRLF is the one a grammar author might reasonably
        // try to match individually (Token('\r'), OneOf("\r\n"), etc.),
        // so cluster-boundary surprises are a common bug source.
        // Keeping it separate surfaces that morphology in test names.
        CarriageReturnLineFeedCluster,
    }

    // One row of the matrix.
    public sealed record NormalizationCase(
        string Source,
        string FormC,
        string FormD,
        string FormKC,
        string FormKD,
        NormalizationCategory Category,
        string Description);

    // Resolve the post-normalization text for a given form. Centralized so
    // the matrix tests don't switch on the form value at every call.
    public static string Project(NormalizationCase row, NormalizationForm form) =>
        form switch
        {
            NormalizationForm.FormC => row.FormC,
            NormalizationForm.FormD => row.FormD,
            NormalizationForm.FormKC => row.FormKC,
            NormalizationForm.FormKD => row.FormKD,
            _ => row.Source,
        };

    // True when the row's post-form text is a single grapheme cluster.
    // OneOf / Token / single-grapheme rules can match these. False for the
    // compatibility-ligature rows under FormKC / FormKD (where ﬁ → "fi" is
    // two graphemes), where Compile is supposed to surface a normalization
    // offender at the rule build. The classification comes from
    // GraphemeHelpers (not the runtime's StringInfo) because it
    // predicts what the parser will do, and GraphemeHelpers uses the
    // same segmentation the parser does.
    public static bool PostFormIsSingleGrapheme(NormalizationCase row, NormalizationForm form)
    {
        string projected = Project(row, form);
        if (projected.Length == 0) return false;
        return GraphemeHelpers.FirstClusterLength(projected.AsSpan()) == projected.Length;
    }

    // The four normalization forms tests iterate.
    public static readonly NormalizationForm[] AllForms = new[]
    {
        NormalizationForm.FormC,
        NormalizationForm.FormD,
        NormalizationForm.FormKC,
        NormalizationForm.FormKD,
    };

    // Cross-product of (table row, form) so [TestCaseSource] can drive
    // one test per pair. Each case names itself with the row's
    // Description and the form so test-runner output is readable.
    public static IEnumerable<TestCaseData> RowFormPairs()
    {
        foreach (var row in All)
            foreach (var form in AllForms)
                yield return new TestCaseData(row, form)
                    .SetName($"{row.Category} | {form} | {row.Description}");
    }

    // Hex render for diagnostics. ToString on a multi-rune grapheme like
    // "👨‍👩‍👧" doesn't tell you the code points. The hex view does, so
    // a failure on that row points at the right text in the table.
    public static string Hex(string text)
    {
        if (text.Length == 0) return "<empty>";
        var builder = new StringBuilder();
        for (int index = 0; index < text.Length; index++)
        {
            if (index > 0) builder.Append(' ');
            builder.Append("U+").Append(((int)text[index]).ToString("X4"));
        }
        return builder.ToString();
    }

    // Single-rune values. Each is one Unicode scalar, expressed via a
    // (char) cast on the explicit hex codepoint. Concatenated with the
    // empty string so the result is a string for the rest of the
    // pipeline.

    // Multi-rune values built by concatenating (char) casts. Splitting
    // ensures no editor or text-processing layer can NFC-convert the
    // sequence back into a precomposed character.

    // Lone surrogates come from the runtime-built UnicodeExamples
    // constants, never \u escapes: IL2CPP replaces an unpaired surrogate
    // in a compiled string literal with U+FFFD, so a literal form never
    // reaches the Unity test player intact.

    // Supplementary-plane sequences. Each rune is a UTF-16 surrogate pair.
    // \u escapes for the surrogates themselves survive the tooling pipeline
    // and the C# compiler reassembles them into the real codepoint.

    // Base "a" followed by 31 copies of U+0316 COMBINING GRAVE ACCENT
    // BELOW. U+0316 has no precomposed form with any letter, so NFC
    // won't compose any of these. 31 non-starters exceeds the Unicode
    // Stream-Safe Format limit of 30 non-starters per cluster.
    // Standard NFC/NFD don't insert a U+034F COMBINING GRAPHEME JOINER
    // here. Only the separate Stream-Safe transformation does. So the
    // long sequence stays the same length under all four normalization
    // forms, and the lexer sees it as one grapheme cluster of 32 runes.
    private static readonly string StreamSafeBoundaryLongSequence =
        "a" + new string((char)UnicodeExamples.CombiningGraveBelowRune, 31);

    // The full table. The NormalizationExamplesSelfCheck fixture below
    // asserts each column equals what the parser's normalizer produces
    // for (Source, form), so a typo here gets caught at test time
    // rather than producing a passing-but-wrong assertion downstream.
    public static IReadOnlyList<NormalizationCase> All { get; } = new[]
    {
        new NormalizationCase(
            Source: "a",
            FormC: "a", FormD: "a", FormKC: "a", FormKD: "a",
            Category: NormalizationCategory.AlreadyNormalized,
            Description: "ASCII letter, identity under every form"),

        new NormalizationCase(
            Source: UnicodeExamples.LatinEAcutePrecomposedGrapheme,                          // looks like é (precomposed)
            FormC: UnicodeExamples.LatinEAcutePrecomposedGrapheme,
            FormD: UnicodeExamples.LatinEAcuteGrapheme,                            // looks like é (decomposed)
            FormKC: UnicodeExamples.LatinEAcutePrecomposedGrapheme,
            FormKD: UnicodeExamples.LatinEAcuteGrapheme,
            Category: NormalizationCategory.PrecomposedDecomposesUnderD,
            Description: "U+00E9 e-acute precomposed, decomposes under D/KD"),

        new NormalizationCase(
            Source: UnicodeExamples.LatinEAcuteGrapheme,                           // looks like é (decomposed)
            FormC: UnicodeExamples.LatinEAcutePrecomposedGrapheme,                           // looks like é (composes under C)
            FormD: UnicodeExamples.LatinEAcuteGrapheme,
            FormKC: UnicodeExamples.LatinEAcutePrecomposedGrapheme,
            FormKD: UnicodeExamples.LatinEAcuteGrapheme,
            Category: NormalizationCategory.DecomposedComposesUnderC,
            Description: "e + combining acute, composes under C/KC"),

        new NormalizationCase(
            Source: UnicodeExamples.AngstromGrapheme,                               // looks like Å (U+212B Angstrom)
            FormC: UnicodeExamples.LatinCapitalAWithRingAboveGrapheme,                            // looks like Å (U+00C5 LATIN A WITH RING)
            FormD: UnicodeExamples.LatinAWithRingAboveDecomposedText,                             // looks like Å (A + combining ring)
            FormKC: UnicodeExamples.LatinCapitalAWithRingAboveGrapheme,
            FormKD: UnicodeExamples.LatinAWithRingAboveDecomposedText,
            Category: NormalizationCategory.CanonicalSingletonToDecomposableRune,
            Description: "U+212B ANGSTROM, singleton to U+00C5, which itself decomposes under FormD"),

        new NormalizationCase(
            Source: UnicodeExamples.OhmGrapheme,                                    // looks like Ω (U+2126 OHM)
            FormC: UnicodeExamples.GreekCapitalOmegaGrapheme,                           // looks like Ω (U+03A9 Greek capital omega)
            FormD: UnicodeExamples.GreekCapitalOmegaGrapheme,
            FormKC: UnicodeExamples.GreekCapitalOmegaGrapheme,
            FormKD: UnicodeExamples.GreekCapitalOmegaGrapheme,
            Category: NormalizationCategory.CanonicalSingletonToStableRune,
            Description: "U+2126 OHM, singleton to U+03A9, target rune is stable"),

        new NormalizationCase(
            Source: UnicodeExamples.KelvinGrapheme,                                 // looks like K (U+212A KELVIN, not ASCII K)
            FormC: "K",                                         // ASCII K (U+004B)
            FormD: "K",
            FormKC: "K",
            FormKD: "K",
            Category: NormalizationCategory.CanonicalSingletonToStableRune,
            Description: "U+212A KELVIN, singleton to ASCII K, target rune is stable"),

        new NormalizationCase(
            Source: UnicodeExamples.DoubleStruckCGrapheme,                              // looks like ℂ (U+2102)
            FormC: UnicodeExamples.DoubleStruckCGrapheme,
            FormD: UnicodeExamples.DoubleStruckCGrapheme,
            FormKC: "C",                                        // ASCII C
            FormKD: "C",
            Category: NormalizationCategory.CompatibilitySingletonRune,
            Description: "U+2102 DOUBLE-STRUCK C, compatibility singleton to ASCII C"),

        new NormalizationCase(
            Source: UnicodeExamples.FiLigatureGrapheme,                                 // looks like ﬁ (U+FB01)
            FormC: UnicodeExamples.FiLigatureGrapheme,
            FormD: UnicodeExamples.FiLigatureGrapheme,
            FormKC: "fi",                                       // two graphemes after KC
            FormKD: "fi",
            Category: NormalizationCategory.CompatibilityMultiGraphemeExpansion,
            Description: "U+FB01 fi-ligature, KC/KD expand to two graphemes"),

        new NormalizationCase(
            Source: UnicodeExamples.RomanNumeralEightGrapheme,                          // looks like Ⅷ (U+2167)
            FormC: UnicodeExamples.RomanNumeralEightGrapheme,
            FormD: UnicodeExamples.RomanNumeralEightGrapheme,
            FormKC: "VIII",                                     // four graphemes after KC
            FormKD: "VIII",
            Category: NormalizationCategory.CompatibilityMultiGraphemeExpansion,
            Description: "U+2167 ROMAN NUMERAL EIGHT, KC/KD expand to four graphemes"),

        new NormalizationCase(
            Source: UnicodeExamples.FullwidthAGrapheme,                                 // looks like Ａ (U+FF21)
            FormC: UnicodeExamples.FullwidthAGrapheme,
            FormD: UnicodeExamples.FullwidthAGrapheme,
            FormKC: "A",                                        // ASCII A
            FormKD: "A",
            Category: NormalizationCategory.FullwidthToHalfwidth,
            Description: "U+FF21 FULLWIDTH A, KC/KD to ASCII A"),

        new NormalizationCase(
            Source: UnicodeExamples.VietnameseACircumflexDotBelowReorderedText,                 // looks like ậ (a + circumflex + dot-below, non-canonical)
            FormC: UnicodeExamples.VietnameseACircumflexDotBelowGrapheme,    // looks like ậ (U+1EAD precomposed)
            FormD: UnicodeExamples.VietnameseACircumflexDotBelowCanonicalText,              // looks like ậ (a + dot-below + circumflex, canonical)
            FormKC: UnicodeExamples.VietnameseACircumflexDotBelowGrapheme,
            FormKD: UnicodeExamples.VietnameseACircumflexDotBelowCanonicalText,
            Category: NormalizationCategory.NonCanonicalCombiningMarkOrderReordersUnderD,
            Description: "Vietnamese a-circumflex-dot-below in non-canonical mark order, reorders under D/KD, composes under C/KC"),

        new NormalizationCase(
            Source: UnicodeExamples.DevanagariQaPrecomposedGrapheme,                    // looks like क़ (U+0958 precomposed)
            FormC: UnicodeExamples.DevanagariKaNuktaDecomposedText,                 // looks like क़ (KA + NUKTA, composition exclusion)
            FormD: UnicodeExamples.DevanagariKaNuktaDecomposedText,
            FormKC: UnicodeExamples.DevanagariKaNuktaDecomposedText,
            FormKD: UnicodeExamples.DevanagariKaNuktaDecomposedText,
            Category: NormalizationCategory.CompositionExcludedDecomposesUnderBoth,
            Description: "U+0958 DEVANAGARI QA, composition exclusion, NFC produces decomposed form"),

        new NormalizationCase(
            Source: StreamSafeBoundaryLongSequence,             // base + 31 combining grave-below marks
            FormC: StreamSafeBoundaryLongSequence,
            FormD: StreamSafeBoundaryLongSequence,
            FormKC: StreamSafeBoundaryLongSequence,
            FormKD: StreamSafeBoundaryLongSequence,
            Category: NormalizationCategory.StreamSafeBoundaryLongCombinerSequence,
            Description: "base + 31 combining grave-below marks, exceeds Stream-Safe 30-non-starter limit, identity any form"),

        new NormalizationCase(
            Source: UnicodeExamples.LoneCombiningGraveText,                         // looks like ̀ (U+0300 alone, defective)
            FormC: UnicodeExamples.LoneCombiningGraveText,
            FormD: UnicodeExamples.LoneCombiningGraveText,
            FormKC: UnicodeExamples.LoneCombiningGraveText,
            FormKD: UnicodeExamples.LoneCombiningGraveText,
            Category: NormalizationCategory.DefectiveCombiningMarkAlone,
            Description: "lone U+0300 combining grave, defective combining sequence, identity any form"),

        new NormalizationCase(
            Source: UnicodeExamples.HighSurrogateMinText,                          // U+D800 lone high surrogate
            FormC: UnicodeExamples.HighSurrogateMinText,                           // (column values aren't reachable: string.Normalize throws)
            FormD: UnicodeExamples.HighSurrogateMinText,
            FormKC: UnicodeExamples.HighSurrogateMinText,
            FormKD: UnicodeExamples.HighSurrogateMinText,
            Category: NormalizationCategory.LoneSurrogateNotNormalizable,
            Description: "U+D800 lone high surrogate, string.Normalize throws under every form"),

        new NormalizationCase(
            Source: UnicodeExamples.QWithDotAboveDotBelowNonCanonicalText,                     // looks like q̣̇ (q + dot-above + dot-below)
            FormC: UnicodeExamples.QWithDotBelowDotAboveCanonicalText,                         // looks like q̣̇ (q + dot-below + dot-above, no compose target)
            FormD: UnicodeExamples.QWithDotBelowDotAboveCanonicalText,
            FormKC: UnicodeExamples.QWithDotBelowDotAboveCanonicalText,
            FormKD: UnicodeExamples.QWithDotBelowDotAboveCanonicalText,
            Category: NormalizationCategory.CombiningMarksReorderWithoutComposing,
            Description: "q + dot-above + dot-below, reorders without composing under any form"),

        new NormalizationCase(
            Source: UnicodeExamples.DWithDotAboveAndDotBelowSourceText,             // looks like Ḍ̇ (Ḋ + dot below)
            FormC: UnicodeExamples.DWithDotBelowAndDotAboveFormCText,               // looks like Ḍ̇ (Ḍ + dot above, composite shifted)
            FormD: UnicodeExamples.DWithBothDotsFullyDecomposedText,                // looks like Ḍ̇ (D + dot-below + dot-above, fully decomposed)
            FormKC: UnicodeExamples.DWithDotBelowAndDotAboveFormCText,
            FormKD: UnicodeExamples.DWithBothDotsFullyDecomposedText,
            Category: NormalizationCategory.CanonicalCompositeShiftsUnderRecomposition,
            Description: "U+1E0A + U+0323, FormC recomposes to U+1E0C + U+0307 (composite shifted)"),

        new NormalizationCase(
            Source: UnicodeExamples.DialytikaTonosPrecomposedGrapheme,                  // looks like ̈́ (U+0344)
            FormC: UnicodeExamples.DialytikaTonosDecomposedText,                    // looks like ̈́ (U+0308 + U+0301)
            FormD: UnicodeExamples.DialytikaTonosDecomposedText,
            FormKC: UnicodeExamples.DialytikaTonosDecomposedText,
            FormKD: UnicodeExamples.DialytikaTonosDecomposedText,
            Category: NormalizationCategory.NonStarterDecomposesToNonStarters,
            Description: "U+0344 dialytika-tonos, single non-starter decomposes to two non-starters under every form"),

        new NormalizationCase(
            Source: UnicodeExamples.HalfwidthKaWithVoicingSourceText,               // looks like ｶﾞ (halfwidth KA + voicing)
            FormC: UnicodeExamples.HalfwidthKaWithVoicingSourceText,                // identity under C
            FormD: UnicodeExamples.HalfwidthKaWithVoicingSourceText,
            FormKC: UnicodeExamples.FullwidthKatakanaGaPrecomposedGrapheme,                     // looks like ガ (U+30AC GA precomposed)
            FormKD: UnicodeExamples.KatakanaKaPlusCombiningVoicingText,             // looks like ガ (U+30AB + U+3099)
            Category: NormalizationCategory.HalfwidthKatakanaComposesUnderKForms,
            Description: "halfwidth KA + voicing, identity under C/D, KC composes to U+30AC, KD splits to U+30AB + U+3099"),

        new NormalizationCase(
            Source: UnicodeExamples.HangulGaPrecomposedGrapheme,                        // looks like 가 (U+AC00)
            FormC: UnicodeExamples.HangulGaPrecomposedGrapheme,
            FormD: UnicodeExamples.HangulGaTwoJamoDecomposedText,                         // looks like 가 (CHOSEONG KIYEOK + JUNGSEONG A)
            FormKC: UnicodeExamples.HangulGaPrecomposedGrapheme,
            FormKD: UnicodeExamples.HangulGaTwoJamoDecomposedText,
            Category: NormalizationCategory.HangulSyllableDecomposesUnderD,
            Description: "U+AC00,2-jamo Hangul syllable, FormD decomposes into initial + vowel"),

        new NormalizationCase(
            Source: UnicodeExamples.HangulGagPrecomposedGrapheme,                       // looks like 각 (U+AC01)
            FormC: UnicodeExamples.HangulGagPrecomposedGrapheme,
            FormD: UnicodeExamples.HangulGagThreeJamoDecomposedText,                      // looks like 각 (initial + vowel + final jamo)
            FormKC: UnicodeExamples.HangulGagPrecomposedGrapheme,
            FormKD: UnicodeExamples.HangulGagThreeJamoDecomposedText,
            Category: NormalizationCategory.HangulSyllableDecomposesUnderD,
            Description: "U+AC01,3-jamo Hangul syllable, FormD decomposes into initial + vowel + final"),

        new NormalizationCase(
            Source: UnicodeExamples.USFlagGrapheme,                                     // looks like 🇺🇸 (regional indicator pair)
            FormC: UnicodeExamples.USFlagGrapheme,
            FormD: UnicodeExamples.USFlagGrapheme,
            FormKC: UnicodeExamples.USFlagGrapheme,
            FormKD: UnicodeExamples.USFlagGrapheme,
            Category: NormalizationCategory.MultiRuneClusterStaysAnyForm,
            Description: "US flag regional-indicator pair, stays under every form"),

        new NormalizationCase(
            Source: UnicodeExamples.FamilyManWomanGirlGrapheme,                             // looks like 👨‍👩‍👧 (man + ZWJ + woman + ZWJ + girl)
            FormC: UnicodeExamples.FamilyManWomanGirlGrapheme,
            FormD: UnicodeExamples.FamilyManWomanGirlGrapheme,
            FormKC: UnicodeExamples.FamilyManWomanGirlGrapheme,
            FormKD: UnicodeExamples.FamilyManWomanGirlGrapheme,
            Category: NormalizationCategory.MultiRuneClusterStaysAnyForm,
            Description: "family emoji (zero-width-joiner sequence), multi-rune cluster, identity any form"),

        new NormalizationCase(
            Source: "\r\n",                                     // looks like CR+LF
            FormC: "\r\n",
            FormD: "\r\n",
            FormKC: "\r\n",
            FormKD: "\r\n",
            Category: NormalizationCategory.CarriageReturnLineFeedCluster,
            Description: "carriage return + line feed, single grapheme, identity any form"),
    };
}

// Self-check on the NormalizationExamples table itself: every
// hand-written FormC/FormD/FormKC/FormKD column equals what
// NormalizationHelpers actually produces, resolved through the same
// process-wide choice Compile and Parse use.
// Catches typos in the table before they pass-but-mislead any
// downstream test that relies on those columns. On CoreCLR the
// normalizer resolves to the runtime's string.Normalize, so this
// checks the table against .NET. On Unity it resolves to the bundled
// UAX #15 normalizer, so the same rows double as an IL2CPP check of
// the bundled tables. Lives next to the table rather than with the
// rule-behavior tests because what it verifies is a property of the
// data, not of any rule.
[TestFixture]
public class NormalizationExamplesSelfCheck
{
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void Each_column_equals_string_Normalize(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        string expected = NormalizationExamples.Project(row, form);
        string actual;
        try
        {
            actual = NormalizationHelpers.Normalize(row.Source, form);
        }
        catch (ArgumentException)
        {
            // The lone-surrogate row's Source can't be normalized under
            // any form (ArgumentException), so those four cases are
            // recorded as Inconclusive rather than asserting against a
            // column that's never reachable through the normalizer.
            Assert.Inconclusive("Source isn't normalizable under this form.");
            return;
        }

        Assert.That(actual, Is.EqualTo(expected),
            $"{row.Description} under {form}: table says \"{NormalizationExamples.Hex(expected)}\", " +
            $"the normalizer says \"{NormalizationExamples.Hex(actual)}\".");
    }
}
