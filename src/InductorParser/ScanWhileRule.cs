using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Scan forward while runes are in a RuneSet, returning the whole run
// as one leaf Symbol. The optimization story: AtLeast(n, OneOf(set))
// produces the same matched text but pays one transaction and one
// per-rune leaf Symbol for every rune in the run, which the tree then
// has to flatten away. ScanWhileRule opens one transaction at the
// top, drops into lexer.AdvanceWhileSingleRuneIn for the inner loop,
// and emits one leaf Symbol over the whole matched span. On the word
// scan benchmarks that's a 2x speedup.
//
// Pairs with ScanUntilRule, which is the inverse stop condition: scan
// while runes are NOT a stopper. Both are leaf-shaped scanners that
// produce one Symbol per matched run.
//
// Tests live in src/InductorParser.Tests/Rules/ScanWhileRuleTests.cs.
// See docs/TestArchitecture.md for the per-rule test conventions
// (success, failure position, WithError propagation, positional fallback,
// sealed-rule rejection).
internal sealed class ScanWhileRule : Rule
{
    private readonly RuneSet _set;
    private readonly int _minimumCount;
    private readonly string _setRendered;

    public ScanWhileRule(RuneSet set, int minimumCount)
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
        int count = lexer.AdvanceWhileSingleRuneIn(_set);
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
        // straight to the next possible run start.
        return new RuleStartRequirements(_set, Advance.Always);
    }
}
