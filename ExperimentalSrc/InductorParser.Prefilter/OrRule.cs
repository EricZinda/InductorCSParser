using System.Collections.Generic;

namespace InductorParser.Prefilter;

// Or matches exactly one branch on success. If every branch has
// at least one required literal (single or its own multi-literal set),
// the union of those literals is required by Or as a whole: any
// successful match is guaranteed to contain one of them as a substring.
// If even one branch has no analyzable literal, the union doesn't hold
// and we return null.
internal static class OrRulePrefilter
{
    internal static IReadOnlyList<(string Text, bool IgnoreCase)>? ComputeRequiredLiteralAlternatives(OrRule rule)
    {
        if (rule.Children.Count == 0) return null;
        var union = new List<(string Text, bool IgnoreCase)>();
        foreach (var child in rule.Children)
        {
            var childSet = RuleExtensions.ComputeRequiredLiteralAlternatives(child);
            if (childSet != null && childSet.Count > 0)
            {
                union.AddRange(childSet);
                continue;
            }
            var single = RuleExtensions.ComputeRequiredLiteral(child);
            if (single == null || single.Value.Text.Length == 0)
                return null;
            union.Add(single.Value);
        }
        return union.Count == 0 ? null : union;
    }
}
