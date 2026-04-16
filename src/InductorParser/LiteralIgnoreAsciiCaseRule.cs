using System;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Case-insensitive literal match, ASCII letters only. Same single-transaction
// shape as LiteralRule; the only difference is the comparison folds ASCII
// A-Z and a-z before testing equality. Non-ASCII code units compare bit-exact,
// so Turkish dotless-I, German sharp-s, Greek sigma variants, etc. do NOT
// match their upper/lower counterparts. That tradeoff is on purpose: full
// Unicode case folding is locale-dependent and grammar-breaking, and the
// keyword-heavy grammars that want this primitive (SQL, HTTP methods, chord
// notation) only ever case-fold ASCII in practice. See docs/UnicodeGotchas.md
// for the longer explanation.
internal sealed class LiteralIgnoreAsciiCaseRule : Rule
{
    private readonly string _expected;

    public LiteralIgnoreAsciiCaseRule(string expected) : base(FlattenType.Delete)
    {
        if (expected == null)
            throw new ArgumentNullException(nameof(expected));
        if (expected.Length == 0)
            throw new ArgumentException("LiteralIgnoreAsciiCase requires a non-empty string.", nameof(expected));
        _expected = expected;
        SetTraceName("LiteralIgnoreAsciiCase");
    }

    internal override Symbol? TryParseRule(Lexer lexer)
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
        return new Symbol(Id, FlattenType, lexer.Input.AsMemory(transaction.StartPosition, consumed));
    }

    // ASCII-only case fold. Both sides compare bit-exact when either char
    // is outside A-Za-z; within A-Za-z the 0x20 bit difference is masked
    // out so 'A' and 'a' hash the same. Non-letters (digits, punctuation,
    // spaces) take the bit-exact path because (c | 0x20) is only a valid
    // case fold for the 26 ASCII letters; folding '[' would give '{' and
    // break "match [" against input "{".
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
}
