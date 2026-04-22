using System.Collections.Generic;
using System.Globalization;
using InductorParser.SyntaxTree;

namespace InductorParser;

public readonly struct ParseResult
{
    private readonly string? _input;
    private readonly Rule? _grammar;
    private readonly IReadOnlyList<Symbol>? _symbols;

    public ParseOutcome Outcome { get; }
    public string ErrorMessage { get; }

    // The top-level Symbols produced by the parse. For a Preserve-typed
    // root this has exactly one element (the root's wrapper). For a
    // Flatten-typed root whose children bubbled up, this is the flat
    // list of those children. For a failed or aborted parse, it is empty.
    public IReadOnlyList<Symbol> Symbols =>
        _symbols ?? System.Array.Empty<Symbol>();

    // Convenience accessor for the common "root is a single Symbol"
    // case. Returns Symbols[0] if there is exactly one top-level
    // Symbol, null otherwise. Callers that know their root is
    // Preserve-typed (the common case for named grammars) can keep using
    // this. For grammars whose root produces multiple top-level
    // Symbols, use Symbols directly.
    public Symbol? Tree =>
        _symbols != null && _symbols.Count == 1 ? _symbols[0] : null;

    public int ErrorCharIndex { get; }

    public int ErrorLine
    {
        get
        {
            ComputeLineAndColumn(out int line, out _);
            return line;
        }
    }

    public int ErrorColumn
    {
        get
        {
            ComputeLineAndColumn(out _, out int column);
            return column;
        }
    }

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

    public bool Success => Outcome == ParseOutcome.Success;

    public string? NameOf(SymbolId id) => _grammar?.NameOf(id);

    public string? Name(Symbol symbol) => symbol == null ? null : NameOf(symbol.Id);

    // Render the tree to a string for debug output. If Symbols has one
    // element, prints that. Otherwise prints each top-level Symbol.
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

    public static ParseResult Succeeded(IReadOnlyList<Symbol> symbols, string input, Rule grammar) =>
        new ParseResult(ParseOutcome.Success, symbols, string.Empty, 0, input, grammar);

    public static ParseResult Failed(int errorCharIndex, string message, string input, Rule grammar) =>
        new ParseResult(ParseOutcome.GrammarMismatch, null, message, errorCharIndex, input, grammar);

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
