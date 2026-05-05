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
    private readonly string _expected;

    internal string Expected => _expected;

    public LiteralIgnoreAsciiCaseRule(string expected) : base(FlattenType.Delete)
    {
        if (expected == null)
            throw new ArgumentNullException(nameof(expected));
        if (expected.Length == 0)
            throw new ArgumentException("LiteralIgnoreAsciiCase requires a non-empty string.", nameof(expected));
        _expected = expected;
        SetTraceName("LiteralIgnoreAsciiCase");
    }

    // Accessor for the state-machine evaluator's lowering pass.
    internal string LoweringExpected => _expected;

    internal override (string Text, bool IgnoreCase)? ComputeRequiredLiteral() =>
        (_expected, true);

    internal override (string Text, bool IgnoreCase)? ComputeConcatenableText() =>
        (_expected, true);

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

    // Return the set of runes this rule might consume first (can be a superset)
    // (TokenSet.Empty when Advance.Never. TokenSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // First rune decides the lookahead. For an ASCII letter, include
        // both cases so the caller's input in either case admits us.
        // Non-letters (digits, punctuation) only match themselves. The
        // same bit-exact rule AsciiCaseEquals applies to non-letter
        // positions.
        //
        // TryPeekRune decodes the first rune correctly even when that rune
        // takes two chars in the C# string (emoji, many CJK). Reading
        // _expected[0] directly would return only the first char, which
        // isn't a usable rune on its own.
        //
        // If the literal starts with a lone surrogate (or other non-decodable
        // first char), TokenSet only holds valid scalars, so there's no
        // single-rune set to advertise. Fall back to Universe — the per-token
        // compare in TryParseRule still works for surrogate-half literals
        // under Compile(null).
        if (!Lexer.TryPeekRune(_expected, 0, out int first, out _))
            return new RuleStartRequirements(TokenSet.Universe, Advance.Always);
        if (IsAsciiLetter((char)first))
        {
            int lower = first | 0x20;
            int upper = lower & ~0x20;
            return new RuleStartRequirements(TokenSet.Single(lower) | TokenSet.Single(upper), Advance.Always);
        }
        return new RuleStartRequirements(TokenSet.Single(first), Advance.Always);
    }
}
