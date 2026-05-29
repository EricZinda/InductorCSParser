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
//
// minimumCount is the minimum number of tokens the run must contain
// to succeed. The default of 1 keeps every successful match consuming
// at least one first-set token, which lets ComputeRuleStart publish
// Advance.Always and lets enclosing rules use the LL(1) lookahead
// shortcut. Passing 0 makes the rule always succeed: an empty run
// produces a zero-width leaf at the current position. The zero-min
// case publishes Advance.Sometimes so the shortcut stays sound, the
// same downgrade BetweenInclusiveRule does for AtLeast == 0
// (Optional / ZeroOrMore).
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
        if (minimumCount < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumCount), minimumCount,
                "minimumCount must be at least 0.");

        _set = set;
        _minimumCount = minimumCount;
        _setRendered = set.ToString();
        SetTraceName(minimumCount == 1 ? "ScanWhile" : $"ScanWhile[{minimumCount}..]");
    }

    internal TokenSet LoweringSet => _set;
    internal int LoweringMinimumCount => _minimumCount;

    internal override void CollectNormalizationOffenders(
        System.Text.NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
    {
        // ScanWhile checks one grapheme per iteration against _set,
        // the same shape OneOf uses, so it form-projects _set through
        // the same helper. See OneOfRule.NormalizeAndValidate.
        OneOfRule.NormalizeAndValidate(this, ref _set, form, offenders);
    }

    protected internal override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
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
            // Record at the position where the scan got stuck.
            // See docs/ErrorArchitecture.md.
            lexer.RecordFailure(lexer.Position, ErrorMessage, ErrorForced);
            return null;
        }

        int length = lexer.Position - startPosition;
        TraceSuccess(lexer, $"count= {count}, {length} chars, wanted at least {_minimumCount} of '{_setRendered}'");

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

}
