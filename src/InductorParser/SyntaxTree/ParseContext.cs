using System.Text;

namespace InductorParser.SyntaxTree;

/// <summary>
/// The per-parse runtime context every Symbol from a parse holds a reference to. Carries what's
/// needed to recover a Symbol's original-input position, raw source text, and human-readable rule
/// name.
/// </summary>
public sealed class ParseContext
{
    /// <summary>The exact string the caller passed to <see cref="Rule.Parse(string)"/>.</summary>
    public string OriginalInput { get; }

    /// <summary>
    /// What the lexer scanned, after grammar-level Unicode normalization. Equals
    /// <see cref="OriginalInput"/> by reference when no normalization happened (or when the input
    /// was already in the target form).
    /// </summary>
    public string ParseInput { get; }

    /// <summary>
    /// The form Compile was called with, used to translate <see cref="ParseInput"/> offsets back to
    /// <see cref="OriginalInput"/> offsets. Null when no normalization was configured.
    /// </summary>
    public NormalizationForm? NormalizationForm { get; }

    /// <summary>
    /// The Rule the parse was launched against. Symbol.DisplayName uses it to resolve a Symbol's
    /// SymbolId back to the rule name the grammar gave it, without forcing the consumer to pass the
    /// grammar through to every tree-walker. Null when no grammar root was supplied, as with
    /// hand-built Symbols.
    /// </summary>
    public Rule? GrammarRoot { get; }

    /// <summary>
    /// Creates a context for one parse. Null inputs are stored as the empty string.
    /// </summary>
    public ParseContext(string originalInput, string parseInput, NormalizationForm? normalizationForm, Rule? grammarRoot = null)
    {
        OriginalInput = originalInput ?? string.Empty;
        ParseInput = parseInput ?? string.Empty;
        NormalizationForm = normalizationForm;
        GrammarRoot = grammarRoot;
    }
}
