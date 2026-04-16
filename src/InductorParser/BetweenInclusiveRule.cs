using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// BetweenInclusive(inner, atLeast, atMost) is the bounded-repetition
// primitive every other count rule reduces to. It greedily matches
// `inner` between `atLeast` and `atMost` times (both inclusive), fails
// if it can't reach `atLeast`, and stops once it reaches `atMost`.
//
// The four named factories are thin wrappers:
//   OneOrMore(inner)      == BetweenInclusive(inner, 1, int.MaxValue)
//   ZeroOrMore(inner)     == BetweenInclusive(inner, 0, int.MaxValue)
//   Optional(inner)       == BetweenInclusive(inner, 0, 1)
//   NOrMore(inner, n)     == BetweenInclusive(inner, n, int.MaxValue)
//
// The C++ original spells the same idea as
// AtLeastAndAtMostExpression<T, AtLeast, AtMost>; renamed here because
// "Between" is ambiguous about endpoint inclusivity and the inclusive
// reading is what grammar authors actually want.
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

    internal override Symbol? TryParseRule(Lexer lexer, bool discard)
    {
        using var transaction = lexer.BeginTransaction();
        // Delete-typed wrapper (e.g. OptionalWhitespace) short-circuits to
        // Discarded on success: no list, no wrapper Symbol, no per-rune
        // leaf Symbols stored. We still run the inner loop for its lexer
        // side-effects (consuming whitespace) but drop every symbol it
        // produces on the floor. On the JSON benchmark this is the single
        // biggest per-member allocation saved.
        //
        // When discard is false we still defer the list allocation until
        // a real (non-Discarded) child appears. Optional / ZeroOrMore that
        // matches zero times (or matches only Delete-typed inners) then
        // produces a wrapper Symbol that shares Array.Empty<Symbol>()
        // instead of paying for a fresh empty list.
        List<Symbol>? matched = null;
        int count = 0;
        while (count < AtMost)
        {
            int positionBefore = lexer.Position;
            var nextSymbol = Inner.TryParse(lexer);
            if (nextSymbol == null) break;
            // Guard against zero-width matches looping forever.
            if (lexer.Position == positionBefore) break;
            if (!discard && !ReferenceEquals(nextSymbol, Symbol.Discarded))
            {
                matched ??= new List<Symbol>();
                matched.Add(nextSymbol);
            }
            count++;
        }
        if (count < AtLeast)
        {
            TraceFailure(lexer, $"count= {count}");
            // Error Positioning: where this rule started. The transaction's
            // rollback (on the `using` exit below) restores lexer.Position
            // to that point. Recording at lexer.Position lets this rule's
            // own WithError claim the slot via the equal-depth rule, even
            // if the failing inner already recorded a null-message there.
            lexer.RecordFailure(lexer.Position, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"count= {count}");
        transaction.Commit();
        if (discard) return Symbol.Discarded;
        return new Symbol(Id, FlattenType, (IReadOnlyList<Symbol>?)matched ?? Array.Empty<Symbol>());
    }
}
