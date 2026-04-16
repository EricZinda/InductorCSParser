using System;
using System.Linq;
using BenchmarkDotNet.Running;
using InductorParser.Benchmarks.Json;
using InductorParser.Benchmarks.Json.ParlotParsers;
using InductorParser.Benchmarks.Json.PegasusParsers;
using InductorParser.Benchmarks.Json.PidginParsers;
using InductorParser.Benchmarks.Json.SpracheParsers;
using InductorParser.Benchmarks.Json.SuperpowerParsers;
using Newtonsoft.Json.Linq;

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

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    // Verify every benchmarked parser actually consumed the whole input
    // and produced a structurally equivalent tree. Without this the bench
    // numbers could be meaningless: a parser that silently stopped at the
    // opening bracket would look fast but be wrong.
    //
    // Strategy: feed each parser the same input, round-trip its output
    // back to a string using the JsonValue.ToString canonical form (or
    // each library's own serializer for Newtonsoft/STJ), and compare
    // byte-for-byte to the input. Any mismatch fails the whole check.
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
                // Use the round-trip variant: PreserveFlattenWrappers keeps
                // Delete-typed nodes (JSON delimiters) in the tree so
                // Tree.ToString() reproduces the full input. The benchmark
                // measurement path uses the faster default options that
                // filter those nodes at parse time.
                var r = InductorJsonParser.ParseForRoundTrip(input);
                if (!r.Success)
                    return $"parse failed: {r.ErrorMessage} at {r.ErrorCharIndex}";
                var matched = r.Tree!.ToString();
                return matched == input ? null : Diff(input, matched);
            });

            failures += Verify("Pegasus", input, () =>
            {
                var r = PegasusJsonParser.Parse(input);
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
            // exception). Skipping it in the spot-check isn't a bye —
            // the benchmark README reports the crash explicitly. Upstream
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

            // The benchmark's Deep row feeds Newtonsoft and STJ explicit
            // MaxDepth-lifted settings (defaults are 64, the Deep input is
            // 256 levels). Use the same settings here or the spot-check
            // would diverge from what the bench actually measures.
            var newtonsoftSettings = new Newtonsoft.Json.JsonSerializerSettings { MaxDepth = 1024 };
            var stjOptions = new System.Text.Json.JsonDocumentOptions { MaxDepth = 1024 };

            failures += Verify("Newtonsoft", input, () =>
            {
                var r = Newtonsoft.Json.JsonConvert.DeserializeObject<JToken>(input, newtonsoftSettings)!;
                var s = r.ToString(Newtonsoft.Json.Formatting.None);
                return s == input ? null : Diff(input, s);
            });

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

        // Warmup — let JIT settle.
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
