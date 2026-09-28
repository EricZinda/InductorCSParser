using System;

namespace InductorParser.Lexing;

/// <summary>
/// The process-wide choice of Unicode implementation, shared by the
/// segmenter (<c>GraphemeSegmentation</c>) and the normalizer
/// (<c>UnicodeNormalization</c>). One setting governs both so
/// segmentation and normalization can never answer from different
/// Unicode data.
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

    // The opt-in from AcceptHostGlobalization. Written only under
    // _settingLock, volatile for the same lock-free getter reason as
    // _requested. HostGlobalizationCheck reads it after the freeze, and
    // the setter throws once frozen, so the value it reads is final.
    private static volatile bool _acceptHostGlobalization;

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
    /// Which Unicode implementation this process uses, for both
    /// segmentation and normalization. Defaults to
    /// <see cref="UnicodeImplementation.Automatic">UnicodeImplementation.Automatic</see>: the built-in
    /// implementations in the netstandard2.1 assembly (the build Unity
    /// and other pre-net8.0 hosts load), the runtime's StringInfo and
    /// <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see> in the net8.0 assembly. Set it once at startup,
    /// before building grammars or parsing. The first segmentation or
    /// normalization query (constructing a Token rule, compiling a
    /// grammar, parsing, mapping positions, or calling any
    /// <see cref="GraphemeHelpers"/> / <see cref="NormalizationHelpers"/>
    /// method) freezes the choice for the life of the process, and
    /// setting it after that throws
    /// <see cref="InvalidOperationException"/>. Frozen because cluster
    /// boundaries and normalized projections are cached and reused,
    /// and compiled grammars store literals rewritten by the chosen
    /// implementation, so switching mid-process would mix answers from
    /// two implementations. Reading this property never freezes
    /// anything and returns the requested value, which may still be
    /// Automatic. For the implementation actually in use, read
    /// <see cref="ActiveImplementation">UnicodeEnvironment.ActiveImplementation</see>.
    /// </summary>
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
    /// Opt-in acceptance of the host's globalization configuration when
    /// the <see cref="UnicodeImplementation.Runtime">UnicodeImplementation.Runtime</see> implementation is active. Defaults to false: when
    /// the parser is normalizing with the runtime's <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see>
    /// and the process is running under invariant globalization (which
    /// makes <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see> return its input unchanged) or Windows
    /// NLS (which normalizes from Windows' own data instead of ICU),
    /// the first normalizing <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> or Parse throws
    /// <see cref="InvalidOperationException"/>, because a normalizing
    /// grammar would silently produce different parses than on a
    /// normally configured host. Set this to true at startup, before
    /// building grammars or parsing, to say the host's globalization is
    /// understood and the runtime implementations are wanted anyway.
    /// Same freeze rule as <see cref="Implementation">UnicodeEnvironment.Implementation</see>: the first
    /// segmentation or normalization query freezes it, and setting it
    /// after that throws. It has no effect when the built-in
    /// implementations are active, since they never touch host
    /// globalization.
    /// </summary>
    public static bool AcceptHostGlobalization
    {
        get => _acceptHostGlobalization;
        set
        {
            lock (_settingLock)
            {
                if (_frozen)
                {
                    throw new InvalidOperationException(
                        "UnicodeEnvironment.AcceptHostGlobalization can't change after "
                        + "the first segmentation or normalization query, the same "
                        + "freeze rule as UnicodeEnvironment.Implementation (its "
                        + "exception message has the full reasoning). Set it once at "
                        + "startup, before building grammars or parsing.");
                }
                _acceptHostGlobalization = value;
            }
        }
    }

    /// <summary>
    /// The implementation being used. It's one of
    /// <see cref="UnicodeImplementation.Runtime">UnicodeImplementation.Runtime</see> or
    /// <see cref="UnicodeImplementation.Bundled">UnicodeImplementation.Bundled</see>, never <see cref="UnicodeImplementation.Automatic">UnicodeImplementation.Automatic</see>.
    /// Reading it resolves and freezes the choice the same way the
    /// first segmentation or normalization query does, so the answer
    /// can never be invalidated by a later change.
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
            _acceptHostGlobalization = false;
            _useBundled = false;
            _frozen = false;
        }
        GraphemeClusterIndex.ResetCacheForTesting();
        TokenSet.ResetNormalizedCacheForTesting();
        Unicode.HostGlobalizationCheck.ResetForTesting();
    }
}
