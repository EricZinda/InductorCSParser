using global::InductorParser;
using global::InductorParser.Benchmarks.Json.InductorParsers;
using global::InductorParser.StateMachine;

namespace InductorParser.Benchmarks.Json.InductorParsers;

// State-machine evaluator variant of InductorJsonParser. Uses the same
// grammar (JsonRule) and the same lexer the recursive variant uses.
// Only the evaluator differs: routes through StateMachineParser, which
// lowers JsonRule to a flat State[] program once and runs it via the
// switch-dispatch inner loop instead of the recursive virtual
// TryParseRule path. Pairs with the InductorParser row in JsonBench
// so the comparison is purely evaluator-vs-evaluator.
//
// Lives in ExperimentalSrc/ alongside the SM engine because it's a
// direct consumer of StateMachineParser.Parse. The recursive-only
// build of src/Benchmarks/ doesn't include this file or any of the
// _InductorParserStateMachine BenchmarkDotNet methods that depend
// on it.
public static class InductorJsonParserStateMachine
{
    // Same options the recursive InductorJsonParser uses internally.
    // MaxDepth=0 disables the recursion-depth budget (the Deep input
    // is 256 levels of nested objects, which would trip the default
    // MaxDepth=1000).
    private static readonly ParseOptions _options = new()
    {
        MaxDepth = 0,
    };

    public static ParseResult Parse(string input) =>
        StateMachineParser.Parse(InductorJsonParser.JsonRule, input, _options);
}
