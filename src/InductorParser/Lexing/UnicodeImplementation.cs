namespace InductorParser.Lexing;

/// <summary>
/// Which Unicode implementation the parser uses for
/// <a href="https://www.unicode.org/reports/tr29/">grapheme cluster segmentation</a> and
/// <a href="https://www.unicode.org/reports/tr15/">Unicode normalization</a>.
/// Set this through <see cref="UnicodeEnvironment.Implementation">UnicodeEnvironment.Implementation</see>.
/// </summary>
public enum UnicodeImplementation
{
    /// <summary>
    /// The right implementation for the runtime the app runs on,
    /// chosen automatically. The library does no detection of
    /// its own: it ships one assembly per target framework, each
    /// baking in what Automatic means, and the runtime picks which
    /// assembly it loads. In the net8.0 assembly Automatic means
    /// <see cref="Runtime">UnicodeImplementation.Runtime</see>, so segmentation and normalization stay
    /// in sync with the rest of the runtime's Unicode machinery. In
    /// the netstandard2.1 assembly (the one Unity's Mono and IL2CPP
    /// load) it means <see cref="Bundled">UnicodeImplementation.Bundled</see>, because those runtimes
    /// ship a StringInfo that predates <a href="https://www.unicode.org/reports/tr29/">UAX #29</a> and a <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see>
    /// that misses mappings and accepts ill-formed UTF-16.
    /// </summary>
    Automatic,

    /// <summary>
    /// Use the runtime's StringInfo and <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see>, at whatever
    /// Unicode version the runtime ships.
    /// </summary>
    Runtime,

    /// <summary>
    /// Use the library's built-in <a href="https://www.unicode.org/reports/tr29/">UAX #29</a> segmenter and <a href="https://www.unicode.org/reports/tr15/">UAX #15</a> normalizer,
    /// which implement <a href="https://www.unicode.org/versions/Unicode16.0.0/">Unicode 16.0</a>. Boundaries and normalized forms are
    /// identical on every runtime, so use this when a client and
    /// server on different runtimes must agree on parse trees.
    /// </summary>
    Bundled,
}
