// Verifies that a count rule's inner `.WithError` surfaces whether or not the
// count rule sits inside a committed `Or` branch.
//
// Under the depth-primary error model an `Or` that commits to a branch keeps
// the failure records added during that branch's run, including the winning
// branch's count-iteration record. So the count rule's inner `.WithError`
// reaches the result the same way whether the count rule is at the top level
// or wrapped inside a committed `Or` branch. See docs/ErrorArchitecture.md for
// the error model.
//
// The two tiny grammars below parse "x," to the same positioned message: one
// keeps the list at the top level, the other wraps it as a committed `Or`
// branch.

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
    // branch. Under the depth-primary model the commit doesn't clear
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
        // The Or's success doesn't clear the winning branch's
        // count-iteration record, so the inner WithError still surfaces.
        Assert.That(result.ErrorMessage, Does.Contain("expected x after comma"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    }
}
