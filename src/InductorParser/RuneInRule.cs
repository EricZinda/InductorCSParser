using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class RuneInRule : Rule
{
    private readonly RuneSet _set;

    // Pre-rendered "[A-Z,a-z]" form of the set, computed once at
    // construction. Trace lines reference this instead of the RuneSet
    // directly so we don't re-render the same string on every traced
    // match — the RuneSet is immutable, so the rendering is too.
    // Worth caching because tracing is intended to be usable while
    // iterating on a grammar, not just for one-off debug runs.
    private readonly string _setRendered;

    public RuneInRule(RuneSet runeSet) : base(FlattenType.None)
    {
        _set = runeSet;
        _setRendered = runeSet.ToString();
    }

    internal override Symbol? TryParseRule(Lexer lexer, bool discard)
    {
        using var transaction = lexer.BeginTransaction();
        var token = lexer.Read();
        if (token.IsEof || !_set.Contains(token.RuneValue))
        {
            TraceFailure(lexer,
                $"found '{(token.IsEof ? "<EOF>" : lexer.Input.Substring(token.Offset, token.Length))}', wanted one of '{_setRendered}'");
            // Error Positioning: the position of the rune we tried to read. RuneInRule
            // does exactly one Read, so the transaction's saved start
            // position is exactly where that rune sits in the input (or
            // equals input.Length on EOF).
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one of '{_setRendered}'");
        transaction.Commit();
        if (discard)
            return Symbol.Discarded;
        // Leaf symbol carries the rune as its id so ToString and tree shape match
        // the C++ behavior where character symbols have id == code point.
        return new Symbol(new SymbolId(token.RuneValue), FlattenType, token.Memory);
    }

    internal override RuleStart ComputeRuleStart()
    {
        return new RuleStart(_set, Advance.Always);
    }
}
