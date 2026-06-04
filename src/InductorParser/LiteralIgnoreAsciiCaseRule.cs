using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Case-insensitive literal match, ASCII only. Same single-transaction
// shape as LiteralRule. The only difference is the compare treats ASCII
// letters case-insensitively. The pattern itself is restricted to ASCII
// (every char in 0x00..0x7F) and the constructor throws on any non-ASCII
// char. Non-ASCII code points in the pattern would be confusing: ASCII
// case-insensitive matching doesn't apply to them, so a non-ASCII letter
// in a pattern labeled "IgnoreAsciiCase" gives the reader the wrong mental
// model. Grammars that want a non-ASCII keyword should use Literal(...).
internal sealed class LiteralIgnoreAsciiCaseRule : Rule
{
    private readonly string _expected;

    // Read-only accessor for the rule's literal text, used by out-of-assembly
    // analyzers that inspect a rule's fixed text.
    internal string? ExpectedText => _expected;

    public LiteralIgnoreAsciiCaseRule(string expected) : base(FlattenType.Delete, emitsLeaf: true)
    {
        if (expected == null)
            throw new ArgumentNullException(nameof(expected));
        if (expected.Length == 0)
            throw new ArgumentException("LiteralIgnoreAsciiCase requires a non-empty string.", nameof(expected));
        for (int charIndex = 0; charIndex < expected.Length; charIndex++)
        {
            char c = expected[charIndex];
            if (c > 0x7F)
                throw new ArgumentException(
                    $"LiteralIgnoreAsciiCase requires an ASCII-only pattern. " +
                    $"Char at index {charIndex} is U+{(int)c:X4} (outside 0x00..0x7F).",
                    nameof(expected));
        }
        _expected = expected;
    }

    // No ValidateNormalization override on purpose. The constructor
    // restricts the pattern to ASCII, and ASCII code points are invariant
    // under all four normalization forms (NFC/NFD/NFKC/NFKD), so there's
    // nothing to convert and the base no-op is correct. Contrast LiteralRule,
    // which accepts arbitrary text and overrides ValidateNormalization to
    // normalize its stored string.

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        int consumed = 0;

        while (consumed < _expected.Length)
        {
            int tokenStart = lexer.Position;
            var token = lexer.Read();
            if (token.IsEof)
            {
                TraceFailure(lexer, $"found '<EOF>', wanted '{_expected}' (case-insensitive)");
                lexer.RecordFailure(tokenStart, ErrorMessage, ErrorForced);
                return null;
            }
            if (consumed + token.Length > _expected.Length
                || !AsciiCaseEquals(token.Chars, _expected.AsSpan(consumed, token.Length)))
            {
                TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted '{_expected}' (case-insensitive)");
                lexer.RecordFailure(tokenStart, ErrorMessage, ErrorForced);
                return null;
            }
            consumed += token.Length;
        }

        TraceSuccess(lexer, $"found '{lexer.Input.Substring(startPosition, consumed)}', wanted '{_expected}' (case-insensitive)");
        // Default FlattenType is Delete: the common case collapses to
        // the shared Discarded value and skips the per-match Symbol allocation.
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

    // ASCII-only case-insensitive compare. Both sides compare bit-exact
    // when either char is outside A-Za-z. Within A-Za-z forcing the 0x20
    // case bit on with `| 0x20` makes 'A' and 'a' compare equal. Non-letters
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

}
