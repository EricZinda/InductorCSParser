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
    private readonly string _setRendered;

    public NoneOfRule(TokenSet runeSet) : base(FlattenType.Preserve)
    {
        _set = runeSet;
        _setRendered = runeSet.ToString();
    }

    // Accessor for the state-machine evaluator's lowering pass.
    internal TokenSet LoweringSet => _set;

    // See Rule.CollectNormalizationOffenders for the contract. Same
    // shape as OneOfRule and shares the implementation.
    internal override void CollectNormalizationOffenders(
        System.Text.NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
    {
        OneOfRule.NormalizeAndValidate(this, ref _set, form, offenders);
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        var token = lexer.Read();
        if (token.IsEof)
        {
            TraceFailure(lexer, $"found '<EOF>', wanted one not in '{_setRendered}'");
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        if (_set.ContainsToken(token.Chars))
        {
            TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one not in '{_setRendered}'");
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one not in '{_setRendered}'");
        transaction.Commit();
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        // Use the rune value as the leaf Id when the rule is unnamed;
        // otherwise use the rule's own Id so .As("name") makes the leaf
        // findable via Tree.Find / Tree.Is / NameOf. See OneOfRule for
        // the rationale.
        int runeValue = token.RuneValue;
        SymbolId leafId = (Name == null && runeValue >= 0) ? new SymbolId(runeValue) : Id;
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (TokenSet.Empty when Advance.Never. TokenSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // ~set throws on a mixed set, so when _set has multi-rune
        // entries we project down to the rune-only part first and
        // complement that. The result is a SUPERSET of the actual
        // first-consumed runes (we can't filter out tokens whose first
        // rune is a multi-rune-entry head, because some of those
        // tokens are single-rune and pass NoneOf), which is the
        // safe direction for the lookahead shortcut.
        TokenSet firstConsumed = _set.HasMultiRuneGraphemes
            ? ~_set.RunesOnlyPart
            : ~_set;
        return new RuleStartRequirements(firstConsumed, Advance.Always);
    }
}
