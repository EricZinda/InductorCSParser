using System;
using System.Collections.Generic;

namespace InductorParser.StateMachine;

// Walks the Rule tree the StateMachine Lowerer is about to compile and
// builds a side table of RuleStartRequirements (the "can I skip this rule?"
// dispatch metadata) plus a set of rules whose subtree carries a .WithError.
//
// The side table is keyed by Rule reference. The Lowerer reads from these
// two collections instead of off Rule itself: the recursive evaluator in
// src/InductorParser/ doesn't carry this metadata anymore, since the
// shortcut it powered was removed there.
//
// One walk, post-order, with cycle detection. A rule's RuleStartRequirements
// reads its children's, so children are computed first. When a cycle is hit
// (LateBoundRule pointing back into an Or that contains it), the in-progress
// rule keeps the pessimistic default (Universe, Sometimes, MustBeIn). That's
// safe: the Lowerer's shortcut sites bail when the requirements look
// pessimistic, so the dispatch falls through to the general path.
//
// Mirrors the per-subclass logic that used to live as ComputeRuleStart
// overrides on each Rule subclass. Switch-on-type instead of virtual
// dispatch, so the rule-side stays free of any StateMachine concept.
internal static class RuleStartAnalysis
{
    internal sealed class Result
    {
        public Dictionary<Rule, RuleStartRequirements> Requirements { get; } =
            new(ReferenceComparer<Rule>.Instance);

        // Rules whose subtree (including themselves) carries any
        // .WithError(...) message. The StateMachine's shortcut sites
        // consult this on a child before bypassing TryParse so a
        // WithError on any descendant still gets a chance to record at
        // the deepest-failure slot. Pessimistic default (a rule not in
        // the set is treated as having no error in its subtree).
        public HashSet<Rule> HasErrorMessageInSubtree { get; } =
            new(ReferenceComparer<Rule>.Instance);

        public RuleStartRequirements Get(Rule rule) =>
            Requirements.TryGetValue(rule, out var requirements)
                ? requirements
                : RuleStartRequirements.Pessimistic;

        public bool HasError(Rule rule) => HasErrorMessageInSubtree.Contains(rule);
    }

    public static Result Build(Rule root)
    {
        var result = new Result();
        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        var computing = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        Walk(root, result, visited, computing);
        return result;
    }

    private static void Walk(Rule r, Result result, HashSet<Rule> visited, HashSet<Rule> computing)
    {
        if (visited.Contains(r)) return;
        if (!computing.Add(r)) return; // cycle: leave at pessimistic default

        foreach (var child in r.Children)
            Walk(child, result, visited, computing);

        var requirements = ComputeFor(r, result);
        Validate(r, requirements);
        result.Requirements[r] = requirements;

        bool hasError = r.ErrorMessage != null;
        foreach (var child in r.Children)
        {
            if (result.HasErrorMessageInSubtree.Contains(child))
            {
                hasError = true;
                break;
            }
        }
        if (hasError) result.HasErrorMessageInSubtree.Add(r);

        computing.Remove(r);
        visited.Add(r);
    }

    private static void Validate(Rule r, RuleStartRequirements requirements)
    {
        if (requirements.Advance == Advance.Never && !requirements.FirstConsumedTokens.IsEmpty)
            throw new InvalidOperationException(
                $"Rule '{r.GetType().Name}' produced Advance.Never with non-empty " +
                $"FirstConsumedTokens. A rule that never advances can't have a set " +
                $"of possible first-consumed tokens. Use TokenSet.Empty for " +
                $"FirstConsumedTokens when Advance is Never.");
        if (requirements.Polarity == Polarity.MustNotBeIn && requirements.Advance != Advance.Always)
            throw new InvalidOperationException(
                $"Rule '{r.GetType().Name}' produced Polarity.MustNotBeIn with " +
                $"Advance.{requirements.Advance}. MustNotBeIn semantics (peek IS in fail-set " +
                $"=> skip) require Advance.Always. Use MustBeIn or Advance.Always.");
    }

    // Dispatched mirror of the per-subclass ComputeRuleStart overrides that
    // used to live on Rule subclasses. Each case derives the rule's
    // RuleStartRequirements from its own data and (for composites) its
    // already-walked children's requirements via `result.Get`.
    private static RuleStartRequirements ComputeFor(Rule rule, Result result)
    {
        switch (rule)
        {
            case AndRule and:
                return RuleStartRequirements.MatchesAllOf(ChildRequirements(and, result));

            case OrRule or:
                return RuleStartRequirements.MatchesAnyOf(ChildRequirements(or, result));

            case BetweenInclusiveRule between:
            {
                var innerRequirements = result.Get(between.Children[0]);
                Advance composedAdvance = between.AtLeast == 0
                    ? (innerRequirements.Advance == Advance.Never
                        ? Advance.Never
                        : Advance.Sometimes)
                    : innerRequirements.Advance;
                return innerRequirements.WithAdvance(composedAdvance);
            }

            case AliasRule alias:
                return result.Get(alias.Children[0]);

            case LateBoundRule lateBound:
                // After Compile, Children[0] is the bound target. If
                // Walk hit a cycle and the target is in `computing`, the
                // target's entry won't be present yet, so result.Get
                // returns the pessimistic default. Same fallback the
                // pre-refactor code applied for cyclic references.
                return lateBound.Children.Count > 0
                    ? result.Get(lateBound.Children[0])
                    : RuleStartRequirements.Pessimistic;

            case GraphemeRule grapheme:
                return RuleStartRequirements.FirstTokenMustBeFirstGraphemeOf(grapheme.ExpectedText!);

            case LiteralRule literal:
                return RuleStartRequirements.FirstTokenMustBeFirstGraphemeOf(literal.ExpectedText!);

            case LiteralIgnoreAsciiCaseRule literalIc:
            {
                string text = literalIc.ExpectedText!;
                string firstElement = System.Globalization.StringInfo.GetNextTextElement(text, 0);
                if (firstElement.Length == 1 && IsAsciiLetter(firstElement[0]))
                {
                    int lower = firstElement[0] | 0x20;
                    int upper = lower & ~0x20;
                    return RuleStartRequirements.FirstTokenMustBeInSet(
                        TokenSet.Single(lower) | TokenSet.Single(upper));
                }
                return RuleStartRequirements.FirstTokenMustBeFirstGraphemeOf(text);
            }

            case OneOfRule oneOf:
                return RuleStartRequirements.FirstTokenMustBeInSet(oneOf.LoweringSet);

            case NoneOfRule noneOf:
                return RuleStartRequirements.FirstTokenMustNotBeInSet(noneOf.LoweringSet);

            case AnyTokenRule:
                return RuleStartRequirements.AlwaysAdvancesByOneToken;

            case ScanWhileRule scanWhile:
                return scanWhile.LoweringMinimumCount == 0
                    ? new RuleStartRequirements(scanWhile.LoweringSet, Advance.Sometimes, Polarity.MustBeIn)
                    : RuleStartRequirements.FirstTokenMustBeInSet(scanWhile.LoweringSet);

            case ScanUntilRule:
                return RuleStartRequirements.MayAdvanceByAnyTokens;

            case NotRule:
            case PeekRule:
            case EofRule:
                return RuleStartRequirements.NeverAdvances;

            case WithinTokenRule within:
            {
                var innerRequirements = result.Get(within.Children[0]).WithAdvance(Advance.Always);
                if (innerRequirements.Polarity == Polarity.MustNotBeIn
                    && innerRequirements.FirstConsumedTokens.HasMultiRuneGraphemes)
                {
                    return new RuleStartRequirements(
                        innerRequirements.FirstConsumedTokens.RunesOnlyPart,
                        Advance.Always,
                        Polarity.MustNotBeIn);
                }
                return innerRequirements;
            }

            default:
                return RuleStartRequirements.Pessimistic;
        }
    }

    private static bool IsAsciiLetter(char c) =>
        (uint)((c | 0x20) - 'a') <= ('z' - 'a');

    // Materializes the list MatchesAnyOf / MatchesAllOf expect:
    // one RuleStartRequirements per child, in Children order. Read
    // from the side table the Walk has already populated in post-order.
    private static List<RuleStartRequirements> ChildRequirements(Rule parent, Result result)
    {
        var list = new List<RuleStartRequirements>(parent.Children.Count);
        for (int i = 0; i < parent.Children.Count; i++)
            list.Add(result.Get(parent.Children[i]));
        return list;
    }
}
