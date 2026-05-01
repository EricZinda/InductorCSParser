using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Scan forward while runes are in a TokenSet, returning the whole run
// as one leaf Symbol. The optimization story: AtLeast(n, OneOf(set))
// produces the same matched text but pays one transaction and one
// per-rune leaf Symbol for every rune in the run, which the tree then
// has to flatten away. ScanWhileRule opens one transaction at the
// top, drops into lexer.AdvanceWhileRuneIn for the inner loop,
// and emits one leaf Symbol over the whole matched span. On the word
// scan benchmarks that's a 2x speedup.
//
// Pairs with ScanUntilRule, which is the inverse stop condition: scan
// while runes are NOT a stopper. Both are leaf-shaped scanners that
// produce one Symbol per matched run.
internal sealed class ScanWhileRule : Rule
{
    private readonly TokenSet _set;
    private readonly int _minimumCount;
    private readonly string _setRendered;

    public ScanWhileRule(TokenSet set, int minimumCount)
        : base(FlattenType.Preserve)
    {
        if (minimumCount < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumCount), minimumCount,
                "minimumCount must be at least 1.");

        _set = set;
        _minimumCount = minimumCount;
        _setRendered = set.ToString();
        SetTraceName(minimumCount == 1 ? "ScanWhile" : $"ScanWhile[{minimumCount}..]");
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        int startPosition = transaction.StartPosition;

        // Lexer primitive instead of a loop of OneOfRule.TryParse calls:
        // one transaction and one Symbol allocation regardless of the
        // run's length, versus one of each per rune in the OneOf form.
        // Dispatch on whether the set has multi-rune entries: rune-only
        // sets stay on the inline-rune fast path; mixed sets pull a
        // full token per iteration so a multi-rune grapheme that's a
        // member of the set can be part of the run.
        int count = _set.HasMultiRuneGraphemes
            ? lexer.AdvanceWhileTokenIn(_set)
            : lexer.AdvanceWhileRuneIn(_set);
        if (count < _minimumCount)
        {
            TraceFailure(lexer, $"count= {count}, wanted at least {_minimumCount} of '{_setRendered}'");
            // If we consumed a short run, report the failure where the
            // shortfall became known, matching AtLeast(OneOf(...)) error
            // positioning. The transaction rolls the actual cursor back.
            lexer.RecordFailure(lexer.Position, ErrorMessage);
            return null;
        }

        int length = lexer.Position - startPosition;
        TraceSuccess(lexer, $"count= {count}, {length} chars, wanted one or more of '{_setRendered}'");
        transaction.Commit();

        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;

        var leafSymbol = new Symbol(Id, FlattenType, lexer.Input.AsMemory(startPosition, length));
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    internal override RuleStartRequirements ComputeRuleStart()
    {
        // minimumCount is guaranteed >= 1, so every successful match consumes
        // a first rune from _set. That lets scanner-style outer loops skip
        // straight to the next possible run start. For sets with multi-rune
        // entries, the first rune of each multi-rune grapheme is also a
        // valid lookahead (the whole grapheme is one token under the
        // GraphemeLexer), so OneOfRule.LookaheadFirstRunes folds those
        // first runes into the rune intervals.
        return new RuleStartRequirements(OneOfRule.LookaheadFirstRunes(_set), Advance.Always);
    }
}
