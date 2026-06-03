using System;
using System.Text;
using InductorParser.SyntaxTree;

namespace InductorParser;

public static partial class Rules
{
    /// <summary>
    /// Encodes a UAX #31 "programming language identifier". Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>,
    /// so the match appears in the tree as one named node whose
    /// children are the per-rune leaves.
    /// </summary>
    /// <remarks>
    /// The same word can be typed more than one way. "café" might be
    /// stored with a single precomposed "é", or with a plain "e"
    /// followed by a combining accent mark drawn on top. Both look
    /// identical in an editor but use different Unicode scalar sequences. By default
    /// the parser treats them as the same identifier, so a grammar
    /// doesn't have to care which form it gets.
    /// <para>
    /// Pass <c>null</c> to <c>Compile(NormalizationForm?)</c> to match
    /// the input string as written, without canonical or compatibility
    /// normalization. Pass <c>NormalizationForm.FormKC</c> for a stronger
    /// rule that also treats fullwidth <c>ｆｏｏ</c> and plain <c>foo</c>,
    /// or the ligature <c>ﬀ</c> and <c>ff</c>, as the same identifier.
    /// That's the Python 3 and Rust behavior. The stronger rule can,
    /// however, collapse things you may want kept distinct. It converts
    /// <c>ℓ</c> (script small L, used in physics) into <c>l</c>, and
    /// <c>Ⅷ</c> (Roman numeral) into <c>VIII</c>. A grammar that parses
    /// math or legal text probably wants those distinctions.
    /// Identifier-heavy grammars (Python source, say) almost always
    /// don't.
    /// </para>
    /// <para>
    /// See docs/UnicodeGotchas.md for recipes that reproduce the
    /// identifier rules of specific languages (Python 3, Rust,
    /// ECMAScript) via these parameters plus the form chosen at
    /// <c>Compile</c> time.
    /// </para>
    /// </remarks>
    /// <param name="form">
    /// The normalization form the rule will be compiled under. Must
    /// match the form passed to <c>Compile</c>. Default is
    /// <see cref="NormalizationForm.FormC"/>, the same default Compile
    /// uses. Pass <see cref="NormalizationForm.FormKC"/> /
    /// <see cref="NormalizationForm.FormKD"/> for compatibility-form
    /// identifiers (Python 3 / Rust style: fullwidth Latin and ligatures
    /// match their plain ASCII equivalents). Pass <c>null</c> to opt out
    /// of normalization at parse time, matching the unnormalized Compile
    /// path.
    /// </param>
    /// <param name="extraStartRunes">
    /// Runes to union into <see cref="TokenSet.XidStart"/> for the
    /// first character. UAX #31 calls this a "profile extension":
    /// the base Start property plus language-specific additions.
    /// Typical value for a programming-language grammar is
    /// <c>TokenSet.Runes("_")</c>. Python and Rust use this shape; C#
    /// also permits leading underscores, though its full identifier
    /// specification differs. Defaults to
    /// <see cref="TokenSet.Empty"/> (the base UAX #31-style profile).
    /// </param>
    /// <param name="extraBodyRunes">
    /// Runes to union into <see cref="TokenSet.XidContinue"/> for
    /// every character after the first. Same idea as
    /// <paramref name="extraStartRunes"/>. ECMAScript, for example,
    /// adds <c>$</c> to both positions. Defaults to
    /// <see cref="TokenSet.Empty"/>.
    /// </param>
    public static Rule Identifier(
        NormalizationForm? form = NormalizationForm.FormC,
        TokenSet extraStartRunes = default,
        TokenSet extraBodyRunes = default)
    {
        // extraStartRunes / extraBodyRunes name single runes (code points).
        // Identifier matches one code point at a time (see the WithinToken
        // note below), so a multi-rune grapheme handed in here is consulted
        // only against single-rune tokens and can never match in any
        // position, under any form. Reject it now instead of silently
        // building a rule with a dead entry.
        static void RejectMultiRuneGraphemes(TokenSet extras, string parameterName)
        {
            if (extras.HasMultiRuneGraphemes)
                throw new InvalidOperationException(
                    $"Identifier: {parameterName} contains the multi-rune grapheme " +
                    $"\"{extras.MultiRuneGraphemes[0]}\", but Identifier matches one code point " +
                    $"at a time, so a multi-rune grapheme can never match. Pass its individual " +
                    $"runes instead.");
        }
        RejectMultiRuneGraphemes(extraStartRunes, nameof(extraStartRunes));
        RejectMultiRuneGraphemes(extraBodyRunes, nameof(extraBodyRunes));

        var start = TokenSet.XidStart | extraStartRunes;
        var body = TokenSet.XidContinue | extraBodyRunes;
        // Identifiers are spec'd by Unicode to match one
        // code point at a time (XID_Start / XID_Continue are per-code-point specs),
        // so the start/body sets have to be expressed in code points (i.e. Runes). 
        // 
        // However, some runes in XidStart / XidContinue list will decompose into multiple
        // runes under FormKC/FormKD (e.g. a ligature), and we need to decide what to do.
        // Unicode specifies the answer: when an XID_Start character decomposes,
        // its first rune (the head) stays in XID_Start and all later runes (the tail) are
        // in XID_Continue. The same needs to be true for the user supplied extraStartRunes.
        //
        // Spec anchor: the split follows from UAX #31's closure
        // guarantee. R4 "Equivalent Normalized Identifiers" says an NFKC
        // implementation must apply Section 5.1 "NFKC Modifications".
        // https://www.unicode.org/reports/tr31/#NFKC_Modifications
        //
        // Section 5.1.3 "Identifier Closure Under Normalization" shows
        // that XID_Start / XID_Continue stay closed under all four
        // Normalization Forms, which is what guarantees the head-stays-start,
        // tail-is-continue split stated above.
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
        // reaches Body on its own as an
        // XID_Continue character. The Start expansion just has to avoid
        // wrongly keeping it. XidIdentifierTests
        // .XidStart_compatibility_continuations_are_all_in_XidContinue
        // verifies, against the runtime's Unicode data, that this holds for
        // every XidStart entry.
        // 
        // The closure guarantee only covers XidStart / XidContinue, not the
        // caller's extras. If a user adds a start rune whose decomposition
        // has a continuation piece that isn't a valid body character, that
        // start character could never match, so reject the grammar now with
        // a message that names the fix.
        if (form == NormalizationForm.FormKC || form == NormalizationForm.FormKD)
        {
            start = start.WithCompatibilityHeadRuneEquivalents(form.Value);
            body = body.WithCompatibilityEquivalents(form.Value);
            if (!extraStartRunes.AllCompatibilityTailRunesIn(
                    form.Value, body,
                    out string offendingStart, out string conversion, out string missingPiece))
            {
                throw new InvalidOperationException(
                    $"Identifier(form: {form.Value}): the extraStartRunes entry \"{offendingStart}\" " +
                    $"normalizes to the multi-grapheme sequence \"{conversion}\", whose continuation " +
                    $"piece \"{missingPiece}\" isn't a valid identifier-continue character. A start " +
                    $"character that decomposes needs every piece after the first to be matchable in " +
                    $"body position. Add \"{missingPiece}\" to extraBodyRunes, or drop " +
                    $"\"{offendingStart}\" from extraStartRunes.");
            }
        }
        // Why rune mode (WithinToken), not whole-token matching: XID_Start
        // and XID_Continue are properties of individual code points, but one
        // token (grapheme cluster) can be several code points with different
        // properties, like Devanagari "हि" (consonant HA = Start, vowel sign
        // I = Continue only). WithinToken cracks each token open and tests
        // its runes one at a time, which is the granularity the XID tables
        // are defined at. That's also why only the first rune is matched
        // against Start and every later rune against Body.
        return And(
            // First token: starts with a Start rune, rest of its runes
            // (if any) are Body runes. Handles precomposed "é", "ñ",
            // etc. as single-rune tokens and "हि"-style
            // consonant+vowel-sign tokens as multi-rune.
            WithinToken(And(OneOf(start), ZeroOrMore(OneOf(body)))),
            // Subsequent tokens: every rune must be a Body rune.
            ZeroOrMore(WithinToken(OneOrMore(OneOf(body))))
        ).FlattenByDefault(FlattenType.Preserve);
    }
}
