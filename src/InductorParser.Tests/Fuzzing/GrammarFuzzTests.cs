using NUnit.Framework;

namespace InductorParser.Tests;

// Runs the differential grammar fuzzer (see GrammarFuzzHarness for the
// oracle list). Everything is deterministic per seed, so the standard
// test is an ordinary reproducible test, not a flaky one.
//
// One thing to know when this fails after an unrelated-looking change:
// the harness draws grammar text from UnicodeExamples by reflection, so
// adding a corpus constant reshuffles which grammars the same seeds
// produce. A new failure after a corpus addition means the fuzzer
// surfaced a real divergence in the reshuffled coverage, not that the
// test is flaky. Every divergence message includes the seed, the input,
// and the grammar expression, which together are the failing repro.
[TestFixture]
public class GrammarFuzzTests
{
    [Test]
    public void Fuzzer_standard_seed_range_finds_no_divergences()
    {
        var harness = new GrammarFuzzHarness();
        harness.RunSeeds(0, 2_000);

        Assert.That(harness.Divergences, Is.Empty,
            string.Join("\n----\n", harness.Divergences));
        // Volume checks so a generator regression that quietly produces
        // trivial grammars (or no successful parses, leaving the tree
        // oracles idle) fails this test instead of passing it hollowly.
        Assert.That(harness.CasesChecked, Is.GreaterThan(10_000),
            "the generator should produce real case volume");
        Assert.That(harness.SuccessfulParses, Is.GreaterThan(500),
            "successful parses are what exercise the tree oracles");
        Assert.That(harness.CrossFormComparisons, Is.GreaterThan(5_000),
            "most grammars should be eligible for the FormC-vs-FormD oracle");
    }

    [Test]
    [Explicit("Deep campaign, about a minute of runtime. Run on demand when hunting, the standard test covers CI.")]
    public void Fuzzer_deep_campaign_finds_no_divergences()
    {
        var harness = new GrammarFuzzHarness();
        harness.RunSeeds(0, 100_000);

        Assert.That(harness.Divergences, Is.Empty,
            string.Join("\n----\n", harness.Divergences));
    }
}
