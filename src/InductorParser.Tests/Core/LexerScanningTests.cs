using NUnit.Framework;
using InductorParser.Lexing;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Direct tests for the Lexer bulk-scan API. AdvanceWhileIn is the public
// entry point and branches on TokenSet.HasMultiRuneGraphemes between the
// two internal loops, AdvanceWhileRuneIn and AdvanceWhileTokenIn. The
// direct tests on the internal loops cover the corner the branch never
// reaches: AdvanceWhileTokenIn handed a rune-only set.
[TestFixture]
public class LexerScanningTests
{
    [Test]
    public void AdvanceWhileIn_consumes_a_run_from_a_rune_only_set()
    {
        var lexer = new Lexer("abc123");

        int count = lexer.AdvanceWhileIn(TokenSet.Letters);

        Assert.That(count, Is.EqualTo(3));
        Assert.That(lexer.Position, Is.EqualTo(3),
            "The cursor stops on the first token that isn't in the set.");
    }

    [Test]
    public void AdvanceWhileIn_consumes_multi_rune_entries_from_a_mixed_set()
    {
        // LineTerminators lists CRLF as a two-rune entry, so the combined
        // set has multi-rune graphemes and AdvanceWhileIn takes the
        // grapheme-cluster path, where CRLF counts as one token.
        var set = TokenSet.Runes("a") | TokenSet.LineTerminators;
        var lexer = new Lexer("a\r\na!");

        int count = lexer.AdvanceWhileIn(set);

        Assert.That(count, Is.EqualTo(3), "a, CRLF, a: three tokens.");
        Assert.That(lexer.Position, Is.EqualTo(4),
            "The cursor stops on the '!', four chars in.");
    }

    [Test]
    public void AdvanceWhileTokenIn_consumes_lone_surrogate_a_rune_only_set_covers()
    {
        // The two AdvanceWhile methods should agree on rune-only sets:
        // AdvanceWhileTokenIn is the superset, accepting either rune or
        // multi-rune grapheme entries. A lone surrogate is a one-char
        // token whose code unit the set's rune intervals can cover (the
        // ContainsToken length-1-surrogate branch routes through
        // ContainsRune for exactly this case).
        //
        // AdvanceWhileRuneIn already handles it: see the 2026-05-15
        // ScanWhile rune-fast-path fix. AdvanceWhileTokenIn's "else"
        // branch gated the membership check on
        // set.HasMultiRuneGraphemes, so a rune-only set with the
        // surrogate as an entry short-circuited to false on the
        // lone-surrogate token and the loop bailed even when the set
        // covered the surrogate. A custom Rule subclass calling
        // AdvanceWhileTokenIn directly (rather than branching like
        // ScanWhileRule does) would silently skip surrogates the set
        // accepts.
        var set = TokenSet.SurrogateRange(0xD800, 0xD800);
        var lexer = new Lexer(HighSurrogateMinText);

        int count = lexer.AdvanceWhileTokenIn(set);

        Assert.That(count, Is.EqualTo(1),
            "AdvanceWhileTokenIn should consume a lone surrogate when the set's "
            + "rune intervals cover it. AdvanceWhileRuneIn handles this; the two "
            + "methods should agree on rune-only sets.");
        Assert.That(lexer.Position, Is.EqualTo(1),
            "Position should advance past the consumed lone surrogate.");
    }

    [Test]
    public void AdvanceWhileTokenIn_and_AdvanceWhileRuneIn_agree_on_rune_only_set_with_surrogate()
    {
        // Same surrogate input, same rune-only set: drive each method
        // against its own lexer instance and assert both produce the
        // same count. AdvanceWhileTokenIn's documented behavior is "in
        // the set, either rune or multi-rune grapheme entries," so the
        // two methods should give the same count for a rune-only set.
        var set = TokenSet.SurrogateRange(0xD800, 0xD800);
        var tokenLexer = new Lexer(HighSurrogateMinText);
        var runeLexer = new Lexer(HighSurrogateMinText);

        int tokenCount = tokenLexer.AdvanceWhileTokenIn(set);
        int runeCount = runeLexer.AdvanceWhileRuneIn(set);

        Assert.That(tokenCount, Is.EqualTo(runeCount),
            "AdvanceWhileTokenIn and AdvanceWhileRuneIn should agree on rune-only sets.");
        Assert.That(tokenLexer.Position, Is.EqualTo(runeLexer.Position),
            "Both methods should advance the lexer to the same position on rune-only sets.");
    }

    [Test]
    public void Grapheme_mode_fuses_a_lone_surrogate_with_a_following_combining_mark()
    {
        // In grapheme mode a stray surrogate isn't always a one-char
        // token. A bare stray is one char, but when a combining mark
        // follows, UAX #29 GB9 (don't break before an Extend) glues the
        // stray to the mark into one multi-char cluster: the segmenter
        // decodes the stray to U+FFFD (grapheme property Other) and won't
        // break before the following Extend. The tokenLength == 1 check in
        // AdvanceWhileRuneIn depends on this. Segmentation comes from
        // GraphemeSegmentation, the same answers the lexer uses everywhere.

        // A lone surrogate on its own is a one-char token.
        var alone = new Lexer(HighSurrogateMinText);
        Assert.That(alone.PeekTokenLength(0), Is.EqualTo(1),
            "A lone surrogate with nothing after it is a one-char token.");

        // A lone surrogate followed by U+0301 COMBINING ACUTE fuses into
        // one two-char cluster.
        string strayThenMark = HighSurrogateMinText + CombiningAcuteText;

        var lexer = new Lexer(strayThenMark);
        Assert.That(lexer.PeekTokenLength(0), Is.EqualTo(2),
            "The stray surrogate fuses with the following combining mark "
            + "into one two-char cluster (not two one-char tokens).");

        // Read advances the cursor over the whole fused cluster in one
        // step.
        var token = lexer.Read();
        Assert.That(token.Length, Is.EqualTo(2),
            "Read consumes the whole fused cluster as one token.");
        Assert.That(lexer.Position, Is.EqualTo(2),
            "The read cursor advances past the whole fused cluster.");
    }
}
