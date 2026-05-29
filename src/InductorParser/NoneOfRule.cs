using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Mirror of OneOfRule with the predicate flipped: matches one token
// iff the token's value isn't in the set.

// ZeroOrMore(NoneOf(stopSet)) is commonly used to
// sweep up arbitrary user-typed text while still stopping at the stop
// characters.
//
// EOF never matches. The rule reads one token. At EOF the token has
// IsEof == true and the rule fails without advancing, same as OneOfRule.
internal sealed class NoneOfRule : Rule
{
    private TokenSet _set;
    // Refreshed by ValidateNormalization when Compile's
    // normalization pass mutates _set. See OneOfRule for the why.
    private string _setRendered;

    public NoneOfRule(TokenSet runeSet) : base(FlattenType.Preserve, emitsLeaf: true)
    {
        _set = runeSet;
        _setRendered = runeSet.ToString();
    }

    // Accessor for an alternative evaluator to read the rule's set
    // without running the rule.
    internal TokenSet LoweringSet => _set;

    // See Rule.ValidateNormalization for how this works. Same
    // shape as OneOfRule and shares the implementation.
    protected override void ValidateNormalization(
        System.Text.NormalizationForm form,
        INormalizationReporter reporter)
    {
        OneOfRule.NormalizeAndValidate(this, ref _set, form, reporter);
        // See OneOfRule.ValidateNormalization for why the
        // rendering has to be refreshed after the set is projected.
        _setRendered = _set.ToString();
    }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        var token = lexer.Read();
        if (token.IsEof)
        {
            TraceFailure(lexer, $"found '<EOF>', wanted one not in '{_setRendered}'");
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }
        if (_set.ContainsToken(token.Chars))
        {
            TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one not in '{_setRendered}'");
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one not in '{_setRendered}'");
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        // See Rule.ResolveLeafId for the leaf-id rule shared across
        // OneOfRule / NoneOfRule / AnyTokenRule / WithinTokenRule.
        SymbolId leafId = ResolveLeafId(token.RuneValue);
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory, lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

}
