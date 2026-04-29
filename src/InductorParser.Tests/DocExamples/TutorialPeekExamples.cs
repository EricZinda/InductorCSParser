using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Verifies the password-validation grammar built up step by step in
// docs/tutorial-peek.md. The doc walks from "scan until a digit" up to
// the full multi-rule grammar that mirrors the StackOverflow regex.
// Each test mirrors a stage in that progression and asserts what the
// stage is supposed to do.
[TestFixture]
public class TutorialPeekExamples
{
    // tutorial-peek.md "Inductor Parser has a rule for this: ScanUntil"
    // The first form: AllOf(ScanUntil(set), OneOf(set)) succeeds only if
    // the input contains a digit (consuming everything up to and including
    // the first one).
    [Test]
    public void Contains_consuming_form_succeeds_only_when_digit_present()
    {
        var rule = AllOf(ScanUntil(RuneSet.Range('0', '9')),
                          OneOf(RuneSet.Range('0', '9')));

        // Wrap with AnyToken+Eof so a partial match doesn't fail Parse's
        // consume-all-input rule, since the doc presents this rule as
        // standalone.
        var anchored = AllOf(rule, ZeroOrMore(AnyToken()), Eof());
        Assert.That(anchored.Parse("abc1xyz").Success, Is.True);
        Assert.That(anchored.Parse("noDigitsAtAll").Success, Is.False);
    }

    // tutorial-peek.md "The Peek rule is designed for just this case":
    // wrap the Contains rule in Peek so it doesn't consume.
    [Test]
    public void Contains_peek_form_does_not_consume_input()
    {
        Rule Contains(RuneSet options) =>
            Peek(AllOf(ScanUntil(options), OneOf(options)));

        // Combine: digit, upper, lower, special. Then run a final
        // AnyToken-loop to consume the actual input. If the Peeks worked,
        // a single password should satisfy all four because each Peek
        // looks at the whole string.
        var rule = AllOf(
            Contains(RuneSet.Range('0', '9')),
            Contains(RuneSet.Range('A', 'Z')),
            Contains(RuneSet.Range('a', 'z')),
            Contains(RuneSet.Runes("#?!")),
            ZeroOrMore(AnyToken()),
            Eof());

        Assert.That(rule.Parse("Abcd1!ef").Success, Is.True);
        Assert.That(rule.Parse("abcd1!ef").Success, Is.False, "no upper");
        Assert.That(rule.Parse("ABCD1!EF").Success, Is.False, "no lower");
        Assert.That(rule.Parse("Abcdef!g").Success, Is.False, "no digit");
        Assert.That(rule.Parse("Abcdef1g").Success, Is.False, "no special");
    }

    // tutorial-peek.md "ScanUntil supports the first one too, using a
    // Rule overload": Contains(Rule rule) variant for whole-string
    // checks like username, "password", websitename.
    [Test]
    public void Contains_rule_overload_finds_substring()
    {
        Rule Contains(Rule innerRule) =>
            Peek(AllOf(ScanUntil(innerRule), innerRule));

        var notPassword = AllOf(
            Not(Contains(Literal("password"))),
            ZeroOrMore(AnyToken()),
            Eof());

        Assert.That(notPassword.Parse("Abcd1!ef").Success, Is.True);
        Assert.That(notPassword.Parse("MyPassWord1!").Success, Is.True,
            "case-sensitive Literal, capital P doesn't match");
        Assert.That(notPassword.Parse("xpasswordy").Success, Is.False);
    }

    // tutorial-peek.md "The original spec said it also can't *be* the
    // original password": Not(AllOf(Literal(orig), Eof())).
    [Test]
    public void Cannot_be_the_original_password()
    {
        var originalPassword = "OldPwd1!";
        var notOriginal = AllOf(
            Not(AllOf(Literal(originalPassword), Eof())),
            ZeroOrMore(AnyToken()),
            Eof());

        Assert.That(notOriginal.Parse(originalPassword).Success, Is.False);
        Assert.That(notOriginal.Parse(originalPassword + "x").Success, Is.True,
            "starts-with old password is fine, equality is the only ban");
        Assert.That(notOriginal.Parse("DifferentPwd1!").Success, Is.True);
    }

    // tutorial-peek.md "So now we have:" the full final pattern from the
    // doc that combines every rule with the AtLeast(8, AnyToken()) length
    // check.
    [Test]
    public void Full_pattern_validates_password()
    {
        Rule Contains(Rule innerRule) =>
            Peek(AllOf(ScanUntil(innerRule), innerRule));

        Rule ContainsSet(RuneSet options) =>
            Peek(AllOf(ScanUntil(options), OneOf(options)));

        var originalPassword = "OldPwd1!";
        var username = "alice";
        var websitename = "exospecies";

        var pattern = AllOf(
            ContainsSet(RuneSet.Range('0', '9')),
            ContainsSet(RuneSet.Range('A', 'Z')),
            ContainsSet(RuneSet.Range('a', 'z')),
            ContainsSet(RuneSet.Runes("#?!")),
            Not(Contains(Literal(username))),
            Not(Contains(Literal("password"))),
            Not(Contains(Literal(websitename))),
            Not(AllOf(Literal(originalPassword), Eof())),
            AtLeast(8, AnyToken()));

        Assert.That(pattern.Parse("AbcdefG1!").Success, Is.True,
            "good: 9 chars with upper, lower, digit, special");
        Assert.That(pattern.Parse("Abcde1!").Success, Is.False,
            "too short: 7 chars");
        Assert.That(pattern.Parse("AbcdefG12").Success, Is.False,
            "no special char");
        Assert.That(pattern.Parse("Abcdalice12!").Success, Is.False,
            "contains username");
        Assert.That(pattern.Parse("AbcdpasswordX1!").Success, Is.False,
            "contains 'password'");
        Assert.That(pattern.Parse("AbcdexospeciesX1!").Success, Is.False,
            "contains websitename");
        Assert.That(pattern.Parse(originalPassword).Success, Is.False,
            "exactly the original password");
    }
}
