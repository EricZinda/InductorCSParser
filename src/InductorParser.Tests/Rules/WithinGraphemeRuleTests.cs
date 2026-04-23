using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Tests for Rules.WithinGrapheme, the combinator that runs an inner rule
// against the runes inside one outer token. Used by Identifier() to handle
// Devanagari / Thai / Arabic-with-vowels under the default grapheme lexer,
// but usable by any grammar that needs to validate grapheme-internal
// structure (emoji sequences, Hangul jamo clusters, ASCII strictness).
[TestFixture]
public class WithinGraphemeRuleTests
{
    [Test]
    public void Single_rune_grapheme_matches_inner_rune_rule()
    {
        var rule = WithinGrapheme(RuneIn(RuneSet.Ascii.Letters));
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void Single_rune_grapheme_fails_when_inner_rejects()
    {
        var rule = WithinGrapheme(RuneIn(RuneSet.Ascii.Letters));
        var result = rule.Parse("3");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Multi_rune_grapheme_matches_when_inner_consumes_all_runes()
    {
        // "é" as e + combining acute is one grapheme containing two runes.
        // The inner rule accepts both in order: a letter then a combining
        // mark. Uses NormalizeInput=null so the decomposed form survives
        // to the lexer.
        var rule = WithinGrapheme(And(
            RuneIn(RuneSet.Ascii.Letters),
            RuneIn(RuneSet.Category(System.Globalization.UnicodeCategory.NonSpacingMark))
        ));
        var result = rule.Parse(LatinEAcuteGrapheme, new ParseOptions { NormalizeInput = null });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(LatinEAcuteGrapheme));
    }

    [Test]
    public void Multi_rune_grapheme_fails_when_inner_matches_only_prefix()
    {
        // A grapheme is atomic. If the inner rule matches just the first
        // rune and leaves the combining mark unconsumed, the whole
        // WithinGrapheme fails rather than accepting a partial match.
        var rule = WithinGrapheme(RuneIn(RuneSet.Ascii.Letters));
        var result = rule.Parse(LatinEAcuteGrapheme, new ParseOptions { NormalizeInput = null });

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Fails_at_end_of_input()
    {
        var rule = WithinGrapheme(AnyToken()).WithError("wanted a grapheme");
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("wanted a grapheme"));
    }

    [Test]
    public void Works_identically_under_rune_lexer_for_single_rune_input()
    {
        // Under RuneLexer each outer token is one rune, so the sub-lexer
        // sees a one-rune stream. Inner rule that needs exactly one rune
        // matches and consumes it, just like under GraphemeLexer for
        // single-rune graphemes.
        var rule = WithinGrapheme(RuneIn(RuneSet.Ascii.Letters));
        var result = rule.Parse("a", new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a"));
    }

    [Test]
    public void Composes_into_zero_or_more_for_multi_grapheme_sequences()
    {
        // ZeroOrMore(WithinGrapheme(letter)) walks a sequence of single-
        // rune graphemes. Proves the combinator composes into the normal
        // repeat combinators without special handling.
        var rule = ZeroOrMore(WithinGrapheme(RuneIn(RuneSet.Ascii.Letters)))
            .Flatten(FlattenType.Preserve)
            .As("letters");
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void Emits_exactly_one_leaf_per_grapheme_regardless_of_inner_structure()
    {
        // The inner rule is a two-piece And, but WithinGrapheme hides
        // that and emits a single leaf Symbol for the whole grapheme.
        // Grammar authors can rely on WithinGrapheme looking like a leaf
        // from the outside.
        var rule = WithinGrapheme(And(
            RuneIn(RuneSet.Ascii.Letters),
            RuneIn(RuneSet.Category(System.Globalization.UnicodeCategory.NonSpacingMark))
        ));
        var result = rule.Parse(LatinEAcuteGrapheme, new ParseOptions { NormalizeInput = null });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // One Symbol in the tree representing the whole grapheme. No
        // children from the inner And / RuneIn pair.
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0));
    }

    [Test]
    public void Reject_multi_rune_graphemes_idiom_works()
    {
        // WithinGrapheme(RuneIn(singleRuneSet)) is the idiomatic "reject
        // any multi-rune grapheme" rule. Passes on ASCII, fails on
        // precomposed "é" (single rune but not ASCII), fails on
        // decomposed "é" (two runes).
        var asciiOnly = WithinGrapheme(RuneIn(RuneSet.Ascii.Letters));

        Assert.That(asciiOnly.Parse("a").Success, Is.True);
        Assert.That(asciiOnly.Parse("é").Success, Is.False);  // é is not ASCII
        Assert.That(asciiOnly.Parse(LatinEAcuteGrapheme,
            new ParseOptions { NormalizeInput = null }).Success, Is.False);  // two runes
    }

    // The three tests below are the real-world reason WithinGrapheme
    // exists. Devanagari, Thai, and Arabic-with-vowels all produce
    // multi-rune graphemes unconditionally (NFC doesn't compose them),
    // which is the case the Latin-decomposed tests above only simulate
    // via NormalizeInput=null. These tests run under the default
    // grapheme lexer and the default NFC normalization.

    [Test]
    public void Devanagari_consonant_plus_vowel_sign_grapheme_matches()
    {
        // "हि" is one grapheme under the grapheme lexer, two runes:
        // U+0939 DEVANAGARI LETTER HA (Lo) + U+093F DEVANAGARI VOWEL SIGN I (Mc).
        // The inner rule walks both runes.
        var rule = WithinGrapheme(And(
            RuneIn(RuneSet.XidStart),
            RuneIn(RuneSet.XidContinue)));
        var result = rule.Parse("हि");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("हि"));
    }

    [Test]
    public void Thai_consonant_plus_sara_am_grapheme_matches()
    {
        // "กำ" is one grapheme, two runes: U+0E01 THAI CHARACTER KO KAI
        // (Lo, in XID_Start) + U+0E33 THAI CHARACTER SARA AM (in
        // XID_Continue, excluded from XID_Start via the NFKC-unstable
        // table since its NFKC decomposition is NIKHAHIT + SARA AA).
        var rule = WithinGrapheme(And(
            RuneIn(RuneSet.XidStart),
            RuneIn(RuneSet.XidContinue)));
        var result = rule.Parse("กำ");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("กำ"));
    }

    [Test]
    public void Arabic_consonant_plus_fatha_grapheme_matches()
    {
        // "كَ" is one grapheme, two runes: U+0643 ARABIC LETTER KAF (Lo) +
        // U+064E ARABIC FATHA (Mn). Represents the common case of Arabic
        // text written with the optional vowel diacritics, which bundle
        // with their preceding consonant under grapheme clustering.
        var rule = WithinGrapheme(And(
            RuneIn(RuneSet.XidStart),
            RuneIn(RuneSet.XidContinue)));
        var result = rule.Parse("كَ");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("كَ"));
    }
}
