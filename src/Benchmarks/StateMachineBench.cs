using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using InductorParser;
using InductorParser.Benchmarks.Json;
using InductorParser.Benchmarks.Json.InductorParsers;
using InductorParser.StateMachine;
using static InductorParser.Rules;

namespace InductorParser.Benchmarks;

// Hand-timed comparison between the existing recursive evaluator
// (Rule.Parse) and the state-machine evaluator (StateMachineParser.Parse)
// across a few representative grammars that stay inside the iteration-1
// supported set (Literal, Token, OneOf, AllOf, FirstOf, BetweenInclusive,
// Optional, Not, Peek, Eof, LateBound).
//
// Stopwatch loop with warmup, same shape as Program.LexerCompare. Not
// BenchmarkDotNet-quality but precise enough to see same-order-of-
// magnitude differences in seconds rather than 10+ minute BDN runs.
internal static class StateMachineBench
{
    public static int Run()
    {
        var grammars = BuildGrammars();
        var inputs = BuildInputs();

        Console.WriteLine($"{"Grammar",-25} {"Input",-15} {"Len",6} {"Recursive us/op",18} {"StateMach us/op",18} {"SM/Rec",10}");
        Console.WriteLine(new string('-', 100));

        foreach (var grammar in grammars)
        {
            // Pre-compile and pre-lower so first-run cost doesn't skew
            // the numbers. We're measuring steady-state parse cost.
            grammar.Rule.Compile();
            var parseOptions = grammar.Options ?? new ParseOptions { NormalizeInput = null };
            // Warm StateMachineParser's per-rule cache.
            foreach (var (label, input) in inputs[grammar.InputKind])
                StateMachineParser.Parse(grammar.Rule, input, parseOptions);

            foreach (var (label, input) in inputs[grammar.InputKind])
            {
                var (recursiveUs, stateMachineUs) = MeasurePair(grammar.Rule, input, parseOptions);
                double ratio = stateMachineUs / recursiveUs;
                Console.WriteLine(
                    $"{grammar.Name,-25} {label,-15} {input.Length,6} {recursiveUs,15:F3} us {stateMachineUs,15:F3} us {ratio,9:F2}x");
            }
        }
        return 0;
    }

    private static (double recursiveUs, double stateMachineUs) MeasurePair(Rule rule, string input, ParseOptions options)
    {
        // JSON inputs are far larger than the small grammars and
        // running 20000 iterations of a 50KB Big-shape parse takes 30+
        // seconds per round. Scale iterations down by input length to
        // keep total bench time bounded while still measuring enough
        // to be stable (each round still runs at least 0.5s).
        int iterations = input.Length switch
        {
            < 64 => 20000,
            < 1024 => 5000,
            < 16384 => 1000,
            _ => 200,
        };
        int warmup = iterations / 10;
        const int rounds = 5;

        // Warmup both paths heavily so tiered JIT settles.
        for (int i = 0; i < warmup; i++)
        {
            rule.Parse(input, options);
            StateMachineParser.Parse(rule, input, options);
        }

        // Take min of N rounds. Min is the right summary statistic for
        // CPU-bound microbenchmarks: noise from OS scheduling and other
        // background processes can only slow a run down, never speed
        // it up, so the minimum is the closest thing to a "no-noise"
        // estimate of the true cost.
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
            {
                var r = rule.Parse(input, options);
                if (!r.Success) throw new InvalidOperationException($"recursive failed: {r.ErrorMessage}");
            }
            stopwatch.Stop();
            double recursiveUs = stopwatch.Elapsed.TotalMicroseconds / iterations;
            if (recursiveUs < bestRecursive) bestRecursive = recursiveUs;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            stopwatch.Restart();
            for (int i = 0; i < iterations; i++)
            {
                var r = StateMachineParser.Parse(rule, input, options);
                if (!r.Success) throw new InvalidOperationException($"state-machine failed: {r.ErrorMessage}");
            }
            stopwatch.Stop();
            double stateMachineUs = stopwatch.Elapsed.TotalMicroseconds / iterations;
            if (stateMachineUs < bestStateMachine) bestStateMachine = stateMachineUs;
        }
        return (bestRecursive, bestStateMachine);
    }

    private record GrammarCase(string Name, Rule Rule, string InputKind, ParseOptions? Options = null);

    private static List<GrammarCase> BuildGrammars()
    {
        // 1. Identifier scan: BetweenInclusive + OneOf, the simplest
        //    hot-loop shape. No backtracking, no recursion.
        var identifier = new GrammarCase(
            "Identifier",
            AllOf(
                OneOf(RuneSet.Letters | RuneSet.Runes("_")),
                ZeroOrMore(OneOf(RuneSet.Letters | RuneSet.Digits | RuneSet.Runes("_"))),
                Eof()),
            "identifier");

        // 2. Balanced parens, recursive via LateBound. Pure structural
        //    recursion, no FirstOf alternatives, no token-level fanout.
        var parens = new LateBoundRule("parens");
        parens.Bind(ZeroOrMore(AllOf(Token('('), parens, Token(')'))));
        var balancedParens = new GrammarCase(
            "BalancedParens",
            AllOf(parens, Eof()),
            "parens");

        // 3. Many-alternative keyword match. Stresses FirstOf's per-alternative
        //    PushBacktrack/FailRestore plus repeated Literal matches. The
        //    grammar accepts a sequence of one of N keywords separated
        //    by commas. Mirrors the keyword-fanout of the chord grammar
        //    minus the LiteralIgnoreAsciiCase.
        var keyword = FirstOf(
            Literal("alpha"), Literal("beta"), Literal("gamma"), Literal("delta"),
            Literal("epsilon"), Literal("zeta"), Literal("eta"), Literal("theta"));
        var keywordList = new GrammarCase(
            "KeywordList",
            AllOf(keyword, ZeroOrMore(AllOf(Token(','), keyword)), Eof()),
            "keywords");

        // 4. Recursive arithmetic with backtracking-heavy shape. expr =
        //    term (('+' / '-') term)*; term = digit / '(' expr ')'.
        var expr = new LateBoundRule("expr");
        var digit = OneOf(RuneSet.Ascii.Digits);
        var term = FirstOf(digit, AllOf(Token('('), expr, Token(')')));
        expr.Bind(AllOf(term, ZeroOrMore(AllOf(OneOf("+-"), term))));
        var arithmetic = new GrammarCase(
            "Arithmetic",
            AllOf(expr, Eof()),
            "arithmetic");

        // 5. Real-world JSON. The full InductorJsonParser grammar that
        //    the JsonBench harness uses (Token, Literal, OneOf, AllOf,
        //    FirstOf, ZeroOrMore, Optional, LateBound, ScanUntil). Run
        //    against the same Big / Long / Deep / Wide shapes the
        //    main JsonBench measures. Both lexers eligible: pick Rune
        //    here to match the harness's default code path.
        var jsonRune = new GrammarCase(
            "JSON-Rune",
            InductorJsonParser.JsonRule,
            "json",
            new ParseOptions { InputUnit = InputUnit.Rune, MaxDepth = 0, NormalizeInput = null });

        return new List<GrammarCase> { identifier, balancedParens, keywordList, arithmetic, jsonRune };
    }

    private static Dictionary<string, List<(string label, string input)>> BuildInputs()
    {
        var dictionary = new Dictionary<string, List<(string, string)>>();

        dictionary["identifier"] = new List<(string, string)>
        {
            ("short",  "x"),
            ("medium", "someIdentifier_42"),
            ("long",   new string('a', 256)),
        };

        dictionary["parens"] = new List<(string, string)>
        {
            ("empty",   ""),
            ("nested4", "(((())))"),
            ("nested16", BuildNestedParens(16)),
        };

        dictionary["keywords"] = new List<(string, string)>
        {
            ("one",    "alpha"),
            ("five",   "alpha,beta,gamma,delta,epsilon"),
            ("forty",  RepeatKeywords(40)),
        };

        dictionary["arithmetic"] = new List<(string, string)>
        {
            ("flat",   "1+2-3+4-5+6-7+8"),
            ("nested", "((1+2)-(3+4))+((5-6)+(7-8))"),
            ("deep",   BuildDeepArithmetic(8)),
        };

        // The same shapes the main JsonBench measures, built via the
        // same JsonBench.BuildJson helper so the comparison is the
        // same input the headline numbers run on. The Long, Deep, and
        // Wide variants stress different JSON shapes (a long array,
        // a deeply nested object chain, a wide flat object).
        dictionary["json"] = new List<(string, string)>
        {
            ("Big",  JsonBench.BuildJson(4, 4, 3).ToString()!),
            ("Long", JsonBench.BuildJson(256, 1, 1).ToString()!),
            ("Deep", JsonBench.BuildJson(1, 256, 1).ToString()!),
            ("Wide", JsonBench.BuildJson(1, 1, 256).ToString()!),
        };
        return dictionary;
    }

    private static string BuildNestedParens(int depth) =>
        new string('(', depth) + new string(')', depth);

    private static string RepeatKeywords(int count)
    {
        var keywords = new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta" };
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(keywords[i % keywords.Length]);
        }
        return sb.ToString();
    }

    private static string BuildDeepArithmetic(int depth)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < depth; i++) sb.Append('(');
        sb.Append('1');
        for (int i = 0; i < depth; i++) sb.Append("+1)");
        return sb.ToString();
    }
}
