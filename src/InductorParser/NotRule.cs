using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Negative lookahead. Runs its inner rule against the current input,
// rolls back the lexer regardless of what inner did, and succeeds iff
// inner FAILED. Consumes no input on either path.
//
// Idiom: Not(stopRule) combined with AnyToken() is the rule-based "match
// everything up to the stop condition" pattern:
//
//     ZeroOrMore(And(Not(stopRule), AnyToken()))
//
// Each iteration checks that stopRule doesn't match here, then consumes
// one token and advances. When stopRule would match, Not fails, the
// And fails, and the ZeroOrMore stops leaving the cursor at the stop.
internal sealed class NotRule : Rule
{
    // FlattenType.Delete because Not is a zero-width lookahead: it
    // contributes no text to the parse tree. Delete ensures the empty
    // Symbol disappears during FlattenInto so it doesn't leave a marker
    // node in the syntax tree.
    public NotRule(Rule inner) : base(FlattenType.Delete, inner)
    {
        if (inner == null) throw new ArgumentNullException(nameof(inner));
    }

    private Rule Inner => Children[0];

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Lookahead only: inner's result is thrown away regardless, so we
        // pass null for outputSymbols. If inner has FlattenType.Flatten
        // (which normally writes its children into a caller-supplied
        // list), Rule.TryParse gives it a throwaway list that nobody
        // reads.
        using var transaction = lexer.BeginTransaction();
        var failureSnapshot = lexer.SaveFailureState();
        var innerResult = Inner.TryParse(lexer, outputSymbols: null);
        // Inner ran as a throwaway probe. Discard the failures and
        // the subtree extent it produced, succeed or fail: those
        // positions were only probed, never consumed. Keeping them would
        // let a probe's deep excursion outrank the real parse's failures.
        // See docs/ErrorArchitecture.md, "Lookahead failures are discarded".
        lexer.RestoreFailureState(failureSnapshot);
        lexer.DiscardSubtreeExtent();
        if (innerResult != null)
        {
            TraceFailure(lexer, $"inner matched");
            // Not records one failure of its own, at the lookahead anchor
            // (its own start, where the user has to change something),
            // carrying its .WithError if it has one. This is the
            // exception to composite anchoring: there is no surviving
            // descendant failure to anchor to. See docs/ErrorArchitecture.md.
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"inner didn't match");
        transaction.Commit();
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>(), lexer.Input.AsMemory(transaction.StartPosition, 0), lexer.Context)
            : Symbol.Discarded;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.NeverAdvances;
}
