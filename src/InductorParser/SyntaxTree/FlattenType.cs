namespace InductorParser.SyntaxTree;

/// <summary>
/// Declares what a rule's successful match contributes to the surrounding parse tree.
/// </summary>
/// <remarks>
/// Set during grammar construction with <see cref="Rule.Flatten(FlattenType)"/> or its
/// shorthands <see cref="Rule.Preserve()"/> / <see cref="Rule.Delete()"/> / <see cref="Rule.Flatten()"/>.
/// Every rule has a default FlattenType that varies by rule class.
/// </remarks>
public enum FlattenType
{
    /// <summary>
    /// Keep the rule's match as a Symbol in the tree, with its children nested underneath it.
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
    /// still includes it, since SourceText reaches back to the original input by character range
    /// rather than walking the tree.
    /// </remarks>
    Delete,

    /// <summary>
    /// Lift the rule's matched children up into the enclosing rule's children list, as if this
    /// rule weren't there. The rule's own Symbol is removed.
    /// </summary>
    /// <remarks>
    /// Use this for structural rules that exist only to combine other rules (a repetition, an
    /// alternation, a grouping) where the rule's Symbol would add a level of nesting the
    /// consumer doesn't want.
    /// </remarks>
    Flatten
}
