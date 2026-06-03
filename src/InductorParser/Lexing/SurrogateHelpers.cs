using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace InductorParser.Lexing;

// One place to ask "does input[pos] start a well-formed UTF-16 surrogate
// pair?" so the high-then-low predicate doesn't drift across the codebase.
// Every forward-direction surrogate-pair decoder (in the lexer, the rules,
// TokenSet construction, and Token's rune accessors) calls into here.
//
// char.IsSurrogatePair computes the same result but only takes a string, not
// a ReadOnlySpan<char>, so the span callers need a handwritten check anyway.
// Defining both overloads here keeps the predicate identical in one place
// instead of split between the BCL and our own span code, lets us skip the
// BCL method's null and range checks (callers guarantee pos is in range), and
// carries AggressiveInlining so the body folds into the caller. Both
// overloads exist so callers don't pay an .AsSpan() round-trip on what they
// already have.
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

    // Enumerate the Unicode scalar values (rune code points) of `text`,
    // decoding each well-formed surrogate pair into one value. A lone
    // surrogate yields its own code-unit value. This allocates an iterator,
    // so it's for cold paths. Hot paths that already walk chars should decode
    // inline with IsSurrogatePairAt rather than enumerate.
    public static IEnumerable<int> EnumerateRuneValues(string text)
    {
        for (int index = 0; index < text.Length;)
        {
            if (IsSurrogatePairAt(text, index))
            {
                yield return char.ConvertToUtf32(text[index], text[index + 1]);
                index += 2;
            }
            else
            {
                yield return text[index];
                index += 1;
            }
        }
    }
}
