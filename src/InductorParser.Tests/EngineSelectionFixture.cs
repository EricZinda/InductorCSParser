using System;
using NUnit.Framework;

namespace InductorParser.Tests;

// Suite-wide engine selector. Reads the INDUCTOR_DEFAULT_ENGINE
// environment variable once before any fixture runs and flips
// ParseOptions.DefaultUseStateMachine on when it's set to
// "statemachine" (case-insensitive). With no env var, the default
// stays false and Rule.Parse routes through the recursive evaluator
// exactly as before. With the env var on, every Rule.Parse call in
// the suite goes through the state machine instead, so the same
// fixtures double as a cross-engine validation pass under
// INDUCTOR_DEFAULT_ENGINE=statemachine dotnet test.
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
        var requested = Environment.GetEnvironmentVariable("INDUCTOR_DEFAULT_ENGINE");
        bool useStateMachine = string.Equals(requested, "statemachine", StringComparison.OrdinalIgnoreCase);
        ParseOptions.DefaultUseStateMachine = useStateMachine;
        TestContext.WriteLine($"InductorParser default engine: {(useStateMachine ? "statemachine" : "recursive")}");
    }
}
