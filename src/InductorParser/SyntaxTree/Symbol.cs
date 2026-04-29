using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using InductorParser;

namespace InductorParser.SyntaxTree;

// A node in the parse tree produced by Rule.Parse. It has wwo shapes:
//
// Composite: carries a list of child Symbols. Used by rules that
//     build structure (AllOf, FirstOf, OneOrMore wrapping content).
//
// Leaf: carries a ReadOnlyMemory<char> pointing into a section of
//     the original input string. Used by rules that match content
//     (Token, Literal, OneOf, ScanUntil). ToString() returns the
//     text it points at. The parse never copies input into a new
//     string.
//
// Because the leaf memory points back into the input, a Symbol can
// also report where in the source it came from: SourceRange returns
// a Start/End pair of SourcePositions covering the same char / rune /
// grapheme / line / column units ParseResult uses for error positions.
public sealed class Symbol
{
    // Shared empty array for the Children field on leaf symbols. Array.Empty<T>()
    // already returns a singleton, so this isn't saving an allocation. It
    // just makes it clearer what is going on
    private static readonly IReadOnlyList<Symbol> EmptyChildren = Array.Empty<Symbol>();

    // Shared Symbol a Rule.TryParse returns in place of a real one when
    // the rule's effective FlattenType is Delete. Consumers like AllOfRule
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
    // if the caller only keeps a reference to a small subtree. That is
    // almost always what you want for parser output (the tree conceptually
    // represents the input, and the input usually stays around anyway),
    // but if you parse a 100 MB document and then keep a 10-character
    // Symbol out of it while dropping everything else, the entire 100 MB
    // input stays GC-rooted until that Symbol is also released. Call
    // ToString() and keep just the resulting string if you want to
    // detach from the original input.
    private readonly ReadOnlyMemory<char> _leafChars;
    private readonly bool _isLeaf;

    // Engine-internal accessor used by StateMachineParser's OutputOps
    // walkers when a Prebuilt op (BridgeToRecursive output) lands inside
    // a match span. Combined with MemoryMarshal.TryGetString, callers
    // can recover the leaf's offset into the original input string and
    // contribute its bounds to the enclosing match's (offset, length).
    // Empty for composite symbols.
    internal ReadOnlyMemory<char> LeafMemory => _leafChars;

    public SymbolId Id { get; }
    public FlattenType FlattenType { get; }
    public IReadOnlyList<Symbol> Children { get; }

    public Symbol(SymbolId id, FlattenType flattenType, IReadOnlyList<Symbol>? children)
    {
        Id = id;
        FlattenType = flattenType;
        // Collapse both null and empty to the shared Array.Empty<Symbol>()
        // singleton. Callers can pass null (easy) or hand off a list they
        // allocated eagerly that ended up empty. The tree stores only the
        // singleton in either case.
        Children = (children == null || children.Count == 0) ? EmptyChildren : children;
        _leafChars = ReadOnlyMemory<char>.Empty;
        _isLeaf = false;
    }

    public Symbol(SymbolId id, FlattenType flattenType, ReadOnlyMemory<char> leafChars)
    {
        Id = id;
        FlattenType = flattenType;
        Children = EmptyChildren;
        _leafChars = leafChars;
        _isLeaf = true;
    }

    // ToString renders the text actually present in the tree: for leaves,
    // the captured text, and for composites, the concatenated text of their
    // children. On the default parse path, FlattenType.Delete rules
    // are gone (filtered during parse) and FlattenType.Flatten
    // wrappers have had their children lifted into the parent, so
    // their own wrapper does not appear in the tree shape. The
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

    // Does this specific Symbol correspond to the given rule? The common
    // tree-walker dispatch pattern ("is this a Number node? a String
    // node?") reads more naturally as symbol.Is(Rule) than as
    // symbol.Id == rule.Id, and hides the Id plumbing from consumer
    // code. Unlike Find, this is a single-node check, no tree walk.
    public bool Is(Rule rule) => Id == rule.Id;

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

    // The span in the original input string that this Symbol consumed,
    // expressed as a SourceRange. Returns null when the Symbol has no
    // associated text (an empty composite, or a composite whose leaves
    // have all been Delete-flattened away). Both Start and End come
    // back as full SourcePositions, so the caller can read line/column,
    // grapheme index, etc. without a separate conversion call.
    //
    // Implementation: leaves carry a ReadOnlyMemory<char> that points
    // into the original input string. MemoryMarshal.TryGetString
    // recovers the underlying string and the leaf's offset. For a
    // composite, walk to the leftmost and rightmost leaves and stitch
    // the start of one to the end of the other.
    public SourceRange? SourceRange
    {
        get
        {
            Symbol? firstLeaf = FindFirstLeafWithText(this);
            Symbol? lastLeaf = FindLastLeafWithText(this);
            if (firstLeaf == null || lastLeaf == null) return null;

            if (!MemoryMarshal.TryGetString(firstLeaf._leafChars, out string? firstInput, out int firstStart, out _))
                return null;
            if (!MemoryMarshal.TryGetString(lastLeaf._leafChars, out string? lastInput, out int lastStart, out int lastLength))
                return null;
            // Both leaves should reference the same input string. If not,
            // we have no meaningful range to report.
            if (!ReferenceEquals(firstInput, lastInput)) return null;

            return new SourceRange(
                SourcePosition.From(firstInput, firstStart),
                SourcePosition.From(firstInput, lastStart + lastLength));
        }
    }

    private static Symbol? FindFirstLeafWithText(Symbol symbol)
    {
        if (symbol._isLeaf)
            return symbol._leafChars.IsEmpty ? null : symbol;
        foreach (var child in symbol.Children)
        {
            var leaf = FindFirstLeafWithText(child);
            if (leaf != null) return leaf;
        }
        return null;
    }

    private static Symbol? FindLastLeafWithText(Symbol symbol)
    {
        if (symbol._isLeaf)
            return symbol._leafChars.IsEmpty ? null : symbol;
        for (int i = symbol.Children.Count - 1; i >= 0; i--)
        {
            var leaf = FindLastLeafWithText(symbol.Children[i]);
            if (leaf != null) return leaf;
        }
        return null;
    }

    public void FlattenInto(List<Symbol> result)
    {
        switch (FlattenType)
        {
            case FlattenType.Delete:
                return;
            case FlattenType.Flatten:
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
                result.Add(new Symbol(Id, FlattenType.Preserve, keptChildren));
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
