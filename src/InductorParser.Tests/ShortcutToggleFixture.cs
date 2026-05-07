using System;
using NUnit.Framework;

namespace InductorParser.Tests;

// Suite-wide setup that reads INDUCTOR_DISABLE_LOOKAHEAD_SHORTCUT and
// flips Rule.DisableLookaheadShortcut accordingly. Set the env var to
// any non-empty, non-"0", non-"false" value to disable the shortcut
// process-wide before any rule is compiled.
//
// Use case: A/B-test the optimization. With the shortcut off the suite
// should still pass except for trace-output tests (which legitimately
// lose their SKIP lines). Any other failure is a soundness bug in the
// shortcut path. Also useful for measuring the optimization's
// performance contribution by timing the same suite both ways.
//
// SetUpFixture at the namespace root fires before any test in
// InductorParser.Tests.* runs. Mirrors EngineSelectionFixture in the
// experimental test project.
[SetUpFixture]
public class ShortcutToggleFixture
{
    [OneTimeSetUp]
    public void ResolveLookaheadShortcut()
    {
        var requested = Environment.GetEnvironmentVariable("INDUCTOR_DISABLE_LOOKAHEAD_SHORTCUT");
        Rule.DisableLookaheadShortcut = !string.IsNullOrEmpty(requested)
            && !string.Equals(requested, "0", StringComparison.Ordinal)
            && !string.Equals(requested, "false", StringComparison.OrdinalIgnoreCase);
        TestContext.WriteLine($"InductorParser lookahead shortcut: {(Rule.DisableLookaheadShortcut ? "disabled" : "enabled")}");
    }
}
