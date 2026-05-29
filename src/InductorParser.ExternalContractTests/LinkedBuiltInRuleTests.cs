// Smoke tests for the built-in rule sources linked into this no-IVT
// assembly (see the <Compile Include Link> block in the .csproj). The
// primary check is that those files compile here at all: if a built-in
// rule ever uses an internal member, or the public + protected
// surface it needs is narrowed, this assembly fails to build. These
// runtime tests are secondary tests: they `new` the
// locally compiled rule types directly (not the Rules.* factories, which
// return the referenced DLL's copies) so the local copy's TryParseRule
// actually runs once, catching a hand-edit that compiles but diverges in
// behavior. They are deliberately thin; the exhaustive per-rule suites
// live in InductorParser.Tests/Rules.
//
// LateBoundRule is absent on purpose: it's a public type, so a local copy
// would collide with the DLL's export. See the .csproj comment.

using NUnit.Framework;

namespace InductorParser.ExternalContractTests;

[TestFixture]
public class LinkedBuiltInRuleTests
{
    [Test]
    public void AndRule_matches_its_children_in_order()
    {
        var ab = new AndRule(new Rule[] { new GraphemeRule("a"), new GraphemeRule("b") });
        Assert.That(ab.Parse("ab").Success, Is.True);
        Assert.That(ab.Parse("ax").Success, Is.False);
    }

    [Test]
    public void OrRule_matches_any_alternative()
    {
        var aOrB = new OrRule(new Rule[] { new GraphemeRule("a"), new GraphemeRule("b") });
        Assert.That(aOrB.Parse("a").Success, Is.True);
        Assert.That(aOrB.Parse("b").Success, Is.True);
        Assert.That(aOrB.Parse("c").Success, Is.False);
    }

    [Test]
    public void NotRule_and_AnyTokenRule_match_a_single_non_a_character()
    {
        // Not('a') is zero-width; pair it with AnyToken so the And consumes
        // one character that isn't 'a'.
        var notA = new AndRule(new Rule[] { new NotRule(new GraphemeRule("a")), new AnyTokenRule() });
        Assert.That(notA.Parse("b").Success, Is.True);
        Assert.That(notA.Parse("a").Success, Is.False);
    }

    [Test]
    public void PeekRule_asserts_without_consuming()
    {
        var peekThenA = new AndRule(new Rule[] { new PeekRule(new GraphemeRule("a")), new GraphemeRule("a") });
        Assert.That(peekThenA.Parse("a").Success, Is.True);
        Assert.That(peekThenA.Parse("b").Success, Is.False);
    }

    [Test]
    public void EofRule_matches_only_at_end_of_input()
    {
        var aThenEof = new AndRule(new Rule[] { new GraphemeRule("a"), new EofRule() });
        Assert.That(aThenEof.Parse("a").Success, Is.True);
        Assert.That(aThenEof.Parse("ab").Success, Is.False);
    }

    [Test]
    public void BetweenInclusiveRule_matches_within_its_bounds()
    {
        var oneToThreeA = new BetweenInclusiveRule(new GraphemeRule("a"), atLeast: 1, atMost: 3);
        Assert.That(oneToThreeA.Parse("aaa").Success, Is.True);
        Assert.That(oneToThreeA.Parse("").Success, Is.False);
    }

    [Test]
    public void LiteralRule_matches_the_exact_string()
    {
        var maj = new LiteralRule("maj");
        Assert.That(maj.Parse("maj").Success, Is.True);
        Assert.That(maj.Parse("min").Success, Is.False);
    }

    [Test]
    public void LiteralIgnoreAsciiCaseRule_matches_case_insensitively()
    {
        var maj = new LiteralIgnoreAsciiCaseRule("maj");
        Assert.That(maj.Parse("MAJ").Success, Is.True);
        Assert.That(maj.Parse("xyz").Success, Is.False);
    }

    [Test]
    public void GraphemeRule_matches_one_user_visible_character()
    {
        var a = new GraphemeRule("a");
        Assert.That(a.Parse("a").Success, Is.True);
        Assert.That(a.Parse("b").Success, Is.False);
    }

    [Test]
    public void OneOfRule_matches_one_member_of_the_set()
    {
        var digit = new OneOfRule(TokenSet.Digits);
        Assert.That(digit.Parse("5").Success, Is.True);
        Assert.That(digit.Parse("a").Success, Is.False);
    }

    [Test]
    public void NoneOfRule_matches_one_non_member_of_the_set()
    {
        var nonDigit = new NoneOfRule(TokenSet.Digits);
        Assert.That(nonDigit.Parse("a").Success, Is.True);
        Assert.That(nonDigit.Parse("5").Success, Is.False);
    }

    [Test]
    public void ScanWhileRule_consumes_a_run_of_set_members()
    {
        var digits = new ScanWhileRule(TokenSet.Digits, minimumCount: 1);
        Assert.That(digits.Parse("123").Success, Is.True);
        Assert.That(digits.Parse("abc").Success, Is.False);
    }

    [Test]
    public void ScanUntilRule_consumes_up_to_the_stopper()
    {
        // Scan until an 'x', treating EOF as a valid terminator, so a run
        // with no 'x' consumes the whole input.
        var untilX = new ScanUntilRule(TokenSet.Runes("x"), eofIsTerminator: true);
        Assert.That(untilX.Parse("abc").Success, Is.True);
    }

    [Test]
    public void WithinTokenRule_runs_its_inner_rule_against_the_token_runes()
    {
        var withinA = new WithinTokenRule(new GraphemeRule("a"));
        Assert.That(withinA.Parse("a").Success, Is.True);
        Assert.That(withinA.Parse("b").Success, Is.False);
    }
}
