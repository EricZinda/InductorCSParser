using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class RuneInRule : Rule
{
    private readonly RuneSet _set;

    public RuneInRule(RuneSet runeSet) : base(FlattenType.None)
    {
        _set = runeSet;
    }

    internal override Symbol? TryParse(Lexer lexer)
    {
        using var transaction = lexer.BeginTransaction();
        var token = lexer.Read();
        if (token.IsEof || !_set.Contains(token.RuneValue))
        {
            // Error Positioning: the position of the rune we tried to read. RuneInRule
            // does exactly one Read, so the transaction's saved start
            // position is exactly where that rune sits in the input (or
            // equals input.Length on EOF).
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        transaction.Commit();
        // Leaf symbol carries the rune as its id so ToString and tree shape match
        // the C++ behavior where character symbols have id == code point.
        return new Symbol(new SymbolId(token.RuneValue), FlattenType, token.Memory);
    }
}
