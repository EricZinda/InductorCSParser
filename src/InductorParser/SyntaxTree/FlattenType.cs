namespace InductorParser.SyntaxTree;

// Declares what a rule's successful match contributes to the surrounding
// parse tree. Set via .Flatten(FlattenType.X) on the rule during grammar
// construction. Every rule has a FlattenType but defaults differ per rule
// class (most leaves default to Delete, most composites to Flatten).
public enum FlattenType
{
    // Keep the rule's match as a Symbol in the tree, with its
    // children nested underneath it. Use this for grammar nodes the
    // caller wants to find, name, or walk as a unit — a "word", a
    // "function-definition", an "expression".
    Preserve,

    // Drop the rule's match entirely. No Symbol enters the tree and
    // no child text survives through this rule. Use this for syntax
    // that the grammar has to assert is present but the consumer
    // doesn't care about — punctuation, keywords, whitespace.
    Delete,

    // Lift the rule's matched children up into the enclosing rule's
    // children list, as if this rule weren't there. The rule's own
    // wrapper Symbol is removed. Use this for structural rules that
    // exist only to combine other rules — a repetition, an
    // alternation, a grouping — where the wrapper would add a level
    // of nesting the consumer doesn't want.
    Flatten
}
