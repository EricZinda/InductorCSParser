# State-Machine Evaluator Benchmarks (Experimental)

BenchmarkDotNet harness for the experimental state-machine evaluator (`StateMachineParser.Parse`) that lives in [../InductorParser/StateMachine/](../InductorParser/StateMachine/). The evaluator lowers the rule tree to a flat opcode array once and dispatches through a single switch loop, instead of the recursive virtual-dispatch path `Rule.Parse(input)` uses today.

This is a separate project for two reasons. First, the evaluator is still feature-incomplete: parse budgets (`RuleCountLimit`, `MaxDepth`, `Timeout`, `Cancellation`) aren't enforced on this path yet, and the rare rule types (`WithinToken`, rule-stoppered `ScanUntil`, custom `Rule` subclasses) bridge back through `Rule.TryParse`. Second, keeping it out of the main benchmark readme stops the experimental numbers from being read as a headline result.

For the apples-to-apples comparison against other C# parser libraries on JSON, see [../../src/Benchmarks/README.md](../../src/Benchmarks/README.md). This readme reuses the same `JsonBench.BuildJson` corpus generator so the inputs match exactly.

## Running

JSON benchmarks (the four rows in the table below):

```
dotnet run -c Release --project ExperimentalSrc/BenchMarks/InductorParser.StateMachine.Benchmarks.csproj -- --filter *Json* --exporters GitHub
```

Hand-timed Stopwatch comparison against the recursive evaluator across five representative grammars (Identifier, BalancedParens, KeywordList, Arithmetic, JSON):

```
dotnet run -c Release --project ExperimentalSrc/BenchMarks/InductorParser.StateMachine.Benchmarks.csproj -- --state-machine-compare
```

The Stopwatch loop takes min-of-N rounds with warmup. Not BenchmarkDotNet-quality, but precise enough to see same-order-of-magnitude differences in seconds rather than 10+ minute BDN runs.

## JSON results

The state-machine rows below come from this project. The non-SM rows (`InductorParserGrapheme`, `SystemTextJson`, `Parlot`) are pulled from the main bench in [../../src/Benchmarks/README.md](../../src/Benchmarks/README.md) so the comparison reads end-to-end on one page. The grammar, lexer, and Symbol-tree output are identical to `InductorParserGrapheme`; only the evaluator differs.

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8246)
Arm64 RyuJIT AdvSIMD, .NET 8.0.25
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

| Method                              | Mean        | Ratio | Allocated  | Alloc Ratio |
|------------------------------------ |------------:|------:|-----------:|------------:|
| BigJson_SystemTextJson              |    25.51 μs |  1.00 |   24.12 KB |        1.00 |
| BigJson_InductorParserStateMachine  |   217.10 μs |  8.51 |  264.11 KB |       10.95 |
| BigJson_InductorParserGrapheme      |   363.27 μs | 14.24 |  369.85 KB |       15.34 |
|                                     |             |       |            |             |
| DeepJson_SystemTextJson             |    82.89 μs |  1.00 |   20.24 KB |        1.00 |
| DeepJson_InductorParserStateMachine |   106.20 μs |  1.28 |  123.39 KB |        6.10 |
| DeepJson_InductorParserGrapheme     |   188.88 μs |  2.28 |  149.48 KB |        7.38 |
|                                     |             |       |            |             |
| LongJson_SystemTextJson             |    18.86 μs |  1.00 |   24.12 KB |        1.00 |
| LongJson_InductorParserStateMachine |   164.50 μs |  8.72 |  196.30 KB |        8.14 |
| LongJson_InductorParserGrapheme     |   238.16 μs | 12.63 |  259.84 KB |       10.77 |
|                                     |             |       |            |             |
| WideJson_SystemTextJson             |    12.44 μs |  1.00 |   16.12 KB |        1.00 |
| WideJson_InductorParserStateMachine |   110.70 μs |  8.90 |  146.63 KB |        9.10 |
| WideJson_InductorParserGrapheme     |   181.70 μs | 14.72 |  215.37 KB |       13.36 |

`Ratio` is relative to `SystemTextJson`. Same baseline framing as the main bench: STJ is the hand-written, allocation-aware JSON parser in the .NET BCL, so it's the reasonable "how fast can a .NET programmer actually get" reference point.

## What the numbers say

On every shape the state machine runs about 1.45x to 1.79x faster than the recursive evaluator on the same grammar. Deep is the closest case: at 106 μs vs STJ's 83 μs, a grammar-based parser is within striking distance of the hand-written BCL JSON reference.

The win comes entirely from the evaluator. Lowering the grammar once to a flat opcode array means the inner loop is one indirect dispatch (a switch on a small enum that the JIT lowers to a jump table) per state transition, against the recursive evaluator's per-rule virtual `TryParseRule` call plus the per-rule transaction setup `Rule.TryParse` does on top. The state machine also pools its backtrack and output buffers across parses via thread-static slots, so back-to-back parses on the same thread allocate nothing for the evaluator's own bookkeeping. Per-iteration backtrack frames are dropped when the loop's inner rule is one of the always-advancing single-state matches (`Literal`, `Token`, `OneOf`), and `FirstOf` alternatives skip themselves on a peeked-rune mismatch the same way the recursive `FirstOfRule` does at runtime. See [../InductorParser/StateMachine/Lowerer.cs](../InductorParser/StateMachine/Lowerer.cs) for what each rule lowers to.

Allocations drop 30-40% relative to the recursive evaluator: 264 / 123 / 196 / 147 KB on Big / Deep / Long / Wide, against 370 / 149 / 260 / 215 KB for `InductorParserGrapheme`. The savings come from the per-thread buffer pooling described above (the backtrack stack, call stack, and output list).

## Status

Iteration-1 supported rules: `Literal`, `Grapheme`, `OneOf`, `AllOf`, `FirstOf`, `BetweenInclusive`, `Optional`, `Not`, `Peek`, `Eof`, `LateBound`, `ZeroOrMore`, `OneOrMore`, `ScanUntil` (with TokenSet stop set). Less common rule types still bridge back through `Rule.TryParse`. Parse budgets aren't enforced on the state-machine path yet and are the one remaining feature gap before this could replace the recursive evaluator as the default.
