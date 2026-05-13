using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Scan forward while tokens are in a TokenSet, returning the whole run
// as one leaf Symbol. The optimization story: AtLeast(n, OneOf(set))
// produces the same matched text but pays one transaction and one
// per-token leaf Symbol for every token in the run, which the tree then
// has to flatten away. ScanWhileRule opens one transaction at the
// top, drops into lexer.AdvanceWhileRuneIn (rune-only sets) or
// lexer.AdvanceWhileTokenIn (sets with multi-rune entries) for the
// inner loop, and emits one leaf Symbol over the whole matched span.
// On the word-scan benchmarks that's a 2x speedup.
//
// Pairs with ScanUntilRule, which is the inverse stop condition: scan
// while tokens are NOT a stopper. Both are leaf-shaped scanners that
// produce one Symbol per matched run.
internal sealed class ScanWhileRule : Rule
{
    private TokenSet _set;
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

    internal override void CollectNormalizationOffenders(
        System.Text.NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
    {
        // ScanWhile matches one grapheme per consumed run iteration
        // against _set, the same shape OneOf uses. Project the set's
        // entries under the chosen form so a user-typed precomposed
        // entry (or a decomposed one) survives normalization to match
        // canonically equivalent input. Without this override the set
        // stays in its pre-normalization shape and the rule silently
        // refuses to match input the lexer's normalization would
        // otherwise hand it. Multi-grapheme conversions are reported
        // through the same NormalizeAndValidate helper OneOf / NoneOf
        // use. The message says OneOf / NoneOf, but the
        // single-grapheme-per-token shape applies here too, and the
        // user sees the offending rule via the offender's `Rule` ref.
        OneOfRule.NormalizeAndValidate(this, ref _set, form, offenders);
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        int startPosition = transaction.StartPosition;

        // Lexer primitive instead of a loop of OneOfRule.TryParse calls:
        // one transaction and one Symbol allocation regardless of the
        // run's length, versus one of each per token in the OneOf form.
        // Dispatch on whether the set has multi-rune entries: rune-only
        // sets stay on the inline-rune fast path; mixed sets pull a
        // full token per iteration so a multi-rune token that's a
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

        var leafSymbol = new Symbol(Id, FlattenType, lexer.Input.AsMemory(startPosition, length), lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.FirstTokenMustBeInSet(_set);
}
