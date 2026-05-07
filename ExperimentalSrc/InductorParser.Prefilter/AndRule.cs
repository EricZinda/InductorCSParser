using System.Collections.Generic;
using System.Text;

namespace InductorParser.Prefilter;

// And's contribution: every child must match in sequence, so any
// single child's required literal is required by the And. A run of
// consecutive children that each have a fixed concatenable text
// contributes a single longer literal (Literal("# ") followed by
// OneOf("Nn") OneOf("Oo")... gives "# noqa" case-insensitive). The
// walker picks the longest candidate seen across all runs and any
// recursed sub-literals, mirroring what regex engines do when they
// extract a "best literal" from a pattern.
internal static class AndRulePrefilter
{
    internal static (string Text, bool IgnoreCase)? ComputeRequiredLiteral(AndRule rule)
    {
        StringBuilder? run = null;
        bool runIgnoreCase = false;
        (string Text, bool IgnoreCase)? best = null;

        foreach (var child in rule.Children)
        {
            var concat = RuleExtensions.ComputeConcatenableText(child);
            if (concat != null && concat.Value.Text.Length > 0)
            {
                run ??= new StringBuilder();
                run.Append(concat.Value.Text);
                runIgnoreCase |= concat.Value.IgnoreCase;
                continue;
            }

            // Non-concatenable child breaks the run. Finalize whatever
            // we accumulated and consider its standalone required
            // literal as a separate candidate.
            FinalizeRun(ref run, ref runIgnoreCase, ref best);

            var childRequired = RuleExtensions.ComputeRequiredLiteral(child);
            if (childRequired != null)
                Maybe(ref best, childRequired.Value);
        }

        FinalizeRun(ref run, ref runIgnoreCase, ref best);
        return best;
    }

    // Surface a multi-literal alternative if any single child has one.
    // The motivating shape is And(Or(L1, L2, L3, L4), other-stuff)
    // where the Or has all-literal branches: any successful match
    // of the And still passes through the Or and so contains one
    // of {L1, L2, L3, L4}. If the And has a long single shared literal
    // already (the And's ComputeRequiredLiteral picks it up), callers
    // typically prefer that and never reach this method; this is for
    // the cases where no single literal is derivable but a child's set
    // is.
    internal static IReadOnlyList<(string Text, bool IgnoreCase)>? ComputeRequiredLiteralAlternatives(AndRule rule)
    {
        foreach (var child in rule.Children)
        {
            var childSet = RuleExtensions.ComputeRequiredLiteralAlternatives(child);
            if (childSet != null && childSet.Count > 0)
                return childSet;
        }
        return null;
    }

    // Only concatenable when every child is concatenable; otherwise
    // we can't promise a fixed-length contribution.
    internal static (string Text, bool IgnoreCase)? ComputeConcatenableText(AndRule rule)
    {
        var builder = new StringBuilder();
        bool ignoreCase = false;
        foreach (var child in rule.Children)
        {
            var concat = RuleExtensions.ComputeConcatenableText(child);
            if (concat == null) return null;
            builder.Append(concat.Value.Text);
            ignoreCase |= concat.Value.IgnoreCase;
        }
        return builder.Length == 0 ? null : (builder.ToString(), ignoreCase);
    }

    private static void FinalizeRun(ref StringBuilder? run, ref bool runIgnoreCase, ref (string Text, bool IgnoreCase)? best)
    {
        if (run == null || run.Length == 0)
        {
            run = null;
            runIgnoreCase = false;
            return;
        }
        Maybe(ref best, (run.ToString(), runIgnoreCase));
        run = null;
        runIgnoreCase = false;
    }

    private static void Maybe(ref (string Text, bool IgnoreCase)? best, (string Text, bool IgnoreCase) candidate)
    {
        if (candidate.Text.Length == 0) return;
        if (best == null || candidate.Text.Length > best.Value.Text.Length)
            best = candidate;
    }
}
