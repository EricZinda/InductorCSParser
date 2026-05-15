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
// At parse time, AliasRule runs its inner rule and produces a Symbol
// carrying the alias's own Id, with the inner's matched content as that
// Symbol's children. If the inner rule is Preserve (the default for any
// named rule, since .As(name) auto-flips to Preserve), the inner would
// normally produce its own Symbol carrying its own Id. AliasRule drops
// that Symbol and emits its own Symbol in place of it, rather than
// nesting the two: the inner's identity is hidden when accessed through
// this alias path.
//
// If the inner is Flatten or Delete (the inner emits no Symbol of its
// own), there's nothing to drop and the alias's Symbol just sits over
// the inner's content directly.
//
// AliasRule supports the standard .As(string) / .As(SymbolId) / .Flatten
// / .WithError modifiers via the base implementation.
//
// Corner case: aliasing a LateBoundRule whose target is itself Preserve
// shows the target as a layer under the alias, because LateBoundRule's
// transparent forwarding writes the target's Symbol into the parent's
// collection list before AliasRule sees it. Aliasing the target rule
// directly avoids the extra layer.
public sealed class AliasRule : Rule
{
    private readonly Rule _inner;

    public AliasRule(Rule inner) : base(FlattenType.Flatten, inner)
    {
        if (inner == null) throw new ArgumentNullException(nameof(inner));
        _inner = inner;
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        int matchStart = transaction.StartPosition;

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
            lexer.RecordFailure(lexer.Position, ErrorMessage);
            return null;
        }

        // Rebadge: when the inner is Preserve it returned its own Symbol
        // carrying its Id. Drop that Symbol and lift its children into our
        // list, so the parse tree shows the alias's identity in place of the
        // inner's. ParseChild already wrote the inner's content into our list
        // when the inner was Flatten (in which case innerSymbol is Discarded),
        // so we only handle the non-Discarded return here.
        if (!ReferenceEquals(innerSymbol, Symbol.Discarded) && outputSymbols != null)
        {
            foreach (var child in innerSymbol.Children)
                outputSymbols.Add(child);
        }

        TraceSuccess(lexer, $"inner matched");
        int matchLength = lexer.Position - matchStart;
        transaction.Commit();

        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, outputSymbols, lexer.Input.AsMemory(matchStart, matchLength), lexer.Context)
            : Symbol.Discarded;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.PassesThroughTo(_inner);
}
