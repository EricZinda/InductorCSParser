using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Mirror of RuneInRule with the predicate flipped: matches one token iff
// the token is not a single rune in the set. A RuneSet is a set of
// Unicode code points, so under GraphemeLexer a multi-rune grapheme
// (skin-toned emoji, ZWJ family, CJK + combining mark) is trivially not
// in any set, since it isn't a single code point at all. 

// ZeroOrMore(RuneNotIn(stopSet)) is commonly used to 
// sweep up arbitrary user-typed text while still stopping at the stop
// characters.
//
// EOF never matches. The rule reads one token; at EOF the token has
// IsEof == true and the rule fails without advancing, same as RuneInRule.
internal sealed class RuneNotInRule : Rule
{
    private readonly RuneSet _set;
    private readonly string _setRendered;

    public RuneNotInRule(RuneSet runeSet) : base(FlattenType.None)
    {
        _set = runeSet;
        _setRendered = runeSet.ToString();
    }

    internal override Symbol? TryParseRule(Lexer lexer)
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
        // set, so it passes RuneNotIn unconditionally. The set.Contains
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
        // When the token is one rune the Symbol's id is that rune's code
        // point, matching RuneInRule's leaf shape. For multi-rune tokens
        // (grapheme clusters) there is no single code point to pin, so the
        // rule's Compile-assigned id is used instead.
        SymbolId leafId = runeValue >= 0 ? new SymbolId(runeValue) : Id;
        return new Symbol(leafId, FlattenType, token.Memory);
    }
}
