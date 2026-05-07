using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Case-insensitive literal match, ASCII letters only. Same single-transaction
// shape as LiteralRule. The only difference is the compare treats ASCII
// letters case-insensitively. Non-ASCII code units compare bit-exact, so
// Turkish dotless-I, German sharp-s, Greek sigma variants, etc. DON'T
// match their upper/lower counterparts. That tradeoff is on purpose: full
// Unicode case-insensitive matching is locale-dependent and grammar-breaking,
// and the keyword-heavy grammars that want this leaf (SQL, HTTP methods,
// chord notation) only ever need ASCII in practice. See docs/UnicodeGotchas.md
// for the longer explanation.
internal sealed class LiteralIgnoreAsciiCaseRule : Rule
{
    private string _expected;

    public LiteralIgnoreAsciiCaseRule(string expected) : base(FlattenType.Delete)
    {
        if (expected == null)
            throw new ArgumentNullException(nameof(expected));
        if (expected.Length == 0)
            throw new ArgumentException("LiteralIgnoreAsciiCase requires a non-empty string.", nameof(expected));
        _expected = expected;
        SetTraceName("LiteralIgnoreAsciiCase");
    }

    internal override string? ExpectedText => _expected;

    internal override void CollectNormalizationOffenders(
        System.Text.NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
    {
        // See Rule.CollectNormalizationOffenders for the contract.
        // Same shape as LiteralRule: replace _expected with the
        // converted form.
        string? normalized = TryConvertToForm(this, _expected, form, offenders, failures);
        if (normalized == null) return;
        if (string.Equals(normalized, _expected, StringComparison.Ordinal)) return;
        _expected = normalized;
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        int consumed = 0;

        while (consumed < _expected.Length)
        {
            int tokenStart = lexer.Position;
            var token = lexer.Read();
            if (token.IsEof)
            {
                TraceFailure(lexer, $"found '<EOF>', wanted '{_expected}' (case-insensitive)");
                lexer.RecordFailure(tokenStart, ErrorMessage);
                return null;
            }
            if (consumed + token.Length > _expected.Length
                || !AsciiCaseEquals(token.Chars, _expected.AsSpan(consumed, token.Length)))
            {
                TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted '{_expected}' (case-insensitive)");
                lexer.RecordFailure(tokenStart, ErrorMessage);
                return null;
            }
            consumed += token.Length;
        }

        TraceSuccess(lexer, $"found '{lexer.Input.Substring(transaction.StartPosition, consumed)}', wanted '{_expected}' (case-insensitive)");
        transaction.Commit();
        // Default FlattenType is Delete: the common case collapses to
        // the shared Discarded value and skips the per-match Symbol allocation.
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        var leafSymbol = new Symbol(Id, FlattenType, lexer.Input.AsMemory(transaction.StartPosition, consumed));
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    // ASCII-only case-insensitive compare. Both sides compare bit-exact
    // when either char is outside A-Za-z. Within A-Za-z the 0x20 bit
    // difference is masked out so 'A' and 'a' hash the same. Non-letters
    // (digits, punctuation, spaces) take the bit-exact path because
    // (c | 0x20) only pairs matching upper and lower cases for the 26
    // ASCII letters. Applying it to '[' would give '{' and break "match ["
    // against input "{".
    private static bool AsciiCaseEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i];
            char cb = b[i];
            if (ca == cb) continue;
            if (IsAsciiLetter(ca) && IsAsciiLetter(cb) && (ca | 0x20) == (cb | 0x20)) continue;
            return false;
        }
        return true;
    }

    private static bool IsAsciiLetter(char c) =>
        (uint)((c | 0x20) - 'a') <= ('z' - 'a');

    internal override RuleStartRequirements ComputeRuleStart()
    {
        // Same first-grapheme extraction as LiteralRule, but when the
        // first cluster is one ASCII letter we admit both cases so input
        // in either case can pass the lookahead check (AsciiCaseEquals is
        // the runtime equivalent). Multi-rune clusters and non-letters
        // go through the standard FirstTokenMustBeFirstGraphemeOf path.
        try
        {
            string firstElement = System.Globalization.StringInfo.GetNextTextElement(_expected, 0);
            if (firstElement.Length == 1 && IsAsciiLetter(firstElement[0]))
            {
                int lower = firstElement[0] | 0x20;
                int upper = lower & ~0x20;
                return RuleStartRequirements.FirstTokenMustBeInSet(
                    TokenSet.Single(lower) | TokenSet.Single(upper));
            }
        }
        catch (ArgumentException)
        {
            return RuleStartRequirements.AlwaysAdvancesByOneToken;
        }
        return RuleStartRequirements.FirstTokenMustBeFirstGraphemeOf(_expected);
    }
}
