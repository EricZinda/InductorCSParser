using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// End-to-end checks that the pass-through-text primitives
// (NoneOf, AnyToken, Not, Peek) compose into the two idioms the
// backlog called out: delimiter-based stops and rule-based stops.
// If one of the primitives regresses, a unit test will fail first.
// This fixture catches the interaction failures that only show up
// when the primitives work together.
[TestFixture]
public class PassThroughTextTests
{
    [Test]
    public void Line_comment_grammar_parses_everything_up_to_newline()
    {
        // Delimiter-based stop: NoneOf(TokenSet.Single('\n')) sweeps up
        // every character that isn't a newline. Stopping at '\n' falls out
        // naturally from ZeroOrMore stopping when the inner fails.
        //
        // WARNING: this example is LF-only on purpose. Under the default
        // GraphemeLexer, "\r\n" is one grapheme cluster, so Grapheme('\n')
        // DOESN'T match a CRLF line ending and NoneOf silently
        // swallows the CRLF grapheme as body content. If you're copying
        // this idiom for a line-based grammar that must accept Windows
        // line endings, add Literal("\r\n") as an explicit alternative
        // on both the body stop and the terminator. See
        // docs/UnicodeGotchas.md § "CRLF Under GraphemeLexer" for the
        // full pattern.
        var lineComment = AllOf(
            Grapheme('/'),
            Grapheme('/'),
            ZeroOrMore(NoneOf(TokenSet.Single('\n'))),
            Grapheme('\n'));

        // PreserveAllSymbols keeps the Grapheme('/') leaves and the
        // Grapheme('\n') leaf in the tree so Tree.ToString reproduces the
        // full comment text.
        var result = lineComment.Parse("// anything up to the newline\n",
            new ParseOptions { PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(),
            Is.EqualTo("// anything up to the newline\n"));
    }

    [Test]
    public void Line_comment_grammar_handles_emoji_in_the_body_under_grapheme_lexer()
    {
        // The whole point of preferring NoneOf over a hand-rolled
        // "any character except these" character class: a multi-rune
        // grapheme like 🎸 passes NoneOf because it isn't any single
        // rune in the stop set. The comment body scoops it up cleanly.
        var lineComment = AllOf(
            Grapheme('/'),
            Grapheme('/'),
            ZeroOrMore(NoneOf(TokenSet.Single('\n'))),
            Grapheme('\n'));

        var result = lineComment.Parse("// playing \uD83C\uDFB8 tonight\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Block_comment_grammar_stops_at_multi_character_terminator()
    {
        // Rule-based stop: ZeroOrMore(AllOf(Not(stopRule), AnyToken())) is
        // how you express "match until a multi-character terminator
        // would fire." A simple NoneOf can't express this because
        // the stop condition spans two characters.
        var closeMarker = AllOf(Grapheme('*'), Grapheme('/'));
        var blockComment = AllOf(
            Grapheme('/'),
            Grapheme('*'),
            ZeroOrMore(AllOf(Not(closeMarker), AnyToken())),
            closeMarker);

        var result = blockComment.Parse("/* body with * inside but not-the-end */",
            new ParseOptions { PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(),
            Is.EqualTo("/* body with * inside but not-the-end */"));
    }

    [Test]
    public void Peek_guards_a_branch_without_consuming_its_lookahead()
    {
        // Peek is useful for disambiguating overlapping prefixes without
        // committing to the disambiguated branch. Here "if" and "iffy"
        // share a prefix. Peek(Not(letter)) confirms the keyword really
        // ends after "if" before the caller commits.
        var keywordIf = AllOf(
            Grapheme('i'),
            Grapheme('f'),
            Peek(Not(OneOf(TokenSet.Letters))));

        var justIfResult = AllOf(keywordIf, ZeroOrMore(AnyToken())).Parse("if x");
        Assert.That(justIfResult.Success, Is.True, justIfResult.ErrorMessage);

        var iffyResult = keywordIf.Parse("iffy");
        Assert.That(iffyResult.Success, Is.False,
            "keywordIf must not match 'iffy' because 'f' is followed by a letter");
    }
}
