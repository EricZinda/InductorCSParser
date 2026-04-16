using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class AndRule : Rule
{
    public AndRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParseRule(Lexer lexer, bool discard)
    {
        using var transaction = lexer.BeginTransaction();
        // When discard is true the And itself will collapse to Discarded
        // on success, so skip the wrapper Symbol and the matched-list
        // allocation. The per-iteration cost goes from "one child Symbol
        // + list entry" to "nothing beyond what the child does."
        List<Symbol>? matched = discard ? null : new List<Symbol>(Children.Count);
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
            // Discarded children contribute nothing to the tree. Filtering
            // here is the other half of the parse-time Delete optimization:
            // with this check, OptionalWhitespace and the like land in the
            // parent's Children list as zero entries instead of one empty
            // wrapper. The reference check is necessary even in the
            // !discard case because a child's own FlattenType isn't a
            // reliable signal: OrRule in Flatten-elide mode and
            // LateBoundRule forwarding both pass a Discarded inner straight
            // through, even though their own FlattenType isn't Delete.
            if (!discard && !ReferenceEquals(symbol, Symbol.Discarded))
                matched!.Add(symbol);
        }
        // Reaching here means every child matched, so the logical count
        // is simply Children.Count. The matched list may hold fewer
        // entries because Discarded children were filtered.
        TraceSuccess(lexer, $"found {Children.Count}");
        transaction.Commit();
        if (discard) return Symbol.Discarded;
        return new Symbol(Id, FlattenType, matched!);
    }
}
