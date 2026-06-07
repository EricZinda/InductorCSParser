// A user-defined Rule subclass: a permutation. Succeeds when every one of its
// item rules matches exactly once, in any order, consecutively in the input.
// The matched items become the children of one Symbol, in the order
// they appeared in the input, so a consumer reads them by name regardless of
// the order they were typed.
//
// Ported from Haskell parsec's Text.Parsec.Perm (the `permute` combinator),
// Copyright (c) Daan Leijen 1999-2001, (c) Paolo Martini 2007, distributed
// under a BSD-style license (https://hackage.haskell.org/package/parsec). The
// algorithm is from the paper "Parsing Permutation Phrases" by Arthur Baars,
// Andres Loh, and Doaitse Swierstra. This is an independent C# reimplementation
// from the module's documented behavior ("each parser is applied exactly once,
// but the actual input can be in any order"), not a translation of parsec's
// type-level Haskell source.
//
// Why a custom rule and not a composition of built-ins:
//
//   The only way to express "A, B, and C in any order" with the built-in
//   combinators is to spell out every ordering as an alternative:
//
//     Or(And(A, B, C), And(A, C, B), And(B, A, C),
//        And(B, C, A), And(C, A, B), And(C, B, A))
//
//   That's N! branches. Three items is already six, four is twenty-four. The
//   shared work (each item is tried over and over across branches) and the
//   factorial size are exactly why parsec ships `permute` as a primitive
//   instead of leaving it to Or. A custom rule does it in N rounds of at most
//   N attempts each (N-squared, not N!), trying each not-yet-matched item at
//   the current position and keeping the first that matches.
//
// It uses only the public + protected surface of InductorParser: ParseChild
// runs each item with its own transaction (a failed item rolls the cursor back
// on its own, so the next item is tried at the same spot), lexer.TickBudget
// bounds the outer loop, lexer.RecordCompositeFailure anchors the failure, and
// the Symbol it returns is built from lexer.Input / lexer.Position / lexer.Context
// the way every composite builds its node. No internal members.
//
// Two limits, both inherited from `permute`'s documented behavior:
//   * Each item must consume input. An item that can match empty (Optional,
//     ZeroOrMore, a bare Not) would "match" without advancing and get marked
//     done at the wrong spot. parsec forbids this on (<||>) for the same
//     reason and offers a separate optional combinator. This rule doesn't try
//     to detect it.
//   * Items are matched greedily in declaration order, so the items should
//     have disjoint first characters (distinct literal prefixes like "width"
//     vs "height"). If two items can both start at the same point, the
//     earlier-declared one wins, which may not be what you want. parsec's
//     type-level machinery sidesteps this. The practical use (a fixed set of
//     distinctly-named fields) doesn't hit it.

using System;
using System.Collections.Generic;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace PermutationSample.Rewrite;

public sealed class PermutationRule : Rule
{
    public PermutationRule(params Rule[] items)
        // Flatten by default like And / Or: a permutation is a sequence whose
        // pieces usually want to bubble into the parent. A caller who names it
        // with .As(...) gets Preserve and a findable Symbol.
        : base(FlattenType.Flatten, emitsLeaf: false, RequireAtLeastOne(items))
    {
    }

    private static Rule[] RequireAtLeastOne(Rule[] items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (items.Length == 0)
            throw new ArgumentException("A permutation needs at least one item.", nameof(items));
        return items;
    }

    protected override Symbol? TryParseRule(
        Lexer lexer,
        int startPosition,
        FlattenType effectiveFlattenType,
        List<Symbol>? outputSymbols)
    {
        int itemCount = Children.Count;
        var done = new bool[itemCount];

        // Preserve builds its own child list to wrap. Flatten writes into the
        // caller's list. Delete writes nowhere (outputSymbols is null here).
        List<Symbol>? collected =
            effectiveFlattenType == FlattenType.Preserve ? new List<Symbol>() : outputSymbols;

        int remaining = itemCount;
        while (remaining > 0)
        {
            // Bound the outer loop for the budget the same way a bulk scan
            // does: each round is one unit of work the Timeout / Cancellation
            // / RuleCountLimit budget can observe.
            lexer.TickBudget();

            bool matchedThisRound = false;
            for (int index = 0; index < itemCount; index++)
            {
                if (done[index]) continue;

                // ParseChild runs the item with its own transaction. On failure
                // that transaction rolls the cursor back to where this round
                // started, so the next item is tried at the same position.
                var itemSymbol = ParseChild(Children[index], lexer, collected);
                if (itemSymbol == null) continue;

                // A Preserve item hands back its own Symbol here, so add it. A Flatten
                // item already wrote its pieces into `collected` and returned
                // Discarded, so adding it would double-count. Same rule And and
                // BetweenInclusive follow.
                if (collected != null && !ReferenceEquals(itemSymbol, Symbol.Discarded))
                    collected.Add(itemSymbol);

                done[index] = true;
                remaining--;
                matchedThisRound = true;
                break;
            }

            if (!matchedThisRound)
            {
                TraceFailure(lexer, $"{remaining} of {itemCount} items unmatched here");
                // Anchor at the current position: this is where no remaining
                // item could match, which is the spot the user has to fix
                // (a missing field, or a duplicate where a different field was
                // expected).
                lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                return null;
            }
        }

        TraceSuccess(lexer, $"all {itemCount} items matched");

        if (effectiveFlattenType != FlattenType.Preserve)
            return Symbol.Discarded;

        int length = lexer.Position - startPosition;
        return new Symbol(Id, FlattenType, collected!,
            lexer.Input.AsMemory(startPosition, length), lexer.Context);
    }
}
