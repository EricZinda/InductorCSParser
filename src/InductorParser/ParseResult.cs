using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// The value returned by Rule.Parse. Wraps either a successful parse
// (Tree / Symbols) or a failure (Outcome, ErrorMessage, and the
// error-position family). Outcome distinguishes "the grammar rejected
// the input" (GrammarMismatch) from "a budget tripped" (Timeout,
// RuleCountLimitExceeded, DepthLimitExceeded, Canceled) so callers
// can show different messages to the user in each case.
//
// The error-position family reports the same point (where the parse
// got furthest before failing) in different units: ErrorCharIndex
// (chars / UTF-16 code units), ErrorTokenIndex (tokens, where a token
// is one character as the user sees it), and the ErrorLine /
// ErrorColumn pair (LSP-style zero-based line and column). Pick
// whichever matches the unit the caller will use the number in.
// ErrorPosition returns all four bundled into one SourcePosition
// struct, so callers that want more than one unit only pay for one
// walk of the input. The same conversion is available on
// Symbol.SourceRange for any node in the parse tree.
//
// readonly struct so returning one is a handful of field copies, not
// a heap allocation.
public readonly struct ParseResult
{
    private readonly string? _input;
    private readonly Rule? _grammar;
    private readonly IReadOnlyList<Symbol>? _symbols;

    // Discriminates the shape of this result: success, grammar
    // mismatch, or which budget tripped. See ParseOutcome for the full
    // list. Always check this (or Success) before reading Tree /
    // Symbols.
    public ParseOutcome Outcome { get; }

    // Human-readable description of what went wrong. Empty string on
    // success. On GrammarMismatch, either the innermost WithError
    // message set by the grammar or a generated "Parse failed at
    // offset N" fallback. On a budget abort, the matching
    // "Parse aborted: ..." string.
    public string ErrorMessage { get; }

    // The top-level Symbols produced by the parse. For a root with
    // FlattenType.Preserve this has exactly one element (the root's
    // wrapper). For a root with FlattenType.Flatten whose children
    // bubbled up, this is the flat list of those children. For a
    // failed or aborted parse, it's empty.
    public IReadOnlyList<Symbol> Symbols =>
        _symbols ?? System.Array.Empty<Symbol>();

    // Convenience accessor for the common "root is a single Symbol"
    // case. Returns Symbols[0] if there's exactly one top-level
    // Symbol, null otherwise. Callers that know their root has
    // FlattenType.Preserve (the common case for named grammars) can
    // keep using this. For grammars whose root produces multiple
    // top-level Symbols, use Symbols directly.
    public Symbol? Tree =>
        _symbols != null && _symbols.Count == 1 ? _symbols[0] : null;

    // Error position in chars (UTF-16 code units), zero-based. The
    // unit string.Substring / Range / Span use, and the unit the
    // Language Server Protocol uses for editor diagnostics. On
    // success this is 0. On failure it's the position of the deepest
    // recorded failure (where the parser got furthest before giving
    // up), capped to the input length so callers can index into the
    // original input string without bounds-checking.
    public int ErrorCharIndex { get; }

    // Error position's zero-based line number, counting \n, \r\n, and
    // lone \r as line breaks. LSP convention. Computed lazily from
    // ErrorCharIndex and the original input.
    public int ErrorLine
    {
        get
        {
            SourcePositionConverter.ToLineColumn(_input ?? string.Empty, ErrorCharIndex, out int line, out _);
            return line;
        }
    }

    // Error position's zero-based column within the line, measured in
    // chars. Computed lazily from ErrorCharIndex and the original
    // input.
    public int ErrorColumn
    {
        get
        {
            SourcePositionConverter.ToLineColumn(_input ?? string.Empty, ErrorCharIndex, out _, out int column);
            return column;
        }
    }

    // Error position in tokens (characters as the user sees them),
    // using the same StringInfo text-element segmentation the lexer
    // uses. On modern .NET this follows UAX #29 extended grapheme
    // clusters. Computed lazily from ErrorCharIndex.
    public int ErrorTokenIndex =>
        SourcePositionConverter.ToTokenIndex(_input ?? string.Empty, ErrorCharIndex);

    // The error position bundled into a SourcePosition struct. Returns
    // null on a successful parse. Use this when you need more than one
    // position unit (line + column for a diagnostic, char index for a
    // span, etc.) so you don't pay for multiple walks of the input.
    public SourcePosition? ErrorPosition =>
        Outcome == ParseOutcome.Success
            ? null
            : SourcePosition.From(_input ?? string.Empty, ErrorCharIndex);

    // Convenience: true when Outcome is Success, false otherwise.
    // Most callers check this first and only inspect Tree / Symbols
    // when it's true.
    public bool Success => Outcome == ParseOutcome.Success;

    // Looks up the human-readable name of a SymbolId in the grammar
    // that produced this result. Returns null if the id isn't known
    // or this is an empty default ParseResult.
    public string? NameOf(SymbolId id) => _grammar?.NameOf(id);

    // Convenience form of NameOf that takes a Symbol directly.
    // Returns null if the symbol is null.
    public string? Name(Symbol symbol) => symbol == null ? null : NameOf(symbol.Id);

    // The span in the caller's original input string that `symbol`
    // consumed, expressed as a SourceRange. Returns null when the
    // Symbol has no associated text (an empty composite, or one
    // whose leaves were all Delete-flattened away), or when its
    // leaves don't trace back to a string-backed source. Both Start
    // and End are full SourcePositions, so the caller can read
    // line/column, grapheme index, etc. without a separate conversion
    // call.
    //
    // FormKC / FormKD note: one original cluster can spawn multiple
    // parseInput leaves (the ligature ﬁ becomes 'f' + 'i' under
    // FormKC). Each leaf's range maps back to a position inside the
    // original cluster, so the first leaf gets [0, 0) (zero-width)
    // and the second gets [0, 1) (full cluster span). Both came from
    // the same source grapheme. Callers that need per-leaf distinction
    // will see overlapping or zero-width ranges here.
    public SourceRange? SourceRangeOf(Symbol symbol)
    {
        if (symbol == null) throw new ArgumentNullException(nameof(symbol));
        if (!symbol.TryGetCharSpan(out string parseInput, out int start, out int endExclusive))
            return null;

        // No ParseResult-level input means this is a default-constructed
        // result (or one where the user hand-built it without an
        // associated input). Fall back to parseInput coords; that's
        // what the leaf memory naturally points at.
        string? originalInput = _input;
        if (originalInput == null)
        {
            return new SourceRange(
                SourcePosition.From(parseInput, start),
                SourcePosition.From(parseInput, endExclusive));
        }

        // Identity case: parseInput IS the caller's input (Normalize
        // returned the same reference because input was already in
        // the target form, or NormalizationForm was null). No
        // translation needed; parseInput coords coincide with original
        // coords.
        if (ReferenceEquals(originalInput, parseInput))
        {
            return new SourceRange(
                SourcePosition.From(originalInput, start),
                SourcePosition.From(originalInput, endExclusive));
        }

        // Non-identity case: Normalize rewrote the input, so leaf
        // offsets are in parseInput coords. Translate each endpoint
        // back to the caller's original input through the same
        // NormalizedPositionMap routine ParseResult.ErrorCharIndex
        // uses on the failure path. The grammar's NormalizationForm is
        // what was used at Compile time; by definition it's whatever
        // produced parseInput from input.
        var form = _grammar?.NormalizationForm;
        int translatedStart = NormalizedPositionMap.TranslateToOriginal(originalInput, parseInput, start, form);
        int translatedEnd = NormalizedPositionMap.TranslateToOriginal(originalInput, parseInput, endExclusive, form);
        return new SourceRange(
            SourcePosition.From(originalInput, translatedStart),
            SourcePosition.From(originalInput, translatedEnd));
    }

    // Render the tree to a string for debug output. If Symbols has
    // one element, prints that. Otherwise prints each top-level
    // Symbol in order.
    public string PrintTree()
    {
        if (_symbols == null || _grammar == null) return string.Empty;
        if (_symbols.Count == 0) return string.Empty;
        if (_symbols.Count == 1) return _symbols[0].PrintTree(_grammar);
        var builder = new System.Text.StringBuilder();
        foreach (var s in _symbols)
            builder.Append(s.PrintTree(_grammar));
        return builder.ToString();
    }

    // The matched input text, as one string. Walks every top-level
    // Symbol and concatenates the text it covers, so a grammar whose
    // root produces a single Preserve wrapper and a grammar whose root
    // bubbles up a flat list of leaves both yield the same string here.
    // Mirrors Symbol.ToString(), which does the same for one Symbol.
    // Returns the empty string on failure (Symbols is empty in that
    // case) and on a default-constructed ParseResult. For a
    // tree-shaped debug rendering, use PrintTree() or ToDebugString().
    public override string ToString()
    {
        if (_symbols == null || _symbols.Count == 0) return string.Empty;
        if (_symbols.Count == 1) return _symbols[0].ToString();
        var builder = new System.Text.StringBuilder();
        foreach (var s in _symbols)
            builder.Append(s.ToString());
        return builder.ToString();
    }

    // Debug-friendly rendering. On success, shows the parse tree
    // (same output as PrintTree) prefixed with "Success:" so dumping
    // a ParseResult in a REPL or debugger immediately shows what was
    // parsed. On failure, a one-line summary with the outcome,
    // character index, and the error message. Not intended as a stable
    // format to parse against.
    public string ToDebugString()
    {
        if (Outcome == ParseOutcome.Success)
        {
            if (_symbols == null || _grammar == null)
                return "Success (empty ParseResult)";
            string tree = PrintTree();
            return tree.Length == 0 ? "Success (no symbols)" : "Success:\n" + tree;
        }
        return $"{Outcome} at char {ErrorCharIndex}: {ErrorMessage}";
    }

    private ParseResult(ParseOutcome outcome, IReadOnlyList<Symbol>? symbols, string errorMessage, int errorCharIndex, string? input, Rule? grammar)
    {
        Outcome = outcome;
        _symbols = symbols;
        ErrorMessage = errorMessage;
        ErrorCharIndex = errorCharIndex;
        _input = input;
        _grammar = grammar;
    }

    // Factory methods called by Rule.Parse to build the three shapes
    // of result. Exposed as public so custom parse drivers can build
    // a ParseResult with the same structure the built-in Parse
    // produces. Normal callers don't construct ParseResults directly.

    // Build a successful result. Outcome is Success, error fields
    // are empty.
    public static ParseResult Succeeded(IReadOnlyList<Symbol> symbols, string input, Rule grammar) =>
        new ParseResult(ParseOutcome.Success, symbols, string.Empty, 0, input, grammar);

    // Build a grammar-mismatch result. Outcome is GrammarMismatch,
    // the error fields carry the deepest-failure message and position.
    public static ParseResult Failed(int errorCharIndex, string message, string input, Rule grammar) =>
        new ParseResult(ParseOutcome.GrammarMismatch, null, message, errorCharIndex, input, grammar);

    // Build a budget-abort result. Outcome is one of Timeout,
    // RuleCountLimitExceeded, DepthLimitExceeded, or Canceled. The
    // error fields carry the matching "Parse aborted: ..." message
    // and the deepest-failure position so callers still get a
    // "how far did we get" hint.
    public static ParseResult Aborted(ParseOutcome outcome, int errorCharIndex, string message, string input, Rule grammar) =>
        new ParseResult(outcome, null, message, errorCharIndex, input, grammar);
}
