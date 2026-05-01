using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using InductorParser.Benchmarks.Json;
using InductorParser.Benchmarks.Json.InductorParsers;

namespace InductorParser.StateMachine.Benchmarks.Json;

// State-machine evaluator JSON benchmark. Mirrors the four
// InductorParserStateMachine rows that used to live in JsonBench
// (Big / Long / Deep / Wide) before the SM split. Pairs against the
// InductorParserRune row in the main JsonBench so a comparison run
// of both projects produces engine-vs-engine numbers on the same
// inputs.
//
// JsonBench.BuildJson is public, so the four shape inputs come from
// the same generator the main JsonBench uses. SystemTextJson is set
// as the BDN baseline in the main JsonBench; this fixture stands
// alone (no baseline tag) because BDN groups baselines per fixture.
[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), ShortRunJob]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class StateMachineJsonBench
{
#nullable disable
    private string _bigJson;
    private string _longJson;
    private string _wideJson;
    private string _deepJson;
#nullable restore

    [GlobalSetup]
    public void Setup()
    {
        _bigJson = JsonBench.BuildJson(4, 4, 3).ToString()!;
        _longJson = JsonBench.BuildJson(256, 1, 1).ToString()!;
        _wideJson = JsonBench.BuildJson(1, 1, 256).ToString()!;
        _deepJson = JsonBench.BuildJson(1, 256, 1).ToString()!;
    }

    [Benchmark, BenchmarkCategory("Big")]
    public object BigJson_InductorParserStateMachine() =>
        InductorJsonParserStateMachine.Parse(_bigJson);

    [Benchmark, BenchmarkCategory("Long")]
    public object LongJson_InductorParserStateMachine() =>
        InductorJsonParserStateMachine.Parse(_longJson);

    [Benchmark, BenchmarkCategory("Deep")]
    public object DeepJson_InductorParserStateMachine() =>
        InductorJsonParserStateMachine.Parse(_deepJson);

    [Benchmark, BenchmarkCategory("Wide")]
    public object WideJson_InductorParserStateMachine() =>
        InductorJsonParserStateMachine.Parse(_wideJson);
}
