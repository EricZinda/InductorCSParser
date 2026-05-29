using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Direct tests for Lexer.SetPosition, validating the two invariants it
// enforces (in-range, on a token boundary) and the tracing / error-tracking
// safety claims documented on the method. The multi-char-token inputs reuse
// the shared UnicodeExamples corpus: GrinningFaceEmojiGrapheme (U+1F600, a
// two-char surrogate pair) and LatinEAcuteGrapheme ("e" + combining acute, a
// two-char decomposed cluster).
[TestFixture]
public class LexerSetPositionTests
{
    // --- Invariant 1: in-range ---

    [Test]
    public void SetPosition_to_a_boundary_in_range_moves_the_cursor()
    {
        var lexer = new Lexer("abc");
        lexer.SetPosition(2);
        Assert.That(lexer.Position, Is.EqualTo(2));
        Assert.That(lexer.Read().RuneValue, Is.EqualTo((int)'c'));
    }

    [Test]
    public void SetPosition_to_zero_and_to_end_are_allowed()
    {
        var lexer = new Lexer("abc");
        lexer.SetPosition(3); // EndPosition is always a boundary (EOF cursor).
        Assert.That(lexer.IsEof, Is.True);
        lexer.SetPosition(0);
        Assert.That(lexer.Position, Is.EqualTo(0));
        Assert.That(lexer.IsEof, Is.False);
    }

    [Test]
    public void SetPosition_negative_throws_ArgumentOutOfRangeException()
    {
        var lexer = new Lexer("abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => lexer.SetPosition(-1));
    }

    [Test]
    public void SetPosition_past_end_throws_ArgumentOutOfRangeException()
    {
        var lexer = new Lexer("abc"); // EndPosition == 3
        Assert.Throws<ArgumentOutOfRangeException>(() => lexer.SetPosition(4));
    }

    // --- Invariant 2: on a token boundary ---

    [Test]
    public void SetPosition_mid_grapheme_cluster_throws_ArgumentException()
    {
        // "e" + combining acute (U+0301) is one grapheme cluster spanning
        // chars [0, 2). Offset 1 sits between the base and the mark.
        var lexer = new Lexer(LatinEAcuteGrapheme);
        Assert.That(lexer.PeekTokenLength(0), Is.EqualTo(2),
            "precondition: the input is one two-char cluster");

        Assert.Throws<ArgumentException>(() => lexer.SetPosition(1));
        // Both real boundaries are accepted.
        Assert.DoesNotThrow(() => lexer.SetPosition(0));
        Assert.DoesNotThrow(() => lexer.SetPosition(2));
    }

    [Test]
    public void SetPosition_inside_surrogate_pair_throws_in_grapheme_mode()
    {
        // The emoji is one grapheme cluster of two chars; offset 1 is not a
        // cluster start.
        var lexer = new Lexer(GrinningFaceEmojiGrapheme);
        Assert.Throws<ArgumentException>(() => lexer.SetPosition(1));
        Assert.DoesNotThrow(() => lexer.SetPosition(0));
        Assert.DoesNotThrow(() => lexer.SetPosition(2));
    }

    [Test]
    public void SetPosition_inside_surrogate_pair_throws_in_rune_mode()
    {
        // Rune mode (the WithinToken sub-lexer mode) tokenizes by rune, so the
        // boundary check is "not inside a surrogate pair" rather than the
        // grapheme-cluster check.
        var lexer = new Lexer(GrinningFaceEmojiGrapheme, oneRunePerToken: true);
        Assert.Throws<ArgumentException>(() => lexer.SetPosition(1));
        Assert.DoesNotThrow(() => lexer.SetPosition(0));
        Assert.DoesNotThrow(() => lexer.SetPosition(2));
    }

    // --- Claim: error tracking is unaffected by cursor moves ---

    [Test]
    public void RecordFailure_records_the_explicit_position_not_the_live_cursor()
    {
        // Jump the cursor to 3, then record at 1: the failure lands at 1, the
        // position passed, not at the cursor.
        var lexer = new Lexer("abcde");
        lexer.SetPosition(3);
        lexer.RecordFailure(1, "boom");

        Assert.That(lexer.DeepestFailurePosition, Is.EqualTo(1));
        Assert.That(lexer.DeepestFailureMessage, Is.EqualTo("boom"));
    }

    [Test]
    public void A_failure_recorded_at_Position_after_a_jump_is_a_real_source_offset()
    {
        // The flip side: a rule that passes lexer.Position records at the
        // jumped cursor, and because SetPosition only accepts in-range
        // boundaries, that value indexes the real input.
        var lexer = new Lexer("abcde");
        lexer.SetPosition(3);
        lexer.RecordFailure(lexer.Position, "here");

        Assert.That(lexer.DeepestFailurePosition, Is.EqualTo(3));
        Assert.That(lexer.Input[lexer.DeepestFailurePosition], Is.EqualTo('d'),
            "the recorded offset indexes the original input");
    }

    [Test]
    public void A_backward_jump_does_not_lower_the_deepest_failure_high_water_mark()
    {
        // The deepest-failure tracker is max-only. Recording a shallow failure
        // after jumping the cursor backward can't rewind it past a deeper one.
        var lexer = new Lexer("abcde");
        lexer.RecordFailure(4, "deep");
        lexer.SetPosition(2);
        lexer.RecordFailure(2, "shallow");

        Assert.That(lexer.DeepestFailurePosition, Is.EqualTo(4));
        Assert.That(lexer.DeepestFailureMessage, Is.EqualTo("deep"));
    }

    [Test]
    public void RecordCompositeFailure_anchors_at_subtree_depth_independent_of_the_cursor()
    {
        // A composite anchors its .WithError at the deepest position its
        // subtree reached (the high-water mark), floored at the position it
        // passes, never at the live cursor. Jump the cursor backward between
        // the child failure and the composite's record to prove it's ignored.
        var lexer = new Lexer("abcde");
        lexer.RecordFailure(4, null);   // a child's mechanical failure, deep in the subtree
        lexer.SetPosition(0);           // cursor jumps away
        lexer.RecordCompositeFailure(floorPosition: 1, errorMessage: "composite", forced: false);

        Assert.That(lexer.DeepestFailurePosition, Is.EqualTo(4),
            "anchored at max(subtree depth 4, floor 1), not at the cursor (0)");
        Assert.That(lexer.DeepestFailureMessage, Is.EqualTo("composite"));
    }

    [Test]
    public void A_committed_transaction_keeps_a_SetPosition_jump_and_its_failures()
    {
        var lexer = new Lexer("abcde");
        using (var transaction = lexer.BeginTransaction())
        {
            lexer.SetPosition(3);
            lexer.RecordFailure(3, "kept");
            transaction.Commit();
        }

        Assert.That(lexer.Position, Is.EqualTo(3), "commit keeps the jumped cursor");
        Assert.That(lexer.DeepestFailureMessage, Is.EqualTo("kept"), "failures survive a commit");
    }

    [Test]
    public void A_jump_inside_a_probe_leaves_no_position_or_failure_residue()
    {
        // A cursor jump and a failure recorded inside a probe are both undone
        // when the probe disposes without committing.
        var lexer = new Lexer("abcde");
        using (lexer.BeginProbe())
        {
            lexer.SetPosition(4);
            lexer.RecordFailure(4, "inner");
        }

        Assert.That(lexer.Position, Is.EqualTo(0), "the probe restores the cursor");
        Assert.That(lexer.DeepestFailureMessage, Is.Null, "the probe restores the failure state");
    }

    // --- Claim: tracing is unaffected but not automatic ---

    [Test]
    public void SetPosition_emits_no_trace_line_and_does_not_corrupt_a_later_Read_line()
    {
        var sink = new StringWriter();
        var lexer = new Lexer("abc", context: null, traceSink: sink, traceLevel: TraceLevel.Diagnostic);

        // Several jumps, no Read: nothing is traced.
        lexer.SetPosition(2);
        lexer.SetPosition(1);
        Assert.That(sink.ToString(), Is.Empty,
            "SetPosition adjusts only the cursor and emits no trace line");

        // The next Read traces normally, with the correct token and post-read
        // position for where the cursor was left: 'b' at offset 1, consumed to 2.
        lexer.Read();
        string trace = sink.ToString();
        Assert.That(trace, Does.Contain("Lexer.Read"), "Read emits its own line");
        Assert.That(trace, Does.Contain("'b'"), "the line names the token actually read at the jumped-to offset");
        Assert.That(trace, Does.Contain("Consumed: 2"), "and the post-read position is correct");
    }

    [Test]
    public void A_rule_that_advances_with_SetPosition_emits_its_own_trace_and_no_Read_lines()
    {
        // The documented pattern for a bulk-advancing rule: walk the cursor by
        // hand, then emit one summary line via TraceSuccess. The summary is the
        // only record of the consumed span, since SetPosition itself traces
        // nothing and the rule never calls Read.
        var sink = new StringWriter();
        var result = new JumpToEndRule().Parse("abc", new ParseOptions { TraceSink = sink });
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        string trace = sink.ToString();
        Assert.That(trace, Does.Contain("JumpToEnd"), "the rule's own TraceSuccess summary appears");
        Assert.That(trace, Does.Contain("jumped 3 chars"), "and it documents the whole consumed span");
        Assert.That(trace, Does.Not.Contain("Lexer.Read"),
            "advancing by SetPosition produced no per-token Read lines");
    }

    // A rule that consumes the entire input by jumping the cursor with
    // SetPosition (never calling Read), then emits one summary trace line.
    private sealed class JumpToEndRule : Rule
    {
        public JumpToEndRule() : base(FlattenType.Delete, emitsLeaf: false)
        {
            SetTraceName("JumpToEnd");
        }

        protected override Symbol? TryParseRule(
            Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
        {
            lexer.SetPosition(lexer.Input.Length);
            TraceSuccess(lexer, $"jumped {lexer.Position - startPosition} chars");
            return Symbol.Discarded;
        }
    }
}
