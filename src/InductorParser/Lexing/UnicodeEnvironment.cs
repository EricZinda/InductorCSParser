using System;

namespace InductorParser.Lexing;

/// <summary>
/// Chooses the Unicode implementation used throughout the process for both segmentation and normalization.
/// The segmenter divides input into grapheme clusters: the units the parser normally reads as tokens,
/// such as a letter with its combining marks or an emoji sequence.
/// The normalizer converts text to the grammar's chosen Unicode normalization form,
/// so equivalent spellings can match a single rule, such as a precomposed accented letter and a letter followed by a combining accent.
/// One setting selects either the built-in implementations or .NET's implementations for both operations.
/// .NET's segmentation and normalization don't necessarily use the same version of Unicode data.
/// </summary>
public static class UnicodeEnvironment
{
    // What UnicodeImplementation.Automatic means in this build. The
    // csproj defines the symbol for the netstandard2.1 target only, so
    // the assembly Unity loads defaults to the built-in implementations
    // and every other build defaults to the runtime's.
#if INDUCTORPARSER_USE_BUNDLED_UNICODE
    private const bool AutomaticMeansBundled = true;
#else
    private const bool AutomaticMeansBundled = false;
#endif

    // The requested implementation, as set through Implementation.
    // Written only under _settingLock. Volatile so the property getter
    // reads the latest value without taking the lock.
    private static volatile UnicodeImplementation _requested =
        UnicodeImplementation.Automatic;

    // The opt-in from AllowNonstandardRuntimeNormalization. Written only under
    // _settingLock, volatile for the same lock-free getter reason as
    // _requested. HostGlobalizationCheck reads it after the freeze, and
    // the setter throws once frozen, so the value it reads is final.
    private static volatile bool _allowNonstandardRuntimeNormalization;

    // Which implementation the dispatchers route to. Written exactly
    // once, inside _settingLock in ResolveAndFreeze, before the
    // volatile _frozen write. Read on the hot path only after a
    // volatile read of _frozen has returned true.
    private static bool _useBundled;

    // True once the choice is resolved. After it flips, _useBundled is
    // final. Volatile for acquire/release ordering (the ECMA-335
    // reasoning spelled out on GraphemeClusterIndex._walkedTo). Inside
    // the lock, ResolveAndFreeze first re-checks _frozen and returns
    // if another thread already resolved (that re-check is what makes
    // the write-once claim on _useBundled true), then writes
    // _useBundled, then writes _frozen last (the volatile write is a
    // release, so it can't move before the _useBundled write).
    // ResolveUseBundled reads _frozen first and _useBundled second
    // (the volatile read is an acquire, so the _useBundled read can't
    // move before it). A thread that sees _frozen == true therefore
    // sees the final _useBundled.
    private static volatile bool _frozen;

    // Serializes the setter and the resolve, so a set racing the first
    // query either lands before resolution (and wins) or observes
    // _frozen under the lock (and throws).
    //
    // The lock object is created inline here rather than in a static
    // constructor, and that's deliberate. A class with only inline
    // field initializers gets marked by the compiler so the runtime
    // can initialize it at any convenient point before first use (the
    // mark is called beforefieldinit), and after that every static
    // field read is just a read. Writing an explicit static
    // constructor removes the mark: the runtime must then run
    // initialization at exactly the first use of the class, which
    // makes the JIT put a "has this type initialized yet?" check in
    // front of static accesses. ResolveUseBundled runs on every
    // segmentation query in the lexer's inner loop, so that check
    // would be paid once per token.
    private static readonly object _settingLock = new object();

    /// <summary>
    /// Chooses the Unicode implementation used for segmentation and normalization throughout the process.
    /// The default is <see cref="UnicodeImplementation.Automatic">UnicodeImplementation.Automatic</see>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <see cref="UnicodeImplementation.Automatic">UnicodeImplementation.Automatic</see>, the choice depends
    /// on which build of InductorParser your application loads:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The <c>netstandard2.1</c> build, used by Unity and compatible hosts that can't load the
    /// <c>net8.0</c> build, uses <see cref="UnicodeImplementation.Bundled">UnicodeImplementation.Bundled</see>.
    /// Both grapheme segmentation and normalization use the implementations and Unicode data included in InductorParser.
    /// </description></item>
    /// <item><description>
    /// The <c>net8.0</c> build uses <see cref="UnicodeImplementation.Runtime">UnicodeImplementation.Runtime</see>.
    /// Grapheme segmentation uses .NET's <see cref="System.Globalization.StringInfo">StringInfo</see>, and
    /// normalization uses <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see>.
    /// Both rely on the Unicode support provided by the .NET runtime and its host environment.
    /// </description></item>
    /// </list>
    /// <para>
    /// To override the automatic choice, set this property at startup, before building grammars or parsing.
    /// The first segmentation or normalization operation makes the choice final for the life of the process.
    /// This can happen when constructing a <see cref="Rules.Token(string)">Rules.Token(string)</see> rule,
    /// compiling a grammar, parsing, mapping positions, or using <see cref="GraphemeHelpers"/> or
    /// <see cref="NormalizationHelpers"/>. Assigning this property after that throws <see cref="InvalidOperationException"/>.
    /// The choice must remain fixed because the parser caches Unicode results and stores normalized literals in compiled grammars.
    /// </para>
    /// <para>
    /// Reading this property doesn't make the choice final. It returns the requested setting, which can
    /// still be <see cref="UnicodeImplementation.Automatic">UnicodeImplementation.Automatic</see>.
    /// Read <see cref="ActiveImplementation">UnicodeEnvironment.ActiveImplementation</see> to find out
    /// whether the runtime's or the built-in implementation was selected. Reading that property does make the choice final.
    /// </para>
    /// </remarks>
    public static UnicodeImplementation Implementation
    {
        get => _requested;
        set
        {
            if (value != UnicodeImplementation.Automatic
                && value != UnicodeImplementation.Runtime
                && value != UnicodeImplementation.Bundled)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "Not a UnicodeImplementation value.");
            }
            lock (_settingLock)
            {
                if (_frozen)
                {
                    throw new InvalidOperationException(
                        "UnicodeEnvironment.Implementation can't change after the first "
                        + "segmentation or normalization query. Constructing a Token rule, "
                        + "compiling a grammar, parsing, mapping positions, and the "
                        + "GraphemeHelpers / NormalizationHelpers methods all resolve and "
                        + "freeze the choice, because cluster boundaries and normalized "
                        + "projections are cached and reused and compiled grammars store "
                        + "literals rewritten by the chosen implementation, and switching "
                        + "now would mix answers from two implementations. Set it once at "
                        + "startup, before building grammars or parsing. The active "
                        + $"implementation is {ActiveImplementation}.");
                }
                _requested = value;
            }
        }
    }

    /// <summary>
    /// By default, the parser throws an exception if .NET is configured to skip Unicode normalization
    /// or use Windows NLS instead of ICU. See
    /// <a href="#InductorParser_Lexing_UnicodeEnvironment_AllowNonstandardRuntimeNormalization_remarks">Remarks</a>
    /// for why these configurations are rejected by default.
    /// <para>
    /// Set this property to <c>true</c> to allow parsing with those settings. The default is <c>false</c>.
    /// This property only applies when using <see cref="UnicodeImplementation.Runtime">UnicodeImplementation.Runtime</see>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// When using <see cref="UnicodeImplementation.Runtime">UnicodeImplementation.Runtime</see>, the parser relies on
    /// .NET to normalize text. Some .NET settings disable normalization or change its results.
    /// The parser checks for those settings to prevent unexpected parsing behavior.
    /// </para>
    /// <para>
    /// <b>Invariant globalization:</b> a .NET mode that runs without culture-specific data and behavior.
    /// It allows applications to run without the usual globalization libraries, but also disables Unicode normalization.
    /// This is an application-wide setting, not a grammar option or the same thing as selecting
    /// <see cref="System.Globalization.CultureInfo.InvariantCulture">CultureInfo.InvariantCulture</see> for an operation.
    /// </para>
    /// <para>
    /// The application or its deployment enables this mode with <c>&lt;InvariantGlobalization&gt;true&lt;/InvariantGlobalization&gt;</c>
    /// in the project file, <c>System.Globalization.Invariant</c> set to <c>true</c> under
    /// <c>runtimeOptions.configProperties</c> in <c>runtimeconfig.json</c>, or the environment variable
    /// <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c>. See
    /// <a href="https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables#set-invariant-mode">.NET invariant-mode configuration</a>.
    /// </para>
    /// <para>
    /// In this mode, <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see>
    /// silently returns the input unchanged: it neither normalizes the text nor reports that normalization
    /// was skipped. If this mode is enabled inadvertently, a grammar requesting normalization would run
    /// without it, creating hard-to-find parsing bugs. For example, a precomposed accented letter and the same letter followed
    /// by a combining accent could fail to match each other even though normalization should make them equivalent.
    /// </para>
    /// <para>
    /// <b>Windows NLS:</b> National Language Support is Windows' built-in globalization service.
    /// .NET normally uses ICU (International Components for Unicode) on supported modern Windows systems,
    /// but can fall back to NLS when the system ICU library is unavailable or can't be loaded.
    /// An application can also explicitly select NLS by setting <c>System.Globalization.UseNls</c> to
    /// <c>true</c> under <c>runtimeOptions.configProperties</c> in <c>runtimeconfig.json</c>, or by setting
    /// the environment variable <c>DOTNET_SYSTEM_GLOBALIZATION_USENLS=1</c> before startup.
    /// These options apply to Windows. See
    /// <a href="https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu#icu-on-windows">.NET's ICU and NLS selection</a>.
    /// </para>
    /// <para>
    /// With NLS, .NET normalizes both the grammar's literals and the input using Windows' normalization support.
    /// Another machine using ICU may have different Unicode data or normalization behavior, so the same
    /// grammar and input can produce different parse results. This safeguard protects against that
    /// difference between machines.
    /// </para>
    /// <para>
    /// When .NET is running in invariant globalization mode or using Windows NLS, the first
    /// <see cref="Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile(NormalizationForm?)</see> or
    /// <see cref="Rule.Parse(string)">Rule.Parse(string)</see> that requires normalization throws
    /// <see cref="InvalidOperationException"/> unless this property is <c>true</c>.
    /// This prevents the application from silently using an unusual configuration that can change parse outcomes.
    /// Setting this property to <c>true</c> explicitly opts into that behavior after you have considered its effect on your grammar.
    /// </para>
    /// <para>
    /// Set this to <c>true</c> to opt into one of these more unusual configurations if you understand
    /// and accept the implications. Set it at startup, before building grammars or parsing.
    /// Like <see cref="Implementation">UnicodeEnvironment.Implementation</see>, this setting becomes fixed
    /// after the first segmentation or normalization query. Assigning it after that throws
    /// <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// This setting has no effect when <see cref="UnicodeImplementation.Bundled">UnicodeImplementation.Bundled</see>
    /// is active, because the built-in implementations use their own Unicode data instead of relying on .NET's globalization support.
    /// </para>
    /// </remarks>
    public static bool AllowNonstandardRuntimeNormalization
    {
        get => _allowNonstandardRuntimeNormalization;
        set
        {
            lock (_settingLock)
            {
                if (_frozen)
                {
                    throw new InvalidOperationException(
                        "UnicodeEnvironment.AllowNonstandardRuntimeNormalization can't change after "
                        + "the first segmentation or normalization query, the same "
                        + "freeze rule as UnicodeEnvironment.Implementation (its "
                        + "exception message has the full reasoning). Set it once at "
                        + "startup, before building grammars or parsing.");
                }
                _allowNonstandardRuntimeNormalization = value;
            }
        }
    }

    /// <summary>
    /// The implementation selected by <see cref="Implementation">UnicodeEnvironment.Implementation</see>.
    /// If that property is set to <see cref="UnicodeImplementation.Automatic">UnicodeImplementation.Automatic</see>,
    /// this property reports which implementation is chosen automatically. Otherwise, it reports the same value.
    /// The result is always
    /// <see cref="UnicodeImplementation.Runtime">UnicodeImplementation.Runtime</see> or
    /// <see cref="UnicodeImplementation.Bundled">UnicodeImplementation.Bundled</see>, never <see cref="UnicodeImplementation.Automatic">UnicodeImplementation.Automatic</see>.
    /// Reading this property makes the choice final, even if no parsing has happened yet.
    /// After that, assigning <see cref="Implementation">UnicodeEnvironment.Implementation</see> or
    /// <see cref="AllowNonstandardRuntimeNormalization">UnicodeEnvironment.AllowNonstandardRuntimeNormalization</see> throws
    /// <see cref="InvalidOperationException"/>. Set those properties before reading this one.
    /// </summary>
    public static UnicodeImplementation ActiveImplementation =>
        ResolveUseBundled()
            ? UnicodeImplementation.Bundled
            : UnicodeImplementation.Runtime;

    // The dispatchers' entry point: resolve on first use, then answer
    // from the frozen choice. One volatile read plus one plain read
    // (_useBundled, ordered by the acquire above) on the hot path, same
    // cost as the check the segmenter's dispatcher paid when it owned
    // this state itself.
    internal static bool ResolveUseBundled()
    {
        if (!_frozen)
            ResolveAndFreeze();
        return _useBundled;
    }

    private static void ResolveAndFreeze()
    {
        lock (_settingLock)
        {
            // Re-check under the lock: another thread may have resolved
            // while this one was waiting.
            if (_frozen)
                return;
            UnicodeImplementation requested = _requested;
            _useBundled = requested == UnicodeImplementation.Bundled
                || (requested == UnicodeImplementation.Automatic && AutomaticMeansBundled);
            // Volatile write last: the release that publishes _useBundled.
            _frozen = true;
        }
    }

    // Test-only: unfreeze, forget the requested implementation and the
    // host-globalization opt-in, and drop every cache whose entries
    // were computed under the previous choice: the per-string
    // cluster-boundary indexes, TokenSet's normalized projections, and
    // the host-globalization verdict (a status forced by one fixture
    // shouldn't leak into the next). Callers must ensure no parse is
    // running concurrently, and shouldn't reuse anything built before
    // the reset afterward: a grammar compiled under the old
    // implementation keeps its rewritten literals, and a Lexer or
    // SourcePositionConverter that already holds a cluster index keeps
    // using the old boundaries, while fresh ones would rebuild under
    // the new implementation and disagree.
    internal static void ResetForTesting()
    {
        lock (_settingLock)
        {
            _requested = UnicodeImplementation.Automatic;
            _allowNonstandardRuntimeNormalization = false;
            _useBundled = false;
            _frozen = false;
        }
        GraphemeClusterIndex.ResetCacheForTesting();
        TokenSet.ResetNormalizedCacheForTesting();
        Unicode.HostGlobalizationCheck.ResetForTesting();
    }
}
