# Testing the State Machine Engine

The state-machine engine lives in `ExperimentalSrc/` as a drop-in sibling of `src/`, and its tests live in this folder as their own project (`InductorParser.StateMachine.Tests.csproj`). The coverage bar for what a rule's test file must contain is the same one documented in [docs/TestArchitecture.md](../../docs/TestArchitecture.md). This doc covers the part that's specific to the state machine: how to run these tests, and how to route parses through the engine instead of the recursive evaluator.

## Running the Tests

```
./ExperimentalSrc/test.sh
```

The script sets `INDUCTOR_DEFAULT_ENGINE=statemachine` (covered below) and runs `dotnet test` on this project. Arguments pass through, so `./ExperimentalSrc/test.sh --filter "FullyQualifiedName~Atom_fragment"` targets one fixture. The main suite's `./test.sh` at the repo root runs only the root solution and knows nothing about this project. To run this project without the engine flip, call `dotnet test ExperimentalSrc/InductorParser.Tests/InductorParser.StateMachine.Tests.csproj` directly. `ExperimentalSrc/InductorParser.Experimental.sln` ties the core projects and the experimental ones together if you want one solution for the IDE.

## Routing Parses Through the State Machine

Every `Rule.Parse(...)` call normally goes through the recursive evaluator. Setting `INDUCTOR_DEFAULT_ENGINE=statemachine` before `dotnet test` flips a process-wide default so every `Rule.Parse(...)` call in this test project routes through the state-machine evaluator instead, without rewriting individual tests.

```
INDUCTOR_DEFAULT_ENGINE=statemachine dotnet test ExperimentalSrc/InductorParser.Tests/InductorParser.StateMachine.Tests.csproj
```

`EngineSelectionFixture` (a `[SetUpFixture]` in this folder) runs once before any fixture. It registers the engine as `Rule.AlternativeEvaluator`, the delegate hook `Rule.Parse` dispatches to when routing is on, then reads the env var and writes `true` into `ParseOptions.DefaultUseAlternativeEvaluator` when the value is `statemachine` (case-insensitive). From there the dispatcher in `Rule.Parse` resolves to the hook instead of `Rule.ParseRecursive`. Cross-engine compare fixtures (`StateMachineE2ECompareTests`, `StateMachineParserTests`, `StateMachineNormalizationCompareTests`, `StateMachineBudgetCompareTests`, `StateMachineGrammarCompareTests`) call `rule.ParseRecursive(...)` directly for the recursive baseline, so the comparison stays apples-to-apples even when the global default is flipped on. A `TestContext.WriteLine` at the start of the run says which engine the suite picked up.

The selector lives on `ParseOptions` as the internal `UseAlternativeEvaluator` (per-call, nullable bool) and `DefaultUseAlternativeEvaluator` (process-wide static). Both are internal on purpose: this is test plumbing, not a documented user feature. Outside callers who explicitly want the state machine should keep calling `StateMachineParser.Parse` directly.

The env var only has an effect in this project. The main suite in `src/InductorParser.Tests/` never loads `EngineSelectionFixture`, so nothing registers the hook there and `INDUCTOR_DEFAULT_ENGINE` is silently inert: every `Rule.Parse` call goes recursive. That's deliberate. `src/` builds and tests cleanly without `ExperimentalSrc/` existing at all.
