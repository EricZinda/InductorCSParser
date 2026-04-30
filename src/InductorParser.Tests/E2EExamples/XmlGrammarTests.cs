using System.Collections.Generic;
using InductorParser;
using NUnit.Framework;
using static InductorParser.Tests.TestHelpers;

namespace InductorParser.Tests;

// XML corpus tests. The C++ InductorParser ships XmlCompiler
// (src/FXPlatform/Languages/XmlCompiler.h), which parses XML with the
// same HTML grammar and then walks the AST to validate that every start
// tag matches its end tag. The grammar itself doesn't enforce tag
// matching. That's an AST-walker responsibility that belongs in a
// compiler, not a parser.
//
// So this fixture just validates that XML-shaped inputs parse (or fail
// to parse) under HtmlGrammar.Document. We reuse HtmlGrammar rather
// than defining a separate XmlGrammar, mirroring the C++ decision to
// share the grammar and add validation in the compiler layer.
//
// One scope gap inherited from the C++ port: `<?xml ... ?>` prolog. The
// C++ defines a ProcessingInstructionRule but never wires it into
// HtmlDocumentRule, so a document that starts with "<?xml version='1.0'?>"
// wouldn't parse under the C++ HTML grammar either. Left off the
// valid-corpus here for the same reason.
//
// TagName has been widened from the literal C++ port (see the note on
// HtmlGrammar.TagName) so realistic XML fixtures (underscores, hyphens,
// dots) parse.
[TestFixture]
public class XmlGrammarTests
{
    private static readonly string[] ValidXmlDocuments =
    {
        // Minimal.
        "<note></note>",
        "<a></a>",

        // Nested.
        "<note><to>Tove</to></note>",
        "<note><to>Tove</to><from>Jani</from></note>",

        // Deeply nested.
        "<bookstore><book><title>X</title><author>Y</author></book></bookstore>",

        // Attributes.
        "<note id=\"501\"></note>",
        "<note id='501'></note>",
        "<note id=501></note>",
        "<note id=\"501\" date=\"2024-01-01\"></note>",

        // Self-closing.
        "<br />",
        "<note id=\"501\" />",

        // Comments.
        "<!-- header --><note></note>",
        "<note><!-- inner -->text</note>",

        // Whitespace around the root.
        "  <note></note>  ",
        "\n<note></note>\n",

        // Mixed content.
        "<note>hello <b>bold</b> world</note>",

        // Simple w3schools-style example with the kind of tag names
        // real XML uses.
        "<breakfast_menu><food><name>Belgian Waffles</name><price>$5.95</price></food></breakfast_menu>",
        "<my-element attr=\"x\"></my-element>",
        "<ns.name></ns.name>",
        "<_leading_underscore></_leading_underscore>",
    };

    private static readonly string[] InvalidXmlDocuments =
    {
        "",
        "   ",
        "plain text with no element",

        // Unclosed element.
        "<note><to>Tove</to>",
        "<note>",

        // Bare text outside any element.
        "text <note></note>",

        // Multiple roots.
        "<a></a><b></b>",

        // Processing instruction unclosed.
        "<?xml version=\"1.0\"?",

        // Unclosed attribute value.
        "<note id=\"501></note>",
        "<note id='501></note>",

        // Malformed angle brackets.
        "<",
        "<>",
        "<note></>",

        // Tag name must start with a letter or underscore, not a digit
        // or hyphen or dot. The widened TagName still keeps the same
        // leading-char restriction as the XML spec.
        "<1foo></1foo>",
        "<-foo></-foo>",
        "<.foo></.foo>",
    };

    [Test]
    public void Valid_xml_documents_parse_under_html_grammar()
    {
        var rejected = new List<string>();
        foreach (var doc in ValidXmlDocuments)
        {
            var result = HtmlGrammar.Document.Parse(doc);
            if (!result.Success)
                rejected.Add($"  {Display(doc)} (col {result.ErrorCharIndex}: {result.ErrorMessage})");
        }
        if (rejected.Count > 0)
            Assert.Fail(
                $"Grammar rejected {rejected.Count} valid XML document(s):\n"
                + string.Join("\n", rejected));
    }

    [Test]
    public void Invalid_xml_documents_rejected()
    {
        var accepted = new List<string>();
        foreach (var doc in InvalidXmlDocuments)
        {
            if (HtmlGrammar.Document.Parse(doc).Success)
                accepted.Add(Display(doc));
        }
        if (accepted.Count > 0)
            Assert.Fail(
                $"Grammar accepted {accepted.Count} invalid XML document(s):\n  "
                + string.Join("\n  ", accepted));
    }

    // Documenting the tag-mismatch caveat. A document like "<a></b>" is
    // structurally well-formed (an element with a start tag, no body, and
    // an end tag) but the names disagree. The grammar accepts it because
    // it doesn't compare the two TagName tokens. That check is the
    // XmlCompiler's job in the C++ port.
    [Test]
    public void Grammar_accepts_tag_name_mismatch_as_designed()
    {
        Assert.That(HtmlGrammar.Document.Parse("<a></b>").Success, Is.True,
            "tag-name matching is an AST-walker check, not a grammar check.");
    }

}
