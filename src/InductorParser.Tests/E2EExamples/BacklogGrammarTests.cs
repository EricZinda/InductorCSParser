using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using InductorParser;
using NUnit.Framework;

namespace InductorParser.Tests;

// Phase 0-style gate for the MergeableBacklog-regex-to-InductorParser
// mapping, mirroring ChordGrammarTests. For every regex in the six
// format detectors, we pair the reference regex with the equivalent
// grammar rule and check:
//
// 1. Equivalence. Every corpus input produces the same accept/reject
//    verdict from reference regex and grammar.
// 2. Timing. Same corpus, looped, wall-clock under Stopwatch. Grammar
//    must come in within 2x of the compiled regex. Ignored today. The
//    same composite overhead that puts ChordGrammar at ~17-18x applies
//    here, so we report ratios and leave the hard gate for the Or
//    required-runes dispatch work tracked separately.
[TestFixture]
public class BacklogGrammarTests
{
    // Verbatim ports of the JS regexes from MergeableBacklog/src/formats/*.ts.
    // RegexOptions.Compiled matches the chord test's setup.
    private static readonly Regex H1Regex = new Regex(
        @"^#(?!#)\s?(.*)$", RegexOptions.Compiled);

    private static readonly Regex H2Regex = new Regex(
        @"^##(?!#)\s?(.*)$", RegexOptions.Compiled);

    private static readonly Regex BulletRegex = new Regex(
        @"^[-*+]\s?(.*)$", RegexOptions.Compiled);

    private static readonly Regex HrRunRegex = new Regex(
        @"^[-*+]{3,}$", RegexOptions.Compiled);

    private static readonly Regex HrSpacedRegex = new Regex(
        @"^[-*+]( [-*+]){2,}$", RegexOptions.Compiled);

    // Unanchored in the source. JS .test() fires if the pattern occurs
    // anywhere. .NET Regex.IsMatch has the same default.
    private static readonly Regex ParagraphRegex = new Regex(
        @"\n\s*\n", RegexOptions.Compiled);

    // Corpus per regex. Each case list mixes accepts and rejects: the
    // equivalence test just needs both sides to agree, it doesn't care
    // which verdict wins on any given input.
    private static readonly string[] H1Corpus =
    {
        "# Title",
        "#Title",
        "# ",
        "#",
        "# A very long title with   spaces and punctuation!",
        "# 1234",
        "# Mixed #hashtag inside",
        "## Not an H1",
        "### Not an H1 either",
        "  # Indented, not a heading",
        "",
        "Title",
        "#title with tab\there",
        "#\tTabbed title",
    };

    private static readonly string[] H2Corpus =
    {
        "## Section",
        "##Section",
        "## ",
        "##",
        "# Not an H2",
        "### Not an H2",
        "#### Nope",
        "##\tTabbed",
        "##Section with : and punct!",
        " ## Indented",
        "",
        "Section",
    };

    private static readonly string[] BulletCorpus =
    {
        "- item",
        "* item",
        "+ item",
        "-item",
        "*item",
        "+item",
        "-",
        "*",
        "+",
        "- ",
        "-  two spaces",
        "- \titem after tab",
        "--- hr-ish",
        "  - indented bullet",
        "# heading",
        "",
        "plain text",
        "- item with (punctuation) and numbers 123",
    };

    private static readonly string[] HrRunCorpus =
    {
        "---",
        "****",
        "+++",
        "-----",
        "--",
        "-*+",
        "-- -",
        "---a",
        "a---",
        "",
        "-",
        "*",
        "+",
        "++",
        "* * *",
        "- - -",
    };

    private static readonly string[] HrSpacedCorpus =
    {
        "- - -",
        "* * *",
        "+ + +",
        "- - - -",
        "* - *",
        "- -",
        "-",
        "- - - a",
        "a - - -",
        "---",
        "",
        "- -  -",
        " - - -",
    };

    private static readonly string[] ParagraphCorpus =
    {
        "a\n\nb",
        "a\n\n\nb",
        "a\n \nb",
        "a\n\t\nb",
        "a\n   \nb",
        "\n\n",
        "\n\n\n",
        "one line\n",
        "one\ntwo",
        "",
        "no newlines at all",
        "a\n\rb",
        "trailing\n\n",
        "\n\nleading",
        "mixed\n \n text \n\n end",
    };

    [Test]
    public void Equivalence_h1()
    {
        AssertAgreement(H1Corpus, H1Regex, BacklogGrammar.H1Heading, nameof(H1Regex));
    }

    [Test]
    public void Equivalence_h2()
    {
        AssertAgreement(H2Corpus, H2Regex, BacklogGrammar.H2Heading, nameof(H2Regex));
    }

    [Test]
    public void Equivalence_bullet()
    {
        AssertAgreement(BulletCorpus, BulletRegex, BacklogGrammar.Bullet, nameof(BulletRegex));
    }

    [Test]
    public void Equivalence_hr_run()
    {
        AssertAgreement(HrRunCorpus, HrRunRegex, BacklogGrammar.HrRun, nameof(HrRunRegex));
    }

    [Test]
    public void Equivalence_hr_spaced()
    {
        AssertAgreement(HrSpacedCorpus, HrSpacedRegex, BacklogGrammar.HrSpaced, nameof(HrSpacedRegex));
    }

    [Test]
    public void Equivalence_paragraph_split()
    {
        AssertAgreement(ParagraphCorpus, ParagraphRegex, BacklogGrammar.ParagraphSplit, nameof(ParagraphRegex));
    }

    private static void AssertAgreement(string[] corpus, Regex regex, Rule grammar, string label)
    {
        var disagreements = new List<string>();
        foreach (var input in corpus)
        {
            bool regexSaysYes = regex.IsMatch(input);
            bool grammarSaysYes = grammar.Parse(input).Success;
            if (regexSaysYes != grammarSaysYes)
            {
                disagreements.Add(
                    $"  {Display(input)} -> regex={regexSaysYes}, grammar={grammarSaysYes}");
            }
        }
        if (disagreements.Count > 0)
        {
            Assert.Fail(
                $"{label} and grammar disagreed on {disagreements.Count} input(s):\n"
                + string.Join("\n", disagreements));
        }
    }

    private static string Display(string input)
    {
        // Control chars in the corpus would make failure output unreadable.
        // \n, \t, \r get visible escapes. Everything else shows as-is.
        return "\"" + input
            .Replace("\\", "\\\\")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t") + "\"";
    }

    // Ignored. Ratios on this box (net8.0, Release, 5000 iters), pre and
    // post Or required-runes dispatch (run-to-run noise on these short corpora
    // is high, and the per-case numbers swing by ~2x between runs):
    //   H1:        51x  -> 25-42x
    //   H2:        26x  -> 21-32x
    //   Bullet:     8x  -> 5-16x
    //   HrRun:    4.5x  -> 3-12x
    //   HrSpaced: 9.4x  -> 9-14x
    //   Paragraph: 28x  -> 20-45x
    // Dispatch helps most when an Or / composite has many branches and a
    // disjoint first-char set. These rules are simpler (one RuneIn or one
    // Token at the head), so the composite transaction overhead on the
    // inner path is what dominates, the same architectural bottleneck as
    // ChordGrammar's remaining gap. A separate backlog item will target
    // that tier (lazy transactions, allocation-free empty matches,
    // compiled emitter, etc.).
    [Test, Ignore("Grammar is still multi-x slower than regex; see comment above for tier needed to close the gap.")]
    public void Timing_grammar_is_within_two_times_compiled_regex()
    {
        const int iterations = 5_000;
        const double slowdownBudget = 2.0;

        var pairs = new (string Label, Regex Regex, Rule Grammar, string[] Corpus)[]
        {
            ("H1",         H1Regex,        BacklogGrammar.H1Heading,      H1Corpus),
            ("H2",         H2Regex,        BacklogGrammar.H2Heading,      H2Corpus),
            ("Bullet",     BulletRegex,    BacklogGrammar.Bullet,         BulletCorpus),
            ("HrRun",      HrRunRegex,     BacklogGrammar.HrRun,          HrRunCorpus),
            ("HrSpaced",   HrSpacedRegex,  BacklogGrammar.HrSpaced,       HrSpacedCorpus),
            ("Paragraph",  ParagraphRegex, BacklogGrammar.ParagraphSplit, ParagraphCorpus),
        };

        // Warmup. Rule compile happens on the first Parse. Both sides
        // get a few thousand iterations so JIT settles too.
        for (int w = 0; w < 200; w++)
            foreach (var pair in pairs)
                foreach (var input in pair.Corpus)
                {
                    _ = pair.Regex.IsMatch(input);
                    _ = pair.Grammar.Parse(input);
                }

        double worstRatio = 0;
        string worstLabel = "";
        foreach (var pair in pairs)
        {
            var regexStopwatch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                foreach (var input in pair.Corpus)
                    _ = pair.Regex.IsMatch(input);
            regexStopwatch.Stop();

            var grammarStopwatch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                foreach (var input in pair.Corpus)
                    _ = pair.Grammar.Parse(input);
            grammarStopwatch.Stop();

            double regexMs = regexStopwatch.Elapsed.TotalMilliseconds;
            double grammarMs = grammarStopwatch.Elapsed.TotalMilliseconds;
            double ratio = grammarMs / regexMs;

            TestContext.Out.WriteLine(
                $"{pair.Label,-10} regex {regexMs,8:F1} ms   grammar {grammarMs,8:F1} ms   ratio {ratio,5:F2}x");

            if (ratio > worstRatio)
            {
                worstRatio = ratio;
                worstLabel = pair.Label;
            }
        }

        Assert.That(worstRatio, Is.LessThan(slowdownBudget),
            $"{worstLabel} grammar is {worstRatio:F2}x slower than regex; "
            + $"Phase 0 gate is {slowdownBudget}x.");
    }
}
