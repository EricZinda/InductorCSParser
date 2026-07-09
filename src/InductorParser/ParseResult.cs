using System;
using System.Collections.Generic;
using InductorParser.SyntaxTree;

namespace InductorParser;

/// <summary>
/// The value returned by Rule.Parse: either a successful parse
/// (the result will be in <see cref="Tree"/> / <see cref="Symbols"/>) or a failure
/// (see <see cref="Outcome"/>, <see cref="ErrorMessage"/>, and the error-position
/// family).
/// </summary>
/// <remarks>
/// <see cref="Outcome"/> distinguishes "the grammar rejected the input"
/// (GrammarMismatch) from "the input isn't valid Unicode" (MalformedInput)
/// from "a budget tripped" (Timeout, RuleCountLimitExceeded,
/// DepthLimitExceeded, Canceled) so callers can show different messages to the
/// user in each case.
/// <para>
/// The error-position family reports the same point (where the parse got
/// furthest before failing) in different units: <see cref="ErrorCharIndex"/>
/// (chars, i.e. UTF-16 code units), <see cref="ErrorTokenIndex"/> (tokens, where a
/// token is one Unicode Grapheme), <see cref="ErrorLine"/>, and the column in
/// either unit, <see cref="ErrorCharColumn"/> (chars, the Language Server Protocol
/// convention) or <see cref="ErrorTokenColumn"/> (graphemes). All zero-based.
/// Pick whichever matches the unit the caller will use the number in.
/// <see cref="ErrorPosition"/> returns all of these bundled into one SourcePosition
/// struct, so callers that want more than one unit only pay for one walk of the
/// input. The same conversion is available on Symbol.SourceRange for any node
/// in the parse tree.
/// </para>
/// </remarks>
public readonly struct ParseResult
{
    private static readonly IReadOnlyList<Symbol> EmptySymbols = Array.Empty<Symbol>();

    private readonly string? _input;
    private readonly Rule? _grammar;
    private readonly IReadOnlyList<Symbol>? _symbols;
    private readonly string? _errorMessage;

    /// <summary>
    /// Returns the shape of this result: success, grammar mismatch, or
    /// which budget tripped. Always check this (or <see cref="Success"/>)
    /// before reading <see cref="Tree"/> / <see cref="Symbols"/>.
    /// </summary>
    /// <remarks>See <see cref="ParseOutcome"/> for the full list.</remarks>
    public ParseOutcome Outcome { get; }

    /// <summary>
    /// Human-readable description of what went wrong. Empty string on success
    /// and on a default-constructed ParseResult.
    /// </summary>
    /// <remarks>
    /// On GrammarMismatch, either the innermost WithError message set by the
    /// grammar or a generated "Unexpected 'x' at line L, column C" fallback. On
    /// MalformedInput, the message from
    /// <see cref="ParseOptions.MalformedInputTemplate"/>. On a budget abort, the
    /// matching "Parse aborted: ..." string. See: docs/ErrorArchitecture.md
    /// <para>
    /// The coalesce below matches how <see cref="Symbols"/> and
    /// <see cref="ToString"/> treat a default-constructed ParseResult (a zeroed
    /// array element, a FirstOrDefault on an empty list): every member returns
    /// a usable value rather than null.
    /// </para>
    /// </remarks>
    public string ErrorMessage => _errorMessage ?? string.Empty;

    /// <summary>
    /// The top-level Symbols produced by the parse. For a failed or aborted
    /// parse, it's empty.
    /// </summary>
    /// <remarks>
    /// For a root with FlattenType.Preserve this has exactly one element (the
    /// root's Symbol). For a root with FlattenType.Flatten whose children
    /// bubbled up, this is the flat list of those children.
    /// </remarks>
    public IReadOnlyList<Symbol> Symbols =>
        _symbols ?? EmptySymbols;

    /// <summary>
    /// Convenience accessor for the common "root is a single Symbol" case.
    /// Returns <see cref="Symbols"/>[0] if there's exactly one top-level
    /// Symbol, null otherwise.
    /// </summary>
    /// <remarks>
    /// Callers that know their root has FlattenType.Preserve (the common case
    /// for named grammars) can keep using this. For grammars whose root
    /// produces multiple top-level Symbols, use <see cref="Symbols"/> directly.
    /// </remarks>
    public Symbol? Tree =>
        _symbols != null && _symbols.Count == 1 ? _symbols[0] : null;

    /// <summary>
    /// Error position in chars (UTF-16 code units), zero-based. On success this
    /// is 0. On failure it's the position of the deepest recorded failure
    /// (where the parser got furthest before giving up). See docs/ErrorArchitecture.md.
    /// </summary>
    /// <remarks>
    /// This is the unit string.Substring, Range and Span use, and the unit the Language
    /// Server Protocol uses for editor diagnostics. Always in [0, input.Length]
    /// (enforced at construction). Note the top of that range: a parse that
    /// fails at end of input reports input.Length, one past the last char, and
    /// that's the most common failure position there is. Check for it before
    /// indexing into the input string with this value.
    /// </remarks>
    public int ErrorCharIndex { get; }

    /// <summary>
    /// Error position's zero-based line number. Computed lazily from
    /// <see cref="ErrorCharIndex"/> and the original input.
    /// </summary>
    /// <remarks>
    /// Line breaks follow UTS #18 §1.6 (RL1.6), the same set Rules.EndOfLine()
    /// accepts: LF, CRLF (one break, not two), lone CR, VT, FF, NEL (U+0085),
    /// LS (U+2028), PS (U+2029). That's a superset of the LF, CRLF, and lone CR
    /// a Language Server Protocol client recognizes, so the number matches an
    /// editor on ordinary source and diverges only on the rarer terminators.
    /// Keeping it aligned with EndOfLine() means every terminator a grammar
    /// consumes also bumps the reported line. See SourcePosition for the same
    /// alignment note.
    /// </remarks>
    public int ErrorLine
    {
        get
        {
            SourcePositionConverter.ToLineColumn(_input ?? string.Empty, ErrorCharIndex, out int line, out _);
            return line;
        }
    }

    /// <summary>
    /// Error position's zero-based column within the line, measured in chars (UTF-16
    /// code units, the Language Server Protocol unit). Computed lazily from
    /// <see cref="ErrorCharIndex"/> and the original input.
    /// </summary>
    public int ErrorCharColumn
    {
        get
        {
            SourcePositionConverter.ToLineColumn(_input ?? string.Empty, ErrorCharIndex, out _, out int column);
            return column;
        }
    }

    /// <summary>
    /// Error position's zero-based column within the line, measured in tokens
    /// (Unicode graphemes). Computed lazily from <see cref="ErrorCharIndex"/> and
    /// the original input.
    /// </summary>
    /// <remarks>
    /// The human-facing counterpart to <see cref="ErrorCharColumn"/>: an emoji, a
    /// flag, or a base character plus a combining mark earlier on the line counts
    /// as one column, not as its several UTF-16 code units, so the number matches
    /// the character a person sees. This is the unit the default error message
    /// reports (via the {tokenColumnNumber} template placeholder). Use
    /// <see cref="ErrorCharColumn"/> instead to match an editor or a Language
    /// Server Protocol client, which count columns in chars.
    /// </remarks>
    public int ErrorTokenColumn =>
        SourcePositionConverter.ToTokenColumn(_input ?? string.Empty, ErrorCharIndex);

    /// <summary>
    /// Error position in tokens (Unicode graphemes), using the
    /// same StringInfo text-element segmentation the lexer uses. Computed lazily
    /// from <see cref="ErrorCharIndex"/>.
    /// </summary>
    /// <remarks>On modern .NET this follows UAX #29 extended grapheme clusters.</remarks>
    public int ErrorTokenIndex =>
        SourcePositionConverter.ToTokenIndex(_input ?? string.Empty, ErrorCharIndex);

    /// <summary>
    /// The error position bundled into a SourcePosition struct. Returns null on
    /// a successful parse.
    /// </summary>
    /// <remarks>
    /// Use this when you need more than one position unit (line + column for a
    /// diagnostic, char index for a span, etc.) so you don't pay for multiple
    /// walks of the input.
    /// </remarks>
    public SourcePosition? ErrorPosition =>
        Outcome == ParseOutcome.Success
            ? null
            : SourcePosition.From(_input ?? string.Empty, ErrorCharIndex);

    /// <summary>
    /// True when <see cref="Outcome"/> is Success, false otherwise. Most callers
    /// check this first and only inspect <see cref="Tree"/> / <see cref="Symbols"/>
    /// when it's true.
    /// </summary>
    /// <remarks>
    /// ParseOutcome.Success is the enum's zero value, so a default-constructed
    /// ParseResult (a zeroed array element, a FirstOrDefault on an empty list)
    /// has Outcome == Success despite never coming from a parse. The
    /// <c>_grammar != null</c> check rejects those: every real result is built
    /// through Succeeded / Failed / Aborted, which stamp the grammar.
    /// </remarks>
    public bool Success => _grammar != null && Outcome == ParseOutcome.Success;

    /// <summary>
    /// Depth-first search across every top-level Symbol for the first node whose
    /// Id matches the rule. Returns null if no match.
    /// </summary>
    public Symbol? Find(Rule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        return Find(rule.Id);
    }

    /// <summary>
    /// Depth-first search across every top-level Symbol for the first node with
    /// this <see cref="SymbolId"/>. Returns null if no match.
    /// </summary>
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

    /// <summary>
    /// Depth-first search across every top-level Symbol that yields every
    /// matching node. Use when the rule can appear multiple times.
    /// </summary>
    public IEnumerable<Symbol> FindAll(Rule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        return FindAll(rule.Id);
    }

    /// <summary>
    /// Depth-first search across every top-level Symbol that yields every node
    /// with this <see cref="SymbolId"/>. Use when the id can appear multiple
    /// times.
    /// </summary>
    public IEnumerable<Symbol> FindAll(SymbolId id)
    {
        if (_symbols == null) yield break;
        foreach (var symbol in _symbols)
            foreach (var found in symbol.FindAll(id))
                yield return found;
    }

    /// <summary>
    /// Looks up the human-readable display label of a <see cref="SymbolId"/> in
    /// the grammar that produced this result. Returns null if the id isn't known.
    /// </summary>
    /// <remarks>
    /// The ParseResult-level mirror of Symbol.DisplayName: same fallback chain
    /// (.As(...) name, else class-derived trace label, else rune text), so it's
    /// a display label, not a dispatch key.
    /// </remarks>
    public string? DisplayNameOf(SymbolId id) => _grammar?.NameOf(id);

    /// <summary>
    /// Convenience form of <see cref="DisplayNameOf(SymbolId)"/> that takes a
    /// Symbol directly. Returns null if the symbol is null.
    /// </summary>
    public string? DisplayName(Symbol symbol) => symbol == null ? null : DisplayNameOf(symbol.Id);

    /// <summary>
    /// Render the tree to a string for debug output. If <see cref="Symbols"/>
    /// has one element, prints that. Otherwise prints each top-level Symbol in
    /// order.
    /// </summary>
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

    /// <summary>
    /// The text of the surviving parse-tree nodes, concatenated into one
    /// string. Delete'd nodes (whitespace, delimiters) drop out, so this is the
    /// matched content, not the verbatim input. Returns the empty string on
    /// failure and on a default-constructed ParseResult.
    /// </summary>
    /// <remarks>
    /// Walks every top-level Symbol and concatenates its surviving leaf text.
    /// Mirrors Symbol.ToString(), which does the same for one Symbol. For a
    /// tree-shaped debug rendering, use <see cref="PrintTree"/> or
    /// <see cref="ToDebugString"/>.
    /// </remarks>
    public override string ToString()
    {
        if (_symbols == null || _symbols.Count == 0) return string.Empty;
        if (_symbols.Count == 1) return _symbols[0].ToString();
        var builder = new System.Text.StringBuilder();
        foreach (var s in _symbols)
            builder.Append(s.ToString());
        return builder.ToString();
    }

    /// <summary>
    /// Debug-friendly rendering. On success, shows the parse tree (same output
    /// as <see cref="PrintTree"/>) prefixed with "Success:". On failure, a
    /// one-line summary with the outcome, character index, and the error
    /// message.
    /// </summary>
    /// <remarks>
    /// A human-readable debug aid whose layout may change between versions.
    /// Read <see cref="Outcome"/>, <see cref="Tree"/>, and
    /// <see cref="ErrorMessage"/> directly rather than parsing this string.
    /// </remarks>
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

    private ParseResult(ParseOutcome outcome, IReadOnlyList<Symbol>? symbols, string errorMessage, int errorCharIndex, string input, Rule grammar)
    {
        Outcome = outcome;
        _symbols = symbols;
        _errorMessage = errorMessage ?? throw new ArgumentNullException(nameof(errorMessage));
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (grammar == null) throw new ArgumentNullException(nameof(grammar));
        int inputLength = input.Length;
        if (errorCharIndex < 0 || errorCharIndex > inputLength)
            throw new ArgumentOutOfRangeException(nameof(errorCharIndex), errorCharIndex,
                $"errorCharIndex must be in [0, {inputLength}] (input.Length).");
        ErrorCharIndex = errorCharIndex;
        _input = input;
        _grammar = grammar;
    }

    // The three factory methods below are public so custom parse drivers can
    // build a ParseResult with the same structure the built-in Parse produces.
    // Normal callers don't construct ParseResults directly.

    /// <summary>
    /// Build a successful result. Outcome is Success, error fields are empty.
    /// </summary>
    public static ParseResult Succeeded(IReadOnlyList<Symbol> symbols, string input, Rule grammar)
    {
        if (symbols == null) throw new ArgumentNullException(nameof(symbols));
        return new ParseResult(ParseOutcome.Success, CopySymbols(symbols), string.Empty, 0, input, grammar);
    }

    /// <summary>
    /// Build a grammar-mismatch result. Outcome is GrammarMismatch, the error
    /// fields have the deepest-failure message and position.
    /// </summary>
    public static ParseResult Failed(int errorCharIndex, string message, string input, Rule grammar) =>
        new ParseResult(ParseOutcome.GrammarMismatch, null, message, errorCharIndex, input, grammar);

    /// <summary>
    /// Build a malformed-input result. Outcome is MalformedInput: the input
    /// couldn't be normalized to the grammar's form because it isn't well-formed
    /// Unicode. The error fields have the localized message and the offending
    /// character index. Rule.Parse builds this in place of letting .NET's
    /// string.Normalize throw. It's public so a custom parse driver that does its
    /// own normalization can report the same shape.
    /// </summary>
    public static ParseResult MalformedInput(int errorCharIndex, string message, string input, Rule grammar) =>
        new ParseResult(ParseOutcome.MalformedInput, null, message, errorCharIndex, input, grammar);

    /// <summary>
    /// Build a budget-abort result. Outcome is one of Timeout,
    /// RuleCountLimitExceeded, DepthLimitExceeded, or Canceled. The error fields
    /// have the matching "Parse aborted: ..." message and the deepest-failure
    /// position so callers still get a "how far did we get" hint.
    /// </summary>
    public static ParseResult Aborted(ParseOutcome outcome, int errorCharIndex, string message, string input, Rule grammar)
    {
        if (!IsAbortOutcome(outcome))
            throw new ArgumentException(
                "ParseResult.Aborted requires an abort outcome: Timeout, RuleCountLimitExceeded, DepthLimitExceeded, or Canceled.",
                nameof(outcome));

        return new ParseResult(outcome, null, message, errorCharIndex, input, grammar);
    }

    private static bool IsAbortOutcome(ParseOutcome outcome) =>
        outcome == ParseOutcome.Timeout
        || outcome == ParseOutcome.RuleCountLimitExceeded
        || outcome == ParseOutcome.DepthLimitExceeded
        || outcome == ParseOutcome.Canceled;

    private static IReadOnlyList<Symbol> CopySymbols(IReadOnlyList<Symbol> symbols)
    {
        int count = symbols.Count;
        if (count == 0)
            return EmptySymbols;

        var copy = new Symbol[count];
        for (int i = 0; i < count; i++)
            copy[i] = symbols[i];
        return Array.AsReadOnly(copy);
    }
}
