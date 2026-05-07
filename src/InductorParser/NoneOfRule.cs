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
        // The lookahead peek sees ONE rune. NoneOf, asked "could a token
        // starting with this rune match?", has to admit that yes for any
        // rune. A token here is one grapheme cluster, and a multi-rune
        // cluster like "X<combining mark>" or "X<ZWJ>Y" starts with X
        // for any base rune X. NoneOf admits any cluster whose chars
        // aren't in _set's multi-rune part, so for every rune R there's
        // some multi-rune cluster starting with R that NoneOf would
        // accept (regardless of whether R itself is in the rune-only
        // part of _set, because R-as-a-single-rune-token and "R..."
        // -as-a-multi-rune-cluster are different tokens with different
        // membership tests).
        //
        // The previous tighter `~_set.RunesOnlyPart` answer ignored that
        // second case. OneOrMore(NoneOf({'a'})) parsing "á" (one
        // grapheme under Compile(null), admitted by NoneOf because the
        // cluster isn't a single-rune 'a') wrongly failed: the
        // BetweenInclusive shortcut peeked 'a', saw it wasn't in
        // ~{'a'}, and concluded NoneOf couldn't match. Universe is the
        // soundest answer.
        return new RuleStartRequirements(TokenSet.Universe, Advance.Always);
    }
}
