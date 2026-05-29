using System.Threading;
using NUnit.Framework;

namespace InductorParser.Tests;

// Tests for the Invariant.That helper. The interesting properties
// here aren't behavioral (a parse using Invariant.That looks the same
// from the outside as a parse that uses a hand-written throw) but
// performance: the message mustn't be built when the condition
// holds, because every site is on a hot path.
//
// Same shape as TracingTests.Off_path_does_not_evaluate_interpolated_arguments:
// place a side effect inside the interpolation hole and assert it
// doesn't fire. If the compiler ever stops honoring the handler
// attribute, the side effect runs and the test fails immediately.
[TestFixture]
public class InvariantTests
{
    [Test]
    public void That_passing_condition_does_not_evaluate_interpolated_arguments()
    {
        // Proves InvariantInterpolatedStringHandler's shouldAppend
        // gate is actually wired up. With condition=true, the handler
        // constructor sets shouldAppend=false, the compiler skips
        // every AppendFormatted call, and the side effect never runs.
        //
        // If sideEffectCount comes back 1, the compiler is eagerly
        // building the string on every call to Invariant.That. That
        // would mean every Lexer.Read interpolates a position into a
        // StringBuilder per token, which is exactly the per-token
        // allocation cost the handler exists to avoid.
        int sideEffectCount = 0;

        Invariant.That(true, $"value: {Interlocked.Increment(ref sideEffectCount)}");

        Assert.That(sideEffectCount, Is.EqualTo(0),
            "Passing condition: the $\"...\" argument must not be evaluated. "
            + "If this fails, every Invariant.That call is allocating on the hot path.");
    }

    [Test]
    public void That_failing_condition_evaluates_interpolated_arguments_exactly_once()
    {
        // Complement to the passing-condition test. When the handler's
        // shouldAppend=true (because we're about to throw), arguments
        // must be evaluated exactly once. Zero would mean the message
        // is empty when the bug report is filed. Two would mean the
        // compiler generated a spurious extra evaluation.
        int sideEffectCount = 0;

        var caught = Assert.Throws<InductorParserBugException>(() =>
            Invariant.That(false, $"value: {Interlocked.Increment(ref sideEffectCount)}"));

        Assert.That(sideEffectCount, Is.EqualTo(1),
            "Failing condition: exactly one evaluation of each interpolated arg.");
        Assert.That(caught!.Message, Does.Contain("value: 1"));
    }

    [Test]
    public void That_failing_condition_throws_InductorParserBugException_with_neutral_framing()
    {
        // Verifies the exception type and the message framing. The
        // "Invariant violated" prefix and the "should never happen"
        // framing read correctly whether the invariant was declared by
        // InductorParser itself or by a user-defined rule using the same
        // public utility, so the message doesn't blame the wrong party.
        var caught = Assert.Throws<InductorParserBugException>(() =>
            Invariant.That(false, "the sky is the wrong color"));

        Assert.That(caught!.Message, Does.StartWith("Invariant violated:"));
        Assert.That(caught.Message, Does.Contain("the sky is the wrong color"));
        Assert.That(caught.Message, Does.Contain("should never happen"));
    }

    [Test]
    public void That_passing_condition_with_plain_string_overload_is_a_no_op()
    {
        // The string overload exists for compile-time-folded constants
        // like "a " + "b " + "c", which the C# spec won't route through
        // the handler. Verify it short-circuits cleanly when the
        // condition holds and throws when it doesn't.
        Assert.DoesNotThrow(() => Invariant.That(true, "would-be message"));
        Assert.Throws<InductorParserBugException>(() => Invariant.That(false, "would-be message"));
    }
}
