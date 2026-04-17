using System;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches an exact multi-character string in a single transaction. This is
// the N-character generalization of CharRule. Where CharRule's expected is
// one grapheme, LiteralRule's expected is any non-empty string.
//
// Why a dedicated rule instead of And(Char('m'), Char('a'), Char('j'))? Each
// Char opens its own transaction; a three-character And(Char, Char, Char)
// does three BeginTransaction/Commit cycles and three RecordFailure slots.
// Literal("maj") does one. For keyword-heavy grammars (chord notation, SQL
// keywords, HTTP methods) this is the difference between per-keyword O(N)
// transaction overhead and O(1).
//
// The match loop is the same lockstep pattern CharRule uses: read a token,
// compare its Chars span against the matching slice of the expected string,
// advance by token.Length. Under GraphemeLexer each iteration typically
// consumes one grapheme worth of chars; under RuneLexer each iteration
// consumes one rune. Same loop, both lexers, because SequenceEqual only
// cares about the underlying chars lining up, not how the lexer chose to
// group them.
//
// Error position on mismatch is the pre-read offset of the specific failing
// token, not the start of the whole attempt. Same as CharRule: this is what
// "points-at-the-offender" means in a multi-token lockstep match.
//
// Default FlattenType is Delete, matching CharRule. The common case for a
// literal is a keyword or delimiter the grammar wants to assert is present
// but doesn't need to materialize in the output tree.
internal sealed class LiteralRule : Rule
{
    private readonly string _expected;

    public LiteralRule(string expected) : base(FlattenType.Delete)
    {
        if (expected == null)
            throw new ArgumentNullException(nameof(expected));
        if (expected.Length == 0)
            throw new ArgumentException("Literal requires a non-empty string.", nameof(expected));
        _expected = expected;
    }

    internal override Symbol? TryParseRule(Lexer lexer, bool discard)
    {
        using var transaction = lexer.BeginTransaction();
        int consumed = 0;

        while (consumed < _expected.Length)
        {
            int tokenStart = lexer.Position;
            var token = lexer.Read();
            if (token.IsEof)
            {
                TraceFailure(lexer, $"found '<EOF>', wanted '{_expected}'");
                lexer.RecordFailure(tokenStart, ErrorMessage);
                return null;
            }
            if (consumed + token.Length > _expected.Length
                || !token.Chars.SequenceEqual(_expected.AsSpan(consumed, token.Length)))
            {
                TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted '{_expected}'");
                lexer.RecordFailure(tokenStart, ErrorMessage);
                return null;
            }
            consumed += token.Length;
        }

        TraceSuccess(lexer, $"found '{_expected}'");
        transaction.Commit();
        // Default FlattenType is Delete: the common case collapses to the
        // shared sentinel and skips the per-match Symbol allocation.
        if (discard)
            return Symbol.Discarded;
        return new Symbol(Id, FlattenType, lexer.Input.AsMemory(transaction.StartPosition, consumed));
    }

    internal override RuleStart ComputeRuleStart()
    {
        // The only rune that can start a match of this literal is the first
        // rune of the expected string. Advance is Always because a literal
        // always consumes at least one rune to match. TryPeekRune decodes
        // the first rune correctly even when it's a non-BMP code point that
        // spans two UTF-16 chars (emoji, supplementary-plane CJK) —
        // _expected[0] would hand back just the high surrogate, which isn't
        // a valid rune.
        Lexer.TryPeekRune(_expected, 0, out int first, out _);
        return new RuleStart(RuneSet.Single(first), Advance.Always);
    }
}
