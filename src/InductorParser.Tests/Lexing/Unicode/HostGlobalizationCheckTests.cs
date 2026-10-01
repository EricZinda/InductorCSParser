using System;
using System.Text;
using NUnit.Framework;
using InductorParser.Lexing;
using InductorParser.Lexing.Unicode;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Tests for the host-globalization check: when the Runtime
// implementation is active and the process runs under invariant
// globalization or Windows NLS, the first normalizing Compile or Parse
// throws, and UnicodeEnvironment.AllowNonstandardRuntimeNormalization is the
// deliberate opt-out. These tests can't run under the real broken
// modes, because globalization is fixed at process start and
// GlobalizationOracleFixture fails the whole suite if this process
// were invariant or NLS. So they force the detection verdict through
// HostGlobalizationCheck.ForceStatusForTesting and check everything
// downstream of detection: the throw, the message, the opt-out, and
// which paths consult the check at all. The end-to-end proof that real
// detection fires under the real environment variables lives in
// HostGlobalizationChildProcessTests, which launches child processes.
[TestFixture]
// NonParallelizable for the same reason as UnicodeEnvironmentSettingTests:
// the forced detection status and the UnicodeEnvironment settings are
// process-wide, so a concurrently running fixture could hit a forced
// Invariant status from here and throw from an ordinary parse.
[NonParallelizable]
public class HostGlobalizationCheckTests
{
    [SetUp]
    public void ResetBefore() => UnicodeEnvironment.ResetForTesting();

    [TearDown]
    public void ResetAfter() => UnicodeEnvironment.ResetForTesting();

    [Test]
    public void Forced_invariant_makes_a_normalizing_compile_throw_with_both_remedies()
    {
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        var exception = Assert.Throws<InvalidOperationException>(
            () => Literal("abc").Compile());
        Assert.That(exception!.Message, Does.Contain("invariant globalization"));
        Assert.That(exception.Message,
            Does.Contain("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"));
        Assert.That(exception.Message,
            Does.Contain("UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled"));
        Assert.That(exception.Message,
            Does.Contain("UnicodeEnvironment.AllowNonstandardRuntimeNormalization = true"));
    }

    [Test]
    public void Forced_nls_names_nls_and_its_switches()
    {
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.WindowsNls);
        var exception = Assert.Throws<InvalidOperationException>(
            () => Literal("abc").Compile());
        Assert.That(exception!.Message, Does.Contain("Windows NLS"));
        Assert.That(exception.Message,
            Does.Contain("DOTNET_SYSTEM_GLOBALIZATION_USENLS"));
        Assert.That(exception.Message,
            Does.Contain("UnicodeEnvironment.AllowNonstandardRuntimeNormalization = true"));
    }

    [Test]
    public void Forced_not_composing_reports_the_probe_and_the_likely_cause()
    {
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.NotComposing);
        var exception = Assert.Throws<InvalidOperationException>(
            () => Literal("abc").Compile());
        Assert.That(exception!.Message, Does.Contain("isn't composing"));
        Assert.That(exception.Message, Does.Contain("U+0065 U+0301"));
        Assert.That(exception.Message,
            Does.Contain("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"));
    }

    [Test]
    public void Accepting_host_globalization_suppresses_the_check()
    {
        // The opt-out, set before anything froze. This process really
        // normalizes (GlobalizationOracleFixture verified ICU), so the
        // parse behaves normally once the check is out of the way:
        // decomposed input matches the precomposed literal under the
        // default FormC compile.
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        UnicodeEnvironment.AllowNonstandardRuntimeNormalization = true;

        var rule = And(
            Literal(UnicodeExamples.LatinEAcutePrecomposedGrapheme),
            Token(';')).Compile();
        var result = rule.Parse(UnicodeExamples.LatinEAcuteGrapheme + ";");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(UnicodeEnvironment.ActiveImplementation,
            Is.EqualTo(UnicodeImplementation.Runtime));
    }

    [Test]
    public void Bundled_implementation_never_consults_the_check()
    {
        // The other remedy: the built-in implementations never touch
        // host globalization, so a forced broken status is irrelevant
        // on that path even without the opt-out.
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;

        var rule = And(
            Literal(UnicodeExamples.LatinEAcutePrecomposedGrapheme),
            Token(';')).Compile();
        var result = rule.Parse(UnicodeExamples.LatinEAcuteGrapheme + ";");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(UnicodeEnvironment.ActiveImplementation,
            Is.EqualTo(UnicodeImplementation.Bundled));
    }

    [Test]
    public void Segmentation_never_consults_the_check()
    {
        // StringInfo's data is compiled into the runtime and ignores
        // the globalization settings, so segmentation queries
        // shouldn't throw even when normalization would. Constructing a Token
        // rule is also a pure segmentation query, so grammar building
        // stays safe.
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        Assert.That(GraphemeHelpers.FirstClusterLength("\r\nx".AsSpan()), Is.EqualTo(2));
        Token('x');
    }

    [Test]
    public void A_grammar_that_never_normalizes_never_consults_the_check()
    {
        // Compile(null) turns input normalization and literal
        // conversion off, so the whole pipeline runs without a single
        // Normalize or IsNormalized call and the check can't fire. A
        // process that opted out of normalization is unaffected by
        // invariant globalization, and throwing at it would be a false
        // alarm.
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        var rule = And(Literal("ab"), Token(';')).Compile(null);
        var result = rule.Parse("ab;");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void IsNormalized_throws_too()
    {
        // Under real invariant globalization IsNormalized always
        // reports true, which would make the IsNormalized-then-Normalize
        // callers silently skip normalization, so the check has to sit
        // ahead of both entry points, not just Normalize.
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        Assert.Throws<InvalidOperationException>(
            () => NormalizationHelpers.IsNormalized("abc", NormalizationForm.FormC));
    }

    [Test]
    public void An_undefined_form_is_an_argument_error_even_when_the_check_would_throw()
    {
        // Argument validation runs before state checks.
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        Assert.Throws<ArgumentException>(
            () => NormalizationHelpers.Normalize("abc", (NormalizationForm)999));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Unnormalizable_text_is_an_argument_error_even_when_the_check_would_throw(
        bool useIsNormalized)
    {
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        string text = char.ConvertFromUtf32(0x1F600) + "\uD800";

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            if (useIsNormalized)
                NormalizationHelpers.IsNormalized(text, NormalizationForm.FormC);
            else
                NormalizationHelpers.Normalize(text, NormalizationForm.FormC);
        });
        Assert.That(exception!.Message, Does.Contain("index 2"));
        Assert.That(exception.Message, Does.Contain("unpaired surrogate"));
    }

    [Test]
    public void The_opt_out_defaults_to_false_and_keeps_the_last_write_before_the_freeze()
    {
        Assert.That(UnicodeEnvironment.AllowNonstandardRuntimeNormalization, Is.False);
        UnicodeEnvironment.AllowNonstandardRuntimeNormalization = true;
        UnicodeEnvironment.AllowNonstandardRuntimeNormalization = false;
        UnicodeEnvironment.AllowNonstandardRuntimeNormalization = true;
        Assert.That(UnicodeEnvironment.AllowNonstandardRuntimeNormalization, Is.True);
    }

    [Test]
    public void Setting_the_opt_out_after_a_query_throws()
    {
        GraphemeHelpers.FirstClusterLength("a".AsSpan());
        var exception = Assert.Throws<InvalidOperationException>(
            () => UnicodeEnvironment.AllowNonstandardRuntimeNormalization = true);
        Assert.That(exception!.Message, Does.Contain("AllowNonstandardRuntimeNormalization"));
        Assert.That(exception.Message, Does.Contain("before building grammars"));
    }

    [Test]
    public void Reset_clears_a_forced_status()
    {
        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        UnicodeEnvironment.ResetForTesting();

        // Real detection runs fresh and passes, because this process
        // normalizes with the ICU the test project ships.
        var rule = Literal("abc").Compile();
        Assert.That(rule.Parse("abc").Success, Is.True);
    }

    [Test]
    public void Forcing_a_status_clears_a_cached_pass()
    {
        // A successful normalization caches "checked, fine" so the hot
        // path stays one read. ForceStatusForTesting has to clear that
        // cached pass, or every fixture running after the first parse
        // in the process would find the check impossible to trip.
        var rule = Literal("abc").Compile();
        Assert.That(rule.Parse("abc").Success, Is.True);

        HostGlobalizationCheck.ForceStatusForTesting(HostGlobalizationStatus.Invariant);
        Assert.Throws<InvalidOperationException>(
            () => NormalizationHelpers.Normalize("abc", NormalizationForm.FormC));
    }
}
