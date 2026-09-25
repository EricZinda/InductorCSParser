using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using InductorParser;
using InductorParser.Lexing;

namespace InductorParser.SyntaxTree;

/// <summary>
/// A node in the parse tree produced by <see cref="Rule.Parse(string)"/>.
/// </summary>
/// <remarks>
/// A Symbol has one of two shapes. A composite has a list of child Symbols and comes from
/// rules that build structure (And, Or, <see cref="InductorParser.Rules.OneOrMore(InductorParser.Rule)">OneOrMore</see>). A leaf stores a section of the original
/// input (a ReadOnlyMemory&lt;char&gt;) and comes from rules that match content (Token, Literal,
/// <see cref="InductorParser.Rules.OneOf(System.String)">OneOf</see>, <see cref="InductorParser.Rules.ScanUntil(InductorParser.Rule,System.Boolean)">ScanUntil</see>). The parse never copies input into a new string.
/// <para>
/// A Symbol can also report where in the source it came from. <see cref="SourceRange"/> returns
/// a Start/End pair of <see cref="SourcePosition"/>s in the same char / token / line / column
/// units <see cref="ParseResult"/> uses for error positions.
/// </para>
/// </remarks>
public sealed class Symbol
{
    // Shared empty list for the Children of leaf symbols.
    private static readonly IReadOnlyList<Symbol> EmptyChildren = Array.Empty<Symbol>();

    /// <summary>
    /// The Symbol a rule returns from TryParse to mean "matched successfully, contributes
    /// nothing" when its effective <see cref="FlattenType"/> is Delete. A rule needs a non-null
    /// value to signal success (null means failure), and this is the value the tree then drops.
    /// </summary>
    /// <remarks>
    /// Consumers like AndRule filter it out before it reaches a parent's <see cref="Children"/>
    /// list, so a <see cref="InductorParser.SyntaxTree.FlattenType.Delete">Delete</see> rule never contributes a Discarded Symbol to the final tree. Never
    /// stored as a child of any real Symbol.
    /// </remarks>
    public static readonly Symbol Discarded = new Symbol(default, FlattenType.Delete, ReadOnlyMemory<char>.Empty);

    // Leaf symbols hold a ReadOnlyMemory<char> into the original input string rather than a
    // copied substring, so the parse allocates no strings. Only ToString materializes text.
    //
    // This involves a tradeoff: a ReadOnlyMemory<char> keeps its backing string alive for as
    // long as the Memory is reachable. So a tree of Symbols whose leaves point into a parsed
    // input string keeps that whole input string alive, even if the caller only keeps a small
    // subtree. That's almost always what you want for parser output (the tree conceptually
    // represents the input, and the input usually stays around anyway), but if you parse a
    // 100 MB document and then keep a 10-character Symbol out of it while dropping everything
    // else, the entire 100 MB input stays GC-rooted until that Symbol is also released. Call
    // ToString() and keep just the resulting string to detach from the original input.
    private readonly ReadOnlyMemory<char> _leafChars;

    // Engine-internal accessor that lets an alternative evaluator recover a leaf's bounds inside
    // a match span.
    internal ReadOnlyMemory<char> LeafMemory => _leafChars;

    private readonly bool _isLeaf;

    /// <summary>
    /// True when this Symbol is a leaf with matched text, false when it's a composite
    /// with child Symbols. A composite with an empty <see cref="Children"/> list still
    /// reports false: zero children isn't the same shape as a leaf.
    /// </summary>
    public bool IsLeaf => _isLeaf;

    // Per-parse context the engine stamps onto every Symbol it builds. It stores the original
    // input, the normalized parse input, the normalization form, and the grammar root, so a
    // Symbol can translate its parseInput-relative _leafChars span back to original-input
    // coordinates (SourceRange / SourceText) and resolve its id to a rule name (DisplayName).
    private readonly ParseContext? _context;

    /// <summary>
    /// Identifies which rule produced this Symbol. Tree walkers compare it via
    /// <see cref="Is(Rule)"/>, <see cref="Find(SymbolId)"/>, and friends.
    /// </summary>
    public SymbolId Id { get; }

    /// <summary>
    /// How this Symbol participates when the tree is flattened: <see cref="InductorParser.SyntaxTree.FlattenType.Delete">Delete</see> drops it, <see cref="FlattenType.Flatten">Flatten</see> lifts
    /// its children into the parent, <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">Preserve</see> keeps it as a node.
    /// </summary>
    public FlattenType FlattenType { get; }

    /// <summary>This Symbol's child Symbols, or an empty list when it's a leaf.</summary>
    public IReadOnlyList<Symbol> Children { get; }

    /// <summary>
    /// Builds a composite Symbol with child Symbols.
    /// </summary>
    /// <remarks>
    /// <paramref name="consumedSpan"/> is every character the rule matched, including ones that
    /// never make it into the tree (<see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see> children filtered out of
    /// <see cref="Children"/>), so <see cref="SourceRange"/> / <see cref="SourceText"/> report
    /// the full match. For a zero-width match, pass a zero-length memory at the rule's anchor
    /// offset so callers still get a position.
    /// </remarks>
    /// <param name="id">The id of the rule producing this Symbol.</param>
    /// <param name="flattenType">How this Symbol participates in flattening.</param>
    /// <param name="children">The child Symbols. Null or empty collapses to a shared empty list.</param>
    /// <param name="consumedSpan">The parse-input section the match covered.</param>
    /// <param name="context">The per-parse context, or null for a hand-built Symbol.</param>
    public Symbol(SymbolId id, FlattenType flattenType, IReadOnlyList<Symbol>? children, ReadOnlyMemory<char> consumedSpan = default, ParseContext? context = null)
        : this(id, flattenType, CopyChildren(children), consumedSpan, context, true)
    {
    }

    // Wraps a freshly-built children collection without the defensive copy the public
    // constructor makes (List/array go through AsReadOnly, so Symbol.Children stays
    // non-castable). The caller must never mutate `children` after this call.
    internal static Symbol FromOwnedChildren(SymbolId id, FlattenType flattenType, IReadOnlyList<Symbol>? children, ReadOnlyMemory<char> consumedSpan = default, ParseContext? context = null) =>
        new Symbol(id, flattenType, WrapOwnedChildren(children), consumedSpan, context, true);

    private Symbol(SymbolId id, FlattenType flattenType, IReadOnlyList<Symbol> children, ReadOnlyMemory<char> consumedSpan, ParseContext? context, bool _)
    {
        Id = id;
        FlattenType = flattenType;
        Children = children;
        _leafChars = consumedSpan;
        _isLeaf = false;
        _context = context;
    }

    /// <summary>
    /// Builds a leaf Symbol whose matched text points into the parse input.
    /// </summary>
    /// <param name="id">The id of the rule producing this Symbol.</param>
    /// <param name="flattenType">How this Symbol participates in flattening.</param>
    /// <param name="leafChars">The matched text, pointing into the parse input.</param>
    /// <param name="context">The per-parse context, or null for a hand-built Symbol.</param>
    public Symbol(SymbolId id, FlattenType flattenType, ReadOnlyMemory<char> leafChars, ParseContext? context = null)
    {
        Id = id;
        FlattenType = flattenType;
        Children = EmptyChildren;
        _leafChars = leafChars;
        _isLeaf = true;
        _context = context;
    }

    /// <summary>
    /// Does this single Symbol come from <paramref name="rule"/>? A single-node check, not a tree
    /// walk. Reads more naturally than comparing <see cref="Id"/> directly and hides the id
    /// plumbing from consumer code.
    /// </summary>
    public bool Is(Rule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        return Id == rule.Id;
    }

    /// <summary>
    /// Does this Symbol come from the rule the grammar named <paramref name="ruleName"/> via
    /// .As("name")? For tree walkers that dispatch on the grammar name rather than hold a Rule
    /// reference.
    /// </summary>
    /// <remarks>
    /// Works only on Symbols that came out of a real <see cref="Rule.Parse(string)"/> call.
    /// Hand-built Symbols (no context) and Symbols whose id maps to an unnamed rule both return
    /// false. The name resolves through the same .As(...) index <see cref="Rule.IdOf(string)"/>
    /// uses, so it's an O(1) lookup after the first call. Class-derived trace labels ("And",
    /// "OneOrMore") aren't in that index, so this never matches them even though
    /// <see cref="DisplayName"/> falls back to them for unnamed rules.
    /// </remarks>
    public bool Is(string ruleName)
    {
        if (ruleName == null) return false;
        Rule? grammarRoot = _context?.GrammarRoot;
        if (grammarRoot == null) return false;
        // Route through the .As(...) name index (the same one Rule.IdOf uses), not DisplayName:
        // DisplayName falls back to trace labels like "And", so matching on it would make
        // Is("And") return true for an anonymous And node.
        SymbolId? namedId = grammarRoot.IdOf(ruleName);
        return namedId.HasValue && namedId.Value == Id;
    }

    /// <summary>
    /// A human-readable label for the rule that produced this Symbol, for debug output and tree
    /// printing, or null when there's no grammar to resolve against.
    /// </summary>
    /// <remarks>
    /// When the rule was constructed with .As("name"), that name is returned. Otherwise, it falls
    /// back the same way <see cref="Rule.NameOf(SymbolId)"/> does: a character-leaf rule resolves
    /// to the matched rune's own text, and any other rule resolves to its class-derived trace
    /// label ("And", "OneOrMore", "BetweenInclusive[1..3]"). So an anonymous And(...)
    /// returns "And" and an anonymous Token('a') leaf returns "a". Returns null when the Symbol
    /// was hand-built with no <see cref="ParseContext"/>, or its id doesn't map to any rule
    /// reachable from the parse's grammar.
    /// <para>
    /// This is a display label, not a dispatch key. Because it includes the trace-label and
    /// rune-text fallbacks it's neither unique nor limited to names the grammar author chose. To
    /// test whether a Symbol came from a rule the author actually named, use
    /// <see cref="Is(string)"/>, which matches only .As(...) names: <see cref="DisplayName"/> can
    /// be "And" while <see cref="Is(string)"/> with "And" is false.
    /// </para>
    /// </remarks>
    public string? DisplayName => _context?.GrammarRoot?.NameOf(Id);

    /// <summary>
    /// Depth-first search for the first Symbol produced by <paramref name="rule"/>.
    /// See <see cref="Find(SymbolId)"/>.
    /// </summary>
    public Symbol? Find(Rule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        return Find(rule.Id);
    }

    /// <summary>
    /// Depth-first search for the first Symbol whose <see cref="Id"/> matches, or null if none
    /// does. Use when you expect exactly one match, such as a named rule that appears once at a
    /// known position in the grammar.
    /// </summary>
    public Symbol? Find(SymbolId id)
    {
        if (Id == id) return this;
        foreach (var child in Children)
        {
            var found = child.Find(id);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Depth-first search yielding every Symbol produced by <paramref name="rule"/>.
    /// See <see cref="FindAll(SymbolId)"/>.
    /// </summary>
    public IEnumerable<Symbol> FindAll(Rule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        return FindAll(rule.Id);
    }

    /// <summary>
    /// Depth-first search yielding every Symbol whose <see cref="Id"/> matches. Use when the rule
    /// can appear multiple times (repetitions, alternations, recursive grammars).
    /// </summary>
    public IEnumerable<Symbol> FindAll(SymbolId id)
    {
        var stack = new Stack<Symbol>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Id == id) yield return node;
            // Push children in reverse so they pop left-to-right, keeping the
            // pre-order, children-in-order sequence the recursive form gave.
            var children = node.Children;
            for (int i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }
    }

    /// <summary>
    /// Depth-first walk yielding every Symbol in the tree, starting with this one. Use to inspect
    /// or transform every node regardless of id.
    /// </summary>
    public IEnumerable<Symbol> Walk()
    {
        var stack = new Stack<Symbol>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            // Push children in reverse so they pop left-to-right, keeping the
            // pre-order, children-in-order sequence the recursive form gave.
            var children = node.Children;
            for (int i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }
    }

    /// <summary>
    /// Renders the text present in the tree: a leaf renders its captured text, and a composite
    /// renders the concatenated text of its children.
    /// </summary>
    /// <remarks>
    /// On the default parse path, <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see> rules are filtered out of the tree, so the
    /// characters they matched don't appear in the result. <see cref="InductorParser.SyntaxTree.FlattenType.Flatten">FlattenType.Flatten</see> Symbols are gone
    /// too, but their children were lifted into the parent, so the characters those children
    /// matched do still appear. To get the exact input verbatim, keep the string you passed to
    /// Parse, read <see cref="SourceText"/>, or set <see cref="InductorParser.ParseOptions.PreserveAllSymbols">ParseOptions.PreserveAllSymbols</see> to keep every
    /// grammar node (including <see cref="InductorParser.SyntaxTree.FlattenType.Delete">Delete</see> ones) in the tree.
    /// <para>
    /// When the grammar normalized the input (any form other than <c><see cref="Rule.Compile(System.Text.NormalizationForm?)">Compile(null)</see></c>), a leaf's
    /// text comes from the normalized parse input, so this renders the normalized form the parser
    /// matched, not the user's original spelling. <see cref="SourceText"/> returns the original.
    /// </para>
    /// </remarks>
    public override string ToString()
    {
        if (_isLeaf) return _leafChars.ToString();
        var builder = new StringBuilder();
        AppendTo(builder);
        return builder.ToString();
    }

    private void AppendTo(StringBuilder builder)
    {
        if (_isLeaf) { builder.Append(_leafChars.Span); return; }
        foreach (var child in Children) child.AppendTo(builder);
    }

    // Recover the parse-input-relative bounds of this Symbol's matched span. Both leaves and
    // composites store the bounds in _leafChars. Returns false for any Symbol without a populated
    // _leafChars, such as a hand-built composite from external code. SourceRange / SourceText
    // consume these and translate the offsets to the caller's original-input coordinates.
    internal bool TryGetCharSpan(out string parseInput, out int start, out int endExclusive)
    {
        parseInput = null!;
        start = 0;
        endExclusive = 0;

        if (!MemoryMarshal.TryGetString(_leafChars, out string? input, out int inputStart, out int inputLength)
            || input == null)
            return false;

        parseInput = input;
        start = inputStart;
        endExclusive = inputStart + inputLength;
        return true;
    }

    /// <summary>
    /// The range of the user's original input this Symbol's match covers, or null when the Symbol
    /// has no associated text (an empty composite, one whose children's leaves don't trace back to
    /// a string-backed source, or a default-constructed Symbol).
    /// </summary>
    /// <remarks>
    /// Under FormC/FormKC/etc normalization the engine scanned a rewritten parse input while the
    /// user typed the original input. This translates parse-input offsets back to original-input
    /// offsets via NormalizedPositionMap so the returned positions line up with what the user
    /// typed. Without normalization (or for a hand-built Symbol with no <see cref="ParseContext"/>),
    /// the backing string is treated as both the parse input and the original input.
    /// </remarks>
    public SourceRange? SourceRange
    {
        get
        {
            if (!TryGetCharSpan(out string parseInput, out int start, out int endExclusive))
                return null;

            string originalInput = _context?.OriginalInput ?? parseInput;
            if (_context == null || ReferenceEquals(originalInput, parseInput))
            {
                // Input and parse input are the same, so there's nothing to translate.
                return new SourceRange(
                    SourcePosition.From(originalInput, start),
                    SourcePosition.From(originalInput, endExclusive));
            }

            var form = _context.NormalizationForm;
            int translatedStart = NormalizedPositionMap.TranslateToOriginal(originalInput, parseInput, start, form);
            int translatedEnd = NormalizedPositionMap.TranslateToOriginal(originalInput, parseInput, endExclusive, form);
            return new SourceRange(
                SourcePosition.From(originalInput, translatedStart),
                SourcePosition.From(originalInput, translatedEnd));
        }
    }

    /// <summary>
    /// The section of the user's original input this Symbol's match covers, returned verbatim, or
    /// the empty string when the Symbol has no associated text (an empty composite, or one whose
    /// leaves don't trace back to a string-backed source).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ToString"/>, which concatenates the text of the leaves present in the
    /// tree and renders it in the normalized form the parser matched, this reaches back to the
    /// original input by character range, so it includes characters matched by <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see>
    /// leaves (the default for Token, Literal, <see cref="InductorParser.Rules.EndOfLine(System.Boolean)">EndOfLine</see>) that
    /// aren't in the tree for <see cref="ToString"/> to render. When the grammar normalized the
    /// input, the parse-input offsets are translated back to the original before the section is
    /// taken, so the result is always a piece of the user's original input.
    /// </remarks>
    public string SourceText
    {
        get
        {
            if (!TryGetCharSpan(out string parseInput, out int start, out int endExclusive))
                return string.Empty;

            string originalInput = _context?.OriginalInput ?? parseInput;
            if (_context == null || ReferenceEquals(originalInput, parseInput))
                return originalInput.Substring(start, endExclusive - start);

            var form = _context.NormalizationForm;
            int translatedStart = NormalizedPositionMap.TranslateToOriginal(originalInput, parseInput, start, form);
            int translatedEnd = NormalizedPositionMap.TranslateToOriginal(originalInput, parseInput, endExclusive, form);
            return originalInput.Substring(translatedStart, translatedEnd - translatedStart);
        }
    }

    /// <summary>
    /// Appends this Symbol's flattened contribution to <paramref name="result"/>: nothing for
    /// <see cref="InductorParser.SyntaxTree.FlattenType.Delete">Delete</see>, the lifted children for <see cref="FlattenType.Flatten">Flatten</see>, and a rebuilt node (or this Symbol unchanged) for
    /// Preserve.
    /// </summary>
    /// <remarks>
    /// Flattens a tree after it has been parsed with <see cref="InductorParser.ParseOptions.PreserveAllSymbols">ParseOptions.PreserveAllSymbols</see> which ignores the default flattening.
    /// Does nothing to a tree that has already been flattened. <see cref="Flatten"/> is the convenience entry point.
    /// </remarks>
    public void FlattenInto(List<Symbol> result)
    {
        switch (FlattenType)
        {
            case FlattenType.Delete:
                return;
            case FlattenType.Flatten:
                // A Flatten leaf has no children to lift, so it adds itself rather than
                // vanishing, matching what the parser does for a Flatten leaf at parse time.
                if (_isLeaf)
                {
                    result.Add(this);
                    return;
                }
                foreach (var child in Children) child.FlattenInto(result);
                return;
            default: // Preserve
                if (_isLeaf)
                {
                    result.Add(this);
                    return;
                }
                var keptChildren = new List<Symbol>();
                foreach (var child in Children) child.FlattenInto(keptChildren);
                // Nothing lifted or dropped, so the rebuild is identical to this.
                if (SameChildren(keptChildren, Children))
                {
                    result.Add(this);
                    return;
                }
                // The rebuild covers the same input section as the original composite: flattening
                // changes which children appear, not what the parser consumed. Pass the recorded
                // span and context through so SourceRange / SourceText still work on the rebuilt
                // Symbol.
                result.Add(FromOwnedChildren(Id, FlattenType.Preserve, keptChildren, _leafChars, _context));
                return;
        }
    }

    /// <summary>
    /// Returns a flattened copy of this subtree: <see cref="InductorParser.SyntaxTree.FlattenType.Delete">Delete</see> nodes dropped and <see cref="FlattenType.Flatten">Flatten</see> nodes' children
    /// lifted into their parents. See <see cref="FlattenInto"/>.
    /// </summary>
    /// <remarks>
    /// Flattens a tree after it has been parsed with <see cref="InductorParser.ParseOptions.PreserveAllSymbols">ParseOptions.PreserveAllSymbols</see> which ignores the default flattening.
    /// Does nothing to a tree that has already been flattened.
    /// </remarks>
    public IReadOnlyList<Symbol> Flatten()
    {
        var list = new List<Symbol>();
        FlattenInto(list);
        return list.Count == 0 ? EmptyChildren : list.AsReadOnly();
    }

    private static IReadOnlyList<Symbol> CopyChildren(IReadOnlyList<Symbol>? children)
    {
        if (children == null)
            return EmptyChildren;

        int count = children.Count;
        if (count == 0)
            return EmptyChildren;

        var copy = new Symbol[count];
        for (int i = 0; i < count; i++)
            copy[i] = children[i];
        return Array.AsReadOnly(copy);
    }

    private static IReadOnlyList<Symbol> WrapOwnedChildren(IReadOnlyList<Symbol>? children)
    {
        if (children == null || children.Count == 0)
            return EmptyChildren;

        if (children is List<Symbol> list)
            return list.AsReadOnly();

        if (children is Symbol[] array)
            return Array.AsReadOnly(array);

        return CopyChildren(children);
    }

    private static bool SameChildren(List<Symbol> rebuilt, IReadOnlyList<Symbol> original)
    {
        if (rebuilt.Count != original.Count) return false;
        for (int i = 0; i < rebuilt.Count; i++)
            if (!ReferenceEquals(rebuilt[i], original[i])) return false;
        return true;
    }
}
