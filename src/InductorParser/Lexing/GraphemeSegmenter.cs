namespace InductorParser.Lexing;

/// <summary>
/// Which UAX #29 grapheme cluster segmenter the parser uses, for
/// token boundaries, Token and TokenSet.Graphemes validation, error
/// positions, and the <see cref="GraphemeHelpers"/> methods. Selected
/// once per process through <see cref="GraphemeHelpers.Segmenter"/>.
/// </summary>
public enum GraphemeSegmenter
{
    /// <summary>
    /// The right segmenter for the runtime the app runs on, chosen
    /// with no configuration. The library does no detection of its
    /// own: it ships one assembly per target framework, each baking in
    /// what Automatic means, and the runtime picks which assembly it
    /// loads. In the net8.0 assembly Automatic means
    /// <see cref="Runtime"/>, so segmentation stays in sync with the
    /// rest of the runtime's Unicode machinery. In the netstandard2.1
    /// assembly (the one Unity's Mono and IL2CPP load) it means
    /// <see cref="Bundled"/>, because those runtimes ship a StringInfo
    /// that predates UAX #29.
    /// </summary>
    Automatic,

    /// <summary>
    /// The runtime's StringInfo, at whatever Unicode version the
    /// runtime ships.
    /// </summary>
    Runtime,

    /// <summary>
    /// The library's bundled UAX #29 state machine, fixed at Unicode
    /// 15.0. Boundaries are identical on every runtime, so use this
    /// when a client and server on different runtimes must agree on
    /// parse trees.
    /// </summary>
    Bundled,
}
