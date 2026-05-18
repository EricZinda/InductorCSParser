using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches a sequence of rules in order. Every child must match for the
// And to succeed. On any child's failure the whole And fails and the
// lexer rolls back to where the And started.
internal sealed class AndRule : Rule
{
    public AndRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        // If we're preserving this node, create a new list to capture its outputSymbols
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>(Children.Count);

        for (int symbolIndex = 0; symbolIndex < Children.Count; symbolIndex++)
        {
            var child = Children[symbolIndex];
            var symbol = ParseChild(child, lexer, outputSymbols);
            if (symbol == null)
            {
                TraceFailure(lexer, $"symbol #{symbolIndex}");
                // Anchor a .WithError on the And at the deepest position
                // its subtree reached. lexer.Position is the failing
                // child's start (its transaction rolled back there) and
                // is the floor; a descendant that probed deeper before
                // failing pushes SubtreeDeepestFailure past it. Recording
                // at the deeper of the two keeps the And's named failure
                // level with its deepest child failure so depth-primary
                // ranking compares them fairly. See
                // docs/ErrorArchitecture.md.
                int anchor = Math.Max(lexer.SubtreeDeepestFailure, lexer.Position);
                lexer.RecordFailure(anchor, ErrorMessage, ErrorForced);
                return null;
            }
            // Don't add child symbols if they're discarded
            if (outputSymbols != null && !ReferenceEquals(symbol, Symbol.Discarded))
                outputSymbols.Add(symbol);
        }
        TraceSuccess(lexer, $"found {Children.Count}");
        int matchStart = transaction.StartPosition;
        int matchLength = lexer.Position - matchStart;
        transaction.Commit();
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, outputSymbols, lexer.Input.AsMemory(matchStart, matchLength), lexer.Context)
            : Symbol.Discarded;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.MatchesAllOf(Children);
}
