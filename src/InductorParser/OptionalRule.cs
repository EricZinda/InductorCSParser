using System;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class OptionalRule : Rule
{
    private Rule Inner => Children[0];

    public OptionalRule(Rule inner) : base(FlattenType.Flatten, inner) { }

    internal override Symbol? TryParse(Lexer lexer)
    {
        using var transaction = lexer.BeginTransaction();
        var symbol = Inner.TryParse(lexer);
        if (symbol == null)
        {
            transaction.Commit();
            return new Symbol(Id, FlattenType, Array.Empty<Symbol>());
        }
        transaction.Commit();
        return new Symbol(Id, FlattenType, new[] { symbol });
    }
}
