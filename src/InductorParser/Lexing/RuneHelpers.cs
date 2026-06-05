using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace InductorParser.Lexing;

/// <summary>
/// Forward-direction UTF-16 rune utilities: "does input[pos] start a
/// well-formed surrogate pair?" (<see cref="IsSurrogatePairAt(string, int)"/>)
/// and the decode-as-runes walkers built on it
/// (<see cref="EnumerateRuneValues"/>, <see cref="RuneCount"/>).
/// </summary>
/// <remarks>
/// This is the rune layer. Its grapheme-cluster counterpart is
/// <c>GraphemeHelpers</c>; the two stay separate because runes and UAX #29
/// clusters are different units, and the lexer's rune-mode / grapheme-mode
/// split rests on keeping them apart.
/// </remarks>
public static class RuneHelpers
{
    /// <summary>
    /// True when <c>input[pos]</c> is a high surrogate followed by a low
    /// surrogate, i.e. a well-formed UTF-16 surrogate pair starts at
    /// <paramref name="pos"/>. False if <c>input[pos]</c> is a stray
    /// surrogate (high without a paired low, or a low in any position) or
    /// any non-surrogate char. Caller guarantees
    /// <c>0 &lt;= pos &lt; input.Length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSurrogatePairAt(string input, int pos)
        => char.IsHighSurrogate(input[pos])
            && pos + 1 < input.Length
            && char.IsLowSurrogate(input[pos + 1]);

    /// <summary>
    /// Span overload of
    /// <see cref="IsSurrogatePairAt(string, int)"/>. Same semantics; exists
    /// so span callers don't pay an <c>.AsSpan()</c> round-trip on what
    /// they already have.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSurrogatePairAt(ReadOnlySpan<char> input, int pos)
        => char.IsHighSurrogate(input[pos])
            && pos + 1 < input.Length
            && char.IsLowSurrogate(input[pos + 1]);

    /// <summary>
    /// Enumerate the Unicode scalar values (rune code points) of
    /// <paramref name="text"/>, decoding each well-formed surrogate pair
    /// into one value. A lone surrogate yields its own code-unit value.
    /// Allocates an iterator, so it's for cold paths; hot paths that
    /// already walk chars should decode inline with
    /// <see cref="IsSurrogatePairAt(string, int)"/> rather than enumerate.
    /// </summary>
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

    /// <summary>
    /// Count the Unicode scalar values (runes) in <paramref name="text"/>,
    /// using the same model as <see cref="EnumerateRuneValues"/> and the
    /// lexer's one-rune-per-token Read: a well-formed high+low surrogate
    /// pair is one rune, and any stray surrogate is one rune. Allocates
    /// nothing, so it's fine on a warm path. To count a prefix, pass a
    /// span over just that range (e.g. <c>input.AsSpan(0, position)</c>);
    /// a high surrogate at the end of the span has no paired low inside
    /// it, so it counts as one rune, matching how the lexer reads a
    /// cluster's runes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RuneCount(ReadOnlySpan<char> text)
    {
        int runes = 0;
        for (int index = 0; index < text.Length; runes++)
            index += IsSurrogatePairAt(text, index) ? 2 : 1;
        return runes;
    }
}
