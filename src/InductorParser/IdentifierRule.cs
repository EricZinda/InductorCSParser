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
    // caller's extras. Both sides need an explicit check. On the start
    // side, if a user adds a start rune whose decomposition has a
    // continuation piece that isn't a valid body character, that start
    // character could never match. On the body side, if a user adds a
    // body rune whose decomposition has a piece outside (XidContinue |
    // extraBodyRunes), WithCompatibilityEquivalents would silently inject
    // that piece into the body set. Either case rejects the grammar with
    // a message that names the fix.
    //
    // The walker in ValidateNormalizationAll visits parents before
    // children, so by the time it recurses into the embedded OneOfRules
    // their sets are already the expanded versions. Their own
    // ValidateNormalization then projects those sets under the form and
    // finds no offenders (the expansion produced form-valid entries).
    protected override void ValidateNormalization(
        System.Text.NormalizationForm form,
        INormalizationReporter reporter)
    {
        if (form != System.Text.NormalizationForm.FormKC && form != System.Text.NormalizationForm.FormKD)
            return;

        var expandedBody = (TokenSet.XidContinue | _extraBodyRunes).WithCompatibilityEquivalents(form);

        if (!_extraStartRunes.AllCompatibilityTailRunesIn(
                form, expandedBody,
                out string offendingStart, out string conversion, out string missingPiece))
        {
            throw new InvalidOperationException(
                $"Identifier (Compile {form}): the extraStartRunes entry \"{offendingStart}\" " +
                $"normalizes to the multi-grapheme sequence \"{conversion}\", whose continuation " +
                $"piece \"{missingPiece}\" isn't a valid identifier-continue character. A start " +
                $"character that decomposes needs every piece after the first to be matchable in " +
                $"body position. Add \"{missingPiece}\" to extraBodyRunes, or drop " +
                $"\"{offendingStart}\" from extraStartRunes.");
        }

        // Mirror check on the body side. Closure covers XidContinue's own
        // entries, but extras don't have the closure guarantee: an extra
        // body rune whose NFKx contains a piece the caller didn't ask for
        // (the SPACE separators inside U+FDFA's Arabic-phrase NFKC are the
        // canonical example) would silently inject that piece into the
        // body set when WithCompatibilityEquivalents runs over the union.
        // The allowed set is (XidContinue | extraBodyRunes): pieces can
        // land in XidContinue (the spec body) OR in the caller's own body
        // extras (an explicit "yes I want this in body" opt-in by the
        // same caller in the same call). Pieces that fall outside both
        // signal an unintended leak, so reject with a message that names
        // the offender and the missing piece.
        if (!_extraBodyRunes.AllCompatibilityPiecesIn(
                form, TokenSet.XidContinue | _extraBodyRunes,
                out string offendingBody, out string bodyConversion, out string bodyMissingPiece))
        {
            throw new InvalidOperationException(
                $"Identifier (Compile {form}): the extraBodyRunes entry \"{offendingBody}\" " +
                $"normalizes to the multi-grapheme sequence \"{bodyConversion}\", whose piece " +
                $"\"{bodyMissingPiece}\" isn't in XidContinue and wasn't added by this " +
                $"extraBodyRunes call. Every piece of a body character's decomposition needs " +
                $"to be matchable in body position. Add \"{bodyMissingPiece}\" to extraBodyRunes " +
                $"to opt in, or drop \"{offendingBody}\" from extraBodyRunes.");
        }

        var expandedStart = (TokenSet.XidStart | _extraStartRunes).WithCompatibilityHeadRuneEquivalents(form);
        _startOneOf.ReplaceSet(expandedStart);
        _firstBodyOneOf.ReplaceSet(expandedBody);
        _secondBodyOneOf.ReplaceSet(expandedBody);
    }
}
