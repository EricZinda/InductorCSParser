using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Scan forward while tokens are in a TokenSet, returning the whole run
// as one leaf Symbol. This exists because AtLeast(n, OneOf(set))
// produces the same matched text but pays one transaction and one
// per-token leaf Symbol for every token in the run, which the tree then
// has to flatten away. ScanWhileRule opens one transaction at the
// top, drops into lexer.AdvanceWhileIn for the inner loop (which picks
// the rune-only or grapheme-cluster walk from the set), and emits one
// leaf Symbol over the whole matched span.
//
// minimumCount is the fewest tokens the run must have to succeed. The
// default of 1 fails on an empty run. Passing 0 makes the rule always
// succeed, producing a zero-width leaf on an empty run. 
//
// Pairs with ScanUntilRule, which is the inverse stop condition: scan
// while tokens aren't a stopper. Both are leaf-shaped scanners that
// produce one Symbol per matched run.
internal sealed class ScanWhileRule : Rule
{
    private TokenSet _set;

    // Accessor for an alternative evaluator to read the rule's set
    // without running the rule.
    internal TokenSet Set => _set;

    private readonly int _minimumCount;

    // Accessor for an alternative evaluator to read the rule's minimum
    // run length without running the rule.
    internal int MinimumCount => _minimumCount;

    // Refreshed by ValidateNormalization when Compile's
    // normalization pass mutates _set. See OneOfRule for the why.
    private string _setRendered;

    public ScanWhileRule(TokenSet set, int minimumCount)
        : base(FlattenType.Preserve, emitsLeaf: true)
    {
        if (minimumCount < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumCount), minimumCount,
                "minimumCount must be at least 0.");

        _set = set;
        _minimumCount = minimumCount;
        _setRendered = set.ToString();
        SetTraceName(minimumCount == 1 ? "ScanWhile" : $"ScanWhile[{minimumCount}..]");
    }

    protected override void ValidateNormalization(
        System.Text.NormalizationForm form,
        INormalizationReporter reporter)
    {
        // ScanWhile checks one grapheme per iteration against _set,
        // the same shape OneOf uses, so it form-projects _set through
        // the same helper. See OneOfRule.NormalizeAndValidate.
        OneOfRule.NormalizeAndValidate(this, ref _set, form, reporter);
        // See OneOfRule.ValidateNormalization for why the
        // rendering has to be refreshed after the set is projected.
        _setRendered = _set.ToString();
    }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Lexer primitive instead of a loop of OneOfRule.TryParse calls:
        // one transaction and one Symbol allocation regardless of the
        // run's length, versus one of each per token in the OneOf form.
        // AdvanceWhileIn keeps rune-only sets on the inline-rune fast
        // path and pulls a full token per iteration for mixed sets, so a
        // multi-rune token that's a member of the set can be part of the run.
        int count = lexer.AdvanceWhileIn(_set);
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
