using System;
using System.Collections.Generic;
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
    //
    // Note that this returns null when the root rule's FlattenType is
    // Flatten (the default for And, Or, and the count rules), because
    // those rules lift their children into the top-level Symbols list
    // rather than producing a single wrapper. If you just want to look
    // up a named child by rule, use Find / FindAll, which walk every
    // top-level Symbol and don't care which shape the root produced.
    public Symbol? Tree =>
        _symbols != null && _symbols.Count == 1 ? _symbols[0] : null;

    // Error position in chars (UTF-16 code units), zero-based. The
    // unit string.Substring / Range / Span use, and the unit the
    // Language Server Protocol uses for editor diagnostics. On
    // success this is 0. On failure it's the position of the deepest
    // recorded failure (where the parser got furthest before giving
    // up). Always in [0, input.Length] (enforced at construction), so
    // callers can index into the original input string without
    // bounds-checking.
    public int ErrorCharIndex { get; }

    // Error position's zero-based line number. Line breaks follow UAX #18
    // Annex C, the same set Rules.EndOfLine() accepts: LF, CRLF (one break,
    // not two), lone CR, VT, FF, NEL (U+0085), LS (U+2028), PS (U+2029).
    // That's a superset of the LF, CRLF, and lone CR a Language Server
    // Protocol client recognizes, so the number matches an editor on
    // ordinary source and diverges only on the rarer terminators. Keeping
    // it aligned with EndOfLine() means every terminator a grammar consumes
    // also bumps the reported line. Computed lazily from ErrorCharIndex and
    // the original input. See SourcePosition for the same alignment note.
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

    // Depth-first search across every top-level Symbol for the first
    // node whose Id matches the rule. Returns null if no match.
    //
    // Symbol.Find requires a single-root tree, but the natural root for
    // most composite rules (And, Or, count rules) defaults to
    // FlattenType.Flatten and lifts its children into the top-level
    // Symbols list, so result.Tree is null and result.Tree.Find blows
    // up with a NullReferenceException. This walks every top-level
    // Symbol in turn, so it works regardless of whether the root
    // preserved itself or flattened its children up. Grammar authors
    // who just want "find the node with this rule's id in the result"
    // can use this without first figuring out which shape their root
    // produced.
    public Symbol? Find(Rule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        return Find(rule.Id);
    }

    public Symbol? Find(SymbolId id)
    {
        if (_symbols == null) return null;
        foreach (var symbol in _symbols)
        {
            var found = symbol.Find(id);
            if (found != null) return found;
        }
        return null;
    }

    // Depth-first search across every top-level Symbol that yields every
    // matching node. Use when the rule can appear multiple times. Like
    // Find, this works regardless of whether the root preserved itself
    // or flattened its children into the top-level Symbols list.
    public IEnumerable<Symbol> FindAll(Rule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        return FindAll(rule.Id);
    }

    public IEnumerable<Symbol> FindAll(SymbolId id)
    {
        if (_symbols == null) yield break;
        foreach (var symbol in _symbols)
            foreach (var found in symbol.FindAll(id))
                yield return found;
    }

    // Looks up the human-readable display label of a SymbolId in the
    // grammar that produced this result. Returns null if the id isn't
    // known or this is an empty default ParseResult. This is the
    // ParseResult-level mirror of Symbol.DisplayName: same fallback
    // chain (.As(...) name, else class-derived trace label, else rune
    // text), so it's a display label, not a dispatch key.
    public string? DisplayNameOf(SymbolId id) => _grammar?.NameOf(id);

    // Convenience form of DisplayNameOf that takes a Symbol directly.
    // Returns null if the symbol is null.
    public string? DisplayName(Symbol symbol) => symbol == null ? null : DisplayNameOf(symbol.Id);

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
        int inputLength = input?.Length ?? 0;
        if (errorCharIndex < 0 || errorCharIndex > inputLength)
            throw new ArgumentOutOfRangeException(nameof(errorCharIndex), errorCharIndex,
                $"errorCharIndex must be in [0, {inputLength}] (input.Length).");
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
