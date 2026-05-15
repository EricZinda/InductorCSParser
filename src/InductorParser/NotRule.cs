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
        var innerResult = Inner.TryParse(lexer, outputSymbols: null);
        if (innerResult != null)
        {
            TraceFailure(lexer, $"inner matched");
            // Record Not's own failure at the lookahead anchor.
            // See docs/ErrorArchitecture.md.
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"inner didn't match");
        // Commit Not's transaction and clear inner's failure records.
        // Inner's failure was the EXPECTED outcome (that's what Not
        // succeeding means), so its records would be noise rather
        // than diagnostic. See docs/ErrorArchitecture.md.
        transaction.Commit(clearFailureRecords: true);
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>(), lexer.Input.AsMemory(transaction.StartPosition, 0), lexer.Context)
            : Symbol.Discarded;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.NeverAdvances;
}
