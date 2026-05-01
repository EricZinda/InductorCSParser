using System;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace InductorParser.Tests;

// Skips a test when the suite is running under the state-machine engine
// (INDUCTOR_DEFAULT_ENGINE=statemachine, set by EngineSelectionFixture).
// Used to mark trace-format tests whose byte-for-byte expected strings
// describe the recursive engine's per-rule trace output. The state
// machine fuses inlined rules during lowering, so it has no per-rule
// frame to emit Enter/Exit lines on, and the trace divergence is
// structural rather than a bug. See backlog 5aaa for the full
// reasoning.
//
// Tests with this attribute still run under the recursive engine,
// which is where the strict trace format is meaningful in the first
// place. The state-machine side has its own structural trace
// assertions in StateMachineTracingTests.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RecursiveEngineOnlyAttribute : NUnitAttribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test)
    {
        if (ParseOptions.DefaultUseAlternativeEvaluator)
        {
            Assert.Ignore(
                "Recursive-engine-only: this test asserts byte-for-byte on the " +
                "recursive engine's per-rule trace output. The state machine " +
                "fuses inlined rules and produces a different trace shape by " +
                "design. See backlog 5aaa.");
        }
    }

    public void AfterTest(ITest test) { }
}
