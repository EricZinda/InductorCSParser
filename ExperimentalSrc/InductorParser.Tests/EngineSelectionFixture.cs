using System;
using NUnit.Framework;
using InductorParser.StateMachine;

namespace InductorParser.Tests;

// Suite-wide engine selector. Two responsibilities, both run once
// before any test in the assembly:
//
//   1. Register the state-machine engine as Rule.AlternativeEvaluator
//      so Rule.Parse can route to it when asked. The engine ships in
//      ExperimentalSrc/ as a separate assembly; this fixture is the
//      bridge that wires it into the routing dispatcher.
//   2. Read the INDUCTOR_DEFAULT_ENGINE environment variable and flip
//      ParseOptions.DefaultUseAlternativeEvaluator on when it's set
//      to "statemachine" (case-insensitive). With no env var the
//      default stays false and Rule.Parse routes through the
//      recursive evaluator exactly as before. With the env var on,
//      every Rule.Parse call in the suite goes through the state
//      machine instead, so the same fixtures double as a cross-engine
//      validation pass under
//      INDUCTOR_DEFAULT_ENGINE=statemachine dotnet test.
//
// Compare fixtures that need a guaranteed recursive baseline
// (StateMachineE2ECompareTests, StateMachineParserTests,
// StateMachineNormalizationCompareTests, StateMachineBudgetCompareTests,
// StateMachineGrammarCompareTests) call rule.ParseRecursive directly
// for the legacy side, so the comparison stays an actual cross-engine
// comparison even when the global default flips on.
//
// Lives at the InductorParser.Tests namespace root so the fixture's
// OneTimeSetUp fires before any test in InductorParser.Tests.* runs.
[SetUpFixture]
public class EngineSelectionFixture
{
    [OneTimeSetUp]
    public void ResolveDefaultEngine()
    {
        Rule.AlternativeEvaluator = StateMachineParser.Parse;
        var requested = Environment.GetEnvironmentVariable("INDUCTOR_DEFAULT_ENGINE");
        bool useStateMachine = string.Equals(requested, "statemachine", StringComparison.OrdinalIgnoreCase);
        ParseOptions.DefaultUseAlternativeEvaluator = useStateMachine;
        TestContext.WriteLine($"InductorParser default engine: {(useStateMachine ? "statemachine" : "recursive")}");

        // INDUCTOR_DISABLE_LOOKAHEAD_SHORTCUT=1 disables the lookahead
        // skip in the StateMachine engine so the suite can A/B against
        // the optimization. Anything that fails outside trace output
        // (which legitimately loses its SKIP lines) is a soundness bug.
        // The recursive engine no longer has this shortcut.
        var disableShortcut = Environment.GetEnvironmentVariable("INDUCTOR_DISABLE_LOOKAHEAD_SHORTCUT");
        Lowerer.DisableLookaheadShortcut = !string.IsNullOrEmpty(disableShortcut)
            && !string.Equals(disableShortcut, "0", StringComparison.Ordinal)
            && !string.Equals(disableShortcut, "false", StringComparison.OrdinalIgnoreCase);
        TestContext.WriteLine($"InductorParser lookahead shortcut: {(Lowerer.DisableLookaheadShortcut ? "disabled" : "enabled")}");
    }
}
