using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for Rules.Integer(). The grammar is "[+-]?\d+":
// an optional leading + or - followed by one or more decimal digits.
//
// Integer() composes Optional(Or(Token('+'), Token('-'))) for the sign.
// Token has FlattenType.Delete by default, so the sign Tokens are
// filtered out of the parse tree even though they participate in the
// match. That contradicts the rule's own description ("Match a signed
// integer") and the symmetric Float() rule (which was fixed in fl0a to
// emit the sign through Flatten). Callers who name the rule via .As(...)
// and then read symbol.ToString() get "5" instead of "-5".
[TestFixture]
public class IntegerRuleTests
{
    [Test]
    public void Matches_unsigned()
    {
        var result = Integer().Parse("5");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.ToString(), Is.EqualTo("5"));
    }

    [Test]
    public void Matches_negative()
    {
        var result = Integer().Parse("-5");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Matches_positive_with_leading_plus()
    {
        var result = Integer().Parse("+5");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Negative_sign_is_in_tree_text()
    {
        // The rule matched "-5", so result.ToString() should render
        // "-5". The sign Token is matched but currently has the default
        // FlattenType.Delete, so it disappears from the tree and the
        // rendered text is "5". Same asymmetry Float() had pre-fl0a.
        var result = Integer().Parse("-5");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.ToString(), Is.EqualTo("-5"));
    }

    [Test]
    public void Positive_sign_is_in_tree_text()
    {
        var result = Integer().Parse("+5");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.ToString(), Is.EqualTo("+5"));
    }

    [Test]
    public void Named_integer_renders_full_match()
    {
        // The grammar-author-facing path: name the rule via .As(...)
        // and look it up via Find. The found Symbol's ToString should
        // be the full matched text including the sign.
        var integer = Integer().As("number");
        var result = integer.Parse("-42");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var match = result.Find(integer);
        Assert.That(match, Is.Not.Null);
        Assert.That(match!.ToString(), Is.EqualTo("-42"));
    }
}
