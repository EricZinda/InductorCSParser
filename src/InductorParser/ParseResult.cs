using System;
using System.Collections.Generic;
using InductorParser.SyntaxTree;

namespace InductorParser;

/// <summary>
/// The value returned by <see cref="InductorParser.Rule.Parse(System.String)">Rule.Parse</see>: either a successful parse
/// (the result will be in <see cref="Tree">ParseResult.Tree</see> / <see cref="Symbols">ParseResult.Symbols</see>) or a failure
/// (see <see cref="Outcome">ParseResult.Outcome</see>, <see cref="ErrorMessage">ParseResult.ErrorMessage</see>, and the error-position
/// family).
/// </summary>
/// <remarks>
/// <see cref="Outcome">ParseResult.Outcome</see> distinguishes "the grammar rejected the input"
/// (<see cref="InductorParser.ParseOutcome.GrammarMismatch">ParseOutcome.GrammarMismatch</see>) from "the input can't be normalized" (<see cref="InductorParser.ParseOutcome.MalformedInput">ParseOutcome.MalformedInput</see>)
/// from "a budget tripped" (<see cref="ParseOutcome.Timeout">ParseOutcome.Timeout</see>, <see cref="InductorParser.ParseOutcome.RuleCountLimitExceeded">ParseOutcome.RuleCountLimitExceeded</see>,
/// <see cref="InductorParser.ParseOutcome.DepthLimitExceeded">ParseOutcome.DepthLimitExceeded</see>, <see cref="InductorParser.ParseOutcome.Canceled">ParseOutcome.Canceled</see>) so callers can show different messages to the
/// user in each case.
/// <para>
/// The error-position family reports the same point (where the parse got
/// furthest before failing) in different units: <see cref="ErrorCharIndex">ParseResult.ErrorCharIndex</see>
/// (chars, i.e. UTF-16 code units), <see cref="ErrorTokenIndex">ParseResult.ErrorTokenIndex</see> (tokens, where a
/// token is one Unicode Grapheme), <see cref="ErrorLine">ParseResult.ErrorLine</see>, and the column in
/// either unit, <see cref="ErrorCharColumn">ParseResult.ErrorCharColumn</see> (chars, the Language Server Protocol
/// convention) or <see cref="ErrorTokenColumn">ParseResult.ErrorTokenColumn</see> (graphemes). All zero-based.
/// Pick whichever matches the unit the caller will use the number in.
/// <see cref="ErrorPosition">ParseResult.ErrorPosition</see> returns all of these packed into one <see cref="InductorParser.SyntaxTree.SourcePosition">SourcePosition</see>
/// struct, so callers that want more than one unit share the position work
/// instead of paying a separate walk per property read. The same conversion is
/// available on <see cref="InductorParser.SyntaxTree.Symbol.SourceRange">Symbol.SourceRange</see> for any node in the parse tree.
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
    /// Reports whether a parse ran and how it ended. Defaults to
    /// <see cref="ParseOutcome.NotRun">ParseOutcome.NotRun</see>.
    /// Check this (or <see cref="Success">ParseResult.Success</see>)
    /// before reading <see cref="Tree">ParseResult.Tree</see> / <see cref="Symbols">ParseResult.Symbols</see>.
    /// </summary>
    /// <remarks>See <see cref="ParseOutcome"/> for the full list.</remarks>
    public ParseOutcome Outcome { get; }

    /// <summary>
    /// Human-readable description of what went wrong. Empty string on success
    /// and on a default-constructed ParseResult.
    /// </summary>
    /// <remarks>
    /// On <see cref="InductorParser.ParseOutcome.GrammarMismatch">ParseOutcome.GrammarMismatch</see>, either the innermost <see cref="InductorParser.Rule.WithError(System.String,System.Boolean)">Rule.WithError</see> message set by the
    /// grammar or a generated "Unexpected 'x' at line L, column C" fallback. On
    /// <see cref="InductorParser.ParseOutcome.MalformedInput">ParseOutcome.MalformedInput</see>, the message from
    /// <see cref="ParseOptions.MalformedInputTemplate">ParseOptions.MalformedInputTemplate</see>. On a budget abort, the
    /// matching "Parse aborted: ..." string. See: <a href="../docs/ErrorArchitecture.md">Error Reporting Architecture</a>
    /// </remarks>
    public string ErrorMessage => _errorMessage ?? string.Empty;

    /// <summary>
    /// The top-level Symbols produced by the parse. For a failed or aborted
    /// parse, it's empty.
    /// </summary>
    /// <remarks>
    /// For a root with <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> this has exactly one element (the
    /// root's Symbol). For a root with <see cref="InductorParser.SyntaxTree.FlattenType.Flatten">FlattenType.Flatten</see> whose children
    /// bubbled up, this is the flat list of those children.
    /// </remarks>
    public IReadOnlyList<Symbol> Symbols =>
        _symbols ?? EmptySymbols;

    /// <summary>
    /// Convenience accessor for the common "root is a single Symbol" case.
    /// Returns <see cref="Symbols">ParseResult.Symbols</see>[0] if there's exactly one top-level
    /// Symbol, null otherwise.
    /// </summary>
    /// <remarks>
    /// Callers that know their root has <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> (the common case
    /// for named grammars) can keep using this. For grammars whose root
    /// produces multiple top-level Symbols, use <see cref="Symbols">ParseResult.Symbols</see> directly.
    /// </remarks>
    public Symbol? Tree =>
        _symbols != null && _symbols.Count == 1 ? _symbols[0] : null;

    /// <summary>
    /// Error position in chars (UTF-16 code units), zero-based. On success this
    /// is 0. On failure it's the position of the deepest recorded failure
    /// (where the parser got furthest before giving up). See <a href="../docs/ErrorArchitecture.md">Error Reporting Architecture</a>.
    /// </summary>
    /// <remarks>
    /// This is the unit string.Substring, Range and Span use, and the unit the Language
    /// Server Protocol uses for editor diagnostics. Always in [0, input.Length]
    /// (enforced at construction). Note the top of that range: a parse that
    /// fails at end of input reports input.Length, one past the last char, and
    /// that's the most common failure position there is. Check for it before
    /// indexing into the input string with this value.
    /// <para>
    /// This is always an index into the string you passed to Parse, even when
    /// the grammar was compiled against a normalization form that rewrote the
    /// input before matching. The parser translates positions back to your
    /// original input before reporting them, and every other position on this
    /// result is derived from this one, so they're all in those coordinates.
    /// See <a href="../docs/MappingPositionsAfterNormalization.md">Mapping Positions Through Unicode Normalization</a>
    /// for how this translation works.
    /// </para>
    /// </remarks>
    public int ErrorCharIndex { get; }

    /// <summary>
    /// Error position's zero-based line number. Computed lazily from
    /// <see cref="ErrorCharIndex">ParseResult.ErrorCharIndex</see> and the original input.
    /// </summary>
    /// <remarks>
    /// Line breaks follow <a href="https://www.unicode.org/reports/tr18/#Line_Boundaries">UTS #18</a> §1.6 (RL1.6), the same set <see cref="InductorParser.Rules.EndOfLine">Rules.EndOfLine()</see>
    /// accepts: LF, CRLF (one break, not two), lone CR, VT, FF, NEL (U+0085),
    /// LS (U+2028), PS (U+2029). That's a superset of the LF, CRLF, and lone CR
    /// a Language Server Protocol client recognizes, so the number matches an
    /// editor on ordinary source and diverges only on the rarer terminators.
    /// Keeping it aligned with <see cref="InductorParser.Rules.EndOfLine">Rules.EndOfLine()</see> means every terminator a grammar
    /// consumes also bumps the reported line. See <see cref="SourcePosition"/> for the same
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
    /// <see cref="ErrorCharIndex">ParseResult.ErrorCharIndex</see> and the original input.
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
    /// (Unicode graphemes). Computed lazily from <see cref="ErrorCharIndex">ParseResult.ErrorCharIndex</see> and
    /// the original input.
    /// </summary>
    /// <remarks>
    /// The human-facing counterpart to <see cref="ErrorCharColumn">ParseResult.ErrorCharColumn</see>: an emoji, a
    /// flag, or a base character plus a combining mark earlier on the line counts
    /// as one column, not as its several UTF-16 code units, so the number matches
    /// the character a person sees. This is the unit the default error message
    /// reports (via the {tokenColumnNumber} template placeholder). Use
    /// <see cref="ErrorCharColumn">ParseResult.ErrorCharColumn</see> instead to match an editor or a Language
    /// Server Protocol client, which count columns in chars.
    /// </remarks>
    public int ErrorTokenColumn =>
        SourcePositionConverter.ToTokenColumn(_input ?? string.Empty, ErrorCharIndex);

    /// <summary>
    /// Error position in tokens (Unicode graphemes), using the
    /// same <a href="https://www.unicode.org/reports/tr29/">UAX #29</a> grapheme segmentation the lexer uses. Computed lazily
    /// from <see cref="ErrorCharIndex">ParseResult.ErrorCharIndex</see>.
    /// </summary>
    public int ErrorTokenIndex =>
        SourcePositionConverter.ToTokenIndex(_input ?? string.Empty, ErrorCharIndex);

    /// <summary>
    /// The error position packed into a <see cref="InductorParser.SyntaxTree.SourcePosition">SourcePosition</see> struct. Returns null on
    /// a successful parse or <see cref="ParseOutcome.NotRun">ParseOutcome.NotRun</see>.
    /// </summary>
    /// <remarks>
    /// Use this when you need more than one position unit (line + column for a
    /// diagnostic, char index for a span, etc.) so you don't pay for multiple
    /// walks of the input.
    /// </remarks>
    public SourcePosition? ErrorPosition =>
        Outcome == ParseOutcome.Success || Outcome == ParseOutcome.NotRun
            ? null
            : SourcePosition.From(_input ?? string.Empty, ErrorCharIndex);

    /// <summary>
    /// True when this result came from a successful parse, false otherwise. Most callers
    /// check this first and only inspect <see cref="Tree">ParseResult.Tree</see> / <see cref="Symbols">ParseResult.Symbols</see>
    /// when it's true.
    /// </summary>
    /// <remarks>
    /// Equivalent to checking whether <see cref="Outcome">ParseResult.Outcome</see>
    /// is <see cref="ParseOutcome.Success">ParseOutcome.Success</see>.
    /// </remarks>
    public bool Success => Outcome == ParseOutcome.Success;

    /// <summary>
    /// Depth-first search across every top-level Symbol for the first node whose
    /// <see cref="SyntaxTree.Symbol.Id">Symbol.Id</see> matches the rule. Returns null if no match.
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
    /// If you named the rule with <see cref="Rule.As(string)">Rule.As("name")</see>, that name is used.
    /// Otherwise, a rule for one specific rune uses that rune's text, such as "a".
    /// Otherwise, it uses a default label, such as "Token" or "And", including for a grapheme made of multiple runes.
    /// <para>
    /// This is a label for the rule. Use <see cref="Symbol.ToString">Symbol.ToString()</see> to get the text it matched.
    /// </para>
    /// </remarks>
    public string? DisplayNameOf(SymbolId id) => _grammar?.NameOf(id);

    /// <summary>
    /// Convenience form of <see cref="DisplayNameOf(SymbolId)">ParseResult.DisplayNameOf(SymbolId)</see> that takes a
    /// Symbol directly. Returns null if the symbol is null.
    /// </summary>
    public string? DisplayName(Symbol symbol) => symbol == null ? null : DisplayNameOf(symbol.Id);

    /// <summary>
    /// Render the tree to a string for debug output. If <see cref="Symbols">ParseResult.Symbols</see>
    /// has one element, prints that. Otherwise prints each top-level Symbol in
    /// order.
    /// </summary>
    /// <example>
    /// Shows node names, matched text, and indentation for child nodes.
    /// <code>
    /// using static InductorParser.Rules;
    ///
    /// var word = OneOrMore(OneOf(TokenSet.Letters)).As("word").Preserve();
    /// var result = word.Parse("hi");
    /// Console.Write(result.PrintTree());
    /// </code>
    /// Output:
    /// <code language="text">
    /// word: "hi"
    ///   'h'
    ///   'i'
    /// </code>
    /// Unlike <see cref="ToDebugString">ParseResult.ToDebugString()</see>, this doesn't include the parse outcome.
    /// </example>
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
    /// string. <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see>'d nodes (whitespace, delimiters) drop out, so this is the
    /// matched content, not the verbatim input. Returns the empty string on
    /// failure and on a default-constructed ParseResult.
    /// </summary>
    /// <remarks>
    /// Walks every top-level Symbol and concatenates its surviving leaf text.
    /// Mirrors <see cref="InductorParser.SyntaxTree.Symbol.ToString">Symbol.ToString()</see>, which does the same for one Symbol. For a
    /// tree-shaped debug rendering, use <see cref="PrintTree">ParseResult.PrintTree()</see> or
    /// <see cref="ToDebugString">ParseResult.ToDebugString()</see>.
    /// </remarks>
    /// <example>
    /// Returns just the surviving text, without node names or a parse outcome.
    /// <code>
    /// using static InductorParser.Rules;
    ///
    /// var word = OneOrMore(OneOf(TokenSet.Letters)).As("word").Preserve();
    /// var result = word.Parse("hi");
    /// Console.WriteLine(result.ToString());
    /// </code>
    /// Output:
    /// <code language="text">
    /// hi
    /// </code>
    /// </example>
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
    /// as <see cref="PrintTree">ParseResult.PrintTree()</see>) prefixed with "Success:". On failure, a
    /// one-line summary with the outcome, character index, and the error
    /// message. Returns "NotRun" for a default result.
    /// </summary>
    /// <remarks>
    /// A human-readable debug aid whose layout may change between versions.
    /// Read <see cref="Outcome">ParseResult.Outcome</see>, <see cref="Tree">ParseResult.Tree</see>, and
    /// <see cref="ErrorMessage">ParseResult.ErrorMessage</see> directly rather than parsing this string.
    /// </remarks>
    /// <example>
    /// Adds the parse outcome to the tree shown by <see cref="PrintTree">ParseResult.PrintTree()</see>.
    /// <code>
    /// using static InductorParser.Rules;
    ///
    /// var word = OneOrMore(OneOf(TokenSet.Letters)).As("word").Preserve();
    /// var result = word.Parse("hi");
    /// Console.Write(result.ToDebugString());
    /// </code>
    /// Output:
    /// <code language="text">
    /// Success:
    /// word: "hi"
    ///   'h'
    ///   'i'
    /// </code>
    /// </example>
    public string ToDebugString()
    {
        if (Outcome == ParseOutcome.NotRun)
            return nameof(ParseOutcome.NotRun);
        if (Outcome == ParseOutcome.Success)
        {
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

    // Shared result construction for the parser and alternative evaluator.
    // Consumers obtain results through Rule.Parse.

    /// <summary>
    /// Build a successful result. <see cref="ParseResult.Outcome">ParseResult.Outcome</see> is <see cref="ParseOutcome.Success">ParseOutcome.Success</see>, error fields are empty.
    /// </summary>
    internal static ParseResult Succeeded(IReadOnlyList<Symbol> symbols, string input, Rule grammar)
    {
        if (symbols == null) throw new ArgumentNullException(nameof(symbols));
        return new ParseResult(ParseOutcome.Success, CopySymbols(symbols), string.Empty, 0, input, grammar);
    }

    /// <summary>
    /// Build a grammar-mismatch result. <see cref="ParseResult.Outcome">ParseResult.Outcome</see> is <see cref="InductorParser.ParseOutcome.GrammarMismatch">ParseOutcome.GrammarMismatch</see>, the error
    /// fields have the deepest-failure message and position.
    /// </summary>
    internal static ParseResult Failed(int errorCharIndex, string message, string input, Rule grammar) =>
        new ParseResult(ParseOutcome.GrammarMismatch, null, message, errorCharIndex, input, grammar);

    /// <summary>
    /// Build a malformed-input result. <see cref="ParseResult.Outcome">ParseResult.Outcome</see> is <see cref="InductorParser.ParseOutcome.MalformedInput">ParseOutcome.MalformedInput</see>: the input
    /// couldn't be normalized to the grammar's form because it isn't well-formed
    /// Unicode. The error fields have the localized message and the offending
    /// character index. <see cref="InductorParser.Rule.Parse(System.String)">Rule.Parse</see> builds this in place of letting .NET's
    /// <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see> throw.
    /// </summary>
    internal static ParseResult MalformedInput(int errorCharIndex, string message, string input, Rule grammar) =>
        new ParseResult(ParseOutcome.MalformedInput, null, message, errorCharIndex, input, grammar);

    /// <summary>
    /// Build a budget-abort result. <see cref="ParseResult.Outcome">ParseResult.Outcome</see> is one of <see cref="ParseOutcome.Timeout">ParseOutcome.Timeout</see>,
    /// <see cref="InductorParser.ParseOutcome.RuleCountLimitExceeded">ParseOutcome.RuleCountLimitExceeded</see>, <see cref="InductorParser.ParseOutcome.DepthLimitExceeded">ParseOutcome.DepthLimitExceeded</see>, or <see cref="InductorParser.ParseOutcome.Canceled">ParseOutcome.Canceled</see>. The error fields
    /// have the matching "Parse aborted: ..." message and the deepest-failure
    /// position so callers still get a "how far did we get" hint.
    /// </summary>
    internal static ParseResult Aborted(ParseOutcome outcome, int errorCharIndex, string message, string input, Rule grammar)
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
