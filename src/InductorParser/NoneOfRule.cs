using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Mirror of OneOfRule with the predicate flipped: matches one token iff
// the token isn't a single rune in the set. A RuneSet is a set of
// Unicode code points, so under GraphemeLexer a multi-rune grapheme
// (skin-toned emoji, ZWJ family, CJK + combining mark) is trivially not
// in any set, since it isn't a single code point at all.

// ZeroOrMore(NoneOf(stopSet)) is commonly used to
// sweep up arbitrary user-typed text while still stopping at the stop
// characters.
//
// EOF never matches. The rule reads one token. At EOF the token has
// IsEof == true and the rule fails without advancing, same as OneOfRule.
//
// Tests live in src/InductorParser.Tests/Rules/NoneOfRuleTests.cs.
// See docs/TestArchitecture.md for the per-rule test conventions
// (success, failure position, WithError propagation, positional fallback,
// sealed-rule rejection).
internal sealed class NoneOfRule : Rule
{
    private readonly RuneSet _set;
    private readonly string _setRendered;

    public NoneOfRule(RuneSet runeSet) : base(FlattenType.Preserve)
    {
        _set = runeSet;
        _setRendered = runeSet.ToString();
    }

    // Accessor for the state-machine evaluator's lowering pass.
    internal RuneSet LoweringSet => _set;

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
        // RuneValue == -1 for multi-rune tokens (grapheme clusters under
        // GraphemeLexer). A multi-rune token isn't any single rune in any
        // set, so it passes NoneOf unconditionally. The set.Contains
        // check only runs on the single-rune branch.
        int runeValue = token.RuneValue;
        if (runeValue >= 0 && _set.Contains(runeValue))
        {
            TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one not in '{_setRendered}'");
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one not in '{_setRendered}'");
        transaction.Commit();
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        SymbolId leafId = runeValue >= 0 ? new SymbolId(runeValue) : Id;
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (RuneSet.Empty when Advance.Never. RuneSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        return new RuleStartRequirements(~_set, Advance.Always);
    }
}
