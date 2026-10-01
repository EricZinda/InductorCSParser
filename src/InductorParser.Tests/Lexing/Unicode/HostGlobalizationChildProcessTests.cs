using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace InductorParser.Tests;

// End-to-end tests for the host-globalization check under the real
// environment variables. The globalization mode of a .NET process is
// fixed at process start, and GlobalizationOracleFixture fails this
// suite if the test process itself were invariant or NLS, so the only
// way to see real detection fire is to launch a child process with the
// variable set. The child is the InductorParser.Tests.GlobalizationChild
// console project (referenced from the test csproj for build order),
// run with dotnet exec from the child's own output folder, where its
// dll, deps.json, and runtimeconfig.json are guaranteed to sit
// together. The child prints ASCII marker lines and this fixture owns
// every assertion. The parts downstream of detection (message
// variants, the opt-out setter rules, which paths consult the check)
// are covered in-process by HostGlobalizationCheckTests.
[TestFixture]
public class HostGlobalizationChildProcessTests
{
    private const string InvariantVariable = "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT";
    private const string NlsVariable = "DOTNET_SYSTEM_GLOBALIZATION_USENLS";

    [Test]
    public void Invariant_globalization_throws_from_the_first_normalizing_compile()
    {
        string output = RunChild("default", (InvariantVariable, "1"));
        Assert.That(output, Does.Contain("OUTCOME:CHECK_THREW"));
        string message = ExtractMessage(output);
        Assert.That(message, Does.Contain("invariant globalization"));
        Assert.That(message, Does.Contain(InvariantVariable));
        Assert.That(message,
            Does.Contain("UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled"));
        Assert.That(message,
            Does.Contain("UnicodeEnvironment.AllowNonstandardRuntimeNormalization = true"));
    }

    [Test]
    public void The_opt_out_under_invariant_globalization_restores_the_silent_behavior()
    {
        // PARSE-SUCCESS:False is the point: with the opt-out set, the
        // runtime implementations run and string.Normalize silently
        // does nothing, so the decomposed input no longer matches the
        // precomposed literal. That's exactly the behavior the opt-out
        // deliberately buys back.
        string output = RunChild("override", (InvariantVariable, "1"));
        Assert.That(output, Does.Contain("OUTCOME:PARSED"));
        Assert.That(output, Does.Contain("PARSE-SUCCESS:False"));
        Assert.That(output, Does.Contain("ACTIVE:Runtime"));
    }

    [Test]
    public void Bundled_under_invariant_globalization_parses_and_normalizes_correctly()
    {
        // The recommended remedy really works with no ICU in the
        // process at all: the built-in normalizer composes the
        // decomposed input and the parse succeeds.
        string output = RunChild("bundled", (InvariantVariable, "1"));
        Assert.That(output, Does.Contain("OUTCOME:PARSED"));
        Assert.That(output, Does.Contain("PARSE-SUCCESS:True"));
        Assert.That(output, Does.Contain("ACTIVE:Bundled"));
    }

    [Test]
    public void A_grammar_that_never_normalizes_runs_under_invariant_globalization()
    {
        string output = RunChild("segmentation-only", (InvariantVariable, "1"));
        Assert.That(output, Does.Contain("OUTCOME:PARSED"));
        Assert.That(output, Does.Contain("PARSE-SUCCESS:True"));
    }

    [Test]
    [Platform(Include = "Win", Reason =
        "DOTNET_SYSTEM_GLOBALIZATION_USENLS only has an effect on Windows.")]
    public void Nls_throws_from_the_first_normalizing_compile()
    {
        string output = RunChild("default", (NlsVariable, "1"));
        Assert.That(output, Does.Contain("OUTCOME:CHECK_THREW"));
        string message = ExtractMessage(output);
        Assert.That(message, Does.Contain("Windows NLS"));
        Assert.That(message, Does.Contain(NlsVariable));
    }

    [Test]
    [Platform(Include = "Win", Reason =
        "DOTNET_SYSTEM_GLOBALIZATION_USENLS only has an effect on Windows.")]
    public void The_opt_out_under_nls_parses_successfully()
    {
        // Contrast with the invariant opt-out test: NLS really does
        // compose e plus combining acute, so the parse succeeds. The
        // NLS risk is edge-case disagreement with ICU, not a
        // normalizer that does nothing.
        string output = RunChild("override", (NlsVariable, "1"));
        Assert.That(output, Does.Contain("OUTCOME:PARSED"));
        Assert.That(output, Does.Contain("PARSE-SUCCESS:True"));
        Assert.That(output, Does.Contain("ACTIVE:Runtime"));
    }

    [Test]
    public void A_clean_environment_passes_the_check_and_parses()
    {
        // The false-positive check. The child has no app-local ICU, so
        // this is detection running against plain host globalization,
        // which the ICU-pinned test process itself can never exercise.
        string output = RunChild("default");
        Assert.That(output, Does.Contain("OUTCOME:PARSED"));
        Assert.That(output, Does.Contain("PARSE-SUCCESS:True"));
        Assert.That(output, Does.Contain("ACTIVE:Runtime"));
    }

    private static string RunChild(
        string scenario, params (string Name, string Value)[] environmentVariables)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetMuxerPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ChildAssemblyPath());
        startInfo.ArgumentList.Add(scenario);

        // Scrub both globalization variables first so nothing leaking
        // from the test host's environment can contaminate a scenario,
        // then apply what the scenario asked for.
        startInfo.Environment.Remove(InvariantVariable);
        startInfo.Environment.Remove(NlsVariable);
        foreach ((string name, string value) in environmentVariables)
            startInfo.Environment[name] = value;

        using var process = Process.Start(startInfo);
        Assert.That(process, Is.Not.Null, "Process.Start returned null.");
        var standardOutputTask = process!.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"Child process timed out. Scenario: {scenario}.");
        }
        string standardOutput = standardOutputTask.GetAwaiter().GetResult();
        string standardError = standardErrorTask.GetAwaiter().GetResult();
        Assert.That(process.ExitCode, Is.Zero,
            $"Child exited with {process.ExitCode}. Scenario: {scenario}.\n"
            + $"stdout:\n{standardOutput}\nstderr:\n{standardError}");
        return standardOutput;
    }

    // The child's dll in the child's own build output, located by
    // reusing this assembly's own path segments so configuration and
    // target framework can never disagree:
    // .../src/InductorParser.Tests/bin/<Config>/<TFM>/ maps to
    // .../src/InductorParser.Tests.GlobalizationChild/bin/<Config>/<TFM>/.
    private static string ChildAssemblyPath()
    {
        var targetFrameworkDirectory =
            new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        DirectoryInfo configurationDirectory = targetFrameworkDirectory.Parent!;
        DirectoryInfo sourceDirectory = configurationDirectory.Parent!.Parent!.Parent!;
        string childAssembly = Path.Combine(
            sourceDirectory.FullName,
            "InductorParser.Tests.GlobalizationChild",
            "bin",
            configurationDirectory.Name,
            targetFrameworkDirectory.Name,
            "InductorParser.Tests.GlobalizationChild.dll");
        Assert.That(File.Exists(childAssembly), Is.True,
            $"Child assembly not found at {childAssembly}. It is built by the "
            + "ProjectReference in InductorParser.Tests.csproj, so check that the "
            + "reference is still there.");
        return childAssembly;
    }

    // The dotnet muxer that launched this test run, so the child runs
    // on the same installation whatever shell started the suite.
    // DOTNET_HOST_PATH is set by the dotnet host for child processes
    // it spawns (dotnet test sets it). The runtime-directory walk is
    // the fallback: .../dotnet/shared/Microsoft.NETCore.App/<version>/ up
    // three levels is the dotnet root. Plain "dotnet" on PATH is the
    // last resort.
    private static string DotnetMuxerPath()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
            return hostPath;

        string muxerFileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string runtimeDirectory =
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        DirectoryInfo? dotnetRoot =
            new DirectoryInfo(runtimeDirectory).Parent?.Parent?.Parent;
        if (dotnetRoot != null)
        {
            string candidate = Path.Combine(dotnetRoot.FullName, muxerFileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return "dotnet";
    }

    private static string ExtractMessage(string output)
    {
        int begin = output.IndexOf("MESSAGE-BEGIN", StringComparison.Ordinal);
        int end = output.IndexOf("MESSAGE-END", StringComparison.Ordinal);
        Assert.That(begin, Is.GreaterThanOrEqualTo(0),
            $"No MESSAGE-BEGIN in child output:\n{output}");
        Assert.That(end, Is.GreaterThan(begin),
            $"No MESSAGE-END in child output:\n{output}");
        return output.Substring(begin, end - begin);
    }
}
