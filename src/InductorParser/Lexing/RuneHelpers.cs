using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace InductorParser.Lexing;

// The one place that reads UTF-16 text at the Unicode-scalar-value (rune)
// level: "does input[pos] start a well-formed surrogate pair?"
// (IsSurrogatePairAt), and the decode-as-runes walkers built on it
// (EnumerateRuneValues, RuneCount). Keeping them together stops the
// high-then-low surrogate predicate from drifting across the codebase: every
// forward-direction rune decoder (in the lexer, the rules, TokenSet
// construction, and Token's rune accessors) calls into here.
//
// char.IsSurrogatePair computes the same predicate but only takes a string,
// not a ReadOnlySpan<char>, so the span callers need a handwritten check
// anyway. Defining both overloads here keeps the predicate identical in one
// place instead of split between the BCL and our own span code, lets us skip
// the BCL method's null and range checks (callers guarantee pos is in range),
// and carries AggressiveInlining so the body folds into the caller. Both
// overloads exist so callers don't pay an .AsSpan() round-trip on what they
// already have.
//
// This is the rune layer. Its grapheme-cluster counterpart is
// GraphemeHelpers; the two stay separate because runes and UAX #29 clusters
// are different units, and the lexer's rune-mode / grapheme-mode split rests
// on keeping them apart.
public static class RuneHelpers
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

    // Count the Unicode scalar values (runes) in `text`, using the same model
    // as EnumerateRuneValues and the lexer's one-rune-per-token Read: a
    // well-formed high+low surrogate pair is one rune, and any stray surrogate
    // is one rune. Unlike EnumerateRuneValues this allocates nothing, so it's
    // fine on a warm path. To count a prefix, pass a span over just that range
    // (e.g. input.AsSpan(0, position)); a high surrogate at the end of the
    // span has no paired low inside it, so it counts as one rune, matching how
    // the lexer reads a cluster's runes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RuneCount(ReadOnlySpan<char> text)
    {
        int runes = 0;
        for (int index = 0; index < text.Length; runes++)
            index += IsSurrogatePairAt(text, index) ? 2 : 1;
        return runes;
    }
}
