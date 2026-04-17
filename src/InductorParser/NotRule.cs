using System;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Negative lookahead. Runs its inner rule against the current input,
// rolls back the lexer regardless of what inner did, and succeeds iff
// inner FAILED. Consumes no input on either path.
//
// Idiom: Not(stopRule) combined with AnyChar() is the rule-based "match
// everything up to the stop condition" pattern:
//
//     ZeroOrMore(And(Not(stopRule), AnyChar()))
//
// Each iteration checks that stopRule doesn't match here, then consumes
// one character and advances. When stopRule would match, Not fails, the
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

    internal override Symbol? TryParseRule(Lexer lexer, bool discard)
    {
        // The transaction rolls back whether inner succeeded or failed.
        // We never Commit, so the `using` exit restores the lexer.
        using var transaction = lexer.BeginTransaction();
        var innerResult = Inner.TryParse(lexer);
        if (innerResult != null)
        {
            // Inner matched, which means Not fails.
            TraceFailure(lexer, $"inner matched");
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"inner did not match");
        // Zero-width success: no children, no consumed text. The Delete
        // flatten type keeps this from cluttering the syntax tree — and
        // with parse-time Delete filtering, Not returns the shared
        // Discarded sentinel on the common path instead of allocating a
        // fresh empty Symbol per negative-lookahead check.
        if (discard)
            return Symbol.Discarded;
        return new Symbol(Id, FlattenType, Array.Empty<Symbol>());
    }

    internal override RuleStart ComputeRuleStart()
    {
        // Zero-width predicate: rolls back regardless of inner result,
        // never advances the lexer. FirstConsumedRunes is Empty (it doesn't
        // consume anything, so the "starting rune" set is empty).
        return new RuleStart(RuneSet.Empty, Advance.Never);
    }
}
