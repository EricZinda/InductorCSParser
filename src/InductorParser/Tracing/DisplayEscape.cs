using System;
using System.Globalization;
using System.Text;
using InductorParser.Lexing;

namespace InductorParser.Tracing;

// One place that decides which chars corrupt a single-line display string
// when written verbatim, and how to render them so they don't. Used by
// every site that puts user text into a one-line render:
//   * TokenSet.ToString builds the "[a-z,U+000D,...]" form, including
//     multi-rune grapheme entries that can contain a CRLF.
//   * SymbolExtensions.PrintTree's short-form `'c'` rendering of a
//     character-leaf id, which can be a Token('\n').Preserve().
//   * Lexer.Read's diagnostic trace `'<tokenText>', Consumed: N`, where the
//     token's chars can include a control / line separator (a CRLF cluster,
//     an LF-only Preserve'd Token, etc.).
//
// All three need the same control-and-line-separator test, so it lives here
// once rather than copied at each caller.
//
// What counts as "corrupting" is the official Unicode general categories
// Control, LineSeparator, and ParagraphSeparator, plus unpaired surrogate
// halves.
//
// Format characters such as ZWJ are deliberately not included. ZWJ is the
// invisible glue inside emoji ZWJ families and similar clusters we want
// rendered as the user-perceived character, and it doesn't break the line.
// Valid surrogate pairs fall through to the verbatim path as a pair,
// reassembling the original supplementary-plane character.
//
// Reading the category from the BCL tables (CharUnicodeInfo.GetUnicodeCategory)
// rather than a hand-kept code-point list keeps the escape set tracking
// new Unicode versions automatically.
internal static class DisplayEscape
{
    /// <summary>
    /// True when <paramref name="c"/>'s Unicode general category is
    /// Control, LineSeparator, or ParagraphSeparator.
    /// </summary>
    public static bool IsControlOrLineSeparator(char c)
    {
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category == UnicodeCategory.Control
            || category == UnicodeCategory.LineSeparator
            || category == UnicodeCategory.ParagraphSeparator;
    }

    private static bool NeedsCodeUnitEscape(char c) =>
        char.IsSurrogate(c) || IsControlOrLineSeparator(c);

    private static bool ContainsEscapableCodeUnit(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (RuneHelpers.IsSurrogatePairAt(text, i))
            {
                i++;
                continue;
            }
            if (NeedsCodeUnitEscape(text[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Return an escaped copy of <paramref name="source"/>[<paramref name="offset"/>..<paramref name="offset"/>+<paramref name="length"/>].
    /// Falls back to a plain <c>Substring</c> when no chars need escape,
    /// so the common single-printable-rune case allocates nothing past
    /// the substring itself.
    /// </summary>
    public static string Escape(string source, int offset, int length)
    {
        ReadOnlySpan<char> text = source.AsSpan(offset, length);
        if (!ContainsEscapableCodeUnit(text))
            return source.Substring(offset, length);

        var sb = new StringBuilder(length + 6);
        AppendEscaped(sb, text);
        return sb.ToString();
    }

    /// <summary>
    /// Append <paramref name="text"/> to <paramref name="builder"/>,
    /// rendering each control or line-separator char as <c>U+XXXX</c> and
    /// every other char verbatim. Use when the caller is already accumulating into a
    /// StringBuilder (TokenSet.ToString's bracketed render is the
    /// canonical caller).
    /// </summary>
    public static void AppendEscaped(StringBuilder builder, ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (RuneHelpers.IsSurrogatePairAt(text, i))
            {
                builder.Append(c);
                builder.Append(text[++i]);
            }
            else if (NeedsCodeUnitEscape(c))
            {
                AppendCodeUnitEscape(builder, c);
            }
            else
            {
                builder.Append(c);
            }
        }
    }

    private static void AppendCodeUnitEscape(StringBuilder builder, char c) =>
        builder.Append("U+").Append(((int)c).ToString("X4"));
}
