using System.Text;

namespace InductorParser.SyntaxTree;

// The per-parse runtime context every Symbol from a parse holds a
// reference to. Carries the pieces of information needed to recover a
// Symbol's original-input position, raw source text, and human-readable
// rule name:
//
//   OriginalInput     — the exact string the caller passed to Rule.Parse.
//   ParseInput        — what the lexer scanned, after grammar-level
//                       Unicode normalization. Equals OriginalInput by
//                       reference when no normalization happened (or
//                       when the input was already in the target form).
//   NormalizationForm — the form Compile was called with, used by
//                       NormalizedPositionMap to translate parseInput
//                       offsets back to original-input offsets. Null
//                       when no normalization was configured.
//   GrammarRoot       — the Rule the parse was launched against. Used
//                       by Symbol.Name to resolve a Symbol's SymbolId
//                       back to the rule name the grammar gave it,
//                       without forcing the consumer to thread the
//                       grammar through to every tree-walker. Null on
//                       hand-built Symbols and on parses launched
//                       before the GrammarRoot wiring landed.
//
// One instance per Rule.Parse call. Hand-built Symbols (test fixtures
// constructing trees by hand) can pass null instead, in which case
// Symbol.SourceRange falls back to treating the leaf's backing string
// as both parseInput and originalInput, and Symbol.Name returns null.
public sealed class ParseContext
{
    public string OriginalInput { get; }
    public string ParseInput { get; }
    public NormalizationForm? NormalizationForm { get; }
    public Rule? GrammarRoot { get; }

    public ParseContext(string originalInput, string parseInput, NormalizationForm? normalizationForm)
        : this(originalInput, parseInput, normalizationForm, grammarRoot: null) { }

    public ParseContext(string originalInput, string parseInput, NormalizationForm? normalizationForm, Rule? grammarRoot)
    {
        OriginalInput = originalInput ?? string.Empty;
        ParseInput = parseInput ?? string.Empty;
        NormalizationForm = normalizationForm;
        GrammarRoot = grammarRoot;
    }
}
