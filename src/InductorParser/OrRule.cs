using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class OrRule : Rule
{
    public OrRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParseRule(Lexer lexer, bool discard)
    {
        for (int symbolIndex = 0; symbolIndex < Children.Count; symbolIndex++)
        {
            using var transaction = lexer.BeginTransaction();
            var child = Children[symbolIndex];
            var symbol = child.TryParse(lexer);
            if (symbol != null)
            {
                TraceSuccess(lexer, $"symbol #{symbolIndex}");
                transaction.Commit();
                // An Or whose own FlattenType is Delete returns Discarded
                // up to the parent: the inner's Symbol is dropped entirely
                // along with any wrapper we'd otherwise build. Suppressed
                // when PreserveFlattenWrappers is set (via the `discard`
                // flag) so the grammar-shape debug view keeps the Or
                // node visible.
                if (discard)
                    return Symbol.Discarded;
                // FlattenType.Flatten on an Or means "post-hoc Flatten
                // would splice the inner symbol straight back into the
                // parent." That splice is the whole point of the wrapper,
                // so do it at parse time and skip both the single-element
                // Symbol[] and the wrapper Symbol. Grammar authors who
                // need the Or's Id to appear in the tree (for Find or
                // because the wrapper is structurally meaningful) opt
                // into it the same way they'd survive a post-hoc
                // .Flatten() call: set FlattenType.None.
                //
                // If the matching inner was itself Discarded (its own
                // FlattenType was Delete), the Flatten-elide path
                // propagates Discarded straight through, keeping the
                // tree free of the sentinel: a Delete-typed child of an
                // Or contributes nothing, same as in any other composite.
                //
                // ParseOptions.PreserveFlattenWrappers forces the wrapper
                // to stay so a debugging caller sees a tree whose shape
                // matches the grammar one-to-one.
                if (FlattenType == FlattenType.Flatten && !lexer.PreserveFlattenWrappers)
                    return symbol;
                return new Symbol(Id, FlattenType, new[] { symbol });
            }
        }
        TraceFailure(lexer, $"");
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
