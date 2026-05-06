namespace InductorParser.Prefilter;

// GraphemeRule (the user-facing Token(...) factory) consumes exactly
// one fixed grapheme cluster. The expected text is both the required
// literal and a concatenable fixed-text contribution. Case-sensitive.
// The accessor on the rule is named LoweringExpected because the
// state-machine lowerer was the original consumer of the read-only
// view of the expected text; the prefilter is now a second consumer
// of the same accessor.
internal static class GraphemeRulePrefilter
{
    internal static (string Text, bool IgnoreCase)? ComputeRequiredLiteral(GraphemeRule rule) =>
        (rule.LoweringExpected, false);

    internal static (string Text, bool IgnoreCase)? ComputeConcatenableText(GraphemeRule rule) =>
        (rule.LoweringExpected, false);
}
