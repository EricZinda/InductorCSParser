using System.Collections.Generic;
using InductorParser;
using NUnit.Framework;
using static InductorParser.Tests.TestHelpers;

namespace InductorParser.Tests;

// End-to-end corpus tests for CssGrammar. No reference regex exists for
// CSS (it's a full language, not a regex-replaceable pattern), so these
// tests use valid/invalid corpora instead of cross-engine equivalence
// like BacklogGrammarTests and ChordGrammarTests do. Every string in
// ValidDocuments must parse. Every string in InvalidDocuments must fail.
[TestFixture]
public class CssGrammarTests
{
    private static readonly string[] ValidDocuments =
    {
        // Empty / whitespace-only documents. The grammar allows this
        // because the top-level is ZeroOrMore rules.
        "",
        "   ",
        "\n\n",
        "/* just a comment */",
        "/* c1 */ /* c2 */",

        // Minimal rule.
        "a{}",
        "a { }",
        "a\n{\n}",

        // Simple selectors.
        "p { color: red; }",
        ".classname { color: red; }",
        "#idname { color: red; }",
        "* { margin: 0; }",
        "a:hover { color: blue; }",
        "a::before { color: blue; }",
        "input:not { color: blue; }",

        // Simple selector sequences.
        "p.classy { color: red; }",
        "div#main { color: red; }",
        "a.nav:hover { color: blue; }",

        // Descendant combinator.
        "div p { color: red; }",
        "body main article p { color: red; }",

        // Selector lists.
        "h1, h2, h3 { color: red; }",
        "a, .link, #main { color: red; }",

        // Multiple declarations.
        "p { color: red; background: blue; }",
        "p { color: red; margin: 0; padding: 10px; }",

        // Values: hex color, short and long.
        "p { color: #fff; }",
        "p { color: #FFFFFF; }",
        "p { color: #abc; }",
        "p { color: #123456; }",

        // Values: rgba.
        "p { color: rgba(255, 255, 255, 1.0); }",
        "p { color: rgba(0,0,0,0.5); }",
        "p { color: rgba( 10 , 20 , 30 , 0.25 ); }",

        // Values: url, quoted and unquoted.
        "p { background: url(\"foo.png\"); }",
        "p { background: url(foo.png); }",
        "p { background: url(/images/x.svg); }",

        // Values: length.
        "p { margin: 10px; }",
        "p { margin: 1.5em; }",
        "p { width: 50%; }",
        "p { font-size: 12pt; }",
        "p { margin: 0; }",

        // Values: strings.
        "p { font-family: \"Helvetica\"; }",
        "p { font-family: 'Courier New'; }",
        "p { content: \"a \\\" b\"; }",

        // Values: comma-separated list.
        "p { font-family: \"Helvetica\", Arial, sans-serif; }",

        // Multiple rules.
        "a { color: red; }\nb { color: blue; }",
        ".header { margin: 0; }\n.footer { padding: 10px; }",

        // Block comment inside whitespace.
        "/* head */ p { color: red; } /* tail */",
        "p /* mid */ { color: /* inline */ red; }",

        // Empty declaration (just ";") is allowed by the grammar.
        // C++ made the property:value part optional.
        "p { ; }",
        "p { color: red; ; }",

        // Permissive multi-value declarations. The value list is a
        // OneOrMore with an optional comma, so trailing tokens that
        // don't form a single tight value are just read as additional
        // values. "10xx" parses as "10" (number) followed by "xx"
        // (identifier), which is a real CSS pattern (e.g. "1px solid").
        // Hex colors have a boundary check that prevents the same
        // split for "#fffff" (see InvalidDocuments).
        "p { margin: 10xx; }",
    };

    private static readonly string[] InvalidDocuments =
    {
        // Missing braces.
        "p",
        "p color: red;",

        // Missing semicolon on a non-empty declaration.
        "p { color: red }",

        // Unclosed block.
        "p { color: red;",

        // Unclosed comment.
        "/* never closed",

        // Unclosed string.
        "p { content: \"never closed; }",

        // Hex with wrong digit count. The Peek(Not(hex)) boundary in
        // ValueColorHex means an N-digit run with N not in {3, 6} has
        // no arm that can terminate on a hex-digit boundary.
        "p { color: #ff; }",
        "p { color: #fffff; }",
        "p { color: #fffffff; }",

        // Bad rgba shape. Note that "rgba" as a bare token still matches
        // Identifier, but the trailing "(...)" has no production so the
        // surrounding declaration can't close with ';'.
        "p { color: rgba(1,2,3); }",        // missing alpha
        "p { color: rgba(1,2,3,4); }",      // alpha must be a Float
        "p { color: rgba(1 2 3 0.5); }",    // no commas

        // Selector must lead with something. A bare combinator doesn't.
        "  > x { color: red; }",

        // Top-level garbage.
        "@@@",

        // Close brace with no open.
        "p } color: red; {",
    };

    [Test]
    public void Valid_documents_all_parse()
    {
        var rejected = new List<string>();
        foreach (var doc in ValidDocuments)
        {
            var result = CssGrammar.Document.Parse(doc);
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
            if (CssGrammar.Document.Parse(doc).Success)
                accepted.Add(Display(doc));
        }
        if (accepted.Count > 0)
            Assert.Fail(
                $"Grammar accepted {accepted.Count} invalid document(s):\n  "
                + string.Join("\n  ", accepted));
    }

    // A couple of targeted fragment tests so a regression in a single
    // sub-rule gives a more pointed error than "the whole document
    // failed."
    [Test]
    public void Block_comment_fragment_accepts_simple_and_rejects_unterminated()
    {
        Assert.That(CssGrammar.BlockComment.Parse("/* hi */").Success, Is.True);
        Assert.That(CssGrammar.BlockComment.Parse("/* ** inside */").Success, Is.True);
        Assert.That(CssGrammar.BlockComment.Parse("/* unterminated").Success, Is.False);
        Assert.That(CssGrammar.BlockComment.Parse("not a comment").Success, Is.False);
    }

    [Test]
    public void Hex_color_requires_3_or_6_digits()
    {
        var grammar = And(CssGrammar.ValueColorHex, Rules.Eof());
        Assert.That(grammar.Parse("#fff").Success, Is.True);
        Assert.That(grammar.Parse("#FFFFFF").Success, Is.True);
        Assert.That(grammar.Parse("#ff").Success, Is.False);
        Assert.That(grammar.Parse("#fffff").Success, Is.False);
        Assert.That(grammar.Parse("#fffffff").Success, Is.False);
    }

    [Test]
    public void Identifier_rejects_leading_digit()
    {
        var grammar = And(CssGrammar.Identifier, Rules.Eof());
        Assert.That(grammar.Parse("foo").Success, Is.True);
        Assert.That(grammar.Parse("foo-bar").Success, Is.True);
        Assert.That(grammar.Parse("_foo").Success, Is.True);
        Assert.That(grammar.Parse("1foo").Success, Is.False);
    }

    private static Rule And(Rule a, Rule b) => Rules.And(a, b);

}
