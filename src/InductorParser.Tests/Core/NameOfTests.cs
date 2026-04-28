using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for Rule.NameOf(SymbolId), the reverse lookup from a SymbolId
// back to a human-readable name. The lookup has two sources: the
// character range (0..0x10FFFF) renders the code point as a single-char
// string, and everything else comes out of a per-grammar index built
// from the rule graph at compile time.
[TestFixture]
public class NameOfTests
{
    [Test]
    public void Ascii_character_id_renders_as_single_char_string()
    {
        var rule = OneOrMore(OneOf(RuneSet.Letters));
        rule.Compile();

        Assert.That(rule.NameOf(new SymbolId('A')), Is.EqualTo("A"));
    }

    [Test]
    public void Bmp_character_id_renders_as_single_char_string()
    {
        var rule = OneOrMore(OneOf(RuneSet.Letters));
        rule.Compile();

        Assert.That(rule.NameOf(new SymbolId(0x6F22)), Is.EqualTo("漢"));
    }

    [Test]
    public void Supplementary_plane_character_id_renders_as_surrogate_pair_string()
    {
        var rule = OneOrMore(OneOf(RuneSet.Letters));
        rule.Compile();

        string expected = new Rune(0x1F3B8).ToString();
        Assert.That(rule.NameOf(new SymbolId(0x1F3B8)), Is.EqualTo(expected));
    }

    [Test]
    public void Surrogate_half_id_returns_null()
    {
        // 0xD800..0xDFFF is inside the character range numerically but
        // isn't a valid Unicode scalar value. The lexer never emits one,
        // but if a caller hand-builds a bad SymbolId we return null
        // rather than crashing on new Rune(...).
        var rule = OneOrMore(OneOf(RuneSet.Letters));
        rule.Compile();

        Assert.That(rule.NameOf(new SymbolId(0xD800)), Is.Null);
    }

    [Test]
    public void Named_rule_returns_the_user_supplied_name()
    {
        var settingName = OneOrMore(OneOf(RuneSet.Letters)).As("settingName");
        settingName.Compile();

        Assert.That(settingName.NameOf(settingName.Id), Is.EqualTo("settingName"));
    }

    [Test]
    public void Unnamed_AllOf_rule_returns_class_derived_name()
    {
        var allOfRule = AllOf(OneOf(RuneSet.Letters), OneOf(RuneSet.Digits));
        allOfRule.Compile();

        Assert.That(allOfRule.NameOf(allOfRule.Id), Is.EqualTo("AllOf"));
    }

    [Test]
    public void OneOrMore_rule_returns_its_factory_trace_name()
    {
        // OneOrMore / ZeroOrMore / Optional are BetweenInclusiveRule under
        // the hood, but they stamp a friendly trace name at construction
        // time. NameOf should surface that friendly name.
        var rule = OneOrMore(OneOf(RuneSet.Letters));
        rule.Compile();

        Assert.That(rule.NameOf(rule.Id), Is.EqualTo("OneOrMore"));
    }

    [Test]
    public void BetweenInclusive_rule_returns_name_with_bounds()
    {
        var rule = BetweenInclusive(1, 3, OneOf(RuneSet.Letters));
        rule.Compile();

        Assert.That(rule.NameOf(rule.Id), Is.EqualTo("BetweenInclusive[1..3]"));
    }

    [Test]
    public void Nested_rules_are_all_indexed_from_the_root()
    {
        var inner = OneOf(RuneSet.Letters).As("letter");
        var outer = OneOrMore(inner).As("word");
        outer.Compile();

        Assert.That(outer.NameOf(outer.Id), Is.EqualTo("word"));
        Assert.That(outer.NameOf(inner.Id), Is.EqualTo("letter"));
    }

    [Test]
    public void Unknown_custom_id_returns_null()
    {
        var rule = OneOrMore(OneOf(RuneSet.Letters)).As("word");
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
        // The id we pass in comes from HashNameToCustomRange rather than
        // rule.Id, because rule.Id is still the default-constructed zero
        // until Compile runs, and zero routes through the character
        // fallback path. Here we predict what id the "word" rule will
        // hash to, call NameOf before Compile, and verify auto-compile
        // happens and the id resolves.
        var rule = OneOrMore(OneOf(RuneSet.Letters)).As("word");
        var predictedId = new SymbolId(Rule.HashNameToCustomRange("word"));

        string? name = rule.NameOf(predictedId);

        Assert.That(name, Is.EqualTo("word"));
    }

    [Test]
    public void NameOf_is_stable_across_repeated_calls()
    {
        // Second call hits the cached _nameIndex path. First call builds it.
        // Both should return the same answer.
        var rule = OneOrMore(OneOf(RuneSet.Letters)).As("word");
        rule.Compile();

        string? first = rule.NameOf(rule.Id);
        string? second = rule.NameOf(rule.Id);

        Assert.That(second, Is.EqualTo(first));
    }
}
