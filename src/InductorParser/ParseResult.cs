using System.Collections.Generic;
using System.Globalization;
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
// (chars / UTF-16 code units), ErrorRuneIndex (Unicode code points),
// ErrorGraphemeIndex (user-perceived characters), and the
// ErrorLine / ErrorColumn pair (LSP-style zero-based line and
// column). Pick whichever matches the unit the caller will use the
// number in.
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
    // failed or aborted parse, it is empty.
    public IReadOnlyList<Symbol> Symbols =>
        _symbols ?? System.Array.Empty<Symbol>();

    // Convenience accessor for the common "root is a single Symbol"
    // case. Returns Symbols[0] if there is exactly one top-level
    // Symbol, null otherwise. Callers that know their root has
    // FlattenType.Preserve (the common case for named grammars) can
    // keep using this. For grammars whose root produces multiple
    // top-level Symbols, use Symbols directly.
    public Symbol? Tree =>
        _symbols != null && _symbols.Count == 1 ? _symbols[0] : null;

    // Error position in chars (UTF-16 code units), zero-based. The
    // unit string.Substring / Range / Span use, and the unit the
    // Language Server Protocol uses for editor diagnostics. On
    // success this is 0. On failure it is the position of the deepest
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
            ComputeLineAndColumn(out int line, out _);
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
            ComputeLineAndColumn(out _, out int column);
            return column;
        }
    }

    // Error position in runes (Unicode scalar values, a.k.a. code
    // points). A surrogate pair counts as one rune, so this index is
    // smaller than or equal to ErrorCharIndex on any input that
    // contains supplementary-plane characters. Computed lazily from
    // ErrorCharIndex.
    public int ErrorRuneIndex
    {
        get
        {
            string input = _input ?? string.Empty;
            int limit = ErrorCharIndex;
            int count = 0;
            int i = 0;
            while (i < limit)
            {
                if (char.IsHighSurrogate(input[i])
                    && i + 1 < input.Length
                    && i + 1 < limit
                    && char.IsLowSurrogate(input[i + 1]))
                {
                    i += 2;
                }
                else
                {
                    i++;
                }
                count++;
            }
            return count;
        }
    }

    // Error position in graphemes (user-perceived characters, per
    // UAX #29). An emoji ZWJ sequence or a letter-plus-combining-mark
    // counts as one grapheme, so this index is smaller than or equal
    // to ErrorRuneIndex on any input that contains multi-rune
    // graphemes. Computed lazily from ErrorCharIndex.
    public int ErrorGraphemeIndex
    {
        get
        {
            string input = _input ?? string.Empty;
            int limit = ErrorCharIndex;
            if (limit <= 0) return 0;
            int count = 0;
            int i = 0;
            while (i < limit)
            {
                string element = StringInfo.GetNextTextElement(input, i);
                int step = element.Length;
                if (step <= 0) step = 1;
                i += step;
                count++;
            }
            return count;
        }
    }

    // Convenience: true when Outcome is Success, false otherwise.
    // Most callers check this first and only inspect Tree / Symbols
    // when it is true.
    public bool Success => Outcome == ParseOutcome.Success;

    // Looks up the human-readable name of a SymbolId in the grammar
    // that produced this result. Returns null if the id isn't known
    // or this is an empty default ParseResult.
    public string? NameOf(SymbolId id) => _grammar?.NameOf(id);

    // Convenience form of NameOf that takes a Symbol directly.
    // Returns null if the symbol is null.
    public string? Name(Symbol symbol) => symbol == null ? null : NameOf(symbol.Id);

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

    private void ComputeLineAndColumn(out int line, out int column)
    {
        string input = _input ?? string.Empty;
        int limit = ErrorCharIndex;
        if (limit > input.Length) limit = input.Length;

        line = 0;
        int lineStart = 0;
        for (int i = 0; i < limit; i++)
        {
            char c = input[i];
            if (c == '\n')
            {
                line++;
                lineStart = i + 1;
            }
            else if (c == '\r' && (i + 1 >= input.Length || input[i + 1] != '\n'))
            {
                line++;
                lineStart = i + 1;
            }
        }
        column = limit - lineStart;
    }
}
