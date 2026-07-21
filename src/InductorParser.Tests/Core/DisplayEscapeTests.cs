using System;
using System.Text;
using NUnit.Framework;
using InductorParser.Tracing;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Direct tests for the shared one-line-display escape helper that
// TokenSet.ToString, SymbolExtensions.PrintTree, and Lexer.Read all
// route through. The three sites' own tests exercise it indirectly,
// but DisplayEscape is the single source of truth for which chars
// get rendered as U+XXXX, and the boundary cases (Cf vs Cc, surrogate
// halves, supplementary-plane emoji, the fast-path no-escape return,
// the offset / length parameters) are easier to pin here than through
// any one of the three render paths.
[TestFixture]
public class DisplayEscapeTests
{
    // ---------------------------------------------------------------
    // IsControlOrLineSeparator
    // ---------------------------------------------------------------

    [TestCase('\0', Description = "NULL")]
    [TestCase('\a', Description = "BEL")]
    [TestCase('\b', Description = "BS")]
    [TestCase('\t', Description = "TAB")]
    [TestCase('\n', Description = "LF")]
    [TestCase('\v', Description = "VT")]
    [TestCase('\f', Description = "FF")]
    [TestCase('\r', Description = "CR")]
    [TestCase('\u001F', Description = "C0 boundary (US)")]
    [TestCase('\u007F', Description = "DEL (C0 / C1 gap)")]
    [TestCase('\u009F', Description = "C1 boundary (APC)")]
    public void IsControlOrLineSeparator_returns_true_for_C0_and_C1_control_chars(char c)
    {
        Assert.That(DisplayEscape.IsControlOrLineSeparator(c), Is.True);
    }

    [Test]
    public void IsControlOrLineSeparator_returns_true_for_NEL()
    {
        Assert.That(DisplayEscape.IsControlOrLineSeparator(NextLineText[0]), Is.True);
    }

    [Test]
    public void IsControlOrLineSeparator_returns_true_for_LINE_SEPARATOR()
    {
        // U+2028 LINE SEPARATOR is Unicode category Zl, not Cc, so a
        // char.IsControl-only check would miss it. DisplayEscape has to
        // catch it because the lexer treats it as a line terminator
        // (TokenSet.LineTerminators) and any grammar that .Preserve's a
        // Token('\u2028') match would otherwise leak a real line
        // separator into the one-line display.
        Assert.That(DisplayEscape.IsControlOrLineSeparator(LineSeparatorText[0]), Is.True);
    }

    [Test]
    public void IsControlOrLineSeparator_returns_true_for_PARAGRAPH_SEPARATOR()
    {
        // U+2029 PARAGRAPH SEPARATOR is Unicode category Zp. Same
        // reason as the U+2028 case above.
        Assert.That(DisplayEscape.IsControlOrLineSeparator(ParagraphSeparatorText[0]), Is.True);
    }

    [TestCase('a', Description = "ASCII lowercase letter")]
    [TestCase('Z', Description = "ASCII uppercase letter")]
    [TestCase('1', Description = "ASCII digit")]
    [TestCase(' ', Description = "ASCII SPACE (Zs, not Zl/Zp)")]
    [TestCase('!', Description = "ASCII punctuation")]
    public void IsControlOrLineSeparator_returns_false_for_printable_chars(char c)
    {
        Assert.That(DisplayEscape.IsControlOrLineSeparator(c), Is.False);
    }

    [Test]
    public void IsControlOrLineSeparator_returns_false_for_named_printable_unicode_chars()
    {
        Assert.That(DisplayEscape.IsControlOrLineSeparator(NoBreakSpaceText[0]), Is.False, "NO-BREAK SPACE (Zs)");
        Assert.That(DisplayEscape.IsControlOrLineSeparator(LatinEAcutePrecomposedGrapheme[0]), Is.False, "Latin precomposed letter");
    }

    [Test]
    public void IsControlOrLineSeparator_returns_false_for_format_chars()
    {
        // ZWJ (U+200D) is Unicode category Cf (Format), deliberately
        // excluded from the escape set: ZWJ is the invisible glue
        // inside emoji ZWJ families and similar clusters, and it
        // doesn't break a one-line display. A char.IsControl check
        // already excludes it, but pin the behavior here so a future
        // refactor that broadens the predicate to "anything not
        // printable" doesn't silently break ZWJ-bearing emoji renders.
        Assert.That(DisplayEscape.IsControlOrLineSeparator(ZeroWidthJoinerText[0]), Is.False, "ZWJ");
        Assert.That(DisplayEscape.IsControlOrLineSeparator(ByteOrderMarkText[0]), Is.False, "ZWNBSP / BOM is also Cf");
    }

    [Test]
    public void IsControlOrLineSeparator_returns_false_for_surrogate_halves()
    {
        // Surrogate halves (U+D800..U+DFFF) are Unicode category Cs.
        // This predicate is intentionally only the category test for
        // line-breaking chars. AppendEscaped handles surrogate context
        // separately: paired halves pass through together, unpaired
        // halves render as U+XXXX.
        Assert.That(DisplayEscape.IsControlOrLineSeparator(HighSurrogateMinText[0]), Is.False, "high surrogate start");
        Assert.That(DisplayEscape.IsControlOrLineSeparator(HighSurrogateMaxText[0]), Is.False, "high surrogate end");
        Assert.That(DisplayEscape.IsControlOrLineSeparator(LowSurrogateMinText[0]), Is.False, "low surrogate start");
        Assert.That(DisplayEscape.IsControlOrLineSeparator(LowSurrogateMaxText[0]), Is.False, "low surrogate end");
    }

    // ---------------------------------------------------------------
    // AppendEscaped
    // ---------------------------------------------------------------

    [Test]
    public void AppendEscaped_writes_nothing_for_empty_span()
    {
        var sb = new StringBuilder("prefix-");
        DisplayEscape.AppendEscaped(sb, "".AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo("prefix-"));
    }

    [Test]
    public void AppendEscaped_passes_printable_chars_through_verbatim()
    {
        var sb = new StringBuilder();
        DisplayEscape.AppendEscaped(sb, "hello world!".AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo("hello world!"));
    }

    [Test]
    public void AppendEscaped_renders_a_single_control_char_as_U_XXXX()
    {
        var sb = new StringBuilder();
        DisplayEscape.AppendEscaped(sb, "\n".AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo("U+000A"));
    }

    [Test]
    public void AppendEscaped_escapes_each_char_independently_in_a_mixed_run()
    {
        // The CRLF cluster from TokenSet.LineTerminators is the canonical
        // mixed multi-char case: two control chars rendered one after the
        // other. No separator between them because the original is a
        // single cluster the rendered form has to round-trip back into.
        var sb = new StringBuilder();
        DisplayEscape.AppendEscaped(sb, "\r\n".AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo("U+000DU+000A"));
    }

    [Test]
    public void AppendEscaped_mixes_verbatim_and_escaped_chars()
    {
        var sb = new StringBuilder();
        DisplayEscape.AppendEscaped(sb, "a\n b".AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo("aU+000A b"));
    }

    [Test]
    public void AppendEscaped_renders_LINE_SEPARATOR_and_PARAGRAPH_SEPARATOR_as_U_XXXX()
    {
        var sb = new StringBuilder();
        DisplayEscape.AppendEscaped(sb, LineSeparatorText.AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo("U+2028"));

        sb.Clear();
        DisplayEscape.AppendEscaped(sb, ParagraphSeparatorText.AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo("U+2029"));
    }

    [Test]
    public void AppendEscaped_keeps_supplementary_plane_emoji_intact()
    {
        // U+1F3B8 (guitar emoji) is a supplementary-plane character
        // that arrives in UTF-16 as the surrogate pair U+D83C U+DFB8.
        // AppendEscaped has to recognize the valid pair and write both
        // halves verbatim so the rendered string reassembles into the
        // emoji.
        var sb = new StringBuilder();
        string guitar = GuitarGrapheme;
        DisplayEscape.AppendEscaped(sb, guitar.AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo(guitar));
    }

    [Test]
    public void AppendEscaped_escapes_unpaired_surrogate_halves()
    {
        // Valid surrogate pairs should keep rendering as the actual
        // supplementary character, but a lone high or low half isn't a
        // valid Unicode scalar value. If it reaches a one-line diagnostic
        // raw, encoders can replace it with U+FFFD or fail, hiding the
        // exact code unit the parser saw.
        var sb = new StringBuilder();
        DisplayEscape.AppendEscaped(sb, (HighSurrogateMinText + "a" + LowSurrogateMaxText).AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo("U+D800aU+DFFF"));
    }

    [Test]
    public void AppendEscaped_renders_NULL_char_with_four_hex_digits()
    {
        // U+0000 needs the full four-digit format `U+0000`. A regression
        // that switched to a variable-width hex format (e.g. `U+0`) would
        // break the pad-to-four convention the U+XXXX form everywhere
        // else in the codebase relies on.
        var sb = new StringBuilder();
        DisplayEscape.AppendEscaped(sb, NullText.AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo("U+0000"));
    }

    [Test]
    public void AppendEscaped_keeps_ZWJ_verbatim_inside_an_emoji_family()
    {
        // Format (Cf) chars like ZWJ are deliberately not escaped so
        // emoji ZWJ families render as the user-perceived character.
        // Without that exception, every ZWJ family in a trace line
        // would explode into "head U+200D head U+200D head ...". This
        // test locks in the policy at the helper layer.
        // Man + ZWJ + Woman (U+1F468 U+200D U+1F469). The ZWJ in the
        // middle has to come through unchanged.
        string family = ManEmojiGrapheme + ZeroWidthJoinerText + WomanEmojiGrapheme;
        var sb = new StringBuilder();
        DisplayEscape.AppendEscaped(sb, family.AsSpan());
        Assert.That(sb.ToString(), Is.EqualTo(family));
    }

    // ---------------------------------------------------------------
    // Escape
    // ---------------------------------------------------------------

    [Test]
    public void Escape_returns_a_plain_substring_when_no_chars_need_escape()
    {
        // The no-escape fast path is what keeps Lexer.Read's
        // per-token trace cost down. The function has to return a
        // string that's value-equal to source.Substring(offset, length)
        // when the scan finds nothing to escape, so the caller can rely
        // on the result being the exact section of the source.
        Assert.That(DisplayEscape.Escape("abcdef", 1, 3), Is.EqualTo("bcd"));
    }

    [Test]
    public void Escape_returns_empty_string_for_zero_length_range()
    {
        Assert.That(DisplayEscape.Escape("abc", 1, 0), Is.EqualTo(""));
    }

    [Test]
    public void Escape_escapes_a_control_char_at_the_start_of_the_range()
    {
        // First-position escape exercises the EscapeSlow fast-tail
        // optimization that copies the chars before `startAt` in bulk.
        // When the very first char escapes, the prefix is zero bytes
        // long and the bulk-copy `if (startAt > 0)` branch must be
        // skipped without writing anything.
        Assert.That(DisplayEscape.Escape("\nab", 0, 3), Is.EqualTo("U+000Aab"));
    }

    [Test]
    public void Escape_escapes_a_control_char_at_the_end_of_the_range()
    {
        // End-position escape exercises the for-loop termination: the
        // chars before the escape get copied in bulk, then the loop
        // runs through one more iteration for the escape itself.
        Assert.That(DisplayEscape.Escape("ab\n", 0, 3), Is.EqualTo("abU+000A"));
    }

    [Test]
    public void Escape_escapes_a_control_char_in_the_middle_of_the_range()
    {
        // Middle-position escape exercises both halves of the
        // EscapeSlow path: the bulk copy of the verbatim prefix and
        // the per-char loop over the rest.
        Assert.That(DisplayEscape.Escape("a\nb", 0, 3), Is.EqualTo("aU+000Ab"));
    }

    [Test]
    public void Escape_honors_the_offset_and_length_parameters()
    {
        // The offset / length aren't just for parity with Substring.
        // They let Lexer.Read pass `_input` directly so the helper
        // doesn't materialize a substring when it can avoid one. Pin
        // both boundary cases: the result starts at `offset` and stops
        // at `offset + length`, not earlier and not later.
        string source = "xx\r\nyy";
        Assert.That(DisplayEscape.Escape(source, 2, 2), Is.EqualTo("U+000DU+000A"),
            "exact range covering only the CRLF");
        Assert.That(DisplayEscape.Escape(source, 0, 6), Is.EqualTo("xxU+000DU+000Ayy"),
            "whole string");
        Assert.That(DisplayEscape.Escape(source, 0, 3), Is.EqualTo("xxU+000D"),
            "stops before the LF");
    }

    [Test]
    public void Escape_handles_multiple_control_chars_in_one_range()
    {
        // Two separate escapes split by a verbatim char. EscapeSlow
        // bulk-copies the prefix up to the first escape, then the
        // per-char loop has to switch between escape and verbatim
        // modes across the remaining chars.
        Assert.That(DisplayEscape.Escape("\na\r", 0, 3), Is.EqualTo("U+000AaU+000D"));
    }

    [Test]
    public void Escape_keeps_supplementary_plane_emoji_intact()
    {
        // Mirrors AppendEscaped's emoji test on the Escape entry point.
        // A supplementary-plane char crosses through as its complete
        // surrogate pair without either half being escaped, so the
        // rendered string is byte-identical to the input range.
        string guitar = GuitarGrapheme;
        Assert.That(DisplayEscape.Escape(guitar, 0, 2), Is.EqualTo(guitar));
    }

    [Test]
    public void Escape_escapes_unpaired_surrogate_halves()
    {
        Assert.That(DisplayEscape.Escape(HighSurrogateMinText + "ab", 0, 3), Is.EqualTo("U+D800ab"));
        Assert.That(DisplayEscape.Escape("ab" + LowSurrogateMaxText, 0, 3), Is.EqualTo("abU+DFFF"));
    }

    [Test]
    public void Escape_escapes_a_surrogate_pair_half_when_the_range_splits_the_pair()
    {
        // Escape works on the requested range, not on the whole source
        // string. A valid pair in the larger source isn't valid if the
        // range exposes only one half.
        string guitar = GuitarGrapheme;

        Assert.That(DisplayEscape.Escape(guitar, 0, 1), Is.EqualTo("U+D83C"));
        Assert.That(DisplayEscape.Escape(guitar, 1, 1), Is.EqualTo("U+DFB8"));
    }
}
