using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// BetweenInclusive(atLeast, atMost, inner) is the
// composite every other count rule reduces to. It greedily matches
// `inner` between `atLeast` and `atMost` times (both inclusive), fails
// if it can't reach `atLeast`, and stops once it reaches `atMost`.
//
// The named factories are thin wrappers:
//   OneOrMore(inner)      == BetweenInclusive(1, int.MaxValue, inner)
//   ZeroOrMore(inner)     == BetweenInclusive(0, int.MaxValue, inner)
//   Optional(inner)       == BetweenInclusive(0, 1, inner)
//   AtLeast(n, inner)     == BetweenInclusive(n, int.MaxValue, inner)
//   AtMost(n, inner)      == BetweenInclusive(0, n, inner)
//   Exactly(n, inner)     == BetweenInclusive(n, n, inner)
internal sealed class BetweenInclusiveRule : Rule
{
    internal int AtLeast { get; }
    internal int AtMost { get; }

    private Rule Inner => Children[0];

    public BetweenInclusiveRule(Rule inner, int atLeast, int atMost, string? traceName = null)
        : base(FlattenType.Flatten, inner)
    {
        if (inner == null)
            throw new ArgumentNullException(nameof(inner));
        if (atLeast < 0)
            throw new ArgumentOutOfRangeException(nameof(atLeast), atLeast,
                "atLeast must be non-negative.");
        if (atMost < atLeast)
            throw new ArgumentOutOfRangeException(nameof(atMost), atMost,
                "atMost must be greater than or equal to atLeast.");
        AtLeast = atLeast;
        AtMost = atMost;
        // When the caller supplies a friendly name (OneOrMore, ZeroOrMore,
        // Optional), the trace reads as the name the grammar author chose.
        // When no name is given (the general BetweenInclusive factory),
        // render the bounds so the reader sees what the rule actually does.
        // int.MaxValue renders as an empty upper bound: "[2..]".
        SetTraceName(traceName ?? BuildBoundsLabel(atLeast, atMost));
    }

    private static string BuildBoundsLabel(int atLeast, int atMost)
    {
        string upper = atMost == int.MaxValue ? "" : atMost.ToString();
        return $"BetweenInclusive[{atLeast}..{upper}]";
    }

    internal override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // If we're preserving this node, create a new list to capture its outputSymbols
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>();
        int count = 0;
        while (count < AtMost)
        {
            int positionBefore = lexer.Position;
            var nextSymbol = ParseChild(Inner, lexer, outputSymbols);
            if (nextSymbol == null) break;
            // Add the matched child to outputSymbols before deciding
            // whether to continue. Zero-width Inner that returns a real
            // wrapper Symbol (e.g. Not(X).Preserve() succeeding when X
            // fails, or any Preserve'd lookahead) would otherwise have
            // its wrapper silently dropped: the wrapper sits in
            // nextSymbol but never reaches the parent's children list.
            // AndRule adds children unconditionally for the same reason;
            // BetweenInclusive matches that behavior here.
            if (outputSymbols != null && !ReferenceEquals(nextSymbol, Symbol.Discarded))
                outputSymbols.Add(nextSymbol);
            count++;
            // Only one empty success is counted. count++ above ran for this
            // match. Break now so the loop doesn't spin matching the same
            // empty span again.
            bool zeroWidthMatch = lexer.Position == positionBefore;
            if (zeroWidthMatch) break;
        }
        if (count < AtLeast)
        {
            TraceFailure(lexer, $"count= {count}");
            // Record this rule's own .WithError, floored at the failing
            // iteration's start (lexer.Position, where that iteration
            // rolled back). See docs/ErrorArchitecture.md for how
            // RecordCompositeFailure anchors it.
            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"count= {count}");
        int matchLength = lexer.Position - startPosition;
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, outputSymbols, lexer.Input.AsMemory(startPosition, matchLength), lexer.Context)
            : Symbol.Discarded;
    }
}
