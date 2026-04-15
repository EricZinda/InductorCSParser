using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class ZeroOrMoreRule : Rule
{
    private Rule Inner => Children[0];

    public ZeroOrMoreRule(Rule inner) : base(FlattenType.Flatten, inner) { }

    internal override Symbol? TryParseRule(Lexer lexer)
    {
        var matched = new List<Symbol>();
        while (true)
        {
            int positionBefore = lexer.Position;
            var nextSymbol = Inner.TryParse(lexer);
            if (nextSymbol == null) break;
            if (lexer.Position == positionBefore) break;
            matched.Add(nextSymbol);
        }
        TraceSuccess(lexer, $"count= {matched.Count}");
        return new Symbol(Id, FlattenType, matched);
    }
}
