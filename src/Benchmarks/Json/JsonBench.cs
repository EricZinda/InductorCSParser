// Forked from Parlot (https://github.com/sebastienros/parlot, BSD-3-Clause). See LICENSE-PARLOT.txt.
// Extended with InductorParser and Pegasus adapters for the InductorCSParser project.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using InductorParser.Benchmarks.Json.InductorParsers;
using InductorParser.Benchmarks.Json.ParlotParsers;
using InductorParser.Benchmarks.Json.PegasusParsers;
using InductorParser.Benchmarks.Json.PidginParsers;
using InductorParser.Benchmarks.Json.SpracheParsers;
using InductorParser.Benchmarks.Json.SuperpowerParsers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parlot.Fluent;

namespace InductorParser.Benchmarks.Json;

[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), ShortRunJob]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class JsonBench
{
#nullable disable
    private string _bigJson;
    private string _longJson;
    private string _wideJson;
    private string _deepJson;
    private Parser<IJson> _parlotCompiled;
#nullable restore

    private static readonly JsonSerializerSettings _jsonSerializerSettings = new() { MaxDepth = 1024 };
    private static readonly System.Text.Json.JsonDocumentOptions _jsonDocumentOptions = new() { MaxDepth = 1024 };
    private static readonly Random _random = new(42);

    [GlobalSetup]
    public void Setup()
    {
        _bigJson = BuildJson(4, 4, 3).ToString()!;
        _longJson = BuildJson(256, 1, 1).ToString()!;
        _wideJson = BuildJson(1, 1, 256).ToString()!;
        _deepJson = BuildJson(1, 256, 1).ToString()!;

        _parlotCompiled = ParlotJsonParser.Json.Compile();
    }

    // -------- Big --------
    // SystemTextJson is the baseline (fastest hand-written reference).

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_ParlotCompiled() => _parlotCompiled.Parse(_bigJson)!;

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_Parlot() => ParlotJsonParser.Parse(_bigJson)!;

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_InductorParserRune() => InductorJsonParser.Parse(_bigJson);

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_InductorParserGrapheme() => InductorJsonParser.ParseGrapheme(_bigJson);

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_InductorParserTyped() => InductorJsonParser.ParseTyped(_bigJson);

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_PegasusOptimized() => PegasusJsonOptimizedParser.Parse(_bigJson);

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_PegasusWiki() => PegasusJsonWikiParser.Parse(_bigJson);

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_Pidgin() => PidginJsonParser.Parse(_bigJson).Value!;

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_Sprache() => SpracheJsonParser.Parse(_bigJson).Value!;

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_Superpower() => SuperpowerJsonParser.Parse(_bigJson);

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_Newtonsoft() => JToken.Parse(_bigJson);

    [Benchmark(Baseline = true), BenchmarkCategory("Big")]
    public object BigJson_SystemTextJson() => System.Text.Json.JsonDocument.Parse(_bigJson);

    // -------- Long --------

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_ParlotCompiled() => _parlotCompiled.Parse(_longJson)!;

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_Parlot() => ParlotJsonParser.Parse(_longJson)!;

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_InductorParserRune() => InductorJsonParser.Parse(_longJson);

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_InductorParserGrapheme() => InductorJsonParser.ParseGrapheme(_longJson);

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_InductorParserTyped() => InductorJsonParser.ParseTyped(_longJson);

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_PegasusOptimized() => PegasusJsonOptimizedParser.Parse(_longJson);

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_PegasusWiki() => PegasusJsonWikiParser.Parse(_longJson);

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_Pidgin() => PidginJsonParser.Parse(_longJson).Value!;

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_Sprache() => SpracheJsonParser.Parse(_longJson).Value!;

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_Superpower() => SuperpowerJsonParser.Parse(_longJson);

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_Newtonsoft() => JToken.Parse(_longJson);

    [Benchmark(Baseline = true), BenchmarkCategory("Long")]
    public object LongJson_SystemTextJson() => System.Text.Json.JsonDocument.Parse(_longJson);

    // -------- Deep --------
    // Superpower omitted: blows the stack on this shape (per upstream Parlot).

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_ParlotCompiled() => _parlotCompiled.Parse(_deepJson)!;

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_Parlot() => ParlotJsonParser.Parse(_deepJson)!;

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_InductorParserRune() => InductorJsonParser.Parse(_deepJson);

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_InductorParserGrapheme() => InductorJsonParser.ParseGrapheme(_deepJson);

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_InductorParserTyped() => InductorJsonParser.ParseTyped(_deepJson);

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_PegasusOptimized() => PegasusJsonOptimizedParser.Parse(_deepJson);

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_PegasusWiki() => PegasusJsonWikiParser.Parse(_deepJson);

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_Pidgin() => PidginJsonParser.Parse(_deepJson).Value!;

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_Sprache() => SpracheJsonParser.Parse(_deepJson).Value!;

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_Newtonsoft() => JsonConvert.DeserializeObject<JToken>(_deepJson, _jsonSerializerSettings)!;

    [Benchmark(Baseline = true), BenchmarkCategory("Deep")]
    public object DeepJson_SystemTextJson() => System.Text.Json.JsonDocument.Parse(_deepJson, _jsonDocumentOptions);

    // -------- Wide --------

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_ParlotCompiled() => _parlotCompiled.Parse(_wideJson)!;

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_Parlot() => ParlotJsonParser.Parse(_wideJson)!;

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_InductorParserRune() => InductorJsonParser.Parse(_wideJson);

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_InductorParserGrapheme() => InductorJsonParser.ParseGrapheme(_wideJson);

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_InductorParserTyped() => InductorJsonParser.ParseTyped(_wideJson);

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_PegasusOptimized() => PegasusJsonOptimizedParser.Parse(_wideJson);

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_PegasusWiki() => PegasusJsonWikiParser.Parse(_wideJson);

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_Pidgin() => PidginJsonParser.Parse(_wideJson).Value!;

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_Sprache() => SpracheJsonParser.Parse(_wideJson).Value!;

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_Superpower() => SuperpowerJsonParser.Parse(_wideJson);

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_Newtonsoft() => JToken.Parse(_wideJson);

    [Benchmark(Baseline = true), BenchmarkCategory("Wide")]
    public object WideJson_SystemTextJson() => System.Text.Json.JsonDocument.Parse(_wideJson);

    public static IJson BuildJson(int length, int depth, int width)
        => new JsonArray(
            Enumerable.Repeat(1, length)
                .Select(_ => BuildObject(depth, width))
                .ToArray()
        );

    private static IJson BuildObject(int depth, int width)
    {
        if (depth == 0)
        {
            return new JsonString(RandomString(6));
        }
        return new JsonObject(
            new Dictionary<string, IJson>(
                Enumerable.Repeat(1, width)
                .Select(_ => new KeyValuePair<string, IJson>(RandomString(5), BuildObject(depth - 1, width)))
            )
        );
    }

    // 1 in 32 ≈ 3.1% of characters in generated strings are specials that
    // require JSON escape encoding on the wire. Rationale:
    //
    // * 0% (upstream's default) never exercises the escape path, so we'd
    //   be measuring escape-code cache behavior instead of steady-state
    //   throughput.
    // * Representative JSON carrying natural text (log messages, product
    //   descriptions, user names with the occasional quoted phrase)
    //   typically contains 1-5% escape-worthy characters. Clean data
    //   payloads (numerical / ID-heavy API responses) sit at roughly 0%.
    //   3% is the middle of the realistic range.
    // * Much above 5% starts measuring escape-decoding throughput
    //   specifically, which is a fine thing to benchmark, but not "overall JSON
    //   parse speed on realistic input."
    //
    // 1 in 32 is cheap to check (single `Random.Next(32) == 0`) and large
    // enough that a 6-character string has only ~17% chance of containing
    // any escape, matching the reality that most short string values
    // don't need escaping.
    private const int EscapeDenominator = 32;

    public static string RandomString(int length)
    {
        const string alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        const string specials = "\"\\\n\r\t\b\f";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            if (_random.Next(EscapeDenominator) == 0)
                sb.Append(specials[_random.Next(specials.Length)]);
            else
                sb.Append(alphanumeric[_random.Next(alphanumeric.Length)]);
        }
        return sb.ToString();
    }
}
