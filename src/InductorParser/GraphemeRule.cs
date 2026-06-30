using System;
using System.Collections.Generic;
using System.Globalization;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Match input whose content is exactly one specified token. A token is
// one character as the user sees it (a grapheme cluster per UAX #29 on
// .NET 5+), possibly built from several runes underneath. The class is
// named GraphemeRule for that reason. The user-facing factory is
// Rules.Token(...), which constructs one of these. The expected token
// is stored as a string at construction and compared against the
// lexer's output at match time.
//
// A token (even a multi-rune one like 👨‍👩‍👧‍👦) arrives from the lexer
// as a single Token whose Chars span is the whole text element. The
// match is one Read and one SequenceEqual compare.
//
// Construction validates that the expected string is exactly one token
// via StringInfo.GetNextTextElement. Token("ab") throws at
// grammar-build time instead of silently failing at parse time. (Note:
// on pre-.NET 5 runtimes StringInfo isn't UAX #29 compliant, so the
// token count for exotic Unicode inputs can be wrong. A future vendored
// UAX #29 grapheme-cluster implementation would make this uniform
// across runtimes.)
//
// If the expected token is exactly one rune (the common case for
// ASCII, emoji that fit in a single code point, CJK, etc.), the Id is
// set to that code point so Symbol leaves produced by this rule
// carry the "id == rune" shape. For multi-rune tokens the Id comes
// from Compile's custom-range assignment.
internal sealed class GraphemeRule : Rule
{
    private string _expected;

    // Read-only accessor for the rule's literal text, used by out-of-assembly
    // analyzers that inspect a rule's fixed text.
    internal string? ExpectedText => _expected;

    public GraphemeRule(string expectedToken) : base(FlattenType.Delete, emitsLeaf: true)
    {
        // Trace name follows the user-facing factory name, not the
        // internal class name. Rules.Token(...) is the only way to
        // construct one, so traces and error labels read "Token".
        SetTraceName("Token");
        if (expectedToken == null)
            throw new ArgumentNullException(nameof(expectedToken));
        if (expectedToken.Length == 0)
            throw new ArgumentException("Token requires a non-empty token.", nameof(expectedToken));
        string firstElement = StringInfo.GetNextTextElement(expectedToken, 0);
        if (firstElement.Length != expectedToken.Length)
            throw new ArgumentException(
                $"Token requires exactly one user-perceived character (one StringInfo text element / grapheme cluster). Use Literal(string) for multi-token matches.",
                nameof(expectedToken));

        _expected = expectedToken;

        // Single-rune tokens get their code point assigned as the rule's
        // Id, matching C++ character-symbol numbering. Multi-rune
        // tokens fall through to Compile's custom-range assignment. At
        // construction the user hasn't named or set an explicit id yet, so
        // SetLeafRuneId always takes effect here. It re-checks those
        // conditions itself for the re-id during the normalization pass below.
        if (RuneHelpers.TrySingleRune(expectedToken, out int runeValue))
            SetLeafRuneId(runeValue);
    }

    protected override void ValidateNormalization(
        System.Text.NormalizationForm form,
        INormalizationReporter reporter)
    {
        // See Rule.ValidateNormalization for how this works.
        // Token-specific: a multi-grapheme conversion (Token("ﬁ") under
        // FormKC, where NFKC = "fi" is two graphemes) is reported as an
        // offender, since Token matches exactly one grapheme by
        // definition. Single-grapheme conversions update _expected and
        // re-id (e.g., U+2126 -> U+03A9 changes the rune).
        string? normalized = TryConvertToForm(this, _expected, form, reporter);
        if (normalized == null) return;
        if (string.Equals(normalized, _expected, StringComparison.Ordinal)) return;

        if (GraphemeHelpers.Count(normalized) > 1)
        {
            reporter.ReportOffender(this, _expected,
                $"<Token converts to multi-grapheme sequence \"{normalized}\" under {form}. " +
                $"Token matches exactly one grapheme. Use Literal(\"{normalized}\") or " +
                $"And(Token-per-grapheme) instead.>");
            return;
        }

        _expected = normalized;
        // Re-id for the converted text.
        if (RuneHelpers.TrySingleRune(_expected, out int runeValue))
        {
            // Still one rune after the conversion (canonical-singleton
            // substitutions like U+2126 -> U+03A9): keep the rune-as-id
            // shape at the new code point.
            SetLeafRuneId(runeValue);
        }
        else
        {
            // The conversion crossed the single-rune boundary: a precomposed
            // rune decomposed into a multi-rune cluster (U+00E9 -> "e + U+0301"
            // under FormD). The constructor's character-range rune id no longer
            // describes the match, so drop it and let Compile's
            // post-normalization anonymous-id pass assign a custom-range id,
            // matching a Token built multi-rune from the start.
            ClearLeafRuneId();
        }
    }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        int consumed = 0;

        // No heap allocations here except the one for the Symbol we return at the end.
        // The lexer emits one token for the whole text element, so the
        // loop runs once in the normal path. It also handles a multi-rune
        // token arriving one rune at a time (WithinTokenRule's sub-lexer):
        // consumed += token.Length until it reaches _expected.Length.
        //
        // tokenStart is the pre-read position for THIS iteration's read.
        // Required for multi-token matches so we report the offender at
        // the specific failing token's start, not at the start of the
        // whole match attempt.
        while (consumed < _expected.Length)
        {
            int tokenStart = lexer.Position;
            var token = lexer.Read();
            // Error Positioning: tokenStart is where the specific failing token began.
            // For a single-token match this equals startPosition.
            // For multi-token lockstep (a multi-rune token under the
            // WithinToken sub-lexer's one-rune-per-token mode) it's the
            // start of whichever token mismatched, not the start of the
            // whole attempt.
            if (token.IsEof)
            {
                TraceFailure(lexer, $"found '<EOF>', wanted '{_expected}'");
                lexer.RecordFailure(tokenStart, ErrorMessage, ErrorForced);
                return null;
            }
            if (consumed + token.Length > _expected.Length
                || !token.Chars.SequenceEqual(_expected.AsSpan(consumed, token.Length)))
            {
                TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted '{_expected}'");
                lexer.RecordFailure(tokenStart, ErrorMessage, ErrorForced);
                return null;
            }
            consumed += token.Length;
        }

        TraceSuccess(lexer, $"found '{_expected}'");
        // Default FlattenType is Delete, so most Token matches end up
        // in the discard branch and return the shared Discarded value
        // (no per-match Symbol allocation). Grammar authors who want
        // the character in the tree opt in with .Flatten(FlattenType.Preserve)
        // on the Token rule.
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        var leafSymbol = new Symbol(Id, FlattenType, lexer.Input.AsMemory(startPosition, consumed), lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }
}
