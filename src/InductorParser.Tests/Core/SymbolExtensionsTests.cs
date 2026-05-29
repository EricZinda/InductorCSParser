using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

// Tests for Symbol.PrintTree, the extension that renders a raw
// Symbol tree using a Rule for name resolution. For ParseResult-
// driven debug output, see ParseResultNamingTests.
[TestFixture]
public class SymbolExtensionsTests
{
    [Test]
    public void PrintTree_renders_named_root_and_character_leaves()
    {
        var word = OneOrMore(OneOf(TokenSet.Letters)).As("word").Flatten(FlattenType.Preserve);
        var result = word.Parse("hi");
        Assert.That(result.Success, Is.True);

        string expected =
            "word: \"hi\"\n" +
            "  'h'\n" +
            "  'i'\n";

        Assert.That(result.Tree!.PrintTree(word), Is.EqualTo(expected));
    }

    [Test]
    public void PrintTree_renders_unnamed_composite_with_class_name()
    {
        // Anonymous And rule, no user name, so the root renders with
        // the class-derived "And" label. Token leaves default to
        // FlattenType.Delete and are filtered at parse time under normal
        // parsing. PreserveAllSymbols keeps them so PrintTree sees
        // a shape matching the grammar one-to-one.
        var pair = And(Token('a'), Token('1'));
        var result = pair.Parse("a1", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(result.Success, Is.True);

        string expected =
            "And: \"a1\"\n" +
            "  'a'\n" +
            "  '1'\n";

        Assert.That(result.Tree!.PrintTree(pair), Is.EqualTo(expected));
    }

    [Test]
    public void PrintTree_indents_nested_subtrees()
    {
        // Two levels of named And wrappers so the printed tree has real
        // depth beyond just a root plus leaves. Token leaves default to
        // FlattenType.Delete. PreserveAllSymbols keeps them so the
        // printed tree shows both the composites and their Token children.
        var first = And(Token('a'), Token('b')).As("first");
        var second = And(Token('c'), Token('d')).As("second");
        var pair = And(first, second).As("pair");
        var result = pair.Parse("abcd", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(result.Success, Is.True);

        string expected =
            "pair: \"abcd\"\n" +
            "  first: \"ab\"\n" +
            "    'a'\n" +
            "    'b'\n" +
            "  second: \"cd\"\n" +
            "    'c'\n" +
            "    'd'\n";

        Assert.That(result.Tree!.PrintTree(pair), Is.EqualTo(expected));
    }

    [Test]
    public void PrintTree_renders_named_single_rune_Token_with_user_supplied_name()
    {
        // A named single-rune Token has a leaf whose Id is in the
        // character range (0..0x10FFFF) but whose user-supplied name
        // differs from the rune's own text. PrintTree uses the long
        // `<name>: "<text>"` form for character leaves whose NameOf
        // returns something other than the rune text.
        var aChar = Token('a').As("aChar");
        var result = aChar.Parse("a");
        Assert.That(result.Success, Is.True);

        Assert.That(result.Tree!.PrintTree(aChar), Is.EqualTo("aChar: \"a\"\n"));
    }

    [Test]
    public void PrintTree_unnamed_single_rune_Token_keeps_compact_rune_form()
    {
        // The compact `'c'` form is the right shape for unnamed single-
        // rune Tokens: the leaf's name and matched text are both the rune
        // itself, so a separate `c: "c"` would be redundant.
        var rule = Token('a').Preserve();
        var result = rule.Parse("a");
        Assert.That(result.Success, Is.True);

        Assert.That(result.Tree!.PrintTree(rule), Is.EqualTo("'a'\n"));
    }

    [Test]
    public void PrintTree_renders_invalid_scalar_id_as_U_FFFD()
    {
        // A leaf whose Id lands in the character range numerically but
        // isn't a valid Unicode scalar (a surrogate half in 0xD800..
        // 0xDFFF) renders as the Unicode replacement character. Locks
        // the fallback against drift to a different placeholder.
        var rule = Token('a').Preserve();
        rule.Compile();
        var staleLeaf = new Symbol(new SymbolId(0xD800), FlattenType.Preserve, "a".AsMemory());

        Assert.That(staleLeaf.PrintTree(rule), Is.EqualTo($"'{UnicodeExamples.ReplacementCharacterText}'\n"));
    }

    [Test]
    public void PrintTree_escapes_control_character_rune_in_short_form()
    {
        // A leaf rule on a single control character (LF, U+000A) carries
        // id 0x0A from GraphemeRule's auto-rune-id pass. The compact
        // `'c'` form would splice a literal newline into the printed
        // tree, breaking the one-leaf-per-line layout that lines up with
        // the indent-per-depth shape every other node uses. Mirrors
        // TokenSet.ToString's AppendGraphemeForDisplay: the rune renders
        // as `'U+XXXX'` so the tree dump stays one line per node.
        var rule = Token('\n').Preserve();
        var result = rule.Parse("\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.PrintTree(rule), Is.EqualTo("'U+000A'\n"));
    }

    [Test]
    public void PrintTree_renders_long_form_when_user_name_matches_rune_text()
    {
        // Edge case the old "charName != runeText" string-compare hack
        // couldn't distinguish from an anonymous rune leaf: a user who
        // explicitly named the rule to the rune's own text. Routing
        // through Rule.UserNameOf instead reads the user-supplied flag
        // directly, so the long `name: "text"` form fires the moment
        // .As(...) is on the rule, regardless of which string the name
        // happens to be.
        var rule = Token('a').As("a");
        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.PrintTree(rule), Is.EqualTo("a: \"a\"\n"));
    }

    [Test]
    public void PrintTree_escapes_line_separator_rune_in_short_form()
    {
        // U+2028 LINE SEPARATOR is in Unicode category Zl, not Cc, so a
        // `char.IsControl`-only check would miss it. The lexer treats
        // it as a line terminator (see TokenSet.LineTerminators), so a
        // grammar that uses it as a Preserve'd Token can land it as a
        // leaf in the printed tree. Render as `'U+2028'` for the same
        // line-breaks-the-output reason as LF / CR. Escape form keeps
        // the test source free of a literal line separator that would
        // split the C# source line.
        var rule = Token('\u2028').Preserve();
        var result = rule.Parse("\u2028");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.PrintTree(rule), Is.EqualTo("'U+2028'\n"));
    }

    [Test]
    public void PrintTree_escapes_control_character_in_named_leaf_long_form()
    {
        // A `.As("nl")` on a Token('\n') routes the leaf through the
        // long-form `name: "text"` branch instead of the compact `'c'`
        // branch. The matched text is "\n" and goes into the rendered
        // line as the quoted span, so the LF splits the line in two
        // unless the long form runs the same Cc / Zl / Zp escape the
        // short form already does.
        var rule = Token('\n').As("nl");
        var result = rule.Parse("\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.PrintTree(rule), Is.EqualTo("nl: \"U+000A\"\n"));
    }

    [Test]
    public void PrintTree_escapes_control_character_in_composite_long_form()
    {
        // A composite Symbol's ToString concatenates its leaves' matched
        // text. When that text spans a control or line-separator char,
        // the long-form `name: "text"` line carries the raw char into
        // the output and the layout collapses, even though the child
        // leaves themselves render correctly (the short-form fix from
        // 2026-05-27 escapes the LF child leaf).
        var body = OneOrMore(AnyToken()).As("body");
        var result = body.Parse("a\nb");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        string expected =
            "body: \"aU+000Ab\"\n" +
            "  'a'\n" +
            "  'U+000A'\n" +
            "  'b'\n";
        Assert.That(result.Tree!.PrintTree(body), Is.EqualTo(expected));
    }
}
