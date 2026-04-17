using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class OrRule : Rule
{
    public OrRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParseRule(Lexer lexer, bool discard)
    {
        // Required-runes shortcut: peek the next rune once, then skip any
        // child whose Advance is Always and whose FirstConsumedRunes rules the
        // lookahead out. For a grammar with disjoint FirstConsumedRunes across
        // branches (ChordGrammar's alternations, JSON's literalChar/escape
        // split), this collapses N branch-and-rollback cycles down to 1.
        //
        // Children with Advance.Sometimes (Optional, ZeroOrMore, an And
        // whose children aren't all consuming) or Advance.Never (Peek, Not,
        // Eof) are always tried — a non-Always rule has at least one
        // zero-rune success path, which can fire on any input including
        // EOF, so the lookahead doesn't rule it out.
        string input = lexer.Input;
        int pos = lexer.Position;
        int peekValue;
        bool hasPeek;
        if (pos >= input.Length)
        {
            hasPeek = false;
            peekValue = -1;
        }
        else
        {
            hasPeek = Lexer.TryPeekRune(input, pos, out peekValue, out _);
        }

        for (int symbolIndex = 0; symbolIndex < Children.Count; symbolIndex++)
        {
            var child = Children[symbolIndex];
            // Skip children the lookahead rules out. A child whose own
            // WithError message is set is tried anyway so its error can
            // still surface via the deepest-failure mechanism — we don't
            // want to silence a rule that went out of its way to describe
            // what it wanted.
            if (child.Advance == Advance.Always
                && child.ErrorMessage == null
                && (!hasPeek || !child.FirstConsumedRunes.Contains(peekValue)))
            {
                continue;
            }
            using var transaction = lexer.BeginTransaction();
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

    internal override RuleStart ComputeRuleStart()
    {
        // Or matches any of its children, so its FirstConsumedRunes is the
        // union of children's FirstConsumedRunes.
        //
        // Advance:
        //   Always    — every child advances. Or always advances too.
        //   Never     — no child advances. Or never advances.
        //   Sometimes — mixed (or matches are in different classes). Or
        //               might or might not advance depending on branch.
        RuneSet union = RuneSet.Empty;
        bool allAlways = Children.Count > 0;
        bool allNever = Children.Count > 0;
        foreach (var child in Children)
        {
            union |= child.FirstConsumedRunes;
            if (child.Advance != Advance.Always) allAlways = false;
            if (child.Advance != Advance.Never) allNever = false;
        }
        Advance advance = allAlways
            ? Advance.Always
            : allNever ? Advance.Never : Advance.Sometimes;
        return new RuleStart(union, advance);
    }
}
