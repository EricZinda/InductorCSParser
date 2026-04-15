using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class OrRule : Rule
{
    public OrRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParse(Lexer lexer)
    {
        foreach (var child in Children)
        {
            using var transaction = lexer.BeginTransaction();
            var symbol = child.TryParse(lexer);
            if (symbol != null)
            {
                transaction.Commit();
                return new Symbol(Id, FlattenType, new[] { symbol });
            }
        }
        // Error Positioning: where OrRule itself started trying. All alternatives
        // failed at or past this point and all rolled back, so
        // lexer.Position sits at that shared starting offset. Each
        // alternative has already recorded its own (possibly deeper)
        // failure; this call exists so OrRule's own WithError message
        // can claim the slot at its starting depth via the equal-depth
        // rule when nothing deeper is present.
        lexer.RecordFailure(lexer.Position, ErrorMessage);
        return null;
    }
}
