using System.Text;

namespace InductorParser.SyntaxTree;

/// <summary>
/// The per-parse runtime context every Symbol from a parse holds a reference to. It stores what's
/// needed to recover a Symbol's original-input position, raw source text, and human-readable rule
/// name. The parser creates one per <see cref="Rule.Parse(string)">Rule.Parse(string)</see> call, so
/// grammar and rule writers only ever read it: a Symbol from a parse holds one, and a user-defined
/// <see cref="Rule"/> subclass gets the current one from
/// <see cref="InductorParser.Lexing.Lexer.Context">Lexer.Context</see> to pass into the Symbols it builds.
/// </summary>
public sealed class ParseContext
{
    /// <summary>The exact string the caller passed to <see cref="Rule.Parse(string)">Rule.Parse(string)</see>.</summary>
    public string OriginalInput { get; }

    /// <summary>
    /// What the lexer scanned, after grammar-level Unicode normalization. Equals
    /// <see cref="OriginalInput">ParseContext.OriginalInput</see> by reference when no normalization happened (or when the input
    /// was already in the target form).
    /// </summary>
    public string ParseInput { get; }

    /// <summary>
    /// The form <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> was called with, used to translate <see cref="ParseInput">ParseContext.ParseInput</see> offsets back to
    /// <see cref="OriginalInput">ParseContext.OriginalInput</see> offsets. Null when no normalization was configured.
    /// </summary>
    public NormalizationForm? NormalizationForm { get; }

    /// <summary>
    /// The Rule the parse was launched against. <see cref="InductorParser.SyntaxTree.Symbol.DisplayName">Symbol.DisplayName</see> uses it to resolve a Symbol's
    /// SymbolId back to the rule name the grammar gave it, without forcing the consumer to pass the
    /// grammar through to every tree-walker. Null when no grammar root was supplied, as with
    /// hand-built Symbols.
    /// </summary>
    public Rule? GrammarRoot { get; }

    /// <summary>
    /// Creates a context for one parse. Null inputs are stored as the empty string. Internal because
    /// only a parsing engine builds one: Rule.Parse here, and the state machine engine in its own
    /// assembly through InternalsVisibleTo.
    /// </summary>
    internal ParseContext(string originalInput, string parseInput, NormalizationForm? normalizationForm, Rule? grammarRoot = null)
    {
        OriginalInput = originalInput ?? string.Empty;
        ParseInput = parseInput ?? string.Empty;
        NormalizationForm = normalizationForm;
        GrammarRoot = grammarRoot;
    }
}
