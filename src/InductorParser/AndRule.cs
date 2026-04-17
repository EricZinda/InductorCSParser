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
        // Defer the matched-list allocation until the first non-Discarded
        // child actually needs to land in it. When discard is true the
        // And itself collapses to Discarded on success, so the list never
        // exists. When discard is false but every child happens to be
        // Delete-typed (e.g. And(OptionalWhitespace(), OptionalWhitespace())),
        // the wrapper Symbol still gets built, but it shares the global
        // Array.Empty<Symbol>() instead of a fresh empty list.
        List<Symbol>? matched = null;
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
            {
                matched ??= new List<Symbol>(Children.Count);
                matched.Add(symbol);
            }
        }
        // Reaching here means every child matched, so the logical count
        // is simply Children.Count. The matched list may hold fewer
        // entries because Discarded children were filtered.
        TraceSuccess(lexer, $"found {Children.Count}");
        transaction.Commit();
        if (discard) return Symbol.Discarded;
        return new Symbol(Id, FlattenType, (IReadOnlyList<Symbol>?)matched ?? Array.Empty<Symbol>());
    }

    internal override RuleStart ComputeRuleStart()
    {
        // Walk children in order, union each consuming child's FirstConsumedRunes
        // (Advance != Never), stop at the first child whose Advance is
        // Always — that child will consume a rune and thus disables the
        // shortcut for later children. Never children (Peek, Not, Eof) are
        // skipped from the union because they don't supply a consumed rune;
        // they're gates, not contributors.
        //
        // Children before the first Always must be: Sometimes or Never. The
        // Sometimes children might or might not consume on any given match;
        // when they do, they're supplying the starting rune, so their
        // FirstConsumedRunes must be in the union.
        //
        // And's overall Advance:
        //   Always    — some child must consume (we hit an Always branch).
        //   Never     — every child was Never (the And is all gates).
        //   Sometimes — at least one Sometimes, no Always (the And might
        //               advance or might not, depending on which children
        //               take their consuming paths).
        RuneSet union = RuneSet.Empty;
        bool anyMightConsume = false;
        foreach (var child in Children)
        {
            if (child.Advance != Advance.Never)
            {
                union |= child.FirstConsumedRunes;
                anyMightConsume = true;
            }
            if (child.Advance == Advance.Always)
                return new RuleStart(union, Advance.Always);
        }
        return new RuleStart(union, anyMightConsume ? Advance.Sometimes : Advance.Never);
    }
}
