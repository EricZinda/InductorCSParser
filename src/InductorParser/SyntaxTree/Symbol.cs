using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using InductorParser;
using InductorParser.Lexing;

namespace InductorParser.SyntaxTree;

// A node in the parse tree produced by Rule.Parse. It has wwo shapes:
//
// Composite: carries a list of child Symbols. Used by rules that
//     build structure (And, Or, OneOrMore wrapping content).
//
// Leaf: carries a ReadOnlyMemory<char> pointing into a section of
//     the original input string. Used by rules that match content
//     (Token, Literal, OneOf, ScanUntil). ToString() returns the
//     text it points at. The parse never copies input into a new
//     string.
//
// Because the leaf memory points back into the input, a Symbol can
// also report where in the source it came from: SourceRange returns
// a Start/End pair of SourcePositions covering the same char / token
// / line / column units ParseResult uses for error positions.
public sealed class Symbol
{
    // Shared empty array for the Children field on leaf symbols. Array.Empty<T>()
    // already returns a singleton, so this isn't saving an allocation. It
    // just makes it clearer what is going on
    private static readonly IReadOnlyList<Symbol> EmptyChildren = Array.Empty<Symbol>();

    // Shared Symbol a Rule.TryParse returns in place of a real one when
    // the rule's effective FlattenType is Delete. Consumers like AndRule
    // filter it out before it reaches the
    // parent's Children list, so rules with FlattenType.Delete never
    // contribute a Discarded Symbol to the final tree.
    //
    // This is so that rules have something non-null to return from TryParse()
    // to indicate success. Null means failure. Never stored as a child of any real Symbol.
    // consumers that see it treat it as "matched successfully, contributes
    // nothing."
    public static readonly Symbol Discarded = new Symbol(default, FlattenType.Delete, ReadOnlyMemory<char>.Empty);

    // Leaf symbols hold a ReadOnlyMemory<char> into the original
    // input string rather than a copied substring. This allows us to
    // never allocate a string during parsing, only when somebody calls 
    // ToString do we materialize the text.
    //
    // This does involve a tradeoff: a ReadOnlyMemory<char>
    // keeps its backing string alive for as long as the Memory itself is
    // reachable. So any tree of Symbols that holds leaves pointing into a
    // parsed input string will keep that whole input string alive, even
    // if the caller only keeps a reference to a small subtree. That's
    // almost always what you want for parser output (the tree conceptually
    // represents the input, and the input usually stays around anyway),
    // but if you parse a 100 MB document and then keep a 10-character
    // Symbol out of it while dropping everything else, the entire 100 MB
    // input stays GC-rooted until that Symbol is also released. Call
    // ToString() and keep just the resulting string if you want to
    // detach from the original input.
    private readonly ReadOnlyMemory<char> _leafChars;
    private readonly bool _isLeaf;
    // Per-parse context the engine stamps onto every Symbol it builds.
    // Carries (originalInput, parseInput, normalizationForm) so Symbol
    // can translate its parseInput-relative _leafChars span back to
    // original-input coordinates for SourceRange / SourceText. 
    private readonly ParseContext? _context;

    // Engine-internal accessor used by alternative-evaluator
    // implementations to recover a leaf symbol's bounds inside a match
    // span. Combined with MemoryMarshal.TryGetString, callers can
    // recover the leaf's offset into the original input string and
    // contribute its bounds to the enclosing match's (offset, length).
    // Empty for composite symbols.
    internal ReadOnlyMemory<char> LeafMemory => _leafChars;

    // True when this Symbol is a leaf (carries text in _leafChars) rather
    // than a composite (carries child Symbols). A composite with an empty
    // Children list still reports false: zero children is not the same
    // shape as a leaf, and ToString / FlattenInto treat the two
    // differently. Used by composites that lift a child's content to tell
    // "lift the child's children" (composite) apart from "the child is
    // itself the content" (leaf).
    internal bool IsLeaf => _isLeaf;

    public SymbolId Id { get; }
    public FlattenType FlattenType { get; }
    public IReadOnlyList<Symbol> Children { get; }

    // Composite constructor. consumedSpan covers the section of
    // parseInput the rule matched, *including* any FlattenType.Delete
    // leading or trailing children that get filtered out of Children
    // before the Symbol is observed (example: And(Token('-').Preserve(),
    // Literal("inf")) parsed against "-inf" has only the "-" leaf in
    // Children, but the recorded span covers all 4 chars so SourceRange
    // / SourceText report the full match). For zero-width composites
    // (Peek, Not, Eof, an Optional that matched zero times via the
    // empty-match shortcut), pass a zero-length memory at the rule's
    // anchor offset so callers still get a position.
    public Symbol(SymbolId id, FlattenType flattenType, IReadOnlyList<Symbol>? children, ReadOnlyMemory<char> consumedSpan = default, ParseContext? context = null)
    {
        Id = id;
        FlattenType = flattenType;
        // Collapse both null and empty to the shared Array.Empty<Symbol>()
        // singleton. Callers can pass null (easy) or hand off a list they
        // allocated eagerly that ended up empty. The tree stores only the
        // singleton in either case.
        Children = (children == null || children.Count == 0) ? EmptyChildren : children;
        _leafChars = consumedSpan;
        _isLeaf = false;
        _context = context;
    }

    // Leaf overload
    public Symbol(SymbolId id, FlattenType flattenType, ReadOnlyMemory<char> leafChars, ParseContext? context = null)
    {
        Id = id;
        FlattenType = flattenType;
        Children = EmptyChildren;
        _leafChars = leafChars;
        _isLeaf = true;
        _context = context;
    }

    // Does this specific Symbol correspond to the given rule? The common
    // tree-walker dispatch pattern ("is this a Number node? a String
    // node?") reads more naturally as symbol.Is(Rule) than as
    // symbol.Id == rule.Id, and hides the Id plumbing from consumer
    // code. Unlike Find, this is a single-node check, no tree walk.
    public bool Is(Rule rule) => Id == rule.Id;

    // String-named variant of Is, for tree walkers that prefer to
    // dispatch on the name the grammar gave the rule via .As("name")
    // rather than hold a reference to the Rule object. Resolves the
    // Symbol's id through the grammar reachable from the parse's
    // ParseContext, so it works only on Symbols that came out of a
    // real Rule.Parse call. Hand-built Symbols (no context) and
    // Symbols whose id maps to an unnamed rule both return false.
    //
    // Use this when you want a typed-AST projection that doesn't
    // require one public static Rule field per named production on
    // the grammar class. The lookup walks a per-grammar name index
    // built lazily on first call to Rule.NameOf / Rule.IdOf, so it's
    // an O(1) string compare per call after the first.
    public bool Is(string ruleName)
    {
        if (ruleName == null) return false;
        string? actual = Name;
        return actual != null && actual == ruleName;
    }

    // The grammar-supplied name of the rule that produced this Symbol
    // — that is, the string the rule was constructed with via
    // .As("name"). Returns null when:
    //
    //   * the Symbol was hand-built with no ParseContext, or
    //   * the rule has no .As(string) name (anonymous composites
    //     like an inline And(...) inside another rule).
    //
    // For rune-leaf Symbols (the Id is a Unicode scalar value), the
    // name is the rune's text by default — Token('a').As("aChar") on
    // an 'a' leaf returns "aChar"; an anonymous Token('a') leaf
    // returns "a".
    public string? Name => _context?.GrammarRoot?.NameOf(Id);

    // Depth-first search for the first Symbol whose Id matches. Returns
    // null if nothing matches. Use when you expect exactly one match
    // (e.g. a named rule that appears once at a known position in the
    // grammar).
    public Symbol? Find(Rule rule) => Find(rule.Id);

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

    // Depth-first search that yields every matching Symbol. Use when
    // the rule can appear multiple times (repetitions, alternations,
    // recursive grammars).
    public IEnumerable<Symbol> FindAll(Rule rule) => FindAll(rule.Id);

    public IEnumerable<Symbol> FindAll(SymbolId id)
    {
        if (Id == id) yield return this;
        foreach (var child in Children)
            foreach (var found in child.FindAll(id))
                yield return found;
    }

    // Depth-first walk that yields every Symbol in the tree, starting
    // with this one. Use when you want to inspect or transform every
    // node regardless of id.
    public IEnumerable<Symbol> Walk()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var descendant in child.Walk())
                yield return descendant;
    }

    // ToString renders the text actually present in the tree: for leaves,
    // the captured text, and for composites, the concatenated text of their
    // children. On the default parse path, FlattenType.Delete rules
    // are gone (filtered during parse) and FlattenType.Flatten
    // wrappers have had their children lifted into the parent, so
    // their own wrapper doesn't appear in the tree shape. The
    // characters under them do, through their surviving
    // FlattenType.Preserve or leaf descendants. Callers who want to
    // rebuild the exact input verbatim should either keep the string
    // they passed to Parse, or enable ParseOptions.PreserveAllSymbols
    // to keep every grammar node (including FlattenType.Delete ones)
    // in the tree.
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

    /// <summary>
    /// Return the UTF-8 byte length of this symbol's rendered text without
    /// materializing that text as an intermediate string.
    /// </summary>
    public long GetUtf8ByteCount()
    {
        if (_isLeaf) return Encoding.UTF8.GetByteCount(_leafChars.Span);

        long total = 0;
        foreach (var child in Children)
            total += child.GetUtf8ByteCount();
        return total;
    }

    // Recover parseInput-relative bounds for this Symbol's matched
    // span. Both leaves and composites store the bounds in _leafChars
    //
    // Returns false for any Symbol without a populated _leafChars:
    // a hand-built composite from external code (e.g. a test fixture
    // that wired children together for a post-hoc-flatten check)

    // SourceRange / SourceText consume these and translate the
    // offsets to the caller's original-input coordinates.
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

    // The user's original-input range this Symbol's match covers.
    // Returns null when the Symbol has no associated text (an empty
    // composite, one whose children's leaves don't trace back to a
    // string-backed source, or a default-constructed Symbol).
    //
    // Under FormC/FormKC/etc normalization, the engine scanned a
    // rewritten parseInput while the user typed the original input.
    // This property translates parseInput offsets back to original-
    // input offsets via NormalizedPositionMap so the returned positions
    // line up with what the user typed. For grammars without
    // normalization (or hand-built Symbols with no ParseContext), the
    // _leafChars backing string is treated as both parseInput and
    // originalInput.
    public SourceRange? SourceRange
    {
        get
        {
            if (!TryGetCharSpan(out string parseInput, out int start, out int endExclusive))
                return null;

            string originalInput = _context?.OriginalInput ?? parseInput;
            if (_context == null || ReferenceEquals(originalInput, parseInput))
            {
                // No need to map through normalization, input and parseinput are the same
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

    // The substring of the user's original input this Symbol's match
    // covers. Returned verbatim — bypasses the per-child flatten-then-
    // render walk that ToString does, so it includes characters
    // matched by FlattenType.Delete leaves (the default for Token,
    // Literal, EndOfLine) that ToString would drop.
    //
    // Returns the empty string when the Symbol has no associated text
    // (an empty composite, or one whose leaves don't trace back to a
    // string-backed source). The non-empty result is always a section
    // of the user's original input, even when the grammar normalized
    // it: the parseInput offsets are translated back to the original
    // before slicing.
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

    public void FlattenInto(List<Symbol> result)
    {
        switch (FlattenType)
        {
            case FlattenType.Delete:
                return;
            case FlattenType.Flatten:
                // A Flatten leaf has no children to lift, so it bubbles up
                // as itself. Mirrors the parse-time Flatten branch in
                // GraphemeRule / OneOfRule etc., which adds the leaf to the
                // parent's outputSymbols rather than dropping it. Without
                // this gate the leaf's text would silently disappear from
                // the post-hoc Flatten output, breaking the
                // PreserveAllSymbols-then-Flatten round-trip for any
                // grammar that uses .Flatten(FlattenType.Flatten) on a leaf
                // rule (Rules.Float()'s leading-minus token is the canonical
                // case).
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
                // if nothing was lifted out or dropped, the rebuild is identical to `this`
                if (SameChildren(keptChildren, Children))
                {
                    result.Add(this);
                    return;
                }
                // The rebuild covers the same input section as the original
                // composite — flattening changes which children appear in
                // the tree, not what the parser consumed. Pass the
                // recorded span AND the context through so SourceRange /
                // SourceText still work on the rebuilt Symbol.
                result.Add(new Symbol(Id, FlattenType.Preserve, keptChildren, _leafChars, _context));
                return;
        }
    }

    public IReadOnlyList<Symbol> Flatten()
    {
        var list = new List<Symbol>();
        FlattenInto(list);
        return list;
    }
    
    private static bool SameChildren(List<Symbol> rebuilt, IReadOnlyList<Symbol> original)
    {
        if (rebuilt.Count != original.Count) return false;
        for (int i = 0; i < rebuilt.Count; i++)
            if (!ReferenceEquals(rebuilt[i], original[i])) return false;
        return true;
    }
}
