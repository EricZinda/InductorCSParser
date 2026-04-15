using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class OneOrMoreRule : Rule
{
    private Rule Inner => Children[0];

    public OneOrMoreRule(Rule inner) : base(FlattenType.Flatten, inner) { }

    internal override Symbol? TryParse(Lexer lexer)
    {
        using var transaction = lexer.BeginTransaction();
        var matched = new List<Symbol>();
        var firstSymbol = Inner.TryParse(lexer);
        if (firstSymbol == null)
        {
            TraceFailure(lexer, $"count= 0");
            // Error Positioning: where OneOrMore started. Equivalently, where the
            // failing first inner started trying (they're the same offset
            // because the inner's transaction rolled back). OneOrMore only
            // fails when the first inner does; later inner failures just
            // terminate the loop and OneOrMore still succeeds. The inner
            // already recorded at this position; this call lets OneOrMore's
            // own WithError claim the slot via the equal-depth rule.
            lexer.RecordFailure(lexer.Position, ErrorMessage);
            return null;
        }
        matched.Add(firstSymbol);
        while (true)
        {
            int positionBefore = lexer.Position;
            var nextSymbol = Inner.TryParse(lexer);
            if (nextSymbol == null) break;
            // Guard against zero-width matches looping forever.
            if (lexer.Position == positionBefore) break;
            matched.Add(nextSymbol);
        }
        TraceSuccess(lexer, $"count= {matched.Count}");
        transaction.Commit();
        return new Symbol(Id, FlattenType, matched);
    }
}
