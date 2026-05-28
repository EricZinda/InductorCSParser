// Implements the rebar runner protocol from BurntSushi/rebar
// (https://github.com/BurntSushi/rebar). No rebar source code is reused;
// this is an independent .NET implementation of the KLV stdin / sample
// stdout contract documented in KLV.md, FORMAT.md, and BYOB.md.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace InductorParser.Benchmarks.Rebar;

internal static class Program
{
    private const string RecursiveEngineName = "inductorparser";
    private const string StateMachineEngineName = "inductorparser-statemachine";

    public static int Main(string[] args)
    {
        // Honor INDUCTOR_DISABLE_LOOKAHEAD_SHORTCUT for A/B-measuring the
        // StateMachine's Or lookahead-skip optimization in the rebar
        // grammars. Set the env var to any non-empty, non-"0", non-"false"
        // value to disable the shortcut for this run. Logged to stderr so
        // the result CSV stays clean. The recursive engine no longer has
        // this shortcut, so the env var only affects StateMachine runs.
        var disableShortcut = Environment.GetEnvironmentVariable("INDUCTOR_DISABLE_LOOKAHEAD_SHORTCUT");
        InductorParser.StateMachine.Lowerer.DisableLookaheadShortcut = !string.IsNullOrEmpty(disableShortcut)
            && !string.Equals(disableShortcut, "0", StringComparison.Ordinal)
            && !string.Equals(disableShortcut, "false", StringComparison.OrdinalIgnoreCase);
        if (InductorParser.StateMachine.Lowerer.DisableLookaheadShortcut)
            Console.Error.WriteLine("InductorParser lookahead shortcut: DISABLED");

        try
        {
            if (args.Length != 1)
                throw new ArgumentException($"Usage: {Assembly.GetExecutingAssembly().GetName().Name} ({RecursiveEngineName} | {StateMachineEngineName} | --version | --self-test)");

            if (args[0] == "--version")
            {
                Console.Out.WriteLine(VersionString());
                return 0;
            }

            if (args[0] == "--self-test")
                return SelfTest.Run();

            if (args[0] == "--bench-shortcut")
                return BenchShortcut.Run();

            bool useStateMachine = args[0] switch
            {
                RecursiveEngineName => false,
                StateMachineEngineName => true,
                _ => throw new ArgumentException($"Unknown engine '{args[0]}'. Expected '{RecursiveEngineName}' or '{StateMachineEngineName}'.")
            };

            var config = RebarConfig.Read(Console.OpenStandardInput());
            foreach (var sample in Run(config, useStateMachine))
                Console.Out.WriteLine($"{sample.DurationNanoseconds.ToString(CultureInfo.InvariantCulture)},{sample.Count.ToString(CultureInfo.InvariantCulture)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static IEnumerable<Sample> Run(RebarConfig config, bool useStateMachine)
    {
        if (config.Model == "compile")
        {
            Warmup(() => RunCompileOnce(config, useStateMachine).Count, config.MaxWarmupIters, config.MaxWarmupTimeNanoseconds);
            return Measure(() => RunCompileOnce(config, useStateMachine), config.MaxIters, config.MaxTimeNanoseconds);
        }

        var plan = BenchmarkRegistry.Build(config, useStateMachine);
        Warmup(() => plan.Count(config.Haystack, config.Model), config.MaxWarmupIters, config.MaxWarmupTimeNanoseconds);
        return Measure(() => RunSearchOnce(plan, config), config.MaxIters, config.MaxTimeNanoseconds);
    }

    private static Sample RunSearchOnce(BenchmarkPlan plan, RebarConfig config)
    {
        long start = Stopwatch.GetTimestamp();
        long count = plan.Count(config.Haystack, config.Model);
        return new Sample(ElapsedNanoseconds(start), count);
    }

    private static Sample RunCompileOnce(RebarConfig config, bool useStateMachine)
    {
        long start = Stopwatch.GetTimestamp();
        var plan = BenchmarkRegistry.Build(config, useStateMachine);
        long duration = ElapsedNanoseconds(start);
        long count = plan.Count(config.Haystack, config.Model);
        return new Sample(duration, count);
    }

    private static void Warmup(Func<long> run, long maxIters, long maxTimeNanoseconds)
    {
        long start = Stopwatch.GetTimestamp();
        for (long iter = 0; iter < maxIters; iter++)
        {
            run();
            if (maxTimeNanoseconds > 0 && ElapsedNanoseconds(start) >= maxTimeNanoseconds)
                break;
        }
    }

    private static IEnumerable<Sample> Measure(Func<Sample> run, long maxIters, long maxTimeNanoseconds)
    {
        long start = Stopwatch.GetTimestamp();
        for (long iter = 0; iter < maxIters; iter++)
        {
            yield return run();
            if (maxTimeNanoseconds > 0 && ElapsedNanoseconds(start) >= maxTimeNanoseconds)
                break;
        }
    }

    private static long ElapsedNanoseconds(long startTimestamp)
    {
        long ticks = Stopwatch.GetTimestamp() - startTimestamp;
        return ticks * 1_000_000_000L / Stopwatch.Frequency;
    }

    private static string VersionString()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.1.0";
        return $"inductorparser-rebar {version}";
    }

    private readonly record struct Sample(long DurationNanoseconds, long Count);

    // Self-contained timing harness for A/B-comparing the lookahead
    // shortcut on a few representative rebar-style grammars without
    // needing the rebar binary or its haystack corpora. Synthesizes
    // each haystack inline so the run is reproducible from this repo
    // alone. Reports min/median/mean per case so noise vs. signal is
    // visible.
    private static class BenchShortcut
    {
        public static int Run()
        {
            var enabled = !InductorParser.StateMachine.Lowerer.DisableLookaheadShortcut;
            Console.Out.WriteLine($"# Lookahead shortcut: {(enabled ? "ENABLED" : "DISABLED")}");
            Console.Out.WriteLine("# columns: case, model, iterations, min_ns, median_ns, mean_ns, count");

            BenchSafe("curated/01-literal/sherlock-en", "count",
                pattern: "Sherlock Holmes",
                haystack: SynthesizeSherlockHaystack(50_000));
            BenchSafe("curated/02-literal-alternate/sherlock-en", "count",
                pattern: "Sherlock Holmes|John Watson|Irene Adler|Inspector Lestrade|Professor Moriarty",
                haystack: SynthesizeSherlockHaystack(50_000));
            BenchSafe("curated/02-literal-alternate/sherlock-casei-en", "count",
                pattern: "Sherlock Holmes|John Watson|Irene Adler|Inspector Lestrade|Professor Moriarty",
                haystack: SynthesizeSherlockHaystack(50_000),
                caseInsensitive: true);
            BenchSafe("curated/04-ruff-noqa/real", "grep-captures",
                pattern: @"(\s*)((?:# [Nn][Oo][Qq][Aa])(?::\s?(([A-Z]+[0-9]+(?:[,\s]+)?)+))?)",
                haystack: SynthesizeRuffNoqaHaystack(40_000));
            BenchSafe("curated/08-words/all-english", "count-spans",
                pattern: @"\b[0-9A-Za-z_]+\b",
                haystack: SynthesizeSherlockHaystack(20_000));
            BenchSafe("curated/10-bounded-repeat/letters-en", "count",
                pattern: @"[A-Za-z]{8,13}",
                haystack: SynthesizeSherlockHaystack(20_000));

            return 0;
        }

        private static void BenchSafe(string name, string model, string haystack,
            string pattern, bool caseInsensitive = false, int iterations = 25)
        {
            try { BenchOne(name, model, haystack, pattern, caseInsensitive, iterations); }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                Console.Out.WriteLine($"{name},{model},SKIP,{inner.GetType().Name}: {inner.Message.Replace('\n', ' ')}");
                Console.Error.WriteLine($"=== {name} stack ===");
                Console.Error.WriteLine(inner.StackTrace);
            }
        }

        private static void BenchOne(string name, string model, string haystack,
            string pattern, bool caseInsensitive = false, int iterations = 25)
        {
            var keyValues = new List<(string, string)>
            {
                ("name", name),
                ("model", model),
                ("haystack", haystack),
                ("pattern", pattern),
                ("max-iters", "1"),
                ("max-warmup-iters", "0"),
                ("max-time", "0"),
                ("max-warmup-time", "0")
            };
            if (caseInsensitive) keyValues.Add(("case-insensitive", "true"));

            var klv = new MemoryStream();
            using (var writer = new StreamWriter(klv, new UTF8Encoding(false)))
            {
                foreach (var (k, v) in keyValues)
                {
                    int byteLen = Encoding.UTF8.GetByteCount(v);
                    writer.Write($"{k}:{byteLen}:{v}\n");
                }
            }
            var config = RebarConfig.Read(klv.ToArray());

            // Warmup: 5 iterations. Lets the JIT settle and primes
            // grammar caches.
            var plan = BenchmarkRegistry.Build(config, useStateMachine: false);
            for (int i = 0; i < 5; i++) plan.Count(config.Haystack, config.Model);

            // Timed: build once, call N times. Measures the parse loop,
            // not Compile.
            var samples = new long[iterations];
            long lastCount = 0;
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                lastCount = plan.Count(config.Haystack, config.Model);
                samples[i] = ElapsedNanoseconds(start);
            }
            Array.Sort(samples);
            long min = samples[0];
            long median = samples[iterations / 2];
            long sum = 0;
            foreach (var s in samples) sum += s;
            long mean = sum / iterations;
            Console.Out.WriteLine($"{name},{model},{iterations},{min},{median},{mean},{lastCount}");
        }

        // Build a haystack of approximately `targetBytes` bytes that
        // contains a few thousand instances of common English words and
        // a sprinkle of the Sherlock-cast names so the literal and
        // literal-alternate cases have non-zero work to do.
        private static string SynthesizeSherlockHaystack(int targetBytes)
        {
            var seed = "the quick brown fox jumps over the lazy dog. " +
                       "Sherlock Holmes deduced the killer. John Watson took notes. " +
                       "Irene Adler smiled. Inspector Lestrade arrived. Professor Moriarty escaped. " +
                       "London nights are foggy. ";
            var builder = new StringBuilder(targetBytes + seed.Length);
            while (builder.Length < targetBytes)
                builder.Append(seed);
            return builder.ToString();
        }

        // Build a haystack that looks like Python source with periodic
        // `# noqa` markers, both bare and with rule lists. Roughly
        // matches the ruff regression input pattern.
        private static string SynthesizeRuffNoqaHaystack(int targetBytes)
        {
            var lines = new[]
            {
                "import os",
                "x = 1  # noqa",
                "y = 2  # noqa: F401",
                "z = 3  # noqa: F401, E501",
                "def foo(a, b):",
                "    return a + b  # noqa: ARG001",
                "pass",
                "if True:",
                "    pass",
                "    # noqa: E501",
                "list_comprehension = [i for i in range(10) if i > 5]",
                ""
            };
            var builder = new StringBuilder(targetBytes + 256);
            int idx = 0;
            while (builder.Length < targetBytes)
            {
                builder.Append(lines[idx % lines.Length]).Append('\n');
                idx++;
            }
            return builder.ToString();
        }
    }

    private static class SelfTest
    {
        public static int Run()
        {
            var cases = new[]
            {
                Case("literal count",
                    name: "curated/01-literal/sherlock-en",
                    model: "count",
                    pattern: "Sherlock Holmes",
                    haystack: "Sherlock Holmes and Sherlock Holmes",
                    expected: 2),
                Case("literal alternate case-insensitive",
                    name: "curated/02-literal-alternate/sherlock-casei-en",
                    model: "count",
                    pattern: "Sherlock Holmes|John Watson|Irene Adler|Inspector Lestrade|Professor Moriarty",
                    haystack: "john watson JOHN WATSON Irene Adler",
                    expected: 3,
                    caseInsensitive: true),
                Case("words all english",
                    name: "curated/08-words/all-english",
                    model: "count-spans",
                    pattern: @"\b[0-9A-Za-z_]+\b",
                    haystack: "abc 123456789012 _x no!",
                    expected: 19),
                Case("words long english",
                    name: "curated/08-words/long-english",
                    model: "count-spans",
                    pattern: @"\b[0-9A-Za-z_]{12,}\b",
                    haystack: "abc 123456789012 _x no!",
                    expected: 12),
                Case("ruff real grep captures",
                    name: "curated/04-ruff-noqa/real",
                    model: "grep-captures",
                    pattern: @"(\s*)((?:# [Nn][Oo][Qq][Aa])(?::\s?(([A-Z]+[0-9]+(?:[,\s]+)?)+))?)",
                    haystack: "# noqa\nx # noqa: F401, E501\npass\n",
                    // Line 1 ("# noqa") contributes 3 (match + leadingWS Success
                    // with 0 chars + noqa); line 2 contributes 5 (match + all
                    // four captures Success). Mirrors how rebar's other
                    // grep-captures runners count g.Success regardless of span
                    // length.
                    expected: 8),
                Case("ruff tweaked grep captures",
                    name: "curated/04-ruff-noqa/tweaked",
                    model: "grep-captures",
                    pattern: @"(?:# [Nn][Oo][Qq][Aa])(?::\s?(([A-Z]+[0-9]+(?:[,\s]+)?)+))?",
                    haystack: "# noqa\nx # noqa: F401, E501\npass\n",
                    expected: 4),
                Case("aws quick grep",
                    name: "curated/09-aws-keys/quick",
                    model: "grep",
                    pattern: @"((?:ASIA|AKIA|AROA|AIDA)([A-Z0-7]{16}))",
                    haystack: "nope\nAIDAABCDEFGHIJKLMNOP\nnope",
                    expected: 1),
                Case("aws quick compile",
                    name: "curated/09-aws-keys/compile-quick",
                    model: "compile",
                    pattern: @"((?:ASIA|AKIA|AROA|AIDA)([A-Z0-7]{16}))",
                    haystack: "AIDAABCDEFGHIJKLMNOP",
                    expected: 1)
            };

            int failures = 0;
            foreach (var test in cases)
            {
                foreach (var engine in new[] { ("recursive", false), ("statemachine", true) })
                {
                    var plan = BenchmarkRegistry.Build(test.Config, engine.Item2);
                    long actual = plan.Count(test.Config.Haystack, test.Config.Model);
                    string label = $"{test.Name} [{engine.Item1}]";
                    if (actual == test.Expected)
                    {
                        Console.Out.WriteLine($"OK   {label}");
                        continue;
                    }

                    failures++;
                    Console.Out.WriteLine($"FAIL {label}: expected {test.Expected}, got {actual}");
                }
            }

            if (failures == 0)
            {
                Console.Out.WriteLine("OK: rebar runner self-test passed.");
                return 0;
            }

            Console.Error.WriteLine($"FAIL: {failures} self-test case(s) failed.");
            return 1;
        }

        private static TestCase Case(
            string testName,
            string name,
            string model,
            string pattern,
            string haystack,
            long expected,
            bool caseInsensitive = false,
            bool unicode = false)
        {
            var config = RebarConfig.Read(BuildKlv(
                ("name", name),
                ("model", model),
                ("pattern", pattern),
                ("case-insensitive", caseInsensitive ? "true" : "false"),
                ("unicode", unicode ? "true" : "false"),
                ("haystack", haystack),
                ("max-iters", "1"),
                ("max-warmup-iters", "0"),
                ("max-time", "0"),
                ("max-warmup-time", "0")));
            return new TestCase(testName, config, expected);
        }

        private static byte[] BuildKlv(params (string Key, string Value)[] items)
        {
            var builder = new StringBuilder();
            foreach (var (key, value) in items)
            {
                int byteLength = Encoding.UTF8.GetByteCount(value);
                builder.Append(key);
                builder.Append(':');
                builder.Append(byteLength.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(value);
                builder.Append('\n');
            }
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        private sealed record TestCase(string Name, RebarConfig Config, long Expected);
    }
}
