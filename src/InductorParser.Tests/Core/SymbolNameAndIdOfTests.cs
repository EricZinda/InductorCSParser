using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for the typed-AST-projection-friendly accessors:
//
//   * Symbol.Name      — the .As(string) name the rule was given, resolved
//                        through the parse-time ParseContext.
//   * Symbol.Is(string) — the string-keyed variant of Symbol.Is(Rule).
//   * Rule.IdOf(string) — name-to-SymbolId reverse lookup so a projection
//                        can cache the id once and dispatch on int compare.
//
// These three together let a tree walker dispatch on named productions
// without forcing the grammar to expose one public static Rule field per
// production. The matrix below covers the corners: named vs. unnamed
// rules, runes vs. composites, hand-built Symbols (no context), and the
// IdOf round-trip with NameOf.
[TestFixture]
public class SymbolNameAndIdOfTests
{
    // A small grammar reused by most cases. "letter" matches one letter;
    // "word" wraps OneOrMore(letter) so the parse tree has both a named
    // composite and a named leaf to interrogate.
    private static Rule BuildLetterAndWordGrammar(out Rule letter, out Rule word)
    {
        letter = OneOf(TokenSet.Letters).As("letter");
        word = OneOrMore(letter).As("word");
        return word;
    }

    [Test]
    public void Symbol_Name_returns_user_supplied_name_on_named_rule()
    {
        BuildLetterAndWordGrammar(out _, out var word);
        var result = word.Parse("hi");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var root = result.Symbols[0];
        Assert.That(root.Name, Is.EqualTo("word"));
        // First child is the first "letter" leaf.
        Assert.That(root.Children[0].Name, Is.EqualTo("letter"));
    }

    [Test]
    public void Symbol_Is_with_string_matches_user_supplied_name()
    {
        BuildLetterAndWordGrammar(out _, out var word);
        var result = word.Parse("hi");

        var root = result.Symbols[0];
        Assert.That(root.Is("word"), Is.True);
        Assert.That(root.Is("letter"), Is.False);
        Assert.That(root.Children[0].Is("letter"), Is.True);
        Assert.That(root.Children[0].Is("word"), Is.False);
    }

    [Test]
    public void Symbol_Is_with_string_treats_null_as_no_match()
    {
        BuildLetterAndWordGrammar(out _, out var word);
        var result = word.Parse("hi");

        var root = result.Symbols[0];
        // Defensive: callers passing a possibly-null lookup should get
        // false rather than a NullReferenceException.
        Assert.That(root.Is((string)null!), Is.False);
    }

    [Test]
    public void Symbol_Is_string_and_Rule_overload_agree()
    {
        BuildLetterAndWordGrammar(out var letter, out var word);
        var result = word.Parse("hi");

        var root = result.Symbols[0];
        Assert.That(root.Is("word"), Is.EqualTo(root.Is(word)));
        Assert.That(root.Is("letter"), Is.EqualTo(root.Is(letter)));
        Assert.That(root.Children[0].Is("letter"),
            Is.EqualTo(root.Children[0].Is(letter)));
    }

    [Test]
    public void Symbol_Name_returns_rune_text_on_anonymous_rune_leaf()
    {
        // Token('a') with no .As(...) — the leaf's Id is the rune code
        // point. Per NameOf's fallback, the name should render as the
        // rune's text ("a").
        var rule = OneOrMore(Token('a').Preserve());
        var result = rule.Parse("aaa");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var firstLeaf = result.Symbols[0];
        Assert.That(firstLeaf.Name, Is.EqualTo("a"));
    }

    [Test]
    public void Symbol_Name_returns_class_derived_name_on_unnamed_composite()
    {
        // An And() with no .As(...) surfaces as the trace label "And"
        // through NameOf, so Symbol.Name on it should be "And" too.
        var rule = And(OneOf(TokenSet.Letters), OneOf(TokenSet.Digits)).Preserve();
        var result = rule.Parse("a1");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        Assert.That(result.Symbols[0].Name, Is.EqualTo("And"));
    }

    [Test]
    public void Symbol_Name_returns_null_on_hand_built_symbol_with_no_context()
    {
        // Symbols constructed by hand (test fixtures, mock trees) carry
        // no ParseContext, so there's no grammar to resolve the id
        // against. Symbol.Name returns null rather than throwing.
        var orphan = new Symbol(new SymbolId(0x100000), FlattenType.Preserve, children: null);
        Assert.That(orphan.Name, Is.Null);
    }

    [Test]
    public void IdOf_returns_the_id_assigned_at_compile_time()
    {
        BuildLetterAndWordGrammar(out var letter, out var word);
        word.Compile();

        Assert.That(word.IdOf("word"), Is.EqualTo(word.Id));
        Assert.That(word.IdOf("letter"), Is.EqualTo(letter.Id));
    }

    [Test]
    public void IdOf_returns_null_on_unknown_name()
    {
        BuildLetterAndWordGrammar(out _, out var word);
        word.Compile();

        Assert.That(word.IdOf("nonexistent"), Is.Null);
    }

    [Test]
    public void IdOf_treats_null_input_as_no_match()
    {
        BuildLetterAndWordGrammar(out _, out var word);
        word.Compile();

        Assert.That(word.IdOf(null!), Is.Null);
    }

    [Test]
    public void IdOf_auto_compiles_when_called_before_compile()
    {
        // Like NameOf, IdOf should auto-compile so callers can cache
        // the result at static-init time without worrying about
        // ordering relative to the first Parse.
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As("word");
        // No explicit Compile() call before the IdOf call below.
        var id = rule.IdOf("word");
        Assert.That(id, Is.Not.Null);
        Assert.That(id, Is.EqualTo(rule.Id));
    }

    [Test]
    public void IdOf_skips_class_derived_trace_names()
    {
        // Unnamed And / OneOrMore / Token rules surface through NameOf
        // as their class-derived trace label ("And", "OneOrMore", ...),
        // but those labels aren't unique so IdOf skips them. Two unnamed
        // And rules in the same grammar would otherwise both want the
        // same key, and the caller couldn't tell which id they got.
        var firstAnd = And(OneOf(TokenSet.Letters), OneOf(TokenSet.Digits));
        var secondAnd = And(OneOf(TokenSet.Digits), OneOf(TokenSet.Letters));
        var root = Or(firstAnd, secondAnd);
        root.Compile();

        Assert.That(root.IdOf("And"), Is.Null);
    }

    [Test]
    public void IdOf_round_trips_with_NameOf()
    {
        BuildLetterAndWordGrammar(out var letter, out var word);
        word.Compile();

        var letterId = word.IdOf("letter")!.Value;
        Assert.That(word.NameOf(letterId), Is.EqualTo("letter"));

        var wordId = word.IdOf("word")!.Value;
        Assert.That(word.NameOf(wordId), Is.EqualTo("word"));
    }

    [Test]
    public void IdOf_works_on_root_and_descendant_alike()
    {
        // Like NameOf, IdOf is scoped to the receiver's subtree. Called
        // on the root, every reachable rule's name resolves. Called on a
        // descendant, only that descendant's subtree resolves.
        BuildLetterAndWordGrammar(out var letter, out var word);
        word.Compile();

        Assert.That(word.IdOf("letter"), Is.Not.Null);
        Assert.That(letter.IdOf("letter"), Is.Not.Null);
        Assert.That(letter.IdOf("word"), Is.Null, "word is not in letter's subtree");
    }
}
