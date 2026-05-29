namespace InductorParser.SyntaxTree;

/// <summary>
/// Declares what Symbols a rule's successful match contribute to the surrounding parse tree.
/// </summary>
/// <remarks>
/// Set during grammar construction with <see cref="Rule.Flatten(FlattenType)"/> or its
/// shorthands: <see cref="Rule.Preserve()"/>, <see cref="Rule.Delete()"/>, <see cref="Rule.Flatten()"/>.
/// Every rule has a default FlattenType that varies by rule class.
/// </remarks>
public enum FlattenType
{
    /// <summary>
    /// Keep the rule's match as a Symbol in the tree with its children nested underneath it.
    /// </summary>
    /// <remarks>
    /// Use this for grammar nodes the caller wants to find, name, or walk as a unit: a "word",
    /// a "function-definition", an "expression".
    /// </remarks>
    Preserve,

    /// <summary>
    /// Drop the rule's match entirely. No Symbol enters the tree and no child text survives
    /// through this rule.
    /// </summary>
    /// <remarks>
    /// Use this for syntax the grammar has to assert is present but the consumer doesn't care
    /// about: punctuation, keywords, whitespace. The dropped text doesn't appear in
    /// <see cref="Symbol.ToString()"/>, but an enclosing Symbol's <see cref="Symbol.SourceText"/>
    /// still includes it since SourceText reaches back to the original input by character range
    /// rather than walking the tree.
    /// </remarks>
    Delete,

    /// <summary>
    /// Pass the rule's matched content up to the enclosing rule, dropping the rule's own
    /// Symbol. What "content" means depends on the rule's shape: a composite's content is
    /// its children (which get lifted into the parent's children list as if this rule weren't
    /// there). A leaf's content is the leaf text itself (which is kept, since it has no separate
    /// children to lift past it).
    /// </summary>
    /// <remarks>
    /// Use this for structural rules that exist only to combine other rules (a repetition, an
    /// alternation, a grouping) where the rule's Symbol would add a level of nesting the
    /// consumer doesn't want.
    /// <para>
    /// On a leaf, <see cref="Flatten"/> and <see cref="Preserve"/> behave identically: the leaf
    /// surfaces in the parent's children list with its own Id and matched text either way. A
    /// leaf has no children for Flatten to lift past, so the FlattenType tag has no effect on a
    /// leaf Symbol in the tree.
    /// </para>
    /// </remarks>
    Flatten
}
