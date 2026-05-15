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
// token count for exotic Unicode inputs can be wrong. See
// backlog/xlll-vendor-a-uax-#29-grapheme-cluster-implementation.md.)
//
// If the expected token is exactly one rune (the common case for
// ASCII, emoji that fit in a single code point, CJK, etc.), the Id is
// pinned to that code point so Symbol leaves produced by this rule
// carry the "id == rune" shape. For multi-rune tokens the Id comes
// from Compile's custom-range assignment.
internal sealed class GraphemeRule : Rule
{
    private string _expected;

    public GraphemeRule(string expectedToken) : base(FlattenType.Delete)
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

        // Single-rune tokens get their code point pinned as the rule's
        // Id, matching C++ character-symbol numbering. Multi-rune
        // tokens fall through to Compile's custom-range assignment.
        if (TrySingleRuneValue(expectedToken, out int runeValue))
            SetIdInternal(new SymbolId(runeValue));
    }

    internal override string? ExpectedText => _expected;

    internal override void CollectNormalizationOffenders(
        System.Text.NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
    {
        // See Rule.CollectNormalizationOffenders for the contract.
        // Token-specific: a multi-grapheme conversion (Token("ﬁ") under
        // FormKC, where NFKC = "fi" is two graphemes) is reported as an
        // offender, since Token matches exactly one grapheme by
        // definition. Single-grapheme conversions update _expected and
        // re-id (e.g., U+2126 -> U+03A9 changes the rune).
        string? normalized = TryConvertToForm(this, _expected, form, offenders, failures);
        if (normalized == null) return;
        if (string.Equals(normalized, _expected, StringComparison.Ordinal)) return;

        if (CountGraphemes(normalized) > 1)
        {
            offenders.Add((this, _expected,
                $"<Token converts to multi-grapheme sequence \"{normalized}\" under {form}. " +
                $"Token matches exactly one grapheme. Use Literal(\"{normalized}\") or " +
                $"And(Token-per-grapheme) instead.>"));
            return;
        }

        _expected = normalized;
        // Skip the rune re-pin when the user fixed an Id via .As(SymbolId);
        // their pin is the stable-numbering contract.
        if (!IsUserSymbolIdPinned && TrySingleRuneValue(_expected, out int runeValue))
            SetIdInternal(new SymbolId(runeValue));
    }

    private static int CountGraphemes(string text)
    {
        if (text.Length == 0) return 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        int count = 0;
        while (enumerator.MoveNext()) count++;
        return count;
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        int consumed = 0;

        // No heap allocations here except the one for the Symbol we return at the end.
        // The lexer emits one token for the whole text element so the
        // loop runs once per match in the normal path. The
        // consumed += token.Length pattern also carries the
        // multi-iteration sub-lexer case (WithinTokenRule's
        // one-rune-per-token sub-lexer reading a multi-rune token):
        // each rune iteration accumulates until consumed catches up
        // to _expected.Length.
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
            // For a single-token match this equals transaction.StartPosition.
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
        transaction.Commit();
        // Default FlattenType is Delete, so most Token matches end up
        // in the discard branch and return the shared Discarded value
        // (no per-match Symbol allocation). Grammar authors who want
        // the character in the tree opt in with .Flatten(FlattenType.Preserve)
        // on the Token rule.
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        var leafSymbol = new Symbol(Id, FlattenType, lexer.Input.AsMemory(transaction.StartPosition, consumed), lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.FirstTokenMustBeFirstGraphemeOf(_expected);

    // True iff the string is exactly one Unicode rune (one UTF-16 char
    // or one surrogate pair). Works without depending on
    // Rune.EnumerateRunes, which isn't in netstandard2.1.
    private static bool TrySingleRuneValue(string s, out int runeValue)
    {
        if (s.Length == 1 && !char.IsSurrogate(s[0]))
        {
            runeValue = s[0];
            return true;
        }
        if (s.Length == 2 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1]))
        {
            runeValue = char.ConvertToUtf32(s[0], s[1]);
            return true;
        }
        runeValue = 0;
        return false;
    }
}
