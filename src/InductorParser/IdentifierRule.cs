using System;
using System.Collections.Generic;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// The UAX #31 "programming language identifier" rule. Wraps the
// two-part identifier shape:
//
//   And(
//     WithinToken(And(OneOf(start), ZeroOrMore(OneOf(body)))),
//     ZeroOrMore(WithinToken(OneOrMore(OneOf(body)))))
//
// as a dedicated rule type so the form-aware expansion of the start and
// body sets can run at Compile time (when the normalization form is
// known).
//
// Parse semantics are exactly an AndRule over the two children: the
// TryParseRule body mirrors AndRule.TryParseRule. The difference is the
// ValidateNormalization override, which runs at Compile time and
// rewrites the embedded start / body OneOfs' TokenSets when the form is
// FormKC or FormKD. Other forms (and the null / strict-UAX #31 case)
// leave the OneOfs alone.
//
// The expansion isn't safe on a hand-written OneOf. A bare
// OneOf(TokenSet.Single(0x0132)) ("IJ" ligature) under FormKC would
// silently change semantics: the user clearly meant the ligature, and a
// OneOf can't consume the multi-grapheme expansion as a single token.
// The expansion is only sound inside Identifier's surrounding
// WithinToken(And(start, ZeroOrMore(body))) shape, which is why it
// lives on this rule type rather than as a general OneOf option.
internal sealed class IdentifierRule : Rule
{
    private readonly TokenSet _extraStartRunes;
    private readonly TokenSet _extraBodyRunes;
    private readonly OneOfRule _startOneOf;
    private readonly OneOfRule _firstBodyOneOf;
    private readonly OneOfRule _secondBodyOneOf;

    public IdentifierRule(TokenSet extraStartRunes, TokenSet extraBodyRunes)
        : this(extraStartRunes, extraBodyRunes,
               new OneOfRule(TokenSet.XidStart | extraStartRunes),
               new OneOfRule(TokenSet.XidContinue | extraBodyRunes),
               new OneOfRule(TokenSet.XidContinue | extraBodyRunes))
    { }

    // Private chained constructor: the OneOfRule instances have to exist
    // before the base(...) call so they can be embedded in the children
    // tree and captured as fields for the Compile-time rewrite. Two
    // distinct body OneOf instances (firstBodyOneOf, secondBodyOneOf)
    // appear in the tree, one inside the first-token WithinToken and one
    // inside the subsequent-tokens WithinToken, so each occurrence gets
    // its own anonymous leaf id.
    private IdentifierRule(
        TokenSet extraStartRunes, TokenSet extraBodyRunes,
        OneOfRule startOneOf, OneOfRule firstBodyOneOf, OneOfRule secondBodyOneOf)
        : base(FlattenType.Preserve, emitsLeaf: false,
               Rules.WithinToken(Rules.And(startOneOf, Rules.ZeroOrMore(firstBodyOneOf))),
               Rules.ZeroOrMore(Rules.WithinToken(Rules.OneOrMore(secondBodyOneOf))))
    {
        _extraStartRunes = extraStartRunes;
        _extraBodyRunes = extraBodyRunes;
        _startOneOf = startOneOf;
        _firstBodyOneOf = firstBodyOneOf;
        _secondBodyOneOf = secondBodyOneOf;
    }

    // Same body as AndRule.TryParseRule: this rule matches each of its
    // two children in order, fails as a whole if either fails, and
    // collects child outputs into either the parent's outputSymbols
    // list (Flatten / Delete) or a fresh list it returns as a Preserve
    // composite.
    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>(Children.Count);

        for (int symbolIndex = 0; symbolIndex < Children.Count; symbolIndex++)
        {
            var child = Children[symbolIndex];
            var symbol = ParseChild(child, lexer, outputSymbols);
            if (symbol == null)
            {
                TraceFailure(lexer, $"symbol #{symbolIndex}");
                lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                return null;
            }
            if (outputSymbols != null && !ReferenceEquals(symbol, Symbol.Discarded))
                outputSymbols.Add(symbol);
        }
        TraceSuccess(lexer, $"found {Children.Count}");
        int matchLength = lexer.Position - startPosition;
        return effectiveFlattenType == FlattenType.Preserve
            ? CreateCompositeFromOwnedChildren(outputSymbols, lexer.Input.AsMemory(startPosition, matchLength), lexer.Context)
            : Symbol.Discarded;
    }

    // Compile-time hook: when the grammar's chosen form is FormKC or
    // FormKD, rewrite the embedded start and body OneOfs' TokenSets so
    // they include the compatibility-equivalent expansions of XidStart
    // and XidContinue.
    //
    // Identifiers are spec'd by Unicode to match one code point at a
    // time (XID_Start / XID_Continue are per-code-point specs), so the
    // start/body sets have to be expressed in code points (i.e. Runes).
    // Some runes in XidStart / XidContinue will decompose into multiple
    // runes under FormKC/FormKD (e.g. a ligature), and Unicode specifies
    // the answer: when an XID_Start character decomposes, its first rune
    // (the head) stays in XID_Start and all later runes (the tail) land
    // in XID_Continue. The same needs to be true for the user-supplied
    // extras.
    //
    // Spec anchor: the split follows from UAX #31's closure guarantee.
    // R4 "Equivalent Normalized Identifiers" says an NFKC implementation
    // must apply Section 5.1 "NFKC Modifications".
    // https://www.unicode.org/reports/tr31/#NFKC_Modifications
    //
    // Section 5.1.3 "Identifier Closure Under Normalization" shows that
    // XID_Start / XID_Continue stay closed under all four Normalization
    // Forms, which is what guarantees the head-stays-start,
    // tail-is-continue split above.
    // https://www.unicode.org/reports/tr31/#Identifier_Closure
    //
    // Concretely: Unicode 17 ch. 7 says U+0140 is represented by "an
    // ordinary 'l' and U+00B7", and U+00B7 is Other_ID_Continue, not
    // XID_Start. So U+0140 may start an identifier (it becomes "l" +
    // continue-dot), but a bare U+00B7 may not. That bare-dot case is
    // exactly what the head/tail split is for: the tail holds
    // continue-only runes that would be wrong in the start set.
    // https://www.unicode.org/versions/Unicode17.0.0/core-spec/chapter-7/
    //
    // There's no code to move the runes Start drops over into Body,
    // because Body already has them. By the closure guarantee above,
    // every later rune of a Start character's decomposition is
    // XID_Continue, and Body starts life as XidContinue, so those runes
    // are already members. (Body also expands the same character and
    // adds both pieces, which picks them up a second time.) U+00B7
    // reaches Body on its own as an XID_Continue character. The Start
    // expansion just has to avoid wrongly keeping it.
    // XidIdentifierTests.XidStart_compatibility_continuations_are_all_in_XidContinue
    // verifies, against the runtime's Unicode data, that this holds for
    // every XidStart entry.
    //
    // The closure guarantee only covers XidStart / XidContinue, not the
    // caller's extras, so both sides have an explicit check. Start side,
    // two parts: the head rune of an extra start rune's NFKx must be a
    // valid identifier-start character (in XidStart, or one the caller
    // also listed in extraStartRunes), and every continuation piece after
    // the head must be a valid body character. The head part matters
    // because some spacing characters decompose to SPACE + a combining
    // mark (U+00A8 DIAERESIS is SPACE + U+0308, U+309B is SPACE + U+3099),
    // and under FormKC/FormKD the input is normalized before lexing, so
    // that SPACE head and a literal leading space are the same U+0020 in
    // the stream. Keeping such a head would let a bare leading space start
    // an identifier, so the head check rejects it unless the caller opts
    // the head in. Body side: every NFKx piece of an extra body rune must
    // land in (XidContinue | extraBodyRunes). Any check failing throws
    // with a message that names the offender and the fix.
    //
    // The walker in ValidateNormalizationAll visits parents before
    // children, so by the time it recurses into the embedded OneOfRules
    // their sets are already the expanded versions. Their own
    // ValidateNormalization then projects those sets under the form and
    // finds no offenders (the expansion produced form-valid entries).
    protected override void ValidateNormalization(
        NormalizationForm form,
        INormalizationReporter reporter)
    {
        if (form != System.Text.NormalizationForm.FormKC && form != System.Text.NormalizationForm.FormKD)
            return;

        var expandedBody = WithCompatibilityRuneEquivalents(TokenSet.XidContinue | _extraBodyRunes, form);

        if (!AllCompatibilityPiecesIn(
                _extraStartRunes, form, expandedBody, NfkxPieceScope.Tail,
                out string offendingStart, out string conversion, out string missingPiece))
        {
            throw new InvalidOperationException(
                $"Identifier can't compile under {form}: the extraStartRunes entry " +
                $"\"{offendingStart}\" becomes \"{conversion}\", and \"{missingPiece}\" in " +
                $"there isn't allowed in the body of an identifier. After the first " +
                $"character, the pieces sit in the body. Add \"{missingPiece}\" to " +
                $"extraBodyRunes, or remove \"{offendingStart}\" from extraStartRunes.");
        }

        // Second start-side check: the HEAD rune of an extra start rune's
        // NFKx must itself be a valid identifier-start character, i.e. a
        // member of (XidStart | extraStartRunes). XidStart's own entries
        // are guaranteed by closure (an XidStart character's NFKx head
        // stays in XidStart), but the caller's extras aren't, so a spacing
        // character whose NFKx head is SPACE (U+00A8, U+309B, ...) would
        // otherwise leak that SPACE into the start set. There's no way to
        // accept such a head without also accepting a literal leading space
        // (normalization makes them the same U+0020), so the head is
        // rejected unless the caller listed it in extraStartRunes too.
        if (!AllCompatibilityPiecesIn(
                _extraStartRunes, form, TokenSet.XidStart | _extraStartRunes, NfkxPieceScope.Head,
                out string headOffendingStart, out string headConversion, out string offendingHead))
        {
            throw new InvalidOperationException(
                $"Identifier can't compile under {form}: the extraStartRunes entry " +
                $"\"{headOffendingStart}\" becomes \"{headConversion}\", and its first " +
                $"character \"{offendingHead}\" isn't allowed at the start of an identifier. " +
                $"Allowing it would let an identifier begin with \"{offendingHead}\". Add " +
                $"\"{offendingHead}\" to extraStartRunes to allow it, or remove " +
                $"\"{headOffendingStart}\" from extraStartRunes.");
        }

        // Mirror check on the body side: every piece of each extra body
        // rune's NFKx must land in the allowed set
        // (XidContinue | extraBodyRunes). XidContinue covers the spec
        // body; the caller's own body extras cover the caller's explicit
        // opt-in. A piece outside both throws with a message naming the
        // offender and the missing piece. The canonical rejection is
        // U+FDFA ARABIC LIGATURE SALLALLAHOU ALAYHE WASALLAM, whose NFKC
        // is an Arabic phrase containing SPACE separators that are in
        // neither XidContinue nor a typical extras list.
        if (!AllCompatibilityPiecesIn(
                _extraBodyRunes, form, TokenSet.XidContinue | _extraBodyRunes, NfkxPieceScope.All,
                out string offendingBody, out string bodyConversion, out string bodyMissingPiece))
        {
            throw new InvalidOperationException(
                $"Identifier can't compile under {form}: the extraBodyRunes entry " +
                $"\"{offendingBody}\" becomes \"{bodyConversion}\", and \"{bodyMissingPiece}\" " +
                $"in there isn't allowed in the body of an identifier. Every piece it " +
                $"becomes has to be valid in the body. Add \"{bodyMissingPiece}\" to " +
                $"extraBodyRunes, or remove \"{offendingBody}\" from extraBodyRunes.");
        }

        var expandedStart = WithCompatibilityHeadRuneEquivalents(TokenSet.XidStart | _extraStartRunes, form);
        _startOneOf.ReplaceSet(expandedStart);
        _firstBodyOneOf.ReplaceSet(expandedBody);
        _secondBodyOneOf.ReplaceSet(expandedBody);
    }

    // ============================================================
    // Compile-time form-projection helpers
    // ============================================================
    //
    // These walk a TokenSet entry by entry, compute each entry's NFKx form,
    // and either project entries into a result set (used by the start /
    // body OneOf rewrite) or validate that NFKx pieces stay in an allowed
    // set (used by the user-extra checks). They live here rather than on
    // TokenSet because the head-stays-X, tail-stays-Y NFKx-closure shape is
    // specific to UAX #31 identifiers; no other Unicode concept needs the
    // same projection. TokenSet's public surface treats it as a SET
    // (membership) plus whole-set transforms; rune-by-rune enumeration is
    // exposed via TokenSet.EnumerateRunes and TokenSet.MultiRuneGraphemes,
    // and bulk construction via TokenSet.FromRanges, which together let
    // these helpers stay in pure rule code without privileged access.

    // Like TokenSet.WithCompatibilityEquivalents, but splits multi-RUNE
    // conversions by rune (not by grapheme cluster) and keeps only the
    // head rune of each. For the start OneOf, which lives inside a
    // WithinToken sub-lexer reading one rune per token (multi-rune
    // grapheme members would be unreachable there).
    //
    // "Multi-rune" covers both multi-grapheme outputs (e.g. U+0140 → "l +
    // U+00B7", 2 clusters) and single-grapheme multi-rune outputs
    // (e.g. U+309B → SPACE + U+3099, one cluster); both need the head
    // kept and the tail dropped since the sub-lexer reads runes one at a
    // time regardless of cluster structure.
    private static TokenSet WithCompatibilityHeadRuneEquivalents(
        TokenSet source, NormalizationForm form)
        => ProjectByRunes(source, form, headOnly: true);

    // Same as above but keeps every rune of each multi-rune NFKx, not just
    // the head. For the body OneOf inside the same WithinToken sub-lexer.
    private static TokenSet WithCompatibilityRuneEquivalents(
        TokenSet source, NormalizationForm form)
        => ProjectByRunes(source, form, headOnly: false);

    private static TokenSet ProjectByRunes(
        TokenSet source, NormalizationForm form, bool headOnly)
    {
        var runes = new List<(int Low, int High)>();
        foreach (int rune in source.EnumerateRunes())
        {
            AddProjectedRunes(char.ConvertFromUtf32(rune), form, headOnly, runes);
        }
        foreach (string grapheme in source.MultiRuneGraphemes)
        {
            AddProjectedRunes(grapheme, form, headOnly, runes);
        }
        // Surrogate members (from TokenSet.Surrogates / SurrogateRange) are
        // intentionally dropped here: EnumerateRunes already skips them, they
        // have no NFKx expansion (Normalize throws on them), and FromRanges
        // rejects them as endpoints. Unlike TokenSet.WithCompatibilityEquivalents,
        // which preserves them for general callers, ProjectByRunes runs only
        // under FormKC / FormKD, where the lexer rejects lone surrogates from
        // input before a rule sees one, so a surrogate carried here could never
        // match. Dropping them keeps this path free of unreachable, untestable
        // work rather than mirroring WithCompatibilityEquivalents for its own
        // sake.
        return TokenSet.FromRanges(runes.ToArray());
    }

    private static void AddProjectedRunes(
        string entry, NormalizationForm form, bool headOnly,
        List<(int Low, int High)> runes)
    {
        string projected;
        try
        {
            projected = entry.IsNormalized(form) ? entry : entry.Normalize(form);
        }
        catch (ArgumentException) { return; }
        if (projected.Length == 0) return;
        if (headOnly)
        {
            int headRune = char.IsHighSurrogate(projected[0])
                            && projected.Length > 1
                            && char.IsLowSurrogate(projected[1])
                ? char.ConvertToUtf32(projected[0], projected[1])
                : projected[0];
            runes.Add((headRune, headRune));
            return;
        }
        foreach (int rune in RuneHelpers.EnumerateRuneValues(projected))
        {
            runes.Add((rune, rune));
        }
    }

    // Which runes of an entry's NFKx decomposition a compatibility check
    // covers. The head is the first rune, the tail is every rune after it.
    //   Tail - the start-side check: a start character that decomposes
    //          needs every piece after the head to be a valid body rune.
    //   Head - the start-side check: the head itself has to be a valid
    //          start rune, else it would leak into the start set.
    //   All  - the body-side check: every piece of a body character's
    //          decomposition has to be a valid body rune.
    private enum NfkxPieceScope { Head, Tail, All }

    // Walk `extras` and confirm the NFKx pieces selected by `scope` of each
    // entry are in `allowedRunes`. Returns true when every entry passes.
    // Returns false at the first entry that doesn't, with `entry` the
    // offending member, `expansion` its normalized form, and
    // `offendingPiece` the rune that wasn't allowed.
    //
    // Called on the caller's extras only. XidStart / XidContinue are closed
    // under NFKx (the head of an XidStart character's decomposition stays in
    // XidStart, the rest land in XidContinue, verified by
    // XidIdentifierTests.XidStart_compatibility_continuations_are_all_in_XidContinue),
    // so their own ~130K entries are guaranteed and don't need re-checking.
    // Entries that are form-stable or whose NFKx is a single rune pass
    // trivially: TryGetMultiRuneConversion returns false and there's nothing
    // to check (for a single-rune entry the only piece is its own head,
    // which the caller already listed).
    private static bool AllCompatibilityPiecesIn(
        TokenSet extras, NormalizationForm form, TokenSet allowedRunes, NfkxPieceScope scope,
        out string entry, out string expansion, out string offendingPiece)
    {
        foreach (int rune in extras.EnumerateRunes())
        {
            entry = char.ConvertFromUtf32(rune);
            if (!CompatibilityPiecesIn(entry, form, allowedRunes, scope, out expansion, out offendingPiece))
                return false;
        }
        foreach (string grapheme in extras.MultiRuneGraphemes)
        {
            if (!CompatibilityPiecesIn(grapheme, form, allowedRunes, scope, out expansion, out offendingPiece))
            {
                entry = grapheme;
                return false;
            }
        }
        entry = "";
        expansion = "";
        offendingPiece = "";
        return true;
    }

    private static bool CompatibilityPiecesIn(
        string entry, NormalizationForm form, TokenSet allowedRunes, NfkxPieceScope scope,
        out string expansion, out string offendingPiece)
    {
        expansion = "";
        offendingPiece = "";
        if (!TryGetMultiRuneConversion(entry, form, out string? converted))
            return true;
        bool isHead = true;
        foreach (int rune in RuneHelpers.EnumerateRuneValues(converted))
        {
            bool head = isHead;
            isHead = false;
            bool covered = scope switch
            {
                NfkxPieceScope.Head => head,
                NfkxPieceScope.Tail => !head,
                _ => true,
            };
            if (covered && !allowedRunes.ContainsRune(rune))
            {
                expansion = converted;
                offendingPiece = char.ConvertFromUtf32(rune);
                return false;
            }
            // Head scope only cares about the first rune, so stop once it's
            // checked rather than walking the tail we're going to ignore.
            if (scope == NfkxPieceScope.Head)
                return true;
        }
        return true;
    }

    // Returns true and sets `converted` if `entry`'s NFKx is a multi-rune
    // sequence. Returns false when the entry is form-stable, when the
    // conversion is single-rune, or when Normalize throws (in practice, an
    // unpaired surrogate). Short-circuits the rune count at 2.
    private static bool TryGetMultiRuneConversion(
        string entry, NormalizationForm form,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? converted)
    {
        converted = null;
        try
        {
            if (entry.IsNormalized(form)) return false;
            string normalized = entry.Normalize(form);
            int count = 0;
            foreach (int _ in RuneHelpers.EnumerateRuneValues(normalized))
            {
                count++;
                if (count > 1) { converted = normalized; return true; }
            }
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

}
