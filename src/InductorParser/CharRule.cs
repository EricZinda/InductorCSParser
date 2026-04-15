using System;
using System.Globalization;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches one grapheme by exact content. The expected grapheme is pinned
// to a string at construction time and compared against the lexer's
// output at match time.
//
// Under GraphemeLexer, a grapheme (even a multi-rune one like 👨‍👩‍👧‍👦)
// arrives as a single token whose Chars span is the whole grapheme. The
// match is one token read and one SequenceEqual compare.
//
// Under RuneLexer, a multi-rune grapheme arrives as multiple rune tokens
// (the family emoji is seven rune tokens: four people + three ZWJs). The
// match reads those tokens in order and compares each against the
// corresponding slice of the expected string. Lockstep one-to-N read.
//
// Both behaviors fall out of the same loop: read a token, compare its
// Chars to the expected[consumed..consumed+token.Length] slice, advance.
// No lexer-specific branching.
//
// Construction validates that the expected string is exactly one
// grapheme via StringInfo.GetNextTextElement. Char("ab") throws at
// grammar-build time instead of silently failing at parse time. (Note:
// on pre-.NET 5 runtimes StringInfo is not UAX #29 compliant, so the
// grapheme count for exotic Unicode inputs can be wrong; see
// backlog/r000.)
//
// If the expected grapheme is exactly one rune (the common case for
// ASCII, emoji that fit in a single code point, CJK, etc.), the Id is
// pinned to that code point so Symbol leaves produced by this rule
// carry the "id == rune" shape. For multi-rune graphemes
// the Id comes from Compile's custom-range assignment.
internal sealed class CharRule : Rule
{
    private readonly string _expected;

    public CharRule(string expectedGrapheme) : base(FlattenType.Delete)
    {
        if (expectedGrapheme == null)
            throw new ArgumentNullException(nameof(expectedGrapheme));
        if (expectedGrapheme.Length == 0)
            throw new ArgumentException("Char requires a non-empty grapheme.", nameof(expectedGrapheme));
        string firstElement = StringInfo.GetNextTextElement(expectedGrapheme, 0);
        if (firstElement.Length != expectedGrapheme.Length)
            throw new ArgumentException(
                $"Char requires exactly one grapheme. Use Literal(string) for multi-grapheme matches.",
                nameof(expectedGrapheme));

        _expected = expectedGrapheme;

        // Single-rune graphemes get their code point pinned as the rule's
        // Id, matching C++ character-symbol numbering. Multi-rune
        // graphemes fall through to Compile's custom-range assignment.
        if (TrySingleRuneValue(expectedGrapheme, out int runeValue))
            SetIdInternal(new SymbolId(runeValue));
    }

    internal override Symbol? TryParse(Lexer lexer)
    {
        using var transaction = lexer.BeginTransaction();
        int consumed = 0;

        // No heap allocations here except the one for the Symbol we return at the end.
        // Iterate once per lexer token, the consumed+= token.length is what does
        // the magic. GraphemeLexer emits one token for
        // the whole grapheme (loop runs once). RuneLexer emits one token
        // per rune, so a 2-rune grapheme takes two iterations, a 7-rune
        // ZWJ emoji takes seven, etc.
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
            // For a single-token match this equals transaction.StartPosition;
            // for multi-token lockstep (multi-rune grapheme under RuneLexer)
            // it's the start of whichever token mismatched, not the start
            // of the whole attempt.
            if (token.IsEof)
            {
                lexer.RecordFailure(tokenStart, ErrorMessage);
                return null;
            }
            if (consumed + token.Length > _expected.Length
                || !token.Chars.SequenceEqual(_expected.AsSpan(consumed, token.Length)))
            {
                lexer.RecordFailure(tokenStart, ErrorMessage);
                return null;
            }
            consumed += token.Length;
        }

        transaction.Commit();
        return new Symbol(Id, FlattenType, lexer.Input.AsMemory(transaction.StartPosition, consumed));
    }

    // True iff the string is exactly one Unicode rune (one BMP char or
    // one surrogate pair). Works without depending on Rune.EnumerateRunes,
    // which isn't in netstandard2.1.
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
