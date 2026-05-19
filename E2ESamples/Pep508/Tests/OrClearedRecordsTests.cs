// Regression coverage for backlog item 0a03, now fixed.
//
// While building the PEP 508 rewrite, the trailing-comma case
// `requests>=1.0,` wouldn't produce a targeted message no matter where
// the `.WithError` went. The cause: an `Or` used to clear every failure
// record added during its run when it committed to a branch, including
// the winning branch's count-iteration record. So a count rule's inner
// `.WithError` surfaced on a top-level path but vanished once the count
// rule sat inside a committed `Or` branch.
//
// The depth-primary error model removed `Or`'s success-clear entirely
// (see docs/ErrorArchitecture.md). These two tiny grammars now behave
// identically: the count rule's inner `.WithError` surfaces whether or
// not an `Or` commits around it.

using InductorParser;
using NUnit.Framework;
using static InductorParser.Rules;

namespace Pep508Sample.Tests;

[TestFixture]
public class OrClearedRecordsTests
{
    // `x (, x)*` then EOF, where the `x` after a comma carries a
    // WithError. On input "x," the loop's last iteration fails.
    private static Rule ListWithoutOr()
    {
        var itemAfterComma = Literal("x").WithError("expected x after comma");
        return And(
            Literal("x"),
            ZeroOrMore(And(Token(','), itemAfterComma)),
            Eof()
        ).Compile();
    }

    // The same list, but wrapped as the second branch of an `Or`. The
    // first branch can never match "x,", so the `Or` commits to the list
    // branch. Under the depth-primary model the commit no longer clears
    // anything, so the result is identical to ListWithoutOr.
    private static Rule ListInsideOr()
    {
        var itemAfterComma = Literal("x").WithError("expected x after comma");
        var list = And(
            Literal("x"),
            ZeroOrMore(And(Token(','), itemAfterComma)));
        return And(
            Or(Literal("ZZZ"), list),
            Eof()
        ).Compile();
    }

    [Test]
    public void Count_rule_on_the_top_level_path_keeps_its_inner_WithError()
    {
        var result = ListWithoutOr().Parse("x,");

        Assert.That(result.Success, Is.False);
        // The doc-stated behavior: the failed iteration's WithError wins.
        Assert.That(result.ErrorMessage, Does.Contain("expected x after comma"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void Count_rule_inside_a_committed_Or_branch_keeps_its_inner_WithError()
    {
        var result = ListInsideOr().Parse("x,");

        Assert.That(result.Success, Is.False);
        // Same grammar, same input, same result as the top-level path.
        // The Or's success no longer clears the winning branch's
        // count-iteration record, so the inner WithError still surfaces.
        // This is backlog item 0a03, fixed by the depth-primary model.
        Assert.That(result.ErrorMessage, Does.Contain("expected x after comma"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }
}
