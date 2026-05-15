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
                // Record at the failing child's start position (lexer
                // is at that position because the child's own
                // transaction rolled back there). That puts the
                // cursor at where the user needs to fix the input,
                // not at the And's overall start. See
                // docs/ErrorArchitecture.md.
                lexer.RecordFailure(lexer.Position, ErrorMessage, ErrorForced);
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
