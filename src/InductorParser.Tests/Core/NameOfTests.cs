using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

// Tests for Rule.NameOf(SymbolId), the reverse lookup from a SymbolId
// back to a human-readable name. The lookup has two sources: the
// character range (0..0x10FFFF) assumes the ID is a single-char
// string and returns that as the name. Everything else comes out of a per-grammar index built
// from the rule graph at compile time.
[TestFixture]
public class NameOfTests
{
    [TestCase(0x0)]    // first valid scalar
    [TestCase(0x40)]   // middle of ASCII
    [TestCase(0x7F)]   // last ASCII
    public void Ascii_codepoint_renders_as_rune_string(int codepoint)
    {
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        rule.Compile();

        string expected = new Rune(codepoint).ToString();
        Assert.That(rule.NameOf(new SymbolId(codepoint)), Is.EqualTo(expected));
    }

    [TestCase(0x80)]   // first non-ASCII BMP
    [TestCase(0x6F22)] // middle (漢)
    [TestCase(0xD7FF)] // last BMP before the surrogate hole
    public void Bmp_codepoint_below_surrogate_hole_renders_as_rune_string(int codepoint)
    {
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        rule.Compile();

        string expected = new Rune(codepoint).ToString();
        Assert.That(rule.NameOf(new SymbolId(codepoint)), Is.EqualTo(expected));
    }

    [TestCase(0xE000)] // first BMP above the surrogate hole
    [TestCase(0xFB00)] // middle (latin ligature ﬀ)
    [TestCase(0xFFFF)] // last BMP
    public void Bmp_codepoint_above_surrogate_hole_renders_as_rune_string(int codepoint)
    {
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        rule.Compile();

        string expected = new Rune(codepoint).ToString();
        Assert.That(rule.NameOf(new SymbolId(codepoint)), Is.EqualTo(expected));
    }

    [TestCase(0x10000)]  // first supplementary plane scalar
    [TestCase(0x1F3B8)]  // middle (guitar emoji 🎸)
    [TestCase(0x10FFFF)] // last valid Unicode scalar
    public void Supplementary_codepoint_renders_as_rune_string(int codepoint)
    {
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        rule.Compile();

        string expected = new Rune(codepoint).ToString();
        Assert.That(rule.NameOf(new SymbolId(codepoint)), Is.EqualTo(expected));
    }

    // 0xD800..0xDFFF is inside the character range numerically but isn't a
    // valid Unicode scalar value. The lexer never emits one, but if a caller
    // hand-builds a bad SymbolId we return null rather than crashing on
    // new Rune(...).
    [TestCase(0xD800)] // low edge of the surrogate hole
    [TestCase(0xDC00)] // boundary between high and low surrogates
    [TestCase(0xDFFF)] // high edge of the surrogate hole
    public void Surrogate_half_returns_null(int codepoint)
    {
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        rule.Compile();

        Assert.That(rule.NameOf(new SymbolId(codepoint)), Is.Null);
    }

    [Test]
    public void Named_rule_returns_the_user_supplied_name()
    {
        var settingName = OneOrMore(OneOf(TokenSet.Letters)).As("settingName");
        settingName.Compile();

        Assert.That(settingName.NameOf(settingName.Id), Is.EqualTo("settingName"));
    }

    [Test]
    public void Unnamed_And_rule_returns_class_derived_name()
    {
        var andRule = And(OneOf(TokenSet.Letters), OneOf(TokenSet.Digits));
        andRule.Compile();

        Assert.That(andRule.NameOf(andRule.Id), Is.EqualTo("And"));
    }

    [Test]
    public void Two_unnamed_And_rules_share_the_class_derived_name_but_have_distinct_ids()
    {
        // The trace label for an unnamed rule comes from its class name, so
        // two unrelated And rules in the same grammar both report "And"
        // from NameOf. They're still distinguishable because each rule gets
        // its own SymbolId at Compile time. If a caller wants to tell two
        // Ands apart by label, the remedy is .As("...").
        var firstAnd = And(OneOf(TokenSet.Letters), OneOf(TokenSet.Digits));
        var secondAnd = And(OneOf(TokenSet.Digits), OneOf(TokenSet.Letters));
        var root = Or(firstAnd, secondAnd);
        root.Compile();

        Assert.That(firstAnd.Id, Is.Not.EqualTo(secondAnd.Id));
        Assert.That(root.NameOf(firstAnd.Id), Is.EqualTo("And"));
        Assert.That(root.NameOf(secondAnd.Id), Is.EqualTo("And"));
    }

    [Test]
    public void OneOrMore_rule_returns_its_factory_trace_name()
    {
        // OneOrMore / ZeroOrMore / Optional are BetweenInclusiveRule under
        // the hood, but they stamp a friendly trace name at construction
        // time. NameOf should surface that friendly name.
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        rule.Compile();

        Assert.That(rule.NameOf(rule.Id), Is.EqualTo("OneOrMore"));
    }

    [Test]
    public void BetweenInclusive_rule_returns_name_with_bounds()
    {
        var rule = BetweenInclusive(1, 3, OneOf(TokenSet.Letters));
        rule.Compile();

        Assert.That(rule.NameOf(rule.Id), Is.EqualTo("BetweenInclusive[1..3]"));
    }

    [Test]
    public void NameOf_on_the_root_resolves_all_descendant_rule_names()
    {
        // NameOf builds its name index by walking from the receiver downward,
        // so calling it on the root grammar lets a tree walker resolve every
        // rule's name without having to hold direct references to nested
        // Rule objects. Every rule in the grammar is reachable from the root,
        // which is why "any id you see while walking a parse tree" works.
        // NameOf called on a non-root rule only sees that rule's descendants,
        // not its siblings or ancestors.
        var inner = OneOf(TokenSet.Letters).As("letter");
        var outer = OneOrMore(inner).As("word");
        outer.Compile();

        Assert.That(outer.NameOf(outer.Id), Is.EqualTo("word"));
        Assert.That(outer.NameOf(inner.Id), Is.EqualTo("letter"));
    }

    [Test]
    public void NameOf_on_a_child_rule_returns_null_for_an_ancestors_id()
    {
        // Flip side of NameOf_on_the_root_resolves_all_descendant_rule_names.
        // NameOf is scoped to the receiver's subtree, so a child rule has no
        // view up the tree. Calling NameOf on the child can resolve the
        // child itself but not its parent, since the parent isn't reachable
        // by walking down from the child.
        var inner = OneOf(TokenSet.Letters).As("letter");
        var outer = OneOrMore(inner).As("word");
        outer.Compile();

        Assert.That(inner.NameOf(inner.Id), Is.EqualTo("letter"));
        Assert.That(inner.NameOf(outer.Id), Is.Null);
    }

    [Test]
    public void Unknown_custom_id_returns_null()
    {
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As("word");
        rule.Compile();

        var stranger = new SymbolId(SymbolRanges.CustomRangeStart + 0x7FFFFF);
        Assert.That(rule.NameOf(stranger), Is.Null);
    }

    [Test]
    public void NameOf_auto_compiles_when_called_before_compile()
    {
        // NameOf needs stable ids, which means Compile has to have run.
        // Callers shouldn't have to remember to call Compile first, so
        // NameOf is expected to auto-compile the graph on first call.
        //
        // We can't pass rule.Id here because it's still zero before Compile
        // (and zero routes through the character fallback). Instead we
        // predict the post-Compile id by hashing the rule's name.
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As("word");
        var predictedId = new SymbolId(Rule.HashNameToCustomRange("word"));

        string? name = rule.NameOf(predictedId);

        Assert.That(name, Is.EqualTo("word"));
    }

    [Test]
    public void NameOf_is_stable_across_repeated_calls()
    {
        // Second call hits the cached _nameIndex path. First call builds it.
        // Both should return the same answer.
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As("word");
        rule.Compile();

        string? first = rule.NameOf(rule.Id);
        string? second = rule.NameOf(rule.Id);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Named_single_rune_Token_returns_the_user_supplied_name()
    {
        // GraphemeRule (Rules.Token) writes its rune's code point as the
        // rule's own Id at construction time. For Token('a').As("aChar"),
        // rule.Id is 0x61 (in the Unicode scalar range) and Name is
        // "aChar". NameOf returns the user-supplied name.
        var aChar = Token('a').As("aChar").Preserve();
        aChar.Compile();

        Assert.That(aChar.NameOf(aChar.Id), Is.EqualTo("aChar"));
    }

    [Test]
    public void Unnamed_single_rune_Token_returns_the_rune_text()
    {
        // For an unnamed Token('a'), rule.Id is 0x61 and Name is null.
        // NameOf returns the rune's text as the default for character-
        // range ids without a user-supplied name.
        var rule = Token('a').Preserve();
        rule.Compile();

        Assert.That(rule.NameOf(rule.Id), Is.EqualTo("a"));
    }

    [Test]
    public void Named_supplementary_Token_returns_the_user_supplied_name()
    {
        // Same shape as Named_single_rune_Token with a supplementary-
        // plane rune (id past 0xFFFF but still in the character range).
        var guitar = Token(UnicodeExamples.GuitarGrapheme).As("guitar").Preserve();
        guitar.Compile();

        Assert.That(guitar.NameOf(guitar.Id), Is.EqualTo("guitar"));
    }
}
