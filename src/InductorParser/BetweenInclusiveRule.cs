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

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();

        // First try to shortcut and exit fast using the "Rule Skip" shortcut described
        // on RuleStartRequirements
        if (!lexer.PreserveFlattenWrappers && Inner.ErrorMessage == null)
        {
            string input = lexer.Input;
            int pos = lexer.Position;
            if (pos < input.Length
                && Lexer.TryPeekRune(input, pos, out int peekValue, out _)
                && Inner.CannotMatchLookahead(peekValue))
            {
                if (AtLeast == 0)
                {
                    TraceSuccess(lexer, $"count= 0");
                    transaction.Commit();
                    return effectiveFlattenType == FlattenType.Preserve
                        ? new Symbol(Id, FlattenType, Array.Empty<Symbol>())
                        : Symbol.Discarded;
                }
                TraceFailure(lexer, $"count= 0");
                lexer.RecordFailure(lexer.Position, ErrorMessage);
                return null;
            }
        }

        // If we are preserving this node, create a new list to capture its outputSymbols
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>();
        int count = 0;
        while (count < AtMost)
        {
            int positionBefore = lexer.Position;
            var nextSymbol = ParseChild(Inner, lexer, outputSymbols);
            if (nextSymbol == null) break;
            // Zero-width-match guard. Inner succeeded but didn't advance the
            // lexer (e.g. Optional, Peek, Not, or any composite of zero-width
            // children). Without this break the loop would spin forever on
            // ZeroOrMore(Optional(X)) and friends, incrementing count without
            // making progress. Exit with whatever count we have. The AtLeast
            // check below decides if that's enough to call the rule a success.
            if (lexer.Position == positionBefore) break;
            // Don't add child symbols if they are discarded
            if (outputSymbols != null && !ReferenceEquals(nextSymbol, Symbol.Discarded))
                outputSymbols.Add(nextSymbol);
            count++;
        }
        if (count < AtLeast)
        {
            TraceFailure(lexer, $"count= {count}");
            lexer.RecordFailure(lexer.Position, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"count= {count}");
        transaction.Commit();
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, outputSymbols)
            : Symbol.Discarded;
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (RuneSet.Empty when Advance.Never. RuneSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first character on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // We need to return *all* characters that *might* be consumed as the first charactr
        // Then, we need to say if the first character will Always/Sometimes/Never be consumed
        //
        // For BetweenInclusive:
        // The set of characters is defined by Inner, so we just return those.
        // Inner defines whether the initial token is Always/Sometimes/Never consumed so we use that
        // *except* if atLeast is zero, because then we can
        // succeed and not advance. In that case, we are *at best* sometimes, but it depends on what inner
        // does. If they are Never, we will never advance. If they are Sometimes, we are sometimes.
        Advance advance;
        if (AtLeast == 0)
            advance = Inner.Advance == Advance.Never ? Advance.Never : Advance.Sometimes;
        else
            advance = Inner.Advance;
        return new RuleStartRequirements(Inner.FirstConsumedRunes, advance);
    }
}
