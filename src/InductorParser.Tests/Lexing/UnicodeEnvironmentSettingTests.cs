using System;
using System.Text;
using NUnit.Framework;
using InductorParser.Lexing;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for the process-wide Unicode implementation setting
// (UnicodeEnvironment.Implementation): Automatic resolution, explicit
// overrides, the freeze-on-first-use rule, the one-setting-for-both
// design (segmentation and normalization can't be chosen separately),
// and the test-only reset. The fixture mutates process-wide state, so
// SetUp and TearDown both restore a fresh unfrozen Automatic through
// UnicodeEnvironment.ResetForTesting.
//
// On .NET 10 the built-in and runtime segmentations produce identical
// results, and the normalizations differ only on the code points
// Unicode 16.0 added (the runtime normalizes through the suite's
// app-local ICU 72.1, which predates them). The differential tests in
// GraphemeSegmentationTests and UnicodeNormalizationTests hold each
// pair equal everywhere they compare, so ordinary input can't tell
// the implementations apart here, and these tests check
// ActiveImplementation instead of parser output. That check proves what it looks like it
// proves: ActiveImplementation reports the same resolved value that
// GraphemeSegmentation and UnicodeNormalization read when they choose
// which implementation to run, and there is no second copy of that
// state to drift out of sync, so when it says Bundled the next
// segmentation or normalization call runs the built-in code.
[TestFixture]
// NonParallelizable because every test in the process shares the one
// UnicodeEnvironment, and this fixture is the only one that mutates
// it. A fixture running at the same time could have the setting frozen
// out from under this one mid-test (any parse freezes it, so this
// fixture's next set would throw), could itself resolve to a Bundled
// value this fixture set a moment earlier instead of the build's
// default, or could be mid-parse when the reset clears the
// cluster-boundary cache and end up mixing boundaries from two
// segmenters.
[NonParallelizable]
public class UnicodeEnvironmentSettingTests
{
    [SetUp]
    public void ResetBefore() => UnicodeEnvironment.ResetForTesting();

    [TearDown]
    public void ResetAfter() => UnicodeEnvironment.ResetForTesting();

    [Test]
    public void Automatic_is_the_default_and_resolves_to_runtime_on_this_build()
    {
        Assert.That(UnicodeEnvironment.Implementation,
            Is.EqualTo(UnicodeImplementation.Automatic));

        // Under dotnet test this always runs on CoreCLR against the
        // net8.0 library build (the highest target the library offers,
        // consumed by the net10.0 test csproj), where Automatic means
        // the runtime's StringInfo and string.Normalize. The only
        // other way test sources run (the Unity PlayMode sync) copies
        // Core/, Rules/, and E2EExamples/ plus the root files, never
        // Lexing/, so this fixture stays off Unity. That placement can't drift
        // silently: if this file ever reached the netstandard2.1
        // build, Automatic would resolve to Bundled there and this
        // assert would fail the IL2CPP pass.
        //
        // Automatic's promise has two halves, one per assembly:
        // Runtime in the net8.0 assembly, which is what this test
        // asserts, and Bundled in the netstandard2.1 assembly. That
        // second half is asserted by
        // Bundled_unicode_implementations_are_active_under_il2cpp in
        // the Unity project's ParseSmokeTest.cs, which runs inside
        // the IL2CPP player, the environment that actually loads the
        // netstandard2.1 assembly.
        GraphemeHelpers.FirstClusterLength("a".AsSpan());
        Assert.That(UnicodeEnvironment.ActiveImplementation,
            Is.EqualTo(UnicodeImplementation.Runtime));
    }

    [Test]
    public void Explicit_bundled_is_active_after_the_first_query()
    {
        // The "a" query exists only to trigger the freeze. No text can
        // tell the implementations apart on this runtime (the fixture
        // header explains why the enum assert is the real check here).
        // The tests that drive real text under an explicit Bundled are
        // Helper_methods_answer_with_the_active_implementation and the
        // end-to-end compile-and-parse test below.
        UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;
        GraphemeHelpers.FirstClusterLength("a".AsSpan());
        Assert.That(UnicodeEnvironment.ActiveImplementation,
            Is.EqualTo(UnicodeImplementation.Bundled));
    }

    [Test]
    public void Explicit_runtime_is_active_after_the_first_query()
    {
        UnicodeEnvironment.Implementation = UnicodeImplementation.Runtime;
        GraphemeHelpers.FirstClusterLength("a".AsSpan());
        Assert.That(UnicodeEnvironment.ActiveImplementation,
            Is.EqualTo(UnicodeImplementation.Runtime));
    }

    [Test]
    public void Setting_twice_before_the_freeze_keeps_the_last_write()
    {
        UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;
        UnicodeEnvironment.Implementation = UnicodeImplementation.Runtime;
        Assert.That(UnicodeEnvironment.ActiveImplementation,
            Is.EqualTo(UnicodeImplementation.Runtime));
    }

    [Test]
    public void Setting_after_a_segmentation_query_throws()
    {
        GraphemeHelpers.FirstClusterLength("a".AsSpan());
        var exception = Assert.Throws<InvalidOperationException>(
            () => UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled);
        Assert.That(exception!.Message, Does.Contain("before building grammars"));
    }

    [Test]
    public void Setting_after_a_normalization_query_throws()
    {
        // The other subsystem's queries freeze the same shared choice:
        // there is one setting, so a normalization query locks
        // segmentation too.
        NormalizationHelpers.Normalize("a", NormalizationForm.FormC);
        Assert.Throws<InvalidOperationException>(
            () => UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled);
    }

    [Test]
    public void Reading_the_active_implementation_freezes()
    {
        _ = UnicodeEnvironment.ActiveImplementation;
        Assert.Throws<InvalidOperationException>(
            () => UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled);
    }

    [Test]
    public void Constructing_a_token_rule_freezes()
    {
        // Token validates its literal is one grapheme cluster through
        // GraphemeHelpers.FirstClusterLength, so grammar construction
        // is a freeze point. This is why the docs say to set the
        // property before building grammars, not just before parsing.
        Token('x');
        Assert.Throws<InvalidOperationException>(
            () => UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled);
    }

    [Test]
    public void Compiling_a_grammar_freezes()
    {
        // Compile converts each literal to the grammar's form through
        // the process-wide normalizer, so a normalizing compile is a
        // freeze point even before the first parse.
        Literal("abc").Compile();
        Assert.Throws<InvalidOperationException>(
            () => UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled);
    }

    [Test]
    public void Undefined_enum_value_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UnicodeEnvironment.Implementation = (UnicodeImplementation)42);
    }

    [Test]
    public void Reset_restores_automatic_and_clears_the_cluster_cache()
    {
        UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;

        // new string guarantees a distinct instance, so a cache entry
        // for an interned literal shared with another fixture can't
        // stand in for this one.
        string input = new string('q', 12);
        var lexer = new Lexer(input);
        lexer.Read();
        Assert.That(GraphemeClusterIndex.HasCachedIndexFor(input), Is.True);

        UnicodeEnvironment.ResetForTesting();

        Assert.That(UnicodeEnvironment.Implementation,
            Is.EqualTo(UnicodeImplementation.Automatic));
        Assert.That(GraphemeClusterIndex.HasCachedIndexFor(input), Is.False);
        Assert.That(UnicodeEnvironment.ActiveImplementation,
            Is.EqualTo(UnicodeImplementation.Runtime));
    }

    [Test]
    public void Compile_and_parse_work_end_to_end_under_an_explicit_bundled_opt_in()
    {
        // The scenario the docs recommend to a CoreCLR server that must
        // agree on parse trees with a Unity client: opt into Bundled at
        // startup, then compile and parse normally. The grammar's
        // literal is precomposed e-acute, the input arrives decomposed
        // (e + combining acute), and the default FormC compile makes
        // them match, with the built-in normalizer doing the converting
        // and the built-in segmenter doing the tokenizing. Everything
        // else in this project runs these paths under the runtime
        // implementations, so this is the one CoreCLR test of the whole
        // pipeline under an explicit Bundled.
        UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;

        var rule = And(
            Literal(UnicodeExamples.LatinEAcutePrecomposedGrapheme),
            Token(';')).Compile();

        var result = rule.Parse(UnicodeExamples.LatinEAcuteGrapheme + ";");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(UnicodeEnvironment.ActiveImplementation,
            Is.EqualTo(UnicodeImplementation.Bundled));

        // A failure position maps back through the built-in normalizer's
        // output to the caller's decomposed coordinates: the missing
        // semicolon after the two-char decomposed e-acute is at char
        // index 2.
        var failure = rule.Parse(UnicodeExamples.LatinEAcuteGrapheme + "x");
        Assert.That(failure.Success, Is.False);
        Assert.That(failure.ErrorCharIndex, Is.EqualTo(2));
    }

    [Test]
    public void Helper_methods_answer_with_the_active_implementation()
    {
        // Both implementations agree on these inputs (the differential
        // suites ensure it), so this is a smoke check that the
        // public passthroughs dispatch at all under an explicit
        // Bundled: decomposed e + acute composes under FormC, and CRLF
        // is one two-char cluster, from the same single setting.
        UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;
        string decomposed = UnicodeExamples.LatinEAcuteGrapheme;
        string composed = NormalizationHelpers.Normalize(decomposed, NormalizationForm.FormC);
        Assert.That(composed, Is.EqualTo(UnicodeExamples.LatinEAcutePrecomposedGrapheme));
        Assert.That(NormalizationHelpers.IsNormalized(decomposed, NormalizationForm.FormC),
            Is.False);
        Assert.That(GraphemeHelpers.FirstClusterLength("\r\nx".AsSpan()), Is.EqualTo(2));
        Assert.That(UnicodeEnvironment.ActiveImplementation,
            Is.EqualTo(UnicodeImplementation.Bundled));
    }
}
