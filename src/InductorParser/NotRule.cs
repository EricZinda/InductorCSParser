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

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Lookahead only: inner's result is thrown away regardless.
        // Pass null; shim will allocate a scratch if inner is Flatten.
        using var transaction = lexer.BeginTransaction();
        var innerResult = Inner.TryParse(lexer, outputSymbols: null);
        if (innerResult != null)
        {
            TraceFailure(lexer, $"inner matched");
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"inner did not match");
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>())
            : Symbol.Discarded;
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (RuneSet.Empty when Advance.Never; RuneSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first character on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // Zero-width predicate: rolls back regardless of inner result,
        // never advances the lexer. FirstConsumedRunes is Empty (it doesn't
        // consume anything, so the "starting rune" set is empty).
        return new RuleStartRequirements(RuneSet.Empty, Advance.Never);
    }
}
