using System;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace InductorParser.Tests;

// Some tests compare the library's built-in Unicode code against what
// the .NET runtime does. Those comparisons only mean something when
// the runtime's Unicode data is known exactly, and that depends on
// the machine. This fixture runs once, before any test in the
// assembly, and stops the whole run with a message explaining the
// problem if the runtime isn't set up the one way the tests expect.
// That way a comparison failure always means the code changed, never
// that the machine did.
//
// Two things get checked:
//
//   - Normalization. string.Normalize doesn't do the work itself. It
//     hands the text to ICU (International Components for Unicode,
//     the open-source library most operating systems supply for
//     Unicode algorithms), and the ICU a machine has changes with OS
//     updates. So the test project supplies its own copy: the
//     Microsoft.ICU.ICU4C.Runtime package reference in
//     InductorParser.Tests.csproj puts ICU 72.1's native DLLs in the
//     build output (under runtimes/<platform>/native/), and the
//     AppLocalIcu setting next to it makes the runtime load that copy
//     instead of the OS one. ICU 72.1 is built from Unicode 15.0 data
//     while the built-in tables are Unicode 16.0, and it stays the
//     oracle anyway for two reasons. No newer ICU exists as an
//     app-local package (Microsoft.ICU.ICU4C.Runtime tops out at
//     72.1, and current Windows ships 72.1 as its OS copy too). And
//     Unicode's normalization stability policy makes the two data
//     versions give identical answers for every code point assigned
//     through 15.0, so the comparison stays valid everywhere except
//     the code points 16.0 added. The differential sweeps skip
//     exactly those (see UnicodeVersionDelta), and the
//     NormalizationTest-16.0.0.txt conformance suite proves their
//     behavior instead. This fixture makes sure that ICU copy is the
//     one actually loaded, and rejects Windows NLS and invariant
//     globalization outright.
//
//   - Segmentation. StringInfo doesn't use ICU. Its Unicode data is
//     compiled into the .NET runtime itself, so the runtime version
//     says which data it has. The built-in segmenter matches .NET
//     10's StringInfo (same Unicode 16.0 data, and neither side
//     implements GB9c), and this fixture refuses any other .NET
//     version until its StringInfo has been checked against the
//     built-in implementation.
//
// The checks read internal runtime state through reflection, the same
// way dotnet/runtime's own PlatformDetection test utility does. That
// is safe here because the .NET 10 check above means the internals
// have a known shape. A plain behavior check (does A plus a combining
// accent compose into one character?) backs it up in case reflection
// ever silently reports nothing.
//
// The file sits under Lexing/Unicode/ because that folder never syncs
// to the Unity test project (these checks only mean something on
// CoreCLR), but the class lives at the InductorParser.Tests namespace
// root so its OneTimeSetUp fires before any test in the assembly.
[SetUpFixture]
public class GlobalizationOracleFixture
{
    [OneTimeSetUp]
    public void VerifyGlobalizationOracle()
    {
        if (Environment.Version.Major != 10)
        {
            Assert.Fail(
                $"This process is running .NET {Environment.Version}, and the Unicode tests "
                + "need .NET 10. StringInfo's Unicode data is compiled into the runtime itself, "
                + "so a different runtime can segment text differently. Run the suite on .NET "
                + "10, or run the GraphemeSegmentationTests sweeps on the new runtime and widen "
                + "this check if they pass.");
        }

        if (ReadGlobalizationModeFlag("Invariant"))
        {
            Assert.Fail(
                "This process is running with invariant globalization, which makes "
                + "string.Normalize return its input unchanged, so every test that compares "
                + "against it would be meaningless. Unset DOTNET_SYSTEM_GLOBALIZATION_INVARIANT "
                + "(or the System.Globalization.Invariant runtimeconfig switch) and rerun.");
        }

        if (ReadGlobalizationModeFlag("UseNls"))
        {
            Assert.Fail(
                "This process is normalizing with Windows NLS instead of ICU. NLS data "
                + "changes with Windows updates and isn't what the tests are written against. "
                + "Unset DOTNET_SYSTEM_GLOBALIZATION_USENLS (or the "
                + "System.Globalization.UseNls runtimeconfig switch) and rerun.");
        }

        // Forces the globalization backend to load before the ICU
        // version is read below, and catches a normalization that
        // isn't normalizing no matter what the reflection above
        // reported. The two strings come from the canary-protected
        // UnicodeExamples corpus: e + combining acute composes to the
        // precomposed U+00E9 under FormC.
        if (UnicodeExamples.LatinEAcuteGrapheme.Normalize(NormalizationForm.FormC)
            != UnicodeExamples.LatinEAcutePrecomposedGrapheme)
        {
            Assert.Fail(
                "string.Normalize did not compose U+0065 U+0301 into U+00E9, so the runtime's "
                + "normalization isn't working and every test that compares against it would "
                + "be meaningless. Check for invariant globalization "
                + "(DOTNET_SYSTEM_GLOBALIZATION_INVARIANT) or a broken globalization backend.");
        }

        Version? icuVersion = ReadIcuVersion();
        if (icuVersion == null || icuVersion.Major != 72 || icuVersion.Minor != 1)
        {
            Assert.Fail(
                "Expected the ICU 72.1 that InductorParser.Tests.csproj ships, but the loaded "
                + $"ICU is {(icuVersion == null ? "unknown" : icuVersion.ToString(2))}. The "
                + "tests compare string.Normalize against exactly that ICU. Its Unicode 15.0 "
                + "data gives the same answers as the built-in tables' Unicode 16.0 data for "
                + "every code point assigned through 15.0 (the normalization stability "
                + "policy), and the sweeps skip the rest. Likely causes: the "
                + "Microsoft.ICU.ICU4C.Runtime PackageReference or the "
                + "System.Globalization.AppLocalIcu RuntimeHostConfigurationOption was removed "
                + "from the csproj (the OS-supplied ICU loads instead), or the package has no "
                + "native asset for this platform.");
        }
    }

    // GlobalizationMode is internal to System.Private.CoreLib. UseNls
    // only exists in the Windows build of the runtime, and a missing
    // property means the flag can't be on.
    private static bool ReadGlobalizationModeFlag(string propertyName)
    {
        object? value = Type
            .GetType("System.Globalization.GlobalizationMode, System.Private.CoreLib")
            ?.GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null);
        return value is true;
    }

    // The version of the ICU the runtime actually loaded, read from
    // the runtime's internal interop layer. GetICUVersion packs
    // major.minor.build.patch into one int, high byte first. Null when
    // it can't be read (NLS or invariant mode, or a future runtime
    // that renamed the internals).
    private static Version? ReadIcuVersion()
    {
        try
        {
            object? packed = Type
                .GetType("Interop+Globalization, System.Private.CoreLib")
                ?.GetMethod("GetICUVersion", BindingFlags.NonPublic | BindingFlags.Static)
                ?.Invoke(null, null);
            if (packed is not int version || version == 0)
                return null;
            return new Version(
                version >> 24, (version >> 16) & 0xFF, (version >> 8) & 0xFF, version & 0xFF);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
