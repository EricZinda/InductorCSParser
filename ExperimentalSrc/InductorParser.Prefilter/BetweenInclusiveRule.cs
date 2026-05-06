using System.Text;

namespace InductorParser.Prefilter;

// BetweenInclusive's contribution: when the lower bound forces at
// least one inner match, the inner's required literal flows through
// because every match contains at least one inner match's text.
// AtLeast == 0 means the rule can succeed without inner ever firing,
// so we can't promise the literal will appear and have to return null.
//
// For concatenable fixed-text, only Exactly(n, fixedTextChild) gives a
// fixed-length contribution (n copies of the inner's concatenable
// text). Loose lower or upper bounds vary in length and break
// concatenation.
internal static class BetweenInclusiveRulePrefilter
{
    internal static (string Text, bool IgnoreCase)? ComputeRequiredLiteral(BetweenInclusiveRule rule)
    {
        if (rule.AtLeast < 1) return null;
        return RuleExtensions.ComputeRequiredLiteral(rule.Children[0]);
    }

    internal static (string Text, bool IgnoreCase)? ComputeConcatenableText(BetweenInclusiveRule rule)
    {
        if (rule.AtLeast != rule.AtMost || rule.AtLeast == 0) return null;
        var inner = RuleExtensions.ComputeConcatenableText(rule.Children[0]);
        if (inner == null || inner.Value.Text.Length == 0) return null;
        // Cap the repetition at a small budget to avoid blowing up
        // memory on Exactly(huge, ...) edge cases. Twenty is plenty
        // for realistic prefilter literals.
        if (rule.AtLeast > 20) return null;
        var builder = new StringBuilder(inner.Value.Text.Length * rule.AtLeast);
        for (int i = 0; i < rule.AtLeast; i++)
            builder.Append(inner.Value.Text);
        return (builder.ToString(), inner.Value.IgnoreCase);
    }
}
