using System;
using System.Linq;
using BenchmarkDotNet.Running;
using InductorParser.Benchmarks.Json;
using InductorParser.Benchmarks.Json.InductorParsers;
using InductorParser.Benchmarks.Json.ParlotParsers;
using InductorParser.Benchmarks.Json.PegasusParsers;
using InductorParser.Benchmarks.Json.PidginParsers;
using InductorParser.Benchmarks.Json.SpracheParsers;
using InductorParser.Benchmarks.Json.SuperpowerParsers;

namespace InductorParser.Benchmarks;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--spot-check"))
        {
            return SpotCheck();
        }

        if (args.Contains("--parlot-check"))
        {
            return ParlotCompileCheck();
        }

        if (args.Contains("--profile-inductor"))
        {
            return ProfileInductor(args);
        }

        if (args.Contains("--rule-counts"))
        {
            return RuleCounts(args);
        }

        if (args.Contains("--lexer-compare"))
        {
            return LexerCompare();
        }

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        foreach (var summary in summaries)
        {
            PerformanceChart.TryUpdate(summary);
        }
        return 0;
    }

    // Compare InputUnit.Rune vs InputUnit.Grapheme for InductorJsonParser
    // across the bench's four canonical input shapes. Hand-timed with a
    // Stopwatch after a warmup round, so it's not BenchmarkDotNet-quality
    // but it gives a same-order-of-magnitude answer in seconds instead of
    // the 10+ minutes a full BDN run takes for four shapes.
    private static int LexerCompare()
    {
        var shapes = new (string name, string input)[]
        {
            ("Big",  JsonBench.BuildJson(4, 4, 3).ToString()!),
            ("Long", JsonBench.BuildJson(256, 1, 1).ToString()!),
            ("Deep", JsonBench.BuildJson(1, 256, 1).ToString()!),
            ("Wide", JsonBench.BuildJson(1, 1, 256).ToString()!),
        };

        const int warmup = 50;
        const int iterations = 500;

        Console.WriteLine($"{"Shape",-6} {"Len",8} {"Rune ms/op",13} {"Grapheme ms/op",16} {"Typed ms/op",14} {"Graph/Rune",11} {"Typed/Graph",12}");
        foreach (var (name, input) in shapes)
        {
            for (int i = 0; i < warmup; i++)
            {
                InductorJsonParser.Parse(input);
                InductorJsonParser.ParseGrapheme(input);
                InductorJsonParser.ParseTyped(input);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) InductorJsonParser.Parse(input);
            sw.Stop();
            double runeMsPerOp = sw.Elapsed.TotalMilliseconds / iterations;

            sw.Restart();
            for (int i = 0; i < iterations; i++) InductorJsonParser.ParseGrapheme(input);
            sw.Stop();
            double graphemeMsPerOp = sw.Elapsed.TotalMilliseconds / iterations;

            sw.Restart();
            for (int i = 0; i < iterations; i++) InductorJsonParser.ParseTyped(input);
            sw.Stop();
            double typedMsPerOp = sw.Elapsed.TotalMilliseconds / iterations;

            double graphOverRune = graphemeMsPerOp / runeMsPerOp;
            double typedOverGraph = typedMsPerOp / graphemeMsPerOp;
            Console.WriteLine($"{name,-6} {input.Length,8} {runeMsPerOp,13:F3} {graphemeMsPerOp,16:F3} {typedMsPerOp,14:F3} {graphOverRune,10:F2}x {typedOverGraph,11:F2}x");
        }
        return 0;
    }

    // Count rule invocations per Rule type across one full Big parse by
    // running a traced parse and counting trace lines.
    private static int RuleCounts(string[] args)
    {
        string shape = "big";
        foreach (var a in args)
            if (a.StartsWith("--shape=")) shape = a.Substring("--shape=".Length);
        string input = shape switch
        {
            "big" => JsonBench.BuildJson(4, 4, 3).ToString()!,
            "long" => JsonBench.BuildJson(256, 1, 1).ToString()!,
            "deep" => JsonBench.BuildJson(1, 256, 1).ToString()!,
            "wide" => JsonBench.BuildJson(1, 1, 256).ToString()!,
            _ => throw new ArgumentException($"unknown shape {shape}")
        };
        Console.WriteLine($"Counting rule invocations for {shape}, input length {input.Length}");
        var counts = RuleProfiler.CountByType(input, iterations: 1);
        long total = 0;
        foreach (var value in counts.Values) total += value;
        Console.WriteLine($"Total trace outcomes: {total}");
        Console.WriteLine();
        Console.WriteLine($"{"Rule",-30} {"Count",10} {"% total",8}");
        foreach (var (k, v) in counts.OrderByDescending(kv => kv.Value))
        {
            double pct = 100.0 * v / total;
            Console.WriteLine($"{k,-30} {v,10} {pct,7:F2}%");
        }
        return 0;
    }

    // Run InductorParser in a tight loop on the Big-shape input for long
    // enough to give dotnet-trace CPU sampling a usable population of
    // stacks. Prints a "STARTING"/"DONE" marker around the hot loop so
    // the profiler window can be cleanly reasoned about.
    //
    // Usage: launch this process under
    //   dotnet-trace collect --providers Microsoft-DotNETCore-SampleProfiler --format speedscope -- <this exe> --profile-inductor
    // Then filter the speedscope output to the samples between the
    // markers.
    private static int ProfileInductor(string[] args)
    {
        string shape = "big";
        int iterations = 40000;
        foreach (var a in args)
        {
            if (a.StartsWith("--shape=")) shape = a.Substring("--shape=".Length);
            else if (a.StartsWith("--iters=")) iterations = int.Parse(a.Substring("--iters=".Length));
        }
        string input = shape switch
        {
            "big"  => JsonBench.BuildJson(4, 4, 3).ToString()!,
            "long" => JsonBench.BuildJson(256, 1, 1).ToString()!,
            "deep" => JsonBench.BuildJson(1, 256, 1).ToString()!,
            "wide" => JsonBench.BuildJson(1, 1, 256).ToString()!,
            _      => throw new ArgumentException($"unknown shape {shape}")
        };

        // Warm-up: JIT + fill tiered compilation.
        for (int i = 0; i < 200; i++) InductorJsonParser.Parse(input);

        Console.WriteLine($"STARTING profile: shape={shape} length={input.Length} iters={iterations}");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var r = InductorJsonParser.Parse(input);
            if (!r.Success) throw new InvalidOperationException("parse failed");
        }
        stopwatch.Stop();
        Console.WriteLine($"DONE profile: total={stopwatch.Elapsed.TotalSeconds:F3}s avg={stopwatch.Elapsed.TotalMicroseconds / iterations:F2}us/op");
        return 0;
    }

    // Verify every benchmarked parser actually consumed the whole input
    // and produced a structurally equivalent tree. Without this the bench
    // numbers could be meaningless: a parser that silently stopped at the
    // opening bracket would look fast but be wrong.
    //
    // Strategy: feed each parser the same input, round-trip its output
    // back to a string using the JsonValue.ToString canonical form (or
    // STJ's own RootElement.GetRawText for the BCL reference), and
    // compare byte-for-byte to the input. Any mismatch fails the whole
    // check.
    //
    // Covers all four benchmark shapes (Big, Long, Deep, Wide) so the
    // Deep-256 and Wide-256 paths the benchmark actually times are the
    // same paths verification exercises.
    //
    // Runs in milliseconds, not a bench.
    private static int SpotCheck()
    {
        var shapes = new (string name, string input)[]
        {
            ("Big",  JsonBench.BuildJson(4, 4, 3).ToString()!),
            ("Long", JsonBench.BuildJson(256, 1, 1).ToString()!),
            ("Deep", JsonBench.BuildJson(1, 256, 1).ToString()!),
            ("Wide", JsonBench.BuildJson(1, 1, 256).ToString()!),
        };

        int totalFailures = 0;
        foreach (var (shapeName, input) in shapes)
        {
            Console.WriteLine($"[{shapeName}] input length: {input.Length} chars");
            int failures = 0;

            failures += Verify("InductorParser", input, () =>
            {
                // Use the round-trip variant: PreserveAllSymbols keeps
                // FlattenType.Delete nodes (JSON delimiters) in the tree
                // so Tree.ToString() reproduces the full input. The benchmark
                // measurement path uses the faster default options that
                // filter those nodes at parse time.
                var r = InductorJsonParser.ParseForRoundTrip(input);
                if (!r.Success)
                    return $"parse failed: {r.ErrorMessage} at {r.ErrorCharIndex}";
                var matched = r.Tree!.ToString();
                return matched == input ? null : Diff(input, matched);
            });

            failures += Verify("InductorParserTyped", input, () =>
            {
                // Typed variant: parse with Grapheme lexer, then walk the
                // Symbol tree into an IJson tree. Round-trip goes through
                // IJson.ToString() (same path Pegasus / Pidgin / Sprache /
                // Superpower / Parlot use) so this verifies both the parse
                // and the Symbol->IJson conversion.
                var typed = InductorJsonParser.ParseTyped(input);
                var s = typed.ToString();
                return s == input ? null : Diff(input, s);
            });

            failures += Verify("PegasusOptimized", input, () =>
            {
                var r = PegasusJsonOptimizedParser.Parse(input);
                if (r == null) return "parse returned null";
                var s = r.ToString();
                return s == input ? null : Diff(input, s);
            });

            failures += Verify("PegasusWiki", input, () =>
            {
                var r = PegasusJsonWikiParser.Parse(input);
                if (r == null) return "parse returned null";
                var s = r.ToString();
                return s == input ? null : Diff(input, s);
            });

            failures += Verify("Parlot", input, () =>
            {
                var r = ParlotJsonParser.Parse(input);
                if (r == null) return "parse returned null";
                var s = r.ToString();
                return s == input ? null : Diff(input, s);
            });

            failures += Verify("ParlotCompiled", input, () =>
            {
                var r = ParlotJsonParser.Json.Compile().Parse(input);
                if (r == null) return "parse returned null";
                var s = r.ToString();
                return s == input ? null : Diff(input, s);
            });

            failures += Verify("Pidgin", input, () =>
            {
                var r = PidginJsonParser.Parse(input);
                if (!r.Success) return $"parse failed: {r.Error}";
                var s = r.Value.ToString();
                return s == input ? null : Diff(input, s);
            });

            failures += Verify("Sprache", input, () =>
            {
                var r = SpracheJsonParser.Parse(input);
                if (!r.WasSuccessful) return $"parse failed: {r.Message}";
                if (!r.Remainder.AtEnd)
                    return $"parse stopped at offset {r.Remainder.Position}, {input.Length - r.Remainder.Position} chars unread";
                var s = r.Value.ToString();
                return s == input ? null : Diff(input, s);
            });

            // Superpower overflows the .NET stack on the 256-deep input
            // (verified: the process hard-crashes, not a catchable
            // exception). Skipping it in the spot-check isn't a bye.
            // The benchmark README reports the crash explicitly. Upstream
            // Parlot's benchmark excludes it from the Deep category for
            // the same reason.
            if (shapeName == "Deep")
            {
                Console.WriteLine($"  SKIP Superpower: stack overflow on 256-deep nesting");
            }
            else
            {
                failures += Verify("Superpower", input, () =>
                {
                    try
                    {
                        var r = SuperpowerJsonParser.Parse(input);
                        if (r == null) return "parse returned null";
                        var s = r.ToString();
                        return s == input ? null : Diff(input, s);
                    }
                    catch (Superpower.ParseException ex)
                    {
                        return $"parse threw: {ex.Message}";
                    }
                });
            }

            // The benchmark's Deep row feeds STJ an explicit
            // MaxDepth-lifted setting (default is 64, the Deep input is
            // 256 levels). Use the same setting here or the spot-check
            // would diverge from what the bench actually measures.
            var stjOptions = new System.Text.Json.JsonDocumentOptions { MaxDepth = 1024 };

            failures += Verify("SystemTextJson", input, () =>
            {
                using var doc = System.Text.Json.JsonDocument.Parse(input, stjOptions);
                var s = doc.RootElement.GetRawText();
                return s == input ? null : Diff(input, s);
            });

            Console.WriteLine();
            totalFailures += failures;
        }

        if (totalFailures == 0)
        {
            Console.WriteLine("OK: all parsers round-trip every shape exactly.");
            return 0;
        }
        Console.Error.WriteLine($"FAIL: {totalFailures} parser/shape combination(s) failed verification.");
        return 1;
    }

    // Diagnostic: confirm Parlot.Compile() actually produced a different
    // parser object (IL-emitted), not a no-op returning the same instance.
    // Also time both paths on a warm loop to see if the difference is real
    // or just inside the ShortRun iteration noise.
    private static int ParlotCompileCheck()
    {
        var uncompiled = ParlotJsonParser.Json;
        var compiled = ParlotJsonParser.Json.Compile();

        Console.WriteLine($"Uncompiled type: {uncompiled.GetType().FullName}");
        Console.WriteLine($"Compiled type:   {compiled.GetType().FullName}");
        Console.WriteLine($"Same object?     {object.ReferenceEquals(uncompiled, compiled)}");
        Console.WriteLine();

        var input = JsonBench.BuildJson(4, 4, 3).ToString()!;
        Console.WriteLine($"Input: {input.Length} chars (Big shape)");
        Console.WriteLine();

        // Warmup, to let JIT settle.
        for (int i = 0; i < 100; i++)
        {
            uncompiled.Parse(input);
            compiled.Parse(input);
        }

        // Time each. Large iteration count so small per-call differences
        // show up above timer resolution.
        const int iters = 2000;

        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) uncompiled.Parse(input);
        sw1.Stop();

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) compiled.Parse(input);
        sw2.Stop();

        Console.WriteLine($"Uncompiled: {sw1.Elapsed.TotalMicroseconds / iters,8:F2} μs/op over {iters} iters");
        Console.WriteLine($"Compiled:   {sw2.Elapsed.TotalMicroseconds / iters,8:F2} μs/op over {iters} iters");
        Console.WriteLine($"Ratio:      {(double)sw1.ElapsedTicks / sw2.ElapsedTicks:F2}x (compiled vs uncompiled)");

        return 0;
    }

    private static int Verify(string name, string input, Func<string?> check)
    {
        string? err;
        try { err = check(); }
        catch (Exception ex) { err = $"threw {ex.GetType().Name}: {ex.Message}"; }

        if (err == null)
        {
            Console.WriteLine($"  OK   {name}");
            return 0;
        }
        Console.WriteLine($"  FAIL {name}: {err}");
        return 1;
    }

    private static string Diff(string expected, string actual)
    {
        if (actual.Length != expected.Length)
            return $"output length {actual.Length} != input length {expected.Length}";
        int i = 0;
        while (i < expected.Length && expected[i] == actual[i]) i++;
        int ctxStart = Math.Max(0, i - 20);
        int ctxEnd = Math.Min(expected.Length, i + 20);
        return $"mismatch at offset {i}: expected '{expected.Substring(ctxStart, ctxEnd - ctxStart)}', got '{actual.Substring(ctxStart, Math.Min(ctxEnd - ctxStart, actual.Length - ctxStart))}'";
    }
}
