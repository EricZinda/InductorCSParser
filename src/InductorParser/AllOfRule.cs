using System;
using System.Collections.Generic;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches a sequence of rules in order. Every child must match for the
// AllOf to succeed. On any child's failure the whole AllOf fails and the
// lexer rolls back to where the AllOf started.
//
// Tests live in src/InductorParser.Tests/Rules/AllOfRuleTests.cs.
// See docs/TestArchitecture.md for the per-rule test conventions
// (success, failure position, WithError propagation, positional fallback,
// sealed-rule rejection).
internal sealed class AllOfRule : Rule
{
    public AllOfRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        // If we are preserving this node, create a new list to capture its outputSymbols
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>(Children.Count);

        for (int symbolIndex = 0; symbolIndex < Children.Count; symbolIndex++)
        {
            var child = Children[symbolIndex];
            var symbol = ParseChild(child, lexer, outputSymbols);
            if (symbol == null)
            {
                TraceFailure(lexer, $"symbol #{symbolIndex}");
                lexer.RecordFailure(lexer.Position, ErrorMessage);
                return null;
            }
            // Don't add child symbols if they are discarded
            if (outputSymbols != null && !ReferenceEquals(symbol, Symbol.Discarded))
                outputSymbols.Add(symbol);
        }
        TraceSuccess(lexer, $"found {Children.Count}");
        transaction.Commit();
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, outputSymbols)
            : Symbol.Discarded;
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (RuneSet.Empty when Advance.Never. RuneSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // We need to return the set of Runes that are necessary, but not sufficent for success.
        // The initial runes from any child that Always or sometimes consumes Runes must 
        // therefore be included, never can be ignored.
        // Furthermore, once we hit the first Always consumes a Rune, we can stop because at that
        // point the first rune in the parse is gone and that's all we are talking about.
        // 
        // Then we can calculate AllOf's own Advance:
        //   Always:    at least one child had Advance.Always so AllOf is guaranteed to consume too on success.
        //   Never:     every child is Never.
        //   Sometimes: otherwise.
        RuneSet union = RuneSet.Empty;
        bool anyMightConsume = false;
        foreach (var child in Children)
        {
            if (child.Advance != Advance.Never)
            {
                union |= child.FirstConsumedRunes;
                anyMightConsume = true;
            }
            if (child.Advance == Advance.Always)
                return new RuleStartRequirements(union, Advance.Always);
        }
        return new RuleStartRequirements(union, anyMightConsume ? Advance.Sometimes : Advance.Never);
    }

    internal override (string Text, bool IgnoreCase)? ComputeRequiredLiteral()
    {
        // Every child must match in sequence, so any single child's
        // required literal is required by the AllOf. A run of
        // consecutive children that each have a fixed concatenable text
        // contributes a single longer literal (Literal("# ") followed
        // by OneOf("Nn") OneOf("Oo")... gives "# noqa" case-insensitive).
        // Pick the longest candidate seen across all runs and any
        // recursed sub-literals, mirroring what regex engines do when
        // they extract a "best literal" from a pattern.
        StringBuilder? run = null;
        bool runIgnoreCase = false;
        (string Text, bool IgnoreCase)? best = null;

        foreach (var child in Children)
        {
            var concat = child.ComputeConcatenableText();
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

            var childRequired = child.ComputeRequiredLiteral();
            if (childRequired != null)
                Maybe(ref best, childRequired.Value);
        }

        FinalizeRun(ref run, ref runIgnoreCase, ref best);
        return best;
    }

    // Surface a multi-literal alternative if any single child has one.
    // The motivating shape is AllOf(FirstOf(L1, L2, L3, L4), other-stuff)
    // where the FirstOf has all-literal branches: any successful match
    // of the AllOf still passes through the FirstOf and so contains one
    // of {L1, L2, L3, L4}. If the AllOf has a long single shared literal
    // already (the AllOf's ComputeRequiredLiteral picks it up), callers
    // typically prefer that and never reach this method; this is for
    // the cases where no single literal is derivable but a child's set
    // is.
    internal override IReadOnlyList<(string Text, bool IgnoreCase)>? ComputeRequiredLiteralAlternatives()
    {
        foreach (var child in Children)
        {
            var childSet = child.ComputeRequiredLiteralAlternatives();
            if (childSet != null && childSet.Count > 0)
                return childSet;
        }
        return null;
    }

    internal override (string Text, bool IgnoreCase)? ComputeConcatenableText()
    {
        // Only concatenable when every child is concatenable; otherwise
        // we can't promise a fixed-length contribution.
        var builder = new StringBuilder();
        bool ignoreCase = false;
        foreach (var child in Children)
        {
            var concat = child.ComputeConcatenableText();
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
