using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches a contiguous run of single-rune tokens from one RuneSet and
// returns the whole run as one leaf Symbol. This is the character-class
// analogue of LiteralRule/ScanUntilRule: use it when the grammar wants a
// maximal run such as [A-Za-z0-9_]+, not when it needs one Symbol per rune.
//
// Tests live in src/InductorParser.Tests/Rules/RuneRunRuleTests.cs.
// See docs/TestArchitecture.md for the per-rule test conventions
// (success, failure position, WithError propagation, positional fallback,
// sealed-rule rejection).
internal sealed class RuneRunRule : Rule
{
    private readonly RuneSet _set;
    private readonly int _minimumCount;
    private readonly string _setRendered;

    public RuneRunRule(RuneSet set, int minimumCount)
        : base(FlattenType.Preserve)
    {
        if (minimumCount < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumCount), minimumCount,
                "minimumCount must be at least 1.");

        _set = set;
        _minimumCount = minimumCount;
        _setRendered = set.ToString();
        SetTraceName(minimumCount == 1 ? "RuneRun" : $"RuneRun[{minimumCount}..]");
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        int startPosition = transaction.StartPosition;

        // This is intentionally a lexer primitive rather than a loop of
        // OneOfRule.TryParse calls. The old spelling of [class]+ built and
        // later flattened one leaf per rune; this consumes the same maximal
        // token run but leaves one Symbol over the original input slice.
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
