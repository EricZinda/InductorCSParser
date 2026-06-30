using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// AliasRule names an existing rule shape so it can appear in a grammar
// under a different identity without duplicating its construction. The
// canonical use case is when one parsing shape is reused under multiple
// names:
//
//     var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
//     var year  = digitSequence.AliasedAs("year");
//     var month = digitSequence.AliasedAs("month");
//
// Tenet: when the alias is Preserve (so it actually emits a Symbol),
// that Symbol behaves like the inner's Symbol would, except for its
// identity (Id, DisplayName, optional .WithError). That means:
//
//   * Same matched span, SourceText, SourceRange.
//   * Same content shape: a composite inner's children appear directly
//     under the alias, and a leaf inner's matched text appears on the
//     alias leaf.
//   * Same ToString rendering.
//   * Identity only differs. `result.Find(innerRule)` is null at the
//     aliased position, while `result.Find(aliasRule)` finds it. The
//     alias's own .WithError fires when the aliased match as a whole
//     fails. The inner's .WithError still fires on its own deep
//     failures inside.
//
// The tenet above applies only to the Preserve case where the alias
// actually asserts an identity in the tree:
//  - A Flatten alias is transparent: it emits a Flatten Symbol that a
//    later Flatten pass lifts away, so the inner's content surfaces in the
//    parent with no alias identity. 
//  - A Delete alias contributes nothing.
//
// Every case in TryParseRule below should preserve "same behavior, different
// identity" for the Preserve case. That single check is the easiest way
// to spot a regression or a corner case.
//
// Aliasing a LateBoundRule needs no special handling: the LateBoundRule
// returns the FlattenType and EmitsLeaf of its bound target, so
// `lateBound.AliasedAs("x")` builds the same tree as aliasing the target
// rule directly.
internal sealed class AliasRule : Rule
{
    private readonly Rule _inner;

    // An alias emits a leaf exactly when it substitutes its identity onto a
    // leaf inner: when the inner emits a leaf, this alias is Preserve (so it
    // actually asserts an identity in the tree), and the inner isn't Delete.
    // The base ctor's emitsLeaf:false default is never read because this getter
    // overrides it, which is the same pattern LateBoundRule uses for a forwarded value.
    //
    // This matters when an alias wraps another alias. A parent alias asks its
    // inner's EmitsLeaf to decide whether to substitute. If this reported the
    // base default (false), aliasing an alias would wrap the inner alias's
    // leaf in an extra composite level and leave the inner alias's identity
    // findable in the tree, both of which break the tenet above. Returning
    // the live condition below instead keeps `leaf.AliasedAs("a").AliasedAs("b")`
    // collapsing to one leaf carrying b's identity, exactly as a single alias
    // of the leaf would.
    public override bool EmitsLeaf =>
        _inner.EmitsLeaf
        && FlattenType == FlattenType.Preserve
        && _inner.FlattenType != FlattenType.Delete;

    public AliasRule(Rule inner)
        : base(FlattenType.Flatten, emitsLeaf: false, inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        _inner = inner;
    }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        int matchStart = startPosition;

        // When emitting our own Symbol (Preserve), collect child output symbols
        // into a fresh list that becomes our Symbol's children. When flattening, write directly
        // into the caller's list. When deleted, the framework gave us null and
        // we don't need to collect anything (the inner still runs so its match
        // advances the lexer).
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>();

        var innerSymbol = ParseChild(_inner, lexer, outputSymbols);
        if (innerSymbol == null)
        {
            TraceFailure(lexer, $"inner failed");
            // Record the alias's own .WithError, floored at the alias's
            // start (matchStart, where the inner rolled back). See
            // docs/ErrorArchitecture.md for how RecordCompositeFailure
            // anchors it.
            lexer.RecordCompositeFailure(matchStart, ErrorMessage, ErrorForced);
            return null;
        }

        // OK, so we succeeded. Now we need to build a proper tree for both the PreserveAllSymbols
        // case so that the tree will collapse appropriately when the user calls Flatten, and the
        // normal case where we build the tree in its final form from the outset
        //
        // Leaf inner + Preserved alias → the alias is replacing the identity of the leaf.
        //                                emit one alias leaf with the matched text.
        //                                aliasEmitsLeaf = true
        // Otherwise → emit an alias composite around whatever the inner contributed.
        //             aliasEmitsLeaf = false
        bool aliasEmitsLeaf = false;
        if (effectiveFlattenType != FlattenType.Delete)
        {
            Invariant.That(outputSymbols != null,
                $"AliasRule entered the content-build block with outputSymbols=null while effectiveFlattenType={effectiveFlattenType}. The framework should have allocated a list for any non-Delete effective type.");

            // First condition asks "should we substitute?". It reads
            // EmitsLeaf (computed above from the inner's shape and this
            // alias's FlattenType), the same property a parent alias reads
            // to decide about this one, so both decisions stay in sync. The
            // answer doesn't change when PreserveAllSymbols forces every
            // inner to act Preserve.
            if (EmitsLeaf)
            {
                // This alias emits one leaf carrying its own identity. Its
                // text is rebuilt from matchedSpan below (matchStart..
                // lexer.Position covers the same characters the inner
                // matched), so the inner's leaf in outputSymbols is
                // a redundant copy. Clear it so we don't emit both.
                aliasEmitsLeaf = true;
                outputSymbols!.Clear();
            }
            // First question: did the inner hand back its own Symbol?
            // It does only when it ran effective Preserve.
            else if (!ReferenceEquals(innerSymbol, Symbol.Discarded))
            {
                // Second question: is this a content-contributing inner, or a
                // Delete inner that only returned a Symbol because
                // PreserveAllSymbols forced it to act Preserve? Read the
                // declared FlattenType, not the effective one, to tell apart.
                if (_inner.FlattenType != FlattenType.Delete)
                {
                    // A Preserve inner. Take its content as the alias's own:
                    // a leaf adds itself, a composite lifts its children up
                    // under the alias.
                    if (innerSymbol.IsLeaf)
                        outputSymbols!.Add(innerSymbol);
                    else
                        foreach (var child in innerSymbol.Children)
                            outputSymbols!.Add(child);
                }
                else
                {
                    // A declared-Delete inner. In production it contributes
                    // nothing, so contribute nothing here too. Skipping it
                    // keeps the debug tree flattening back to the production
                    // tree instead of carrying children production drops.
                }
            }
            else
            {
                // The inner returned Discarded: a Flatten inner already wrote
                // its content into outputSymbols, or a Delete inner contributed
                // nothing. Either way, nothing to do.
            }
        }

        TraceSuccess(lexer, $"inner matched");
        int matchLength = lexer.Position - matchStart;

        if (effectiveFlattenType != FlattenType.Preserve)
            return Symbol.Discarded;

        var matchedSpan = lexer.Input.AsMemory(matchStart, matchLength);
        return aliasEmitsLeaf
            ? new Symbol(Id, FlattenType, matchedSpan, lexer.Context)
            : CreateCompositeFromOwnedChildren(outputSymbols, matchedSpan, lexer.Context);
    }

}
