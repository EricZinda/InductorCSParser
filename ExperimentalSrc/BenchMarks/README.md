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

The state-machine rows below come from this project. The non-SM rows (`InductorParserToken`, `SystemTextJson`, `Parlot`) are pulled from the main bench in [../../src/Benchmarks/README.md](../../src/Benchmarks/README.md) so the comparison reads end-to-end on one page. The grammar, lexer, and Symbol-tree output are identical to `InductorParserToken`; only the evaluator differs.

```
BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8246)
Arm64 RyuJIT AdvSIMD, .NET 8.0.25
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

| Method                              | Mean        | Ratio | Allocated  | Alloc Ratio |
|------------------------------------ |------------:|------:|-----------:|------------:|
| BigJson_SystemTextJson              |    28.21 μs |  1.00 |   24.12 KB |        1.00 |
| BigJson_InductorParserStateMachine  |   184.22 μs |  6.53 |  193.56 KB |        8.02 |
| BigJson_InductorParserToken         |   240.31 μs |  8.52 |  197.50 KB |        8.19 |
|                                     |             |       |            |             |
| DeepJson_SystemTextJson             |    86.00 μs |  1.00 |   20.24 KB |        1.00 |
| DeepJson_InductorParserStateMachine |    94.08 μs |  1.09 |   86.48 KB |        4.27 |
| DeepJson_InductorParserToken        |   144.99 μs |  1.69 |   88.45 KB |        4.37 |
|                                     |             |       |            |             |
| LongJson_SystemTextJson             |    19.17 μs |  1.00 |   24.12 KB |        1.00 |
| LongJson_InductorParserStateMachine |   136.60 μs |  7.13 |  140.36 KB |        5.82 |
| LongJson_InductorParserToken        |   160.29 μs |  8.36 |  143.90 KB |        5.97 |
|                                     |             |       |            |             |
| WideJson_SystemTextJson             |    12.11 μs |  1.00 |   16.12 KB |        1.00 |
| WideJson_InductorParserStateMachine |    90.48 μs |  7.47 |  108.52 KB |        6.73 |
| WideJson_InductorParserToken        |   116.21 μs |  9.60 |  111.29 KB |        6.90 |

`Ratio` is relative to `SystemTextJson`. Same baseline framing as the main bench: STJ is the hand-written, allocation-aware JSON parser in the .NET BCL, so it's the reasonable "how fast can a .NET programmer actually get" reference point.

## What the numbers say

On every shape the state machine runs about 1.17x to 1.54x faster than the recursive evaluator on the same grammar. Deep is the closest case: at 94.08 μs vs STJ's 86.00 μs (1.09x), a grammar-based parser is essentially even with the hand-written BCL JSON reference.

The win comes entirely from the evaluator. Lowering the grammar once to a flat opcode array means the inner loop is one indirect dispatch (a switch on a small enum that the JIT lowers to a jump table) per state transition, against the recursive evaluator's per-rule virtual `TryParseRule` call plus the per-rule transaction setup `Rule.TryParse` does on top. The state machine also pools its backtrack and output buffers across parses via thread-static slots, so back-to-back parses on the same thread allocate nothing for the evaluator's own bookkeeping. Per-iteration backtrack frames are dropped when the loop's inner rule is one of the always-advancing single-state matches (`Literal`, `Token`, `OneOf`), and `FirstOf` alternatives skip themselves on a peeked-rune mismatch the same way the recursive `FirstOfRule` does at runtime. See [../InductorParser/StateMachine/Lowerer.cs](../InductorParser/StateMachine/Lowerer.cs) for what each rule lowers to.

Allocations are now essentially the same as the recursive evaluator: 194 / 86 / 140 / 109 KB on Big / Deep / Long / Wide, against 198 / 88 / 144 / 111 KB for `InductorParserToken` (about 2% less across the board). Earlier runs showed a 30-40% drop, but the recursive evaluator has since caught up on its own bookkeeping allocations, so the buffer-pooling advantage no longer shows up as a meaningful gap.

## Status

Iteration-1 supported rules: `Literal`, `Grapheme`, `OneOf`, `AllOf`, `FirstOf`, `BetweenInclusive`, `Optional`, `Not`, `Peek`, `Eof`, `LateBound`, `ZeroOrMore`, `OneOrMore`, `ScanUntil` (with TokenSet stop set). Less common rule types still bridge back through `Rule.TryParse`. Parse budgets aren't enforced on the state-machine path yet and are the one remaining feature gap before this could replace the recursive evaluator as the default.
