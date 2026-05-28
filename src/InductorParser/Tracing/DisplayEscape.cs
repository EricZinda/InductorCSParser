using System;
using System.Globalization;
using System.Text;

namespace InductorParser.Tracing;

// One place that decides which chars corrupt a single-line display string
// when written verbatim, and how to render them so they don't. Used by
// every site that splices user-bearing text into a one-line render:
//   * TokenSet.ToString builds "[a-z,U+000D,...]" form. The multi-rune
//     grapheme entries used to leak a raw CRLF until 2026-05-20.
//   * SymbolExtensions.PrintTree's short-form `'c'` rendering of a
//     character-leaf id used to leak the same on a Token('\n').Preserve().
//   * Lexer.Read's diagnostic trace `'<tokenText>', Consumed: N` used to
//     leak the same on any token whose chars include a control / line
//     separator (the CRLF cluster, an LF-only Preserve'd Token, etc.).
//
// All three callers used the same Cc / Zl / Zp test, written three
// times. Centralizing here keeps them from drifting apart and gives a
// single home for the rules below.
//
// What counts as "corrupting":
//   * Control (Cc): LF, CR, VT, FF, NEL, and the rest of the C0/C1
//     block. char.IsControl reports exactly this set.
//   * LineSeparator (Zl): U+2028.
//   * ParagraphSeparator (Zp): U+2029.
// Format characters (Cf) such as ZWJ are deliberately NOT included. ZWJ
// is the invisible glue inside emoji ZWJ families and similar clusters
// we want rendered as the user-perceived character, and it doesn't break
// the line. Every line-breaking and control character lives in the BMP,
// so the per-char (vs per-rune) check is enough: the surrogate halves
// of a supplementary-plane character report as Surrogate and fall
// through to the verbatim path, reassembling the original character.
//
// Reading the category from the BCL tables (CharUnicodeInfo.GetUnicodeCategory)
// rather than a hand-kept code-point list keeps the escape set tracking
// new Unicode versions automatically.
internal static class DisplayEscape
{
    /// <summary>
    /// True when <paramref name="c"/>'s Unicode General Category is
    /// Control (Cc), LineSeparator (Zl), or ParagraphSeparator (Zp).
    /// </summary>
    public static bool IsControlOrLineSeparator(char c)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category == UnicodeCategory.Control
            || category == UnicodeCategory.LineSeparator
            || category == UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>
    /// Append <paramref name="text"/> to <paramref name="builder"/>,
    /// rendering each Cc / Zl / Zp char as <c>U+XXXX</c> and every other
    /// char verbatim. Use when the caller is already accumulating into a
    /// StringBuilder (TokenSet.ToString's bracketed render is the
    /// canonical caller).
    /// </summary>
    public static void AppendEscaped(StringBuilder builder, ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (IsControlOrLineSeparator(c))
                builder.Append("U+").Append(((int)c).ToString("X4"));
            else
                builder.Append(c);
        }
    }

    /// <summary>
    /// Return an escaped copy of <paramref name="source"/>[<paramref name="offset"/>..<paramref name="offset"/>+<paramref name="length"/>].
    /// Falls back to a plain <c>Substring</c> when no chars need escape,
    /// so the common single-printable-rune case allocates nothing past
    /// the substring itself.
    /// </summary>
    public static string Escape(string source, int offset, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (IsControlOrLineSeparator(source[offset + i]))
                return EscapeSlow(source, offset, length, i);
        }
        return source.Substring(offset, length);
    }

    // Build the escaped string starting from `startAt`, the first
    // index whose char needs escaping. The chars before it are already
    // known to be verbatim. Pulled out so the fast path (no escape
    // needed) stays a single scan plus one Substring.
    private static string EscapeSlow(string source, int offset, int length, int startAt)
    {
        var sb = new StringBuilder(length + 6);
        if (startAt > 0)
            sb.Append(source, offset, startAt);
        for (int i = startAt; i < length; i++)
        {
            char c = source[offset + i];
            if (IsControlOrLineSeparator(c))
                sb.Append("U+").Append(((int)c).ToString("X4"));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
