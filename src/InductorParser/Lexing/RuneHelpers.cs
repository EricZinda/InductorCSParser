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
/// <c>GraphemeHelpers</c>. The two stay separate because runes and UAX #29
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
    /// <see cref="IsSurrogatePairAt(string, int)"/> with the same semantics.
    /// It exists so span callers don't pay an <c>.AsSpan()</c> round-trip on
    /// what they already have.
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
    /// Allocates an iterator, so it's for cold paths. Hot paths that
    /// already walk chars should decode inline with
    /// <see cref="IsSurrogatePairAt(string, int)"/> rather than enumerate.
    /// </summary>
    public static IEnumerable<int> EnumerateRuneValues(string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));

        return EnumerateRuneValuesCore(text);
    }

    // Split from the public method so the null check above throws at
    // the call. An iterator method defers its whole body, null check
    // included, so a single-method version would return normally on
    // null and surface a NullReferenceException at the first MoveNext
    // instead. Same trap class as the GraphemeHelpers.Count null fix.
    private static IEnumerable<int> EnumerateRuneValuesCore(string text)
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
    /// span over just that range (e.g. <c>input.AsSpan(0, position)</c>).
    /// A high surrogate at the end of the span has no paired low inside
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

    /// <summary>
    /// True when <paramref name="grapheme"/> is exactly one Unicode rune (one
    /// non-surrogate UTF-16 char, or one surrogate pair), giving its code point
    /// in <paramref name="runeValue"/>. Exposed so user-defined rules can reuse
    /// it instead of duplicating the surrogate-pair decode.
    /// </summary>
    public static bool TrySingleRune(string grapheme, out int runeValue)
    {
        if (grapheme == null)
            throw new ArgumentNullException(nameof(grapheme));

        return TrySingleRune(grapheme.AsSpan(), out runeValue);
    }

    /// <summary>
    /// Span overload of <see cref="TrySingleRune(string, out int)"/>. Same
    /// rule: true for one non-surrogate char or one surrogate pair, false for a
    /// lone surrogate or a multi-rune span. Exists so span callers don't pay an
    /// <c>.AsSpan()</c> round-trip on what they already have.
    /// </summary>
    public static bool TrySingleRune(ReadOnlySpan<char> grapheme, out int runeValue)
    {
        if (grapheme.Length == 1)
        {
            char c = grapheme[0];
            if (char.IsSurrogate(c)) { runeValue = -1; return false; }
            runeValue = c;
            return true;
        }
        if (grapheme.Length == 2 && IsSurrogatePairAt(grapheme, 0))
        {
            runeValue = char.ConvertToUtf32(grapheme[0], grapheme[1]);
            return true;
        }
        runeValue = -1;
        return false;
    }
}
