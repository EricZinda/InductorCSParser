using System.Collections.Generic;
using InductorParser;
using NUnit.Framework;
using static InductorParser.Tests.TestHelpers;

namespace InductorParser.Tests;

// End-to-end corpus tests for HtmlGrammar. Same structure as
// CssGrammarTests: valid corpus must parse, invalid corpus must fail.
[TestFixture]
public class HtmlGrammarTests
{
    private static readonly string[] ValidDocuments =
    {
        // Minimal element.
        "<p></p>",
        "<div></div>",

        // Text content.
        "<p>hello</p>",
        "<p>hello world</p>",
        "<p>line with ! and ? and : punctuation</p>",

        // Attributes in every flavor (all using void-tag closing since
        // an "attribute-only" start tag on a non-void element would need
        // a matching end tag).
        "<input disabled />",
        "<input type=text />",                      // unquoted
        "<input type='text' />",                    // single-quoted
        "<input type=\"text\" />",                  // double-quoted
        "<input type=\"text\" disabled />",         // mixed

        // Whitespace between tag, attr, "/" and ">".
        "<br />",
        "<br/>",
        "<br   />",
        "<hr\n/>",

        // Nested elements.
        "<div><p>inner</p></div>",
        "<ul><li>a</li><li>b</li></ul>",
        "<div><p><span>deep</span></p></div>",

        // Mixed text and elements.
        "<p>hello <b>bold</b> world</p>",
        "<div>pre<br/>post</div>",

        // Comments at document top and inside.
        "<!-- top comment --><p></p>",
        "<p><!-- inner --></p>",
        "<!-- a -->\n<!-- b -->\n<p></p>",

        // Whitespace around the root.
        "   <p></p>",
        "<p></p>   ",
        "\n\n<p></p>\n",

        // Style block: non-replaceable, contents ignored until </style>.
        "<style>body { color: red; }</style>",
        "<style type=\"text/css\">a { color: red; }</style>",
        "<style>/* this is legal */ a { } /* ok */</style>",
    };

    private static readonly string[] InvalidDocuments =
    {
        // Empty input.
        "",
        "   ",

        // Text outside any element.
        "hello",
        "text before <p></p>",

        // Mismatched close tag. The grammar itself only checks structural
        // form, not tag-name matching, but EndTag expects "</TagName>",
        // and if the TagName doesn't appear at all or is just wrong-cased
        // nothing closes the outer element. However, this grammar IS
        // tolerant of tag-name mismatches (see XmlGrammarTests for the
        // tag-match version). So this entry is here to guard true
        // structural errors, not name mismatches.
        "<p>unclosed",
        "<p></p",

        // Unclosed comment.
        "<!-- never closed",

        // Void element with extra junk.
        "<br / >",     // space between / and > is not allowed in void
        "<br>",         // normal start needs a matching end; no </br>

        // Attribute quote mismatch.
        "<p a=\"x'></p>",
        "<p a='x\"></p>",

        // Bare closing tag with no opening.
        "</p>",

        // Completely malformed.
        "<",
        "<>",
        "< p></p>",     // space after < isn't a tag name

        // Two roots is allowed by this grammar? Check. The grammar is
        // ZeroOrMore(ws|comment) Element OptionalWs Eof, so only one
        // element. So this must fail.
        "<p></p><p></p>",
    };

    [Test]
    public void Valid_documents_all_parse()
    {
        var rejected = new List<string>();
        foreach (var doc in ValidDocuments)
        {
            var result = HtmlGrammar.Document.Parse(doc);
            if (!result.Success)
                rejected.Add($"  {Display(doc)} (col {result.ErrorCharIndex}: {result.ErrorMessage})");
        }
        if (rejected.Count > 0)
            Assert.Fail(
                $"Grammar rejected {rejected.Count} valid document(s):\n"
                + string.Join("\n", rejected));
    }

    [Test]
    public void Invalid_documents_all_rejected()
    {
        var accepted = new List<string>();
        foreach (var doc in InvalidDocuments)
        {
            if (HtmlGrammar.Document.Parse(doc).Success)
                accepted.Add(Display(doc));
        }
        if (accepted.Count > 0)
            Assert.Fail(
                $"Grammar accepted {accepted.Count} invalid document(s):\n  "
                + string.Join("\n  ", accepted));
    }

    [Test]
    public void Comment_fragment_handles_simple_and_multiline()
    {
        Assert.That(HtmlGrammar.Comment.Parse("<!-- hi -->").Success, Is.True);
        Assert.That(HtmlGrammar.Comment.Parse("<!-- one\ntwo\nthree -->").Success, Is.True);
        Assert.That(HtmlGrammar.Comment.Parse("<!-- unterminated").Success, Is.False);
    }

    [Test]
    public void Attribute_fragment_handles_all_four_flavors()
    {
        Assert.That(HtmlGrammar.Attribute.Parse("disabled").Success, Is.True);
        Assert.That(HtmlGrammar.Attribute.Parse("type=text").Success, Is.True);
        Assert.That(HtmlGrammar.Attribute.Parse("type='text'").Success, Is.True);
        Assert.That(HtmlGrammar.Attribute.Parse("type=\"text\"").Success, Is.True);
    }

    [Test]
    public void Style_block_ignores_html_looking_content()
    {
        // The body of <style> is non-replaceable: a "<span>" inside should
        // NOT be treated as a real element. If the grammar tried to parse
        // it as one, the body rule would never reach </style>.
        var doc = "<style>/* note: <span> is not a tag here */ a { }</style>";
        Assert.That(HtmlGrammar.Document.Parse(doc).Success, Is.True);
    }

}
