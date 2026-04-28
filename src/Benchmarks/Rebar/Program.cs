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
    private const string EngineName = "inductorparser";

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1)
                throw new ArgumentException($"Usage: {Assembly.GetExecutingAssembly().GetName().Name} ({EngineName} | --version | --self-test)");

            if (args[0] == "--version")
            {
                Console.Out.WriteLine(VersionString());
                return 0;
            }

            if (args[0] == "--self-test")
                return SelfTest.Run();

            if (args[0] != EngineName)
                throw new ArgumentException($"Unknown engine '{args[0]}'. Expected '{EngineName}'.");

            var config = RebarConfig.Read(Console.OpenStandardInput());
            foreach (var sample in Run(config))
                Console.Out.WriteLine($"{sample.DurationNanoseconds.ToString(CultureInfo.InvariantCulture)},{sample.Count.ToString(CultureInfo.InvariantCulture)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static IEnumerable<Sample> Run(RebarConfig config)
    {
        if (config.Model == "compile")
        {
            Warmup(() => RunCompileOnce(config).Count, config.MaxWarmupIters, config.MaxWarmupTimeNanoseconds);
            return Measure(() => RunCompileOnce(config), config.MaxIters, config.MaxTimeNanoseconds);
        }

        var plan = BenchmarkRegistry.Build(config);
        Warmup(() => plan.Count(config.Haystack, config.Model), config.MaxWarmupIters, config.MaxWarmupTimeNanoseconds);
        return Measure(() => RunSearchOnce(plan, config), config.MaxIters, config.MaxTimeNanoseconds);
    }

    private static Sample RunSearchOnce(BenchmarkPlan plan, RebarConfig config)
    {
        long start = Stopwatch.GetTimestamp();
        long count = plan.Count(config.Haystack, config.Model);
        return new Sample(ElapsedNanoseconds(start), count);
    }

    private static Sample RunCompileOnce(RebarConfig config)
    {
        long start = Stopwatch.GetTimestamp();
        var plan = BenchmarkRegistry.Build(config);
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
                    expected: 7),
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
                var plan = BenchmarkRegistry.Build(test.Config);
                long actual = plan.Count(test.Config.Haystack, test.Config.Model);
                if (actual == test.Expected)
                {
                    Console.Out.WriteLine($"OK   {test.Name}");
                    continue;
                }

                failures++;
                Console.Out.WriteLine($"FAIL {test.Name}: expected {test.Expected}, got {actual}");
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
