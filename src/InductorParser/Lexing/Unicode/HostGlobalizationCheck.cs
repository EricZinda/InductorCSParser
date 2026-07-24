using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace InductorParser.Lexing.Unicode;

/// <summary>
/// Whether the runtime's string.Normalize works normally in this
/// process, and if not, why.
/// </summary>
internal enum HostGlobalizationStatus
{
    /// <summary>Detection hasn't run yet. The field default.</summary>
    Unknown = 0,

    /// <summary>
    /// string.Normalize is composing and no altered mode was detected.
    /// </summary>
    RuntimeNormalizes,

    /// <summary>
    /// Invariant globalization: string.Normalize returns its input
    /// unchanged and IsNormalized always reports true.
    /// </summary>
    Invariant,

    /// <summary>
    /// Windows NLS: string.Normalize uses Windows' own normalization
    /// data instead of ICU.
    /// </summary>
    WindowsNls,

    /// <summary>
    /// The compose probe failed but the runtime's mode flags couldn't
    /// be read, so the cause is unknown.
    /// </summary>
    NotComposing,
}

/// <summary>
/// Detects whether the runtime's string.Normalize actually normalizes
/// in this process. .NET has process-wide globalization settings
/// (invariant globalization, Windows NLS) that change what
/// string.Normalize returns without the parser or its caller doing
/// anything, and they belong to the host app, so a parser embedded in
/// someone else's app inherits whatever that app chose. When the
/// Runtime implementation is active, <c>UnicodeNormalization</c> calls
/// <see cref="EnsureRuntimeNormalizationIsTrustworthy"/> before every
/// runtime-path Normalize or IsNormalized, and the first normalizing
/// Compile or Parse in an affected process throws an actionable
/// <see cref="InvalidOperationException"/> instead of quietly parsing
/// differently. <see cref="UnicodeEnvironment.AcceptHostGlobalization"/>
/// is the deliberate opt-out.
/// </summary>
internal static class HostGlobalizationCheck
{
    // True once the check has passed for this process, either because
    // detection said runtime normalization works or because
    // UnicodeEnvironment.AcceptHostGlobalization opted in. Once true,
    // every later call skips straight past the check. A failed check
    // leaves it false on purpose: if a caller catches the exception
    // and parses again, the next normalizing call throws again,
    // instead of the parser treating one throw as "already warned" and
    // quietly proceeding with normalization that still doesn't work.
    // The race with another thread's first call is benign: writes only
    // move false to true and both racers agree.
    private static volatile bool _accepted;

    // Cached detection verdict, computed once because the globalization
    // mode is fixed at process start. The race is benign here too:
    // Detect is deterministic, so racing threads write equal values.
    private static volatile HostGlobalizationStatus _status;

    // Deliberately no static constructor and no field initializers.
    // Both fields start at their zero values, so the class keeps
    // beforefieldinit and the JIT never puts an initialization check in
    // front of the _accepted read (the UnicodeEnvironment._settingLock
    // comment has the full story).

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if the host's
    /// globalization configuration makes the runtime's string.Normalize
    /// untrustworthy and
    /// <see cref="UnicodeEnvironment.AcceptHostGlobalization"/> hasn't
    /// opted in. Unnormalizable <paramref name="input"/> throws the
    /// public API's <see cref="ArgumentException"/> instead, so argument
    /// validation wins over host state, the same precedence the
    /// undefined-form check already gets. Called on the Runtime branch
    /// of the normalization dispatchers only: the built-in
    /// implementations never touch host globalization, and segmentation
    /// is unaffected either way. After the first pass this is one
    /// volatile bool read, and the runtime normalizer itself rejects
    /// bad input.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnsureRuntimeNormalizationIsTrustworthy(string input)
    {
        if (_accepted) return;
        int badIndex = UnicodeNormalization.FindFirstUnnormalizableIndex(input);
        if (badIndex >= 0)
            throw UnicodeNormalization.CreateUnnormalizableTextException(input, badIndex);
        PassOrThrow();
    }

    // The cold path, factored out so the inlined fast path stays one
    // read and a branch, the same hot/cold split as Invariant.That and
    // ParseBudget.ThrowBudgetExceeded.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PassOrThrow()
    {
        HostGlobalizationStatus status = _status;
        if (status == HostGlobalizationStatus.Unknown)
        {
            status = Detect();
            _status = status;
        }
        if (status == HostGlobalizationStatus.RuntimeNormalizes
            || UnicodeEnvironment.AcceptHostGlobalization)
        {
            _accepted = true;
            return;
        }
        throw CreateException(status);
    }

    private static HostGlobalizationStatus Detect()
    {
        // Invariant mode is detectable with plain behavior: normalize
        // a decomposed e plus combining acute and see whether it
        // composes into the single precomposed character. A working
        // string.Normalize composes it, and under invariant
        // globalization the input comes back unchanged. This behavior
        // check runs before the reflection below because it works even
        // in trimmed apps, where reflection can find nothing, and
        // trimmed containers are exactly where invariant globalization
        // is common. The two strings are built from numeric char
        // values, never string literals, because the decomposed and
        // precomposed forms render identically in an editor, and a
        // tool that normalized this source file would silently turn
        // the check into a comparison of a string against itself.
        string decomposedEAcute = new string(new[] { (char)0x0065, (char)0x0301 });
        string precomposedEAcute = ((char)0x00E9).ToString();
        bool composes;
        try
        {
            composes = decomposedEAcute.Normalize(NormalizationForm.FormC)
                == precomposedEAcute;
        }
        catch (Exception)
        {
            composes = false;
        }

        if (!composes)
        {
            // The flag turns "not composing" into the precise name of
            // the mode when the runtime exposes it.
            return ReadGlobalizationModeFlag("Invariant")
                ? HostGlobalizationStatus.Invariant
                : HostGlobalizationStatus.NotComposing;
        }

        // NLS normalizes this input correctly, so the behavior check
        // can't see it and only the flag can. The UseNls property only
        // exists in Windows builds of the runtime, and a missing
        // property means the flag can't be on.
        return ReadGlobalizationModeFlag("UseNls")
            ? HostGlobalizationStatus.WindowsNls
            : HostGlobalizationStatus.RuntimeNormalizes;
    }

    // GlobalizationMode is internal to System.Private.CoreLib, and
    // there is no public API for these flags. The obvious public
    // route, AppContext.TryGetSwitch, only sees the runtimeconfig
    // switch and misses the environment-variable route entirely:
    // GlobalizationMode reads the switch first and the environment
    // variable second, and nothing writes the effective mode back into
    // AppContext. Exposing the flags publicly is an open API proposal
    // (https://github.com/dotnet/runtime/issues/81429), so until that
    // ships, reflection is what there is. Everything is null-safe and
    // try/catch wrapped so a runtime that renamed the internals (Mono
    // and IL2CPP already lack this type name) degrades to "not
    // detected" instead of throwing.
    private static bool ReadGlobalizationModeFlag(string propertyName)
    {
        try
        {
            object? value = Type
                .GetType("System.Globalization.GlobalizationMode, System.Private.CoreLib")
                ?.GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);
            return value is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The two remedies, shared by every message. Both must be set at
    // startup because the exception can only fire after the first
    // normalization query froze the environment, so a catch block is
    // too late to apply either one.
    private const string TwoWaysOut =
        " There are two ways out, both set at startup before building grammars or "
        + "parsing: UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled "
        + "switches to the parser's built-in normalizer and segmenter, which never "
        + "touch host globalization and parse identically on every host, and "
        + "UnicodeEnvironment.AcceptHostGlobalization = true ";

    private static InvalidOperationException CreateException(
        HostGlobalizationStatus status)
    {
        Invariant.That(
            status == HostGlobalizationStatus.Invariant
            || status == HostGlobalizationStatus.WindowsNls
            || status == HostGlobalizationStatus.NotComposing,
            $"CreateException called with non-failure status {status}");
        string message = status switch
        {
            HostGlobalizationStatus.Invariant =>
                "The parser resolved to the Runtime Unicode implementation, so it "
                + "normalizes with the runtime's string.Normalize, but this process is "
                + "running with invariant globalization, which makes string.Normalize "
                + "return its input unchanged and IsNormalized always report true. This "
                + "grammar asked for normalization, and under invariant globalization "
                + "that normalization silently does nothing: decomposed input stops "
                + "matching precomposed literals, FormKC and FormKD grammars stop "
                + "applying compatibility mappings, and parses quietly differ from the "
                + "same grammar and input on a normally configured host. Invariant "
                + "globalization is turned on by the InvariantGlobalization project "
                + "property, the DOTNET_SYSTEM_GLOBALIZATION_INVARIANT environment "
                + "variable, or the System.Globalization.Invariant runtimeconfig switch."
                + TwoWaysOut
                + "accepts the host's behavior and suppresses this check.",
            HostGlobalizationStatus.WindowsNls =>
                "The parser resolved to the Runtime Unicode implementation, so it "
                + "normalizes with the runtime's string.Normalize, but this process is "
                + "normalizing with Windows NLS instead of ICU. NLS normalization data "
                + "ships with Windows and moves with Windows servicing, so it can lag "
                + "the ICU data other hosts use, and two machines can disagree on edge "
                + "cases. This grammar asked for normalization, so parses on this host "
                + "can quietly differ from the same grammar and input on an ICU host. "
                + "NLS is turned on by the DOTNET_SYSTEM_GLOBALIZATION_USENLS "
                + "environment variable or the System.Globalization.UseNls "
                + "runtimeconfig switch."
                + TwoWaysOut
                + "keeps NLS deliberately and suppresses this check.",
            _ =>
                "The parser resolved to the Runtime Unicode implementation, so it "
                + "normalizes with the runtime's string.Normalize, but string.Normalize "
                + "on this host isn't composing: U+0065 U+0301 (e plus combining acute) "
                + "did not become U+00E9 under FormC. The usual cause is invariant "
                + "globalization, turned on by the InvariantGlobalization project "
                + "property, the DOTNET_SYSTEM_GLOBALIZATION_INVARIANT environment "
                + "variable, or the System.Globalization.Invariant runtimeconfig "
                + "switch, though this runtime doesn't expose which mode it is in. "
                + "Whatever the cause, a normalizing grammar can't work this way: "
                + "decomposed input stops matching precomposed literals, and parses "
                + "quietly differ from a normally configured host."
                + TwoWaysOut
                + "accepts the host's behavior and suppresses this check.",
        };
        return new InvalidOperationException(message);
    }

    // Test-only: pretend detection reported the given status. Clears
    // _accepted too, because a pass cached by an earlier normalization
    // anywhere in the process would otherwise mask the forced status.
    internal static void ForceStatusForTesting(HostGlobalizationStatus status)
    {
        _status = status;
        _accepted = false;
    }

    // Test-only: forget the cached verdict and the cached pass, so the
    // next runtime-path normalization detects fresh. Called from
    // UnicodeEnvironment.ResetForTesting so a forced status can't leak
    // into later fixtures.
    internal static void ResetForTesting()
    {
        _status = HostGlobalizationStatus.Unknown;
        _accepted = false;
    }
}
