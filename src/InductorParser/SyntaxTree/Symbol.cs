using System;
using System.Collections.Generic;
using System.Text;
using InductorParser;

namespace InductorParser.SyntaxTree;

public sealed class Symbol
{
    // Shared sentinel for the Children field on leaf symbols. Array.Empty<T>()
    // already returns a singleton, so this isn't saving an allocation; it's
    // just a named alias that reads better than "Array.Empty<Symbol>()" at
    // the use site inside the leaf constructor below.
    private static readonly IReadOnlyList<Symbol> EmptyChildren = Array.Empty<Symbol>();

    // Parse-time sentinel a Rule.TryParse returns in place of a real Symbol
    // when the rule's effective FlattenType is Delete. Consumers (AndRule /
    // OrRule / BetweenInclusiveRule) filter this before it reaches the
    // parent's Children list, so Delete-typed rules never contribute a
    // wrapper Symbol, a matched-list allocation, or a leaf-per-rune entry
    // to the final tree.
    //
    // Non-null so the "TryParse returned non-null" success signal still
    // works for Delete rules. Never stored as a child of any real Symbol;
    // consumers that see it treat it as "matched successfully, contributes
    // nothing."
    public static readonly Symbol Discarded = new Symbol(default, FlattenType.Delete, ReadOnlyMemory<char>.Empty);

    // Leaf symbols hold a slice (ReadOnlyMemory<char>) into the original
    // input string rather than a copied substring. This is the deferred-
    // materialization path: during parsing we never allocate a per-leaf
    // string, only when somebody calls ToString do we materialize the
    // text.
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

    public SymbolId Id { get; }
    public FlattenType FlattenType { get; }
    public IReadOnlyList<Symbol> Children { get; }

    public Symbol(SymbolId id, FlattenType flattenType, IReadOnlyList<Symbol> children)
    {
        Id = id;
        FlattenType = flattenType;
        Children = children;
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
    // the captured slice; for composites, the concatenated text of their
    // kept children. Delete-typed rules that were filtered at parse time
    // are not in the tree, so their text does not appear here either.
    // Callers who want the full matched input should either keep the
    // string they passed to Parse, or enable
    // ParseOptions.PreserveFlattenWrappers to keep every grammar node
    // (including Delete-typed ones) in the tree.
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

    public IEnumerable<Symbol> FindAll(Rule rule) => FindAll(rule.Id);

    public IEnumerable<Symbol> FindAll(SymbolId id)
    {
        if (Id == id) yield return this;
        foreach (var child in Children)
            foreach (var found in child.FindAll(id))
                yield return found;
    }

    public IEnumerable<Symbol> Walk()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var descendant in child.Walk())
                yield return descendant;
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
            default: // None
                if (_isLeaf)
                {
                    result.Add(this);
                    return;
                }
                var keptChildren = new List<Symbol>();
                foreach (var child in Children) child.FlattenInto(keptChildren);
                result.Add(new Symbol(Id, FlattenType.None, keptChildren));
                return;
        }
    }

    public IReadOnlyList<Symbol> Flatten()
    {
        var list = new List<Symbol>();
        FlattenInto(list);
        return list;
    }
}
