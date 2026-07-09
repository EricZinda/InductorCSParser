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
        Assert.That(result.ErrorMessage, Is.EqualTo("Unexpected 'x' at line 1, column 1."));
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
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a' at line 1, column 1."));
    }

    [Test]
    public void Or_descendant_WithError_surfaces_when_per_child_shortcut_would_skip_composite()
    {
        // Or(Or(Token('a').WithError("want 'a'"), Token('b')), Token('c')) against "d".
        //
        // The outer Or's per-child shortcut consults each child's
        // HasErrorMessageInSubtree gate. The inner Or itself has no .WithError,
        // but Token('a').WithError lives in its subtree, so the gate is true
        // and the shortcut is bypassed for that child. The inner Or runs,
        // its own per-child shortcut tries Token('a').WithError and skips
        // Token('b'). "want 'a'" lands at the deepest-failure slot for
        // offset 0 and surfaces as ErrorMessage. Pre-fix the gate was
        // child.ErrorMessage == null, which silently skipped the inner Or
        // and dropped its descendant's message.
        var rule = Or(Or(Token('a').WithError("want 'a'"), Token('b')), Token('c'));
        var result = rule.Parse("d");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(result.ErrorMessage, Is.EqualTo("want 'a' at line 1, column 1."));
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
        Assert.That(result.ErrorMessage, Is.EqualTo("need 'c' at line 1, column 3."));
    }

    [Test]
    public void Or_rejects_null_child_rule()
    {
        var exception = Assert.Throws<ArgumentException>(() => Or(Token('a'), null!));

        Assert.That(exception!.ParamName, Is.EqualTo("children"));
        Assert.That(exception.Message, Does.Contain("index 1"));
    }

    [Test]
    public void Or_rejects_empty_child_list()
    {
        var exception = Assert.Throws<ArgumentException>(() => Or());

        Assert.That(exception!.ParamName, Is.EqualTo("children"));
        Assert.That(exception.Message, Does.Contain("at least one child"));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_trace_success_produces_expected_output()
    {
        // Third alternative wins. Each alternative runs in its own
        // transaction. Token('a') and Token('b') read 'c', fail, and
        // roll back. Token('c') then runs and matches.
        var sink = NewSink();
        Or(Token('a'), Token('b'), Token('c')).Parse("c", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'c', Consumed: 1",
            "      FAIL | Token: found 'c', wanted 'a'",
            "      Lexer.Read: 'c', Consumed: 1",
            "      FAIL | Token: found 'c', wanted 'b'",
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
        // Each alternative reads 'z', fails, and rolls back. Or's FAIL
        // line fires at depth 1 since the outer transaction is still
        // open when TraceFailure runs. The empty detail means no
        // ": {detail}" tail - the line reads "FAIL | Or".
        var sink = NewSink();
        Or(Token('a'), Token('b')).Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'z', Consumed: 1",
            "      FAIL | Token: found 'z', wanted 'a'",
            "      Lexer.Read: 'z', Consumed: 1",
            "      FAIL | Token: found 'z', wanted 'b'",
            "   FAIL | Or"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    [RecursiveEngineOnly]
    public void Or_trace_failure_with_WithError_renders_with_single_space_before_quoted_message()
    {
        // Or's failure trace body is empty (no per-child detail to surface
        // when all alternatives failed), so AppendErrorMessage runs on a
        // zero-length body. The WithError message is the only thing on the
        // line after the label. The format should be one space between
        // ": " and the opening quote, matching how Token's failure trace
        // renders its WithError (see TracingTests:
        // WithError_message_appears_in_quotes_after_trace_body_on_failure).
        var sink = NewSink();
        Or(Token('a'), Token('b'))
            .WithError("expected ab")
            .Parse("z", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'z', Consumed: 1",
            "      FAIL | Token: found 'z', wanted 'a'",
            "      Lexer.Read: 'z', Consumed: 1",
            "      FAIL | Token: found 'z', wanted 'b'",
            "   FAIL | Or: \"expected ab\"",
            "   Lexer.RecordFailure: first named failure at char 0"
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
    public void Or_copies_children_array_so_compiled_rule_stays_immutable()
    {
        var children = new[] { Token('a'), Token('b') };
        var rule = Or(children);
        rule.Compile();

        children[0] = Token('z');

        var result = rule.Parse("a");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Or_children_property_does_not_expose_mutable_child_array()
    {
        var rule = Or(Token('a'), Token('b'));
        rule.Compile();

        Assert.That(rule.Children as Rule[], Is.Null);

        var writable = rule.Children as System.Collections.Generic.IList<Rule>;
        Assert.That(writable, Is.Not.Null);
        Assert.That(writable!.IsReadOnly, Is.True);
        Assert.Throws<NotSupportedException>(() => { writable[0] = Token('z'); });
    }

    [Test]
    public void Or_shortcut_doesnt_skip_And_with_optional_NoneOf_prefix()
    {
        // This verifies the lookahead-shortcut soundness story for an And
        // whose first child is Optional(NoneOf(...)). The Optional can
        // either match zero (so the next sibling sees the lookahead) or
        // match a single token that's outside the NoneOf's set, so the And's
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

    [Test]
    public void SourceText_on_Or_returns_the_winning_alternative_text()
    {
        // Or is Flatten by default, and naming it with .As("choice")
        // gives it a surviving composite Symbol. The composite's
        // consumed span covers what the winning alternative matched
        // (here "second"), and SourceText returns that verbatim regardless
        // of whether the alternative's leaf was Preserve or Delete.
        var rule = Or(Literal("first"), Literal("second"), Literal("third"))
            .As("choice");
        var result = rule.Parse("second");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.SourceText, Is.EqualTo("second"),
            "SourceText should return the winning alternative's match, not the first or last.");
        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo("second".Length));
    }

    [Test]
    public void SourceText_on_Or_returns_matched_text_under_every_FlattenType()
    {
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => Or(Literal("a"), Literal("b")),
            input: "b",
            expectedSourceText: "b");
    }

    [Test]
    public void Or_named_WithError_beats_same_depth_mechanical_branch_failures()
    {
        // Branches share the leading '!' prefix and each Literal records
        // a mechanical failure at the mid-read position (1). The Or
        // has a named WithError, and composite anchoring records it at
        // the position its branches reached (1), where it ties the
        // mechanical failures on depth and wins the named-beats-
        // mechanical tie-break. (The Newsboat operator case from
        // docs/ErrorArchitecture.md.)
        //
        // Both branches fail at the same offset, so this verifies the
        // equal-depth tie-break only. The pick-the-deepest behavior is
        // covered by the sibling test below, where the branches fail at
        // different offsets.
        var rule = Or(Literal("!~"), Literal("!=")).WithError("expected operator");

        var result = rule.Parse("!!");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("expected operator at line 1, column 2."));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Or_named_WithError_anchors_at_the_deepest_branch_when_branches_fail_at_different_depths()
    {
        // The sibling test above has every branch fail at the same
        // offset, so it can't tell "deepest" apart from "first" or
        // "last". Here the three branches fail at different offsets.
        // On "abcX":
        //   Literal("aZ")   fails at 1 ('Z' vs 'b')
        //   Literal("abcZ") fails at 3 ('Z' vs 'X')   <- deepest
        //   Literal("aQ")   fails at 1 ('Q' vs 'b')
        // The deepest branch is the middle one, so a correct anchor
        // can't be the first branch's depth, the last branch's, or the
        // shallowest. Only the maximum. Composite anchoring records the
        // Or's named WithError at offset 3, where it beats the middle
        // branch's mechanical failure on the named-vs-mechanical tie.
        var rule = Or(Literal("aZ"), Literal("abcZ"), Literal("aQ"))
            .WithError("expected one of the three forms");

        var result = rule.Parse("abcX");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(result.ErrorMessage, Is.EqualTo("expected one of the three forms at line 1, column 4."));
    }

    [Test]
    public void Or_rejected_branch_WithError_is_kept_as_a_near_miss()
    {
        // The email-domain shape from docs/ErrorArchitecture.md, Case 4.
        // The email branch matches "alice@" then fails wanting a domain,
        // recording its WithError at position 6 (EOF). The username
        // branch succeeds and the Or commits to it. The rejected email
        // branch's failure isn't cleared: it's a real near-miss, and at
        // position 6 it's deeper than the eventual failure of the
        // trailing '.' (position 5), so depth-primary ranking surfaces it.
        var letters = OneOf(TokenSet.Letters);
        var email = And(
            OneOrMore(letters),
            Token('@'),
            OneOrMore(letters).WithError("expected domain after '@'"));
        var username = OneOrMore(letters);
        var identifier = Or(email, username);
        var document = And(identifier, Literal("."));

        var result = document.Parse("alice@");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("expected domain after '@'"));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(6));
    }
}
