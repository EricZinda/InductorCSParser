using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

[TestFixture]
public class OrRuleTests
{
    [Test]
    public void Or_returns_the_first_alternative_that_matches()
    {
        // Token defaults to FlattenType.Delete, so the matched 'b' would
        // be filtered out of the tree at parse time. PreserveAllSymbols
        // keeps the Token leaf in the tree so Tree.ToString() shows the
        // text that was actually matched.
        var rule = Or(Token('a'), Token('b'), Token('c'));
        var result = rule.Parse("b", new ParseOptions { PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void Or_failure_without_WithError_falls_back_to_positional_message()
    {
        // All alternatives fail, none have WithError. Null message at
        // offset 0, positional fallback renders.
        var rule = Or(Token('a'), Token('b'), Token('c'));
        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Does.StartWith("Parse failed at offset 0"));
    }

    [Test]
    public void Or_all_children_fail_at_same_position_first_writer_wins()
    {
        // All three alternatives try at offset 0 and fail. Each records at
        // pre-read position 0 with its own WithError message. Equal depth,
        // so the first-writer wins the message slot. That's Token('a'),
        // which Or tries first.
        var rule = Or(Token('a').WithError("want 'a'"),
                           Token('b').WithError("want 'b'"),
                           Token('c').WithError("want 'c'"));

        var result = rule.Parse("x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void Or_descendant_WithError_surfaces_when_per_child_shortcut_would_skip_composite()
    {
        // Or(Or(Token('a').WithError("want 'a'"), Token('b')), Token('c')) against "d".
        //
        // The outer Or's per-child shortcut consults each child's
        // HasErrorMessageInSubtree gate. The inner Or itself has no .WithError,
        // but Token('a').WithError lives in its subtree, so the gate is true
        // and the shortcut is bypassed for that child. The inner Or runs;
        // its own per-child shortcut tries Token('a').WithError and skips
        // Token('b'). "want 'a'" lands at the deepest-failure slot for
        // offset 0 and surfaces as ErrorMessage. Pre-fix the gate was
        // child.ErrorMessage == null, which silently skipped the inner Or
        // and dropped its descendant's message.
        var rule = Or(Or(Token('a').WithError("want 'a'"), Token('b')), Token('c'));
        var result = rule.Parse("d");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    [Test]
    public void Or_child_that_consumes_deeper_wins_the_position_and_message()
    {
        // First branch matches "ab" then fails on 'x' at offset 2, recording
        // its Token('c') WithError there. Second branch matches "a" then
        // fails on 'b' at offset 1, recording its Token('d') WithError there.
        // Deepest-wins picks offset 2, so the first branch's "need 'c'"
        // surfaces.
        var rule = Or(And(Token('a'), Token('b'), Token('c').WithError("need 'c'")),
                           And(Token('a'), Token('d').WithError("need 'd'")));

        var result = rule.Parse("abx");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c'"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_trace_success_produces_expected_output()
    {
        // Third alternative wins. Required-runes dispatch skips Token('a') and
        // Token('b') on lookahead 'c' (their FirstConsumedTokens don't contain 'c'
        // and neither is empty-capable), so only the matching Token('c')
        // branch opens a transaction and emits trace lines. The
        // nesting remains depth 2 (Or's transaction + Token's transaction).
        var sink = NewSink();
        Or(Token('a'), Token('b'), Token('c')).Parse("c", new ParseOptions { TraceSink = sink });

        // The shortcut rules out Token('a') and Token('b') on the 'c'
        // peek before either alt's transaction opens, emitting a SKIP
        // line per skipped child so the trace shows what Or
        // considered. Token('c') then runs and matches.
        string expected = Lines(
            "SKIP | Token: shortcut: peek 'c' not in '[a]'",
            "SKIP | Token: shortcut: peek 'c' not in '[b]'",
            "      Lexer.Read: 'c', Consumed: 1",
            "      SUCC | Token: found 'c'",
            "   SUCC | Or: symbol #2"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_trace_failure_produces_expected_output()
    {
        // Required-runes dispatch rules out both Token('a') and Token('b') on
        // lookahead 'z', so no child transaction ever opens. Each skip emits
        // a SKIP trace line under the child's label, then Or emits its
        // own FAIL line after the loop. No transaction is open by then so
        // the FAIL line carries no indentation, and an empty detail means
        // no ": {detail}" tail - the line reads "FAIL | Or".
        var sink = NewSink();
        Or(Token('a'), Token('b')).Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "SKIP | Token: shortcut: peek 'z' not in '[a]'",
            "SKIP | Token: shortcut: peek 'z' not in '[b]'",
            "FAIL | Or"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Sealed_Or_rejects_Flatten()
    {
        var rule = Or(Token('a'), Token('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_Or_rejects_WithError()
    {
        var rule = Or(Token('a'), Token('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_Or_rejects_As()
    {
        var rule = Or(Token('a'), Token('b'));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    [Test]
    public void Or_shortcut_doesnt_skip_And_with_optional_NoneOf_prefix()
    {
        // This pins the lookahead-shortcut soundness story for an And
        // whose first child is Optional(NoneOf(...)). The Optional can
        // either match zero (so the next sibling sees the lookahead) or
        // match a single token NOT in the NoneOf's set, so the And's
        // true first-token set is "anything in {a,b,c} OR anything not
        // in {x,y}" = anything except {x,y} \ {a,b,c} = anything except
        // nothing-here = Universe.
        //
        // The And's published RuleStartRequirements is derived by
        // running Optional through WithAdvance(Sometimes) (because the
        // outer BetweenInclusive's atLeast-zero downgrades Inner.Advance)
        // and then composing with OneOf("abc") in MatchesAllOf. WithAdvance
        // forces polarity to MustBeIn but keeps the original set, so the
        // composition treats {x,y} (the NoneOf's fail-set) as a
        // MustBeIn-style consume-set. Combined with OneOf's {a,b,c} the
        // And publishes ({x,y,a,b,c}, Always, MustBeIn).
        //
        // Inside the outer Or, that triple makes CannotMatchLookahead
        // skip the And on peek 'z'. The Or commits to the Token('z')
        // alternative, which only consumes 'z', and the parse fails on
        // the trailing 'b' instead of the And consuming both characters
        // and the parse succeeding.
        var rule = Or(
            And(Optional(NoneOf("xy")), OneOf("abc")),
            Token('z'));

        var result = rule.Parse("zb");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }
}
