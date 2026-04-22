using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Positive lookahead. Runs its inner rule against the current input,
// rolls back the lexer regardless of what inner did, and succeeds iff
// inner SUCCEEDED. Consumes no input on either path.
//
// Useful when a rule needs to confirm that specific content is ahead
// without actually consuming it. For example, an if-statement rule that
// wants to check for the "else" keyword without committing the lexer:
// Peek(Literal("else")).
internal sealed class PeekRule : Rule
{
    public PeekRule(Rule inner) : base(FlattenType.Delete, inner)
    {
        if (inner == null) throw new ArgumentNullException(nameof(inner));
    }

    private Rule Inner => Children[0];

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Lookahead only: inner's result is thrown away.
        using var transaction = lexer.BeginTransaction();
        var innerResult = Inner.TryParse(lexer, outputSymbols: null);
        if (innerResult == null)
        {
            TraceFailure(lexer, $"inner did not match");
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"inner matched");
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
        // never advances the lexer. FirstConsumedRunes is Empty
        return new RuleStartRequirements(RuneSet.Empty, Advance.Never);
    }
}
