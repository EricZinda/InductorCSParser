namespace InductorParser.Prefilter;

// GraphemeRule (the user-facing Token(...) factory) consumes exactly
// one fixed grapheme cluster. The expected text is both the required
// literal and a concatenable fixed-text contribution. Case-sensitive.
internal static class GraphemeRulePrefilter
{
    internal static (string Text, bool IgnoreCase)? ComputeRequiredLiteral(GraphemeRule rule) =>
        (rule.ExpectedText!, false);

    internal static (string Text, bool IgnoreCase)? ComputeConcatenableText(GraphemeRule rule) =>
        (rule.ExpectedText!, false);
}
