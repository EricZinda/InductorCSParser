using System;
using System.Collections.Generic;
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
}
