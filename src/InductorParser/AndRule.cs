using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class AndRule : Rule
{
    public AndRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParse(Lexer lexer)
    {
        using var transaction = lexer.BeginTransaction();
        var matched = new List<Symbol>();
        for (int symbolIndex = 0; symbolIndex < Children.Count; symbolIndex++)
        {
            var child = Children[symbolIndex];
            var symbol = child.TryParse(lexer);
            if (symbol == null)
            {
                TraceFailure(lexer, $"symbol #{symbolIndex}");
                // Error Positioning: where the failing child started trying. After
                // the child's transaction rolls back on its failure path,
                // lexer.Position is exactly the child's own pre-read
                // position. The child already recorded there; this call
                // mostly exists so AndRule's own WithError message can
                // claim the slot via the equal-depth rule if it wasn't claimed.
                lexer.RecordFailure(lexer.Position, ErrorMessage);
                return null;
            }
            matched.Add(symbol);
        }
        TraceSuccess(lexer, $"found {matched.Count}");
        transaction.Commit();
        return new Symbol(Id, FlattenType, matched);
    }
}
