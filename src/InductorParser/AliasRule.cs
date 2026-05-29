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
// identity (Id, Name, optional .WithError). That means:
//
//   * Same matched span, SourceText, SourceRange.
//   * Same content shape: a composite inner's children appear directly
//     under the alias; a leaf inner's matched text appears on the alias
//     leaf.
//   * Same ToString rendering.
//   * Identity only differs: `result.Find(innerRule)` is null at the
//     aliased position; `result.Find(aliasRule)` finds it. The alias's
//     own .WithError fires when the alias-wrapped match as a whole
//     fails; the inner's .WithError still fires on its own deep
//     failures inside.
//
// A Flatten alias is transparent: it emits a Flatten Symbol that
// post-hoc Flatten lifts away, so the inner's content surfaces in the
// parent with no alias identity. A Delete alias contributes nothing.
// The tenet above applies only to the Preserve case where the alias
// actually asserts an identity in the tree.
//
// Every case in TryParseRule should preserve "same behavior, different
// identity" for the Preserve case. That single check is the easiest way
// to spot a regression or a corner case.
//
// Aliasing a LateBoundRule needs no special handling: the LateBoundRule
// forwards FlattenType and EmitsLeaf to its bound target, so
// `lateBound.AliasedAs("x")` builds the same tree as aliasing the target
// rule directly.
internal sealed class AliasRule : Rule
{
    private readonly Rule _inner;

    public AliasRule(Rule inner)
        : base(FlattenType.Flatten, emitsLeaf: false, inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        _inner = inner;
    }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        int matchStart = startPosition;

        // When emitting our own Symbol (Preserve), collect into a fresh list
        // that becomes the Symbol's children. When flattening, write directly
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

        // Leaf inner + Preserved alias → emit one alias leaf with the matched text.
        // Otherwise → emit an alias composite around whatever the inner contributed.
        bool aliasEmitsLeaf = false;
        if (effectiveFlattenType != FlattenType.Delete)
        {
            Invariant.That(outputSymbols != null,
                $"AliasRule entered the content-build block with outputSymbols=null while effectiveFlattenType={effectiveFlattenType}; the framework should have allocated a list for any non-Delete effective type.");

            // First condition asks "should we substitute?" via the rule's
            // declared semantics. Static check: the answer must be the same
            // regardless of PreserveAllSymbols forcing every inner to act
            // Preserve.
            if (_inner.EmitsLeaf
                && FlattenType == FlattenType.Preserve
                && _inner.FlattenType != FlattenType.Delete)
            {
                // The alias leaf's text comes from the matchedSpan
                // computed below: the lexer already advanced through the
                // inner's match, so matchStart..lexer.Position covers the
                // same characters the inner leaf carried. Drop whatever
                // the inner contributed (innerSymbol or its leaf in
                // outputSymbols).
                aliasEmitsLeaf = true;
                outputSymbols!.Clear();
            }
            // Second condition asks "did the inner return its own Symbol
            // for us to incorporate?". Runtime question: the inner returns
            // its own Symbol only when it ran Preserve effective. Under
            // Flatten or Delete effective it returns Symbol.Discarded
            // (Flatten already wrote its content into outputSymbols, and
            // Delete contributed nothing).
            else if (!ReferenceEquals(innerSymbol, Symbol.Discarded))
            {
                if (innerSymbol.IsLeaf)
                    outputSymbols!.Add(innerSymbol);
                else
                    foreach (var child in innerSymbol.Children)
                        outputSymbols!.Add(child);
            }
            else
            {
                // Inner returned Discarded: it already wrote its content
                // into outputSymbols (Flatten inner) or contributed
                // nothing (Delete inner). Either way, nothing to do.
            }
        }

        TraceSuccess(lexer, $"inner matched");
        int matchLength = lexer.Position - matchStart;

        if (effectiveFlattenType != FlattenType.Preserve)
            return Symbol.Discarded;

        var matchedSpan = lexer.Input.AsMemory(matchStart, matchLength);
        return aliasEmitsLeaf
            ? new Symbol(Id, FlattenType, matchedSpan, lexer.Context)
            : new Symbol(Id, FlattenType, outputSymbols, matchedSpan, lexer.Context);
    }

}
