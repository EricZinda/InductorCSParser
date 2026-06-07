using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches the first child that succeeds. Tries children left-to-right,
// committing to whichever one matches first. If none match, the Or fails.
// Each child rule manages its own lexer rollback (as TryParseRule
// requires), so a failed alternative naturally leaves the lexer where it
// was before Or called it.
internal sealed class OrRule : Rule
{
    public OrRule(Rule[]? children) : base(FlattenType.Flatten, emitsLeaf: false, RequireChildren(children)) { }

    private static Rule[] RequireChildren(Rule[]? children)
    {
        if (children == null || children.Length == 0)
            throw new ArgumentException("Or requires at least one child rule.", nameof(children));

        return children;
    }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Rule.TryParse's outer transaction wraps every branch attempt and
        // keeps each branch's failures, ranked by depth as near-misses, whether
        // the Or succeeds or fails. See docs/ErrorArchitecture.md.

        // If we're preserving this node, create a new list to capture its outputSymbols
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>();

        for (int symbolIndex = 0; symbolIndex < Children.Count; symbolIndex++)
        {
            var child = Children[symbolIndex];
            int matchStart = lexer.Position;
            var symbol = ParseChild(child, lexer, outputSymbols);
            if (symbol != null)
            {
                TraceSuccess(lexer, $"symbol #{symbolIndex}");
                int matchLength = lexer.Position - matchStart;
                // Returning non-null makes Rule.TryParse commit the outer
                // transaction, keeping the position the winning branch
                // reached. The commit doesn't clear the rejected branches'
                // failures: a rejected branch is the parser genuinely
                // trying to read the input, and its failure is a near-miss
                // kept and ranked by depth. See docs/ErrorArchitecture.md.
                if (outputSymbols != null && !ReferenceEquals(symbol, Symbol.Discarded))
                    outputSymbols.Add(symbol);
                return effectiveFlattenType == FlattenType.Preserve
                    ? CreateCompositeFromOwnedChildren(outputSymbols, lexer.Input.AsMemory(matchStart, matchLength), lexer.Context)
                    : Symbol.Discarded;
            }
        }
        TraceFailure(lexer, $"");
        // Record the Or's own .WithError, floored at the Or's start
        // (startPosition, where every branch rolled back). See
        // docs/ErrorArchitecture.md for how RecordCompositeFailure
        // anchors it.
        lexer.RecordCompositeFailure(startPosition, ErrorMessage, ErrorForced);
        return null;
    }
}
