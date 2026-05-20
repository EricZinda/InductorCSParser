using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using InductorParser.Lexing;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests.Lexing;

// Direct unit tests for GraphemeClusterIndex. Each fixture builds a
// reference list of cluster boundaries via StringInfo's
// TextElementEnumerator (the source of truth) and confirms the
// GraphemeClusterIndex agrees on IsClusterStart, LengthAt, and
// CountClustersUpTo for every offset.
[TestFixture]
public class GraphemeClusterIndexTests
{
    private static List<int> ReferenceBoundaries(string input)
    {
        var boundaries = new List<int> { 0 };
        var enumerator = StringInfo.GetTextElementEnumerator(input);
        while (enumerator.MoveNext())
        {
            int idx = enumerator.ElementIndex;
            if (idx > 0) boundaries.Add(idx);
        }
        boundaries.Add(input.Length);
        return boundaries;
    }

    private static void AssertMatchesReference(string input)
    {
        var index = GraphemeClusterIndex.For(input);
        var reference = ReferenceBoundaries(input);
        var asSet = new HashSet<int>(reference);

        for (int i = 0; i <= input.Length; i++)
        {
            bool expected = asSet.Contains(i) && (input.Length > 0 || i == 0);
            Assert.That(index.IsClusterStart(i), Is.EqualTo(expected),
                $"IsClusterStart({i}) for input length {input.Length}");
        }

        for (int i = 0; i < reference.Count - 1; i++)
        {
            int start = reference[i];
            int expectedLen = reference[i + 1] - start;
            Assert.That(index.LengthAt(start), Is.EqualTo(expectedLen),
                $"LengthAt({start})");
        }
        Assert.That(index.LengthAt(input.Length), Is.EqualTo(0),
            "LengthAt(end) is 0");

        for (int i = 0; i <= input.Length; i++)
        {
            // CountClustersUpTo(i) is the count of clusters FULLY
            // contained in [0, i): a cluster counts only once its end
            // boundary is at or before i. The cluster ends are the
            // reference boundaries other than the leading 0.
            int expected = 0;
            foreach (int b in reference)
            {
                if (b == 0) continue;
                if (b <= i) expected++;
                else break;
            }
            Assert.That(index.CountClustersUpTo(i), Is.EqualTo(expected),
                $"CountClustersUpTo({i})");
        }
    }

    [Test]
    public void Empty_input()
    {
        AssertMatchesReference(string.Empty);
        var index = GraphemeClusterIndex.For(string.Empty);
        Assert.That(index.IsClusterStart(0), Is.True, "EOF is always a boundary");
        Assert.That(index.LengthAt(0), Is.EqualTo(0));
        Assert.That(index.CountClustersUpTo(0), Is.EqualTo(0));
    }

    [Test]
    public void Pure_ASCII_every_offset_is_a_boundary()
    {
        AssertMatchesReference("hello world");
    }

    [Test]
    public void Surrogate_pair_emoji_is_one_two_char_cluster()
    {
        // U+1F600 grinning face = "😀" in UTF-16.
        AssertMatchesReference(UnicodeExamples.GrinningFaceEmojiGrapheme);
    }

    [Test]
    public void CRLF_is_one_two_char_cluster()
    {
        AssertMatchesReference("a\r\nb");
    }

    [Test]
    public void Combining_mark_glues_to_base()
    {
        // e + U+0301 (combining acute accent) = é
        AssertMatchesReference(UnicodeExamples.LatinEAcuteGrapheme);
    }

    [Test]
    public void Stacked_combining_marks_glue_to_base()
    {
        // c + U+0301 + U+0302 = c with acute and circumflex stacked.
        AssertMatchesReference(Canary("ć̂", "latin small letter c + combining acute accent + combining circumflex accent", 0x0063, 0x0301, 0x0302));
    }

    [Test]
    public void Emoji_ZWJ_sequence_is_one_cluster()
    {
        // Man + ZWJ + Woman = one cluster under GB11 on .NET 5+.
        AssertMatchesReference(Canary("👨‍👩", "man + zero width joiner + woman", 0x1F468, 0x200D, 0x1F469));
    }

    [Test]
    public void Variation_selector_glues_to_base()
    {
        // # + U+FE0F (VS16) = keycap base
        AssertMatchesReference(Canary("#️", "number sign + variation selector-16", 0x0023, 0xFE0F));
    }

    [Test]
    public void Indic_conjunct_matches_runtime()
    {
        // क + virama + ष. UAX #29 rev. 39 (GB9c) keeps these glued as
        // one cluster; earlier revisions break before the trailing
        // consonant. Either way the index agrees with StringInfo,
        // because both walk the same enumerator. This test verifies that
        // agreement on whichever runtime is hosting the suite.
        AssertMatchesReference(Canary("क्ष", "devanagari letter ka + devanagari sign virama + devanagari letter ssa", 0x0915, 0x094D, 0x0937));
    }

    [Test]
    public void Mixed_input_with_multiple_cluster_shapes()
    {
        AssertMatchesReference($"ab\r\nc{UnicodeExamples.CombiningAcuteText}d{UnicodeExamples.GrinningFaceEmojiGrapheme}e");
    }

    [Test]
    public void For_returns_same_instance_for_same_string_reference()
    {
        string input = "hello";
        var first = GraphemeClusterIndex.For(input);
        var second = GraphemeClusterIndex.For(input);
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void IsClusterStart_at_end_of_input_is_true()
    {
        // The post-validation gate in the scanner-skip relies on this:
        // a candidate landing at exactly input.Length is the EOF
        // sentinel boundary, never mid-cluster.
        var index = GraphemeClusterIndex.For("abc");
        Assert.That(index.IsClusterStart(3), Is.True);
    }

    [Test]
    public void IsClusterStart_at_negative_or_past_end_is_false()
    {
        var index = GraphemeClusterIndex.For("abc");
        Assert.That(index.IsClusterStart(-1), Is.False);
        Assert.That(index.IsClusterStart(4), Is.False);
    }

    [Test]
    public void LengthAt_non_cluster_start_throws()
    {
        // CRLF: position 1 (the LF) is mid-cluster. Asking for the
        // length of "the cluster starting at the LF" is incoherent.
        // The API throws an InductorParserBugException (which derives
        // from InvalidOperationException) so a Lexer bug that handed
        // us a mid-cluster offset surfaces immediately and the bug
        // exception type identifies it as an internal invariant break,
        // not a user-API misuse.
        var index = GraphemeClusterIndex.For("\r\n");
        Assert.Throws<InductorParserBugException>(() => index.LengthAt(1));
    }

    [Test]
    public void Concurrent_walks_of_same_input_dont_corrupt_index()
    {
        // GraphemeClusterIndex.For caches by string instance, so multiple
        // threads parsing the same input share one index instance. The
        // instance has mutable state: a TextElementEnumerator that gets
        // advanced and a bool[] _isStart array that gets written during
        // EnsureWalkedTo. Without synchronization the threads race on the
        // enumerator and lose _isStart updates: positions that ARE
        // grapheme cluster starts end up unmarked, IsClusterStart returns
        // the wrong answer for them, and the next LengthAt call against
        // such a position throws the "not a cluster start" invariant
        // violation.
        //
        // This is a DoS / correctness concern, not just a perf one. An
        // application that parses the same interned or cached string from
        // multiple threads (a web server handling identical request
        // bodies, a config file re-parsed in parallel, or simply a string
        // literal that two requests reach at the same time) can crash
        // with an exception that single-threaded test runs never see.
        // LateBoundRule's own doc claims "concurrent parses of the
        // compiled grammar need no synchronization," and the test
        // verifies that promise at the cache layer.
        //
        // Trial-loop and fresh-per-trial input mean each trial starts
        // with an empty cache and a long enough walk that the threads
        // overlap inside EnsureWalkedTo, which is where the race lives.
        // The mixed-cluster shape (CRLF and ASCII alternated) forces
        // StringInfo to compute real cluster lengths so each MoveNext
        // does observable work the race can interfere with.
        for (int trial = 0; trial < 20; trial++)
        {
            string input = string.Concat(Enumerable.Range(0, 4000)
                .Select(rowIndex => rowIndex % 2 == 0 ? "a" : "\r\n"));
            var index = GraphemeClusterIndex.For(input);

            var expectedStarts = new HashSet<int>();
            var referenceEnumerator = StringInfo.GetTextElementEnumerator(input);
            while (referenceEnumerator.MoveNext())
                expectedStarts.Add(referenceEnumerator.ElementIndex);
            expectedStarts.Add(input.Length);

            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[Math.Max(8, Environment.ProcessorCount * 2)];
            for (int taskIndex = 0; taskIndex < tasks.Length; taskIndex++)
            {
                tasks[taskIndex] = Task.Run(() =>
                {
                    try
                    {
                        for (int position = 0; position <= input.Length; position++)
                        {
                            bool isStart = index.IsClusterStart(position);
                            if (isStart && position < input.Length)
                                index.LengthAt(position);
                        }
                    }
                    catch (Exception exception)
                    {
                        exceptions.Add(exception);
                    }
                });
            }
            Task.WaitAll(tasks);

            Assert.That(exceptions, Is.Empty,
                $"Trial {trial}: concurrent walks must not throw, " +
                $"but got: {(exceptions.IsEmpty ? "" : exceptions.First().ToString())}");
            for (int position = 0; position <= input.Length; position++)
            {
                Assert.That(index.IsClusterStart(position),
                    Is.EqualTo(expectedStarts.Contains(position)),
                    $"Trial {trial}: IsClusterStart({position}) disagreed with " +
                    $"the reference walk, which means a concurrent MoveNext race " +
                    $"dropped an _isStart update.");
            }
        }
    }

    [Test]
    public void Out_of_order_queries_still_return_correct_results()
    {
        // Walk doesn't reset; querying near the end first should still
        // give correct answers for earlier positions. Offsets in
        // "a\r\nb́c": 0=a, 1=\r, 2=\n, 3=b, 4=́, 5=c.
        // Clusters: [a], [\r\n], [b́], [c]; boundaries at
        // 0, 1, 3, 5, 6.
        string input = $"a\r\nb{UnicodeExamples.CombiningAcuteText}c";
        var index = GraphemeClusterIndex.For(input);
        Assert.That(index.IsClusterStart(input.Length), Is.True, "EOF");
        Assert.That(index.IsClusterStart(0), Is.True, "a");
        Assert.That(index.IsClusterStart(1), Is.True, "CR (start of CRLF cluster)");
        Assert.That(index.IsClusterStart(2), Is.False, "LF inside CRLF");
        Assert.That(index.IsClusterStart(3), Is.True, "b (start of b+combining cluster)");
        Assert.That(index.IsClusterStart(4), Is.False, "combining mark inside cluster");
        Assert.That(index.IsClusterStart(5), Is.True, "c");
    }
}
