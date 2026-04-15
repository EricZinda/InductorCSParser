using System;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class OptionalRule : Rule
{
    private Rule Inner => Children[0];

    public OptionalRule(Rule inner) : base(FlattenType.Flatten, inner) { }

    internal override Symbol? TryParseRule(Lexer lexer)
    {
        using var transaction = lexer.BeginTransaction();
        var symbol = Inner.TryParse(lexer);
        if (symbol == null)
        {
            TraceSuccess(lexer, $"count= 0");
            transaction.Commit();
            return new Symbol(Id, FlattenType, Array.Empty<Symbol>());
        }
        TraceSuccess(lexer, $"count= 1");
        transaction.Commit();
        return new Symbol(Id, FlattenType, new[] { symbol });
    }
}
