using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NUnit.Framework;
using InductorParser;
using InductorParser.StateMachine;
using static InductorParser.Rules;

namespace InductorParser.Tests.StateMachine;

// Hand-timed Stopwatch comparison between the recursive evaluator and
// the state-machine evaluator on the chord and backlog grammars. Both
// grammars are part of the existing test suite and have curated
// corpora of representative inputs, so this is a more realistic
// comparison than the synthetic StateMachineBench grammars.
//
// ChordGrammar: heavy LiteralIgnoreAsciiCase + FirstOf-of-keywords use.
// Stress-tests the new MatchLiteralIgnoreAsciiCase opcode plus the
// existing FirstOf first-rune-skip and atomic-inner BetweenInclusive paths.
//
// BacklogGrammar: mostly natively lowered (Grapheme, OneOf, NoneOf,
// AnyToken, ZeroOrMore, AtLeast, Not, Optional, Eof). One rule
// (ParagraphSplit) routes through the bridge for its rule-stoppered
// ScanUntil; the others run native.
//
// The test is [Explicit] so it doesn't run on the regular suite. To
// see the comparison, run with --filter:
//   dotnet test --filter FullyQualifiedName~StateMachineGrammarCompareTests
[TestFixture, Explicit("Hand-timed comparison; run on demand.")]
public class StateMachineGrammarCompareTests
{
    // Chord grammar's reference regex. Verbatim copy of the original
    // UnityTabs regex from ChordGrammarTests.cs.
    private static readonly Regex ChordRegex = new Regex(
        @"^[A-Ga-g][#b♯♭x]*(maj|min|m|dim|°|o|aug|\+|sus[24]?|5)?(6|7|9|11|13)?(maj|M|Δ|m|ø|°)?(7|9|11|13)?(add[2469]|add1[13]|b5|#5|b9|#9|#11|b13|no[357]|sus[24]?|alt)*(\/[A-Ga-g][#b♯♭x]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Backlog reference regexes, verbatim ports from
    // MergeableBacklog/src/formats/*.ts as in BacklogGrammarTests.cs.
    private static readonly Regex H1Regex = new Regex(@"^#(?!#)\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex H2Regex = new Regex(@"^##(?!#)\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new Regex(@"^[-*+]\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex HrRunRegex = new Regex(@"^[-*+]{3,}$", RegexOptions.Compiled);
    private static readonly Regex HrSpacedRegex = new Regex(@"^[-*+]( [-*+]){2,}$", RegexOptions.Compiled);
    private static readonly Regex ParagraphRegex = new Regex(@"\n\s*\n", RegexOptions.Compiled);

    [Test]
    public void Chord_grammar_state_machine_vs_recursive_vs_regex()
    {
        Compare("Chord", ChordRegex, ChordGrammar.Chord, ChordCorpus, 5000);
    }

    [Test]
    public void Chord_matcher_grammar_state_machine_vs_recursive_vs_regex()
    {
        // "Matcher" variant: same shape as ChordGrammar.Chord but every
        // leaf rule that defaults to Preserve (OneOf) is forced to
        // Delete, so the parse builds zero Symbols. This is the apples-
        // to-apples shape vs Regex.IsMatch, which also produces no
        // tree. Routes the state-machine evaluator through TryMatch,
        // which skips TreeBuilder + ParseResult construction so the
        // comparison really is "did this match", same shape as
        // Regex.IsMatch.
        CompareMatcher("Chord (matcher)", ChordRegex, ChordMatcherGrammar, ChordCorpus, 5000);
    }

    [Test]
    public void Backlog_h1_state_machine_vs_recursive_vs_regex()
    {
        Compare("Backlog/H1", H1Regex, BacklogGrammar.H1Heading, BacklogH1, 5000);
    }

    [Test]
    public void Backlog_h2_state_machine_vs_recursive_vs_regex()
    {
        Compare("Backlog/H2", H2Regex, BacklogGrammar.H2Heading, BacklogH2, 5000);
    }

    [Test]
    public void Backlog_bullet_state_machine_vs_recursive_vs_regex()
    {
        Compare("Backlog/Bullet", BulletRegex, BacklogGrammar.Bullet, BacklogBullet, 5000);
    }

    [Test]
    public void Backlog_hr_run_state_machine_vs_recursive_vs_regex()
    {
        Compare("Backlog/HrRun", HrRunRegex, BacklogGrammar.HrRun, BacklogHrRun, 5000);
    }

    [Test]
    public void Backlog_hr_spaced_state_machine_vs_recursive_vs_regex()
    {
        Compare("Backlog/HrSpaced", HrSpacedRegex, BacklogGrammar.HrSpaced, BacklogHrSpaced, 5000);
    }

    [Test]
    public void Backlog_paragraph_state_machine_vs_recursive_vs_regex()
    {
        // ParagraphSplit uses ScanUntil with a Rule-based stopper,
        // which routes through the BridgeToRecursive opcode in the
        // state-machine evaluator. So this row measures the bridge
        // overhead vs the pure recursive path.
        Compare("Backlog/Para (bridge)", ParagraphRegex, BacklogGrammar.ParagraphSplit, BacklogParagraph, 5000);
    }

    [Test]
    public void Backlog_h1_matcher_state_machine_vs_recursive_vs_regex()
    {
        CompareMatcher("Backlog/H1 (matcher)", H1Regex, BacklogMatcher.H1Heading, BacklogH1, 5000);
    }

    [Test]
    public void Backlog_bullet_matcher_state_machine_vs_recursive_vs_regex()
    {
        CompareMatcher("Backlog/Bullet (matcher)", BulletRegex, BacklogMatcher.Bullet, BacklogBullet, 5000);
    }

    [Test]
    public void Backlog_hr_run_matcher_state_machine_vs_recursive_vs_regex()
    {
        CompareMatcher("Backlog/HrRun (matcher)", HrRunRegex, BacklogMatcher.HrRun, BacklogHrRun, 5000);
    }

    [Test]
    public void Backlog_hr_spaced_matcher_state_machine_vs_recursive_vs_regex()
    {
        CompareMatcher("Backlog/HrSpaced (matcher)", HrSpacedRegex, BacklogMatcher.HrSpaced, BacklogHrSpaced, 5000);
    }

    // ---- Matcher-only grammars ----
    //
    // Same rule shape as the corresponding "production" grammar but
    // every leaf that the default would Preserve is forced to Delete,
    // so the parse builds zero Symbols on success. Mirrors what
    // Regex.IsMatch returns: a single bool, no tree. The point is to
    // see how fast the parsers actually parse when no tree-building
    // overhead is in the picture, since that's the only thing
    // Regex.IsMatch is doing.

    private static readonly Rule ChordMatcherGrammar = BuildChordMatcher();

    private static Rule BuildChordMatcher()
    {
        var root = OneOf("ABCDEFGabcdefg").Delete();
        var accidental = OneOf("#bB♯♭xX").Delete();

        var quality1 = FirstOf(
            LiteralIgnoreAsciiCase("maj"),
            LiteralIgnoreAsciiCase("min"),
            LiteralIgnoreAsciiCase("dim"),
            LiteralIgnoreAsciiCase("aug"),
            AllOf(LiteralIgnoreAsciiCase("sus"), Optional(OneOf("24").Delete())),
            LiteralIgnoreAsciiCase("m"),
            LiteralIgnoreAsciiCase("o"),
            Grapheme('°'),
            Grapheme('+'),
            Grapheme('5')
        );

        var ext1 = FirstOf(
            LiteralIgnoreAsciiCase("11"),
            LiteralIgnoreAsciiCase("13"),
            Grapheme('6'),
            Grapheme('7'),
            Grapheme('9')
        );

        var quality2 = FirstOf(
            LiteralIgnoreAsciiCase("maj"),
            LiteralIgnoreAsciiCase("m"),
            Grapheme('Δ'),
            Grapheme('ø'),
            Grapheme('°')
        );

        var ext2 = FirstOf(
            LiteralIgnoreAsciiCase("11"),
            LiteralIgnoreAsciiCase("13"),
            Grapheme('7'),
            Grapheme('9')
        );

        var addMod = FirstOf(
            AllOf(LiteralIgnoreAsciiCase("add1"), OneOf("13").Delete()),
            AllOf(LiteralIgnoreAsciiCase("add"), OneOf("2469").Delete()),
            LiteralIgnoreAsciiCase("b13"),
            LiteralIgnoreAsciiCase("#11"),
            LiteralIgnoreAsciiCase("b5"),
            LiteralIgnoreAsciiCase("b9"),
            LiteralIgnoreAsciiCase("#5"),
            LiteralIgnoreAsciiCase("#9"),
            AllOf(LiteralIgnoreAsciiCase("no"), OneOf("357").Delete()),
            AllOf(LiteralIgnoreAsciiCase("sus"), Optional(OneOf("24").Delete())),
            LiteralIgnoreAsciiCase("alt")
        );

        var slashBass = AllOf(Grapheme('/'), root, ZeroOrMore(accidental));

        return AllOf(
            root,
            ZeroOrMore(accidental),
            Optional(quality1),
            Optional(ext1),
            Optional(quality2),
            Optional(ext2),
            ZeroOrMore(addMod),
            Optional(slashBass),
            Eof()
        );
    }

    private static class BacklogMatcher
    {
        // RestOfLine: ZeroOrMore(NoneOf("\n").Delete()).
        // OptionalOneWhitespace: Optional(OneOf(...).Delete()).
        // Both produce zero Symbols on success.
        private static readonly Rule RestOfLine =
            ZeroOrMore(NoneOf("\n").Delete());

        private static readonly Rule OptionalOneWhitespace =
            Optional(OneOf(RuneSet.InlineWhitespace).Delete());

        public static readonly Rule H1Heading = AllOf(
            Grapheme('#'),
            Not(Grapheme('#')),
            OptionalOneWhitespace,
            RestOfLine,
            Eof());

        public static readonly Rule Bullet = AllOf(
            OneOf("-*+").Delete(),
            OptionalOneWhitespace,
            RestOfLine,
            Eof());

        public static readonly Rule HrRun = AllOf(
            AtLeast(3, OneOf("-*+").Delete()),
            Eof());

        public static readonly Rule HrSpaced = AllOf(
            OneOf("-*+").Delete(),
            AtLeast(2, AllOf(Grapheme(' '), OneOf("-*+").Delete())),
            Eof());
    }

    // Three-way timing on a corpus: compiled regex, recursive
    // evaluator, state-machine evaluator. Same workload (one
    // accept/reject per input). Ratios reported relative to the
    // compiled regex (regex = 1.00x baseline).
    //
    // Uses InputUnit.Rune so the state-machine evaluator can take the
    // fused-scan opcodes (ScanOneOfRune / ScanNoneOfRune). On the
    // ASCII-only inputs in these corpora, Rune and Grapheme produce
    // identical accept/reject verdicts.
    private static void Compare(string label, Regex regex, Rule rule, string[] corpus, int iterations)
    {
        var options = new ParseOptions { NormalizeInput = null, MaxDepth = 0, InputUnit = InputUnit.Rune };

        // Warmup: enough for tiered JIT and the rule's own Compile()
        // pass to settle. Hits all three paths so PGO has a chance to
        // see each.
        for (int i = 0; i < 1000; i++)
        {
            foreach (var input in corpus)
            {
                _ = regex.IsMatch(input);
                rule.ParseRecursive(input, options);
                StateMachineParser.Parse(rule, input, options);
            }
        }

        const int rounds = 5;
        double bestRegex = double.MaxValue;
        double bestRecursive = double.MaxValue;
        double bestStateMachine = double.MaxValue;
        var stopwatch = new Stopwatch();
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            stopwatch.Restart();
            for (int i = 0; i < iterations; i++)
                foreach (var input in corpus)
                    _ = regex.IsMatch(input);
            stopwatch.Stop();
            double regexMs = stopwatch.Elapsed.TotalMilliseconds;
            if (regexMs < bestRegex) bestRegex = regexMs;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            stopwatch.Restart();
            for (int i = 0; i < iterations; i++)
                foreach (var input in corpus)
                    _ = rule.ParseRecursive(input, options);
            stopwatch.Stop();
            double recursiveMs = stopwatch.Elapsed.TotalMilliseconds;
            if (recursiveMs < bestRecursive) bestRecursive = recursiveMs;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            stopwatch.Restart();
            for (int i = 0; i < iterations; i++)
                foreach (var input in corpus)
                    _ = StateMachineParser.Parse(rule, input, options);
            stopwatch.Stop();
            double stateMachineMs = stopwatch.Elapsed.TotalMilliseconds;
            if (stateMachineMs < bestStateMachine) bestStateMachine = stateMachineMs;
        }

        long perRunCount = (long)iterations * corpus.Length;
        double regexUsPerOp = bestRegex * 1000.0 / perRunCount;
        double recursiveUsPerOp = bestRecursive * 1000.0 / perRunCount;
        double stateMachineUsPerOp = bestStateMachine * 1000.0 / perRunCount;
        double recRatio = bestRecursive / bestRegex;
        double smRatio = bestStateMachine / bestRegex;
        TestContext.Out.WriteLine(
            $"{label,-25} corpus={corpus.Length,3}  " +
            $"Regex={regexUsPerOp,6:F3}us  Rec={recursiveUsPerOp,6:F3}us({recRatio,5:F2}x)  " +
            $"SM={stateMachineUsPerOp,6:F3}us({smRatio,5:F2}x)");
    }

    // Same as Compare but routes the state-machine evaluator through
    // TryMatch instead of Parse. Apples-to-apples vs Regex.IsMatch:
    // both return just bool, neither builds a tree. Used for the
    // "matcher" rows where the parsers' grammars are constructed
    // with all leaves Delete-flagged so no Symbols are produced.
    private static void CompareMatcher(string label, Regex regex, Rule rule, string[] corpus, int iterations)
    {
        var options = new ParseOptions { NormalizeInput = null, MaxDepth = 0, InputUnit = InputUnit.Rune };

        for (int i = 0; i < 1000; i++)
        {
            foreach (var input in corpus)
            {
                _ = regex.IsMatch(input);
                rule.ParseRecursive(input, options);
                StateMachineParser.TryMatch(rule, input, options);
            }
        }

        const int rounds = 5;
        double bestRegex = double.MaxValue;
        double bestRecursive = double.MaxValue;
        double bestStateMachine = double.MaxValue;
        var stopwatch = new Stopwatch();
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            stopwatch.Restart();
            for (int i = 0; i < iterations; i++)
                foreach (var input in corpus)
                    _ = regex.IsMatch(input);
            stopwatch.Stop();
            double regexMs = stopwatch.Elapsed.TotalMilliseconds;
            if (regexMs < bestRegex) bestRegex = regexMs;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            stopwatch.Restart();
            for (int i = 0; i < iterations; i++)
                foreach (var input in corpus)
                    _ = rule.ParseRecursive(input, options);
            stopwatch.Stop();
            double recursiveMs = stopwatch.Elapsed.TotalMilliseconds;
            if (recursiveMs < bestRecursive) bestRecursive = recursiveMs;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            stopwatch.Restart();
            for (int i = 0; i < iterations; i++)
                foreach (var input in corpus)
                    _ = StateMachineParser.TryMatch(rule, input, options);
            stopwatch.Stop();
            double stateMachineMs = stopwatch.Elapsed.TotalMilliseconds;
            if (stateMachineMs < bestStateMachine) bestStateMachine = stateMachineMs;
        }

        long perRunCount = (long)iterations * corpus.Length;
        double regexUsPerOp = bestRegex * 1000.0 / perRunCount;
        double recursiveUsPerOp = bestRecursive * 1000.0 / perRunCount;
        double stateMachineUsPerOp = bestStateMachine * 1000.0 / perRunCount;
        double recRatio = bestRecursive / bestRegex;
        double smRatio = bestStateMachine / bestRegex;
        TestContext.Out.WriteLine(
            $"{label,-25} corpus={corpus.Length,3}  " +
            $"Regex={regexUsPerOp,6:F3}us  Rec={recursiveUsPerOp,6:F3}us({recRatio,5:F2}x)  " +
            $"SM={stateMachineUsPerOp,6:F3}us({smRatio,5:F2}x) [TryMatch]");
    }

    // Subset of ChordGrammarTests.ValidChords + InvalidChords. Keeps
    // the corpus small enough that 5000-iteration runs finish in a
    // few seconds, while covering every alternation branch in the
    // grammar (root / accidental / quality1 / quality2 / extension /
    // addmod / slash bass) plus enough rejects to exercise the
    // failure path.
    private static readonly string[] ChordCorpus =
    {
        "C", "Am", "F#m7", "Cmaj7", "Bb", "Ebm9", "Csus2", "Csus4",
        "Cadd9", "C#m7b5", "C/G", "F#m7/A", "Caug7", "Cdim",
        "C7b9", "Cmaj7#11", "Bm/A", "Bø7",
        "X", "Cnonsense", "Cfoo", "C!", "C/", "/G", "Csus3",
    };

    private static readonly string[] BacklogH1 =
    {
        "# Title", "#Title", "# ", "#",
        "# A very long title with   spaces and punctuation!",
        "# 1234", "# Mixed #hashtag inside",
        "## Not an H1", "### Not an H1 either",
        "  # Indented", "", "Title",
    };

    private static readonly string[] BacklogH2 =
    {
        "## Section", "##Section", "## ", "##",
        "# Not an H2", "### Not an H2", "#### Nope",
        "##\tTabbed", "##Section with : and punct!",
        " ## Indented", "", "Section",
    };

    private static readonly string[] BacklogBullet =
    {
        "- item", "* item", "+ item", "-item", "*item", "+item",
        "-", "*", "+", "- ", "-  two spaces", "- \titem after tab",
        "--- hr-ish", "  - indented bullet", "# heading", "",
        "plain text", "- item with (punctuation) and numbers 123",
    };

    private static readonly string[] BacklogHrRun =
    {
        "---", "****", "+++", "-----", "--", "-*+", "-- -",
        "---a", "a---", "", "-", "*", "+", "++", "* * *", "- - -",
    };

    private static readonly string[] BacklogHrSpaced =
    {
        "- - -", "* * *", "+ + +", "- - - -", "* - *", "- -", "-",
        "- - - a", "a - - -", "---", "", "- -  -", " - - -",
    };

    private static readonly string[] BacklogParagraph =
    {
        "a\n\nb", "a\n\n\nb", "a\n \nb", "a\n\t\nb", "a\n   \nb",
        "\n\n", "\n\n\n", "one line\n", "one\ntwo", "",
        "no newlines at all", "a\n\rb", "trailing\n\n",
        "\n\nleading", "mixed\n \n text \n\n end",
    };
}
