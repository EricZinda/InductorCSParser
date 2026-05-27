using System;
using System.Runtime.CompilerServices;

namespace InductorParser.Lexing;

// One place to ask "does input[pos] start a well-formed UTF-16 surrogate
// pair?" so the high-then-low predicate doesn't drift across the codebase.
// All forward-direction surrogate-pair decoders (Lexer.NextTokenLength,
// Lexer.TryPeekRune, Rule's LiteralRule formatter, TokenSet's set
// construction, Token.RuneValue / Token.FirstRune, the Canary
// test helper) call into here.
//
// AggressiveInlining keeps the call free at the IL/JIT level; the body
// is three short calls and folds into the caller. The string and span
// overloads exist so callers don't have to pay for an .AsSpan() round-
// trip on a string they already have.
internal static class SurrogateHelpers
{
    // True when input[pos] is a high surrogate followed by a low surrogate,
    // i.e. a well-formed UTF-16 surrogate pair starts at `pos`. False if
    // input[pos] is a stray surrogate (high without a paired low, or a low
    // in any position) or any non-surrogate char.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSurrogatePairAt(string input, int pos)
        => char.IsHighSurrogate(input[pos])
            && pos + 1 < input.Length
            && char.IsLowSurrogate(input[pos + 1]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSurrogatePairAt(ReadOnlySpan<char> input, int pos)
        => char.IsHighSurrogate(input[pos])
            && pos + 1 < input.Length
            && char.IsLowSurrogate(input[pos + 1]);
}
