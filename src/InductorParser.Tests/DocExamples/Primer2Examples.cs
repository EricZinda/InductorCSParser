using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests.DocExamples;

// Verifies the runnable code examples in docs/primer2.md (the INI-style
// config grammar, the tree walker, error handling, and Unicode-aware
// error positions) against their documented behavior. Each test mirrors
// a code block from the doc and asserts what the doc claims.
[TestFixture]
public class Primer2Examples
{
    // The grammar exactly as written in primer2.md "The grammar:" block.
    // Returns the named rules a test wants to assert against, since the
    // doc walker uses references to `section`, `keyValue`, `key`, etc.
    private static (Rule config, Rule section, Rule keyValue, Rule key,
                    Rule value, Rule integerValue) BuildGrammar()
    {
        var lineEndRunes = TokenSet.LineTerminators;
        // Every single-rune whitespace, line terminators included, for
        // NoneOf stop sets. TokenSet.InlineWhitespace is intra-line only,
        // so unioning with lineEndRunes restores "any whitespace rune."
        var anySpaceRunes = TokenSet.InlineWhitespace | lineEndRunes;

        var name = OneOrMore(NoneOf(TokenSet.Runes("]") | anySpaceRunes)).As("name");
        var key = OneOrMore(NoneOf(TokenSet.Runes("=") | anySpaceRunes)).As("key");

        var section = And(Token('['), name, Token(']'), Optional(InlineWhitespace()), EndOfLine())
            .As("section");

        var quotedString = And(
            Token('"'),
            ZeroOrMore(NoneOf(TokenSet.Runes("\"") | lineEndRunes)),
            Token('"')).As("quotedString");

        var bareWord = OneOrMore(NoneOf(anySpaceRunes | TokenSet.Runes("\""))).As("bareWord");

        var floatValue = Float().As("float");
        var integerValue = Integer().As("integer");

        var value = Or(floatValue, integerValue, quotedString, bareWord).As("value");

        var keyValue = And(key, Optional(InlineWhitespace()), Token('='), Optional(InlineWhitespace()),
                             value, Optional(InlineWhitespace()), EndOfLine())
            .As("keyValue");

        var blankLine = And(Optional(InlineWhitespace()), EndOfLine());

        var line = Or(section, keyValue, blankLine);
        var config = And(ZeroOrMore(line), Eof()).As("config");

        return (config, section, keyValue, key, value, integerValue);
    }

    // primer2.md "What the tree looks like": the doc claims that running
    //   config.Parse("[server]\nhost = \"localhost\"\nport = 8080\n")
    // produces a tree with config / section(name=server) / two keyValue
    // children. The tree shows the [], =, and quote tokens are all gone
    // (Delete flatten), and value carries one named typed child each.
    [Test]
    public void Tree_shape_matches_doc_after_flatten()
    {
        var (config, section, keyValue, key, _, integerValue) = BuildGrammar();

        var result = config.Parse("[server]\nhost = \"localhost\"\nport = 8080\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var root = result.Tree!;
        Assert.That(root.Children.Count, Is.EqualTo(3),
            "config has section + 2 keyValue children after flatten");

        var sectionSym = root.Children[0];
        Assert.That(sectionSym.Is(section), Is.True);
        Assert.That(sectionSym.Children[0].ToString(), Is.EqualTo("server"));

        var hostKeyValue = root.Children[1];
        Assert.That(hostKeyValue.Is(keyValue), Is.True);
        Assert.That(hostKeyValue.Children[0].ToString(), Is.EqualTo("host"));
        // value's typed child is a quotedString carrying just the body.
        var hostValue = hostKeyValue.Children[1];
        Assert.That(hostValue.Children[0].ToString(), Is.EqualTo("localhost"));

        var portKeyValue = root.Children[2];
        Assert.That(portKeyValue.Is(keyValue), Is.True);
        Assert.That(portKeyValue.Children[0].ToString(), Is.EqualTo("port"));
        var portValue = portKeyValue.Children[1];
        Assert.That(portValue.Children[0].Is(integerValue), Is.True);
        Assert.That(portValue.Children[0].ToString(), Is.EqualTo("8080"));
    }

    // primer2.md "Walking the tree": the FindSetting walker the doc
    // shows. Verbatim from the doc, with `section` and `keyValue` passed
    // in as parameters since the doc's local references to them rely on
    // closure scope.
    private static Symbol? FindSetting(Symbol config, Rule section, Rule keyValue,
                                       string sectionName, string keyName)
    {
        string? currentSection = null;
        foreach (var child in config.Children)
        {
            if (child.Is(section))
            {
                currentSection = child.Children[0].ToString();
            }
            else if (child.Is(keyValue) && currentSection == sectionName)
            {
                string thisKey = child.Children[0].ToString();
                if (thisKey == keyName)
                    return child.Children[1];
            }
        }
        return null;
    }

    // primer2.md "To read [server]/port as an integer:" the doc shows a
    // FindSetting + integer-typed-child + int.Parse pattern that should
    // produce 8080 for the server/port value.
    [Test]
    public void Walker_finds_typed_integer_value()
    {
        var (config, section, keyValue, _, _, integerValue) = BuildGrammar();

        var result = config.Parse("[server]\nhost = \"localhost\"\nport = 8080\n");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var portValue = FindSetting(result.Tree!, section, keyValue, "server", "port");
        Assert.That(portValue, Is.Not.Null);

        var typed = portValue!.Children[0];
        Assert.That(typed.Is(integerValue), Is.True);
        int port = int.Parse(typed.ToString(), CultureInfo.InvariantCulture);
        Assert.That(port, Is.EqualTo(8080));
    }

    // primer2.md "If you want every section regardless of context": the
    // FindAll example. Returns every section node in the tree.
    [Test]
    public void FindAll_returns_every_section()
    {
        var (config, section, _, _, _, _) = BuildGrammar();

        var result = config.Parse("[server]\nhost = \"localhost\"\nport = 8080\n\n[client]\ntimeout = 30\n");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var sections = result.Tree!.FindAll(section).ToList();
        Assert.That(sections.Count, Is.EqualTo(2));
        Assert.That(sections[0].Children[0].ToString(), Is.EqualTo("server"));
        Assert.That(sections[1].Children[0].ToString(), Is.EqualTo("client"));
    }

    // primer2.md "Walking the tree with LINQ": the four LINQ entry points
    // the doc shows (Children, Walk, FindAll, Flatten). The last one, the
    // "Flattened tree as a list of every Symbol" line, used to read
    // `result.Tree!.FlattenInto().OfType<Symbol>()`, which doesn't compile:
    // Symbol.FlattenInto takes a List<Symbol> and returns void. The list-
    // returning method is Flatten(). This test runs all four so the snippet
    // can't drift back.
    [Test]
    public void Walking_the_tree_with_linq_entry_points()
    {
        var (config, section, _, _, _, integerValue) = BuildGrammar();

        var result = config.Parse("[server]\nhost = \"localhost\"\nport = 8080\n");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // Direct children only (no recursion)
        var directSections = result.Tree!.Children.Where(c => c.Is(section)).ToList();
        Assert.That(directSections.Count, Is.EqualTo(1));

        // Entire subtree, pre-order walk
        var integers = result.Tree!.Walk().Where(s => s.Is(integerValue)).ToList();
        Assert.That(integers.Count, Is.EqualTo(1));
        Assert.That(integers[0].ToString(), Is.EqualTo("8080"));

        // Flattened tree as a list of every Symbol. Flatten() returns
        // IReadOnlyList<Symbol>, so it's a direct LINQ target exactly as
        // the surrounding prose claims.
        var flattened = result.Tree!.Flatten().OfType<Symbol>().ToList();
        Assert.That(flattened.Count, Is.GreaterThan(0));
        Assert.That(flattened.All(s => s is Symbol), Is.True);
    }

    // primer2.md "When the parse fails": the doc claims that
    //   config.Parse("[server]\nport oops\n")
    // fails at "line 1, column 5" with an error message that surfaces
    // somewhere in the parse. (Language Server Protocol: 0-based line means
    // line 1 in the doc corresponds to ErrorLine == 1.)
    [Test]
    public void Parse_failure_reports_line_and_column()
    {
        var (config, _, _, _, _, _) = BuildGrammar();

        var result = config.Parse("[server]\nport oops\n");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorLine, Is.EqualTo(1),
            "Second line of input is line 1 in Language Server Protocol-style 0-based lines");
        Assert.That(result.ErrorColumn, Is.EqualTo(5),
            "The space-then-'o' fails where the '=' should be at col 5");
        Assert.That(result.ErrorMessage, Is.Not.Empty);
    }

    // primer2.md "To upgrade it, attach .WithError(...) to the rule
    // that's most likely to be where the user went wrong". Re-runs the
    // same input with WithError and asserts the custom message surfaces.
    [Test]
    public void WithError_message_surfaces_on_missing_equals()
    {
        var lineEndRunes = TokenSet.LineTerminators;
        var anySpaceRunes = TokenSet.InlineWhitespace | lineEndRunes;

        var name = OneOrMore(NoneOf(TokenSet.Runes("]") | anySpaceRunes)).As("name");
        var key = OneOrMore(NoneOf(TokenSet.Runes("=") | anySpaceRunes)).As("key");

        var section = And(Token('['), name, Token(']'), Optional(InlineWhitespace()), EndOfLine())
            .As("section");

        var quotedString = And(
            Token('"'),
            ZeroOrMore(NoneOf(TokenSet.Runes("\"") | lineEndRunes)),
            Token('"')).As("quotedString");

        var bareWord = OneOrMore(NoneOf(anySpaceRunes | TokenSet.Runes("\""))).As("bareWord");

        var value = Or(
            Float().As("float"),
            Integer().As("integer"),
            quotedString,
            bareWord).As("value");

        var keyValue = And(
            key,
            Optional(InlineWhitespace()),
            Token('=').WithError("Expected '=' after the setting name"),
            Optional(InlineWhitespace()),
            value,
            Optional(InlineWhitespace()),
            EndOfLine())
            .As("keyValue");

        var blankLine = And(Optional(InlineWhitespace()), EndOfLine());
        var line = Or(section, keyValue, blankLine);
        var config = And(ZeroOrMore(line), Eof()).As("config");

        var result = config.Parse("[server]\nport oops\n");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Expected '=' after the setting name"));
    }

    // primer2.md "ParseOptions carries a set of templates with {name}-
    // style placeholders". The doc swaps the catch-all default messages
    // for French versions and shows the resulting ErrorMessage. Verifies
    // both the rendered output and the placeholder substitution.
    [Test]
    public void Templates_render_French_default_message()
    {
        var (config, _, _, _, _, _) = BuildGrammar();

        var options = new ParseOptions
        {
            PositionalErrorTemplate = $"Erreur {UnicodeExamples.LatinSmallAWithGraveGrapheme} la position {{charIndex}}: caract{UnicodeExamples.LatinSmallEWithGraveGrapheme}re '{{character}}' inattendu.",
            EndOfInputErrorTemplate = $"Fin d'entr{UnicodeExamples.LatinEAcutePrecomposedGrapheme}e inattendue.",
        };

        var result = config.Parse("[server]\nport oops\n", options);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo($"Erreur {UnicodeExamples.LatinSmallAWithGraveGrapheme} la position 14: caract{UnicodeExamples.LatinSmallEWithGraveGrapheme}re 'o' inattendu."));
    }

    // primer2.md "Unicode and where the error actually is". The doc claims
    // that for input "[\u{family}]\nport oops\n", with the family ZWJ
    // emoji at the start, the error position diverges across units:
    //   ErrorCharIndex      == 16 (UTF-16 code units)
    //   ErrorTokenIndex  == 9  (graphemes)
    //   ErrorLine           == 1
    //   ErrorColumn         == 5  (UTF-16 chars, Language Server Protocol)
    [Test]
    public void Family_emoji_position_divergence_matches_doc()
    {
        var (config, _, _, _, _, _) = BuildGrammar();

        // 👨‍👩‍👧 is U+1F468 ZWJ U+1F469 ZWJ U+1F467: 5 runes, 8 UTF-16 chars,
        // 1 grapheme. The full input is "[" + family + "]\nport oops\n".
        string family = UnicodeExamples.FamilyManWomanGirlGrapheme;
        string input = "[" + family + "]\nport oops\n";

        var result = config.Parse(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(16),
            "8 UTF-16 chars for family + '[' + ']' + '\\n' + 4 chars 'port' + ' ' = 16");
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(9),
            "1 grapheme for family + '[' + ']' + '\\n' + 4 graphemes 'port' + ' ' = 9");
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(5));
    }

    // primer2.md "you might want to disallow duplicate section names":
    // the SourceRange-based duplicate detection example.
    [Test]
    public void Duplicate_section_detection_uses_SourceRange()
    {
        var (config, section, _, _, _, _) = BuildGrammar();

        var result = config.Parse("[server]\nport = 80\n\n[server]\nport = 81\n");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var seen = new HashSet<string>();
        Symbol? offending = null;
        foreach (var sectionSymbol in result.Tree!.FindAll(section))
        {
            string sectionName = sectionSymbol.Children[0].ToString();
            if (!seen.Add(sectionName))
            {
                offending = sectionSymbol;
                break;
            }
        }

        Assert.That(offending, Is.Not.Null);
        // "Line 4" in the doc's 1-based human form is line 3 in the 0-based
        // Language Server Protocol convention.
        int humanLine = offending!.SourceRange!.Value.Start.Line + 1;
        Assert.That(humanLine, Is.EqualTo(4));
    }
}
