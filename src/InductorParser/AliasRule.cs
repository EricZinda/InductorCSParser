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
//  - A Flatten alias (the state every alias starts in, until .As(...)
//    names it) is transparent: it delegates to the inner outright, the
//    way LateBoundRule delegates to its bound target, so the tree comes
//    out exactly as if the alias weren't written and `result.Find` on a
//    named inner still finds it. The one behavior a transparent alias
//    keeps for itself is its own .WithError, which records when the
//    aliased match fails as a whole.
//  - In a Delete alias the inner still runs and
//    consumes input, but contributes nothing to the tree.
//
// Every case in TryParseRule below should preserve "same behavior, different
// identity" for the Preserve case, and "same tree as no alias at all" for
// the Flatten case. Those two checks are the easiest way to spot a
// regression or a corner case.
//
// Aliasing a LateBoundRule needs no special handling: the LateBoundRule
// returns the FlattenType and EmitsLeaf of its bound target, so
// `lateBound.AliasedAs("x")` builds the same tree as aliasing the target
// rule directly.
internal sealed class AliasRule : Rule
{
    private readonly Rule _inner;

    // The question every decision in this class branches on: does this
    // alias assert an identity in the tree yet? Its own policy starts as
    // Flatten and stays there until .As(...) names it (flipping it to
    // Preserve) or .Delete() suppresses it. While it's Flatten there's
    // no identity to assert, so the alias is transparent. It reads
    // DeclaredFlattenType, not FlattenType, because the FlattenType
    // getter's answer depends on this one. 
    internal bool IsTransparent => DeclaredFlattenType == FlattenType.Flatten;

    // Exposed for LateBoundRule's FlattenType resolver, which walks
    // through transparent aliases to the first concrete rule.
    internal Rule Inner => _inner;

    // A transparent alias reports its inner's FlattenType, so every
    // parent sets up effectiveFlattenType and outputSymbols exactly as it
    // would for the inner written in the alias's place. Once the alias
    // asserts an identity (Preserve via .As, or Delete), it reports its
    // own policy.
    public override FlattenType FlattenType =>
        IsTransparent ? _inner.FlattenType : DeclaredFlattenType;

    // Transparent: forward the inner's, like FlattenType above.
    // Identity-asserting: true exactly when the alias substitutes its
    // identity onto a leaf inner, meaning the inner emits a leaf, this
    // alias is Preserve, and the inner isn't Delete. 
    public override bool EmitsLeaf =>
        IsTransparent
            ? _inner.EmitsLeaf
            : _inner.EmitsLeaf
              && DeclaredFlattenType == FlattenType.Preserve
              && _inner.FlattenType != FlattenType.Delete;

    public AliasRule(Rule inner)
        : base(FlattenType.Flatten, emitsLeaf: false, inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        _inner = inner;
    }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        if (IsTransparent)
        {
            // Delegate wholly to the inner, the way LateBoundRule forwards to its target.
            var innerResult = ParseChild(_inner, lexer, outputSymbols);
            if (innerResult == null)
            {
                // The alias keeps only its own .WithError: it records
                // when the aliased match fails as a whole, anchored at the
                // alias's start (startPosition, where the inner rolled back).
                // See docs/ErrorArchitecture.md for how RecordCompositeFailure
                // anchors it.
                TraceFailure(lexer, $"inner failed");
                lexer.RecordCompositeFailure(startPosition, ErrorMessage, ErrorForced);
                return null;
            }
            TraceSuccess(lexer, $"inner matched");
            return innerResult;
        }

        // Identity path: this alias is Preserve (emits its own Symbol) or
        // Delete (removes the match from the final tree). Transparent is
        // always Flatten and returned above, so the effective type here
        // can't be Flatten.
        Invariant.That(effectiveFlattenType != FlattenType.Flatten,
            $"AliasRule's identity path ran with effectiveFlattenType=Flatten while its own policy is {DeclaredFlattenType}. A Flatten alias is transparent and never reaches the identity path.");

        // When emitting our own Symbol (Preserve), collect child output
        // symbols into a fresh list that becomes our Symbol's children.
        // When deleted, the framework gave us null and we don't need to
        // collect anything (the inner still runs so its match advances
        // the lexer).
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>();
        int matchStart = startPosition;
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

        TraceSuccess(lexer, $"inner matched");

        // A Delete alias emits nothing. The inner still ran, so its
        // match consumed input.
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;

        var matchedSpan = lexer.Input.AsMemory(matchStart, lexer.Position - matchStart);

        // Leaf inner + Preserve alias: the alias is replacing the
        // identity of the leaf. Emit one alias leaf with the matched
        // text. outputSymbols is unused, so anything the inner wrote
        // there is discarded with it.
        if (EmitsLeaf)
            return new Symbol(Id, FlattenType, matchedSpan, lexer.Context);

        // Otherwise: emit an alias composite around whatever the inner
        // contributed. A Flatten inner already wrote its content into
        // outputSymbols. A Preserve inner (declared, or forced by
        // PreserveAllSymbols) handed back a Symbol representing itself,
        // placed below.
        if (!ReferenceEquals(innerSymbol, Symbol.Discarded))
        {
            if (innerSymbol.IsLeaf)
            {
                // A leaf we're not replacing: EmitsLeaf ruled
                // substitution out above.
                outputSymbols!.Add(innerSymbol);
            }
            else if (_inner.FlattenType == FlattenType.Delete)
            {
                // A Delete inner goes in whole: its node must stay
                // so the post-hoc Flatten pass drops it the way
                // production does.
                outputSymbols!.Add(innerSymbol);
            }
            else if (DeclaredFlattenType == FlattenType.Preserve)
            {
                // Per the tenet, a Preserve alias replaces a
                // composite inner's identity with its own: the
                // inner's node is dropped and its children lift
                // up under the alias.
                foreach (var child in innerSymbol.Children)
                    outputSymbols!.Add(child);
            }
            else
            {
                // A Delete alias, reached only under
                // PreserveAllSymbols, shows the inner whole in
                // the debug tree.
                outputSymbols!.Add(innerSymbol);
            }
        }
        return CreateCompositeFromOwnedChildren(outputSymbols, matchedSpan, lexer.Context);
    }

}
