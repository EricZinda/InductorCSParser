using NUnit.Framework;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the runnable code examples in docs/Primer3.md (the todo-list
// grammar and its "Error Index" section). Each test mirrors a code block
// from the doc and asserts what the doc claims.
//
// Non-ASCII test data is built from hex code points (no raw glyphs in
// source) so the UnicodeLiteralCanary scanner stays happy.
[TestFixture]
public class Primer3Examples
{
    // U+20BB7, a supplementary-plane CJK ideograph: one token (one
    // grapheme), two UTF-16 chars. The doc uses it to show char/token
    // index divergence.
    private static readonly string RareKanji = char.ConvertFromUtf32(0x20BB7);
    // U+7530, a BMP CJK ideograph: one token, one char.
    private static readonly string Ta = char.ConvertFromUtf32(0x7530);

    // The grammar exactly as written in Primer3.md. Returns the named
    // `itemText` rule the "Error Index" example looks up.
    private static (Rule list, Rule itemText) BuildGrammar()
    {
        var priority = Or(Literal("top"),
                          Literal("med"),
                          Literal("low"))
                          .As("priority");

        var itemText = OneOrMore(And(Not(EndOfLine(eofIsEol: true)),
                                     AnyToken()))
                                     .As("itemText");

        var todoLine = And(Optional(InlineWhitespace()),
                           Token('['),
                           Optional(InlineWhitespace()),
                           priority,
                           Optional(InlineWhitespace()),
                           Token(']'),
                           Optional(InlineWhitespace()),
                           itemText,
                           EndOfLine(eofIsEol: true));

        var list = OneOrMore(todoLine);
        return (list, itemText);
    }

    // The root is OneOrMore(todoLine), a Flatten root: its children bubble
    // up to the top level, so result.Tree is null and a lookup has to go
    // through result.Find. Going through result.Tree throws here, which is
    // why the doc uses result.Find.
    [Test]
    public void Tree_is_null_on_a_flatten_root_so_lookups_use_result_Find()
    {
        var (list, itemText) = BuildGrammar();
        var result = list.Parse("[top] " + RareKanji + Ta + " fix\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree, Is.Null,
            "OneOrMore is a Flatten root, so its children bubble up and Tree is null");
        Assert.Throws<System.NullReferenceException>(() =>
        {
            var _ = result.Tree!.Find(itemText)!.SourceRange!.Value;
        });
    }

    // Primer3.md "Error Index": the two documented examples. The first
    // reports a failure position in chars (16) and tokens (15) on the
    // second line. The second reads the matched item text's SourceRange
    // via result.Find and claims a width of 7 chars / 6 tokens (the
    // supplementary kanji is one token but two chars).
    [Test]
    public void Error_index_and_sourcerange_examples_match_doc()
    {
        var (list, itemText) = BuildGrammar();

        var failing = list.Parse("[top] " + RareKanji + Ta + " broke\nBAD");
        Assert.That(failing.Success, Is.False);
        Assert.That(failing.ErrorCharIndex, Is.EqualTo(16));
        Assert.That(failing.ErrorTokenIndex, Is.EqualTo(15));

        var ok = list.Parse("[top] " + RareKanji + Ta + " fix\n");
        Assert.That(ok.Success, Is.True, ok.ErrorMessage);
        var range = ok.Find(itemText)!.SourceRange!.Value;
        Assert.That(range.End.CharIndex - range.Start.CharIndex, Is.EqualTo(7));
        Assert.That(range.End.TokenIndex - range.Start.TokenIndex, Is.EqualTo(6));
    }
}
