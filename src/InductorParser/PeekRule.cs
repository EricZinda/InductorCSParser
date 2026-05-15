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
            TraceFailure(lexer, $"inner didn't match");
            // Record Peek's own failure at the lookahead anchor.
            // See docs/ErrorArchitecture.md.
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"inner matched");
        // Peek is zero-width: the using transaction will roll the lexer
        // back to transaction.StartPosition on dispose. Record a
        // zero-length consumed span at that anchor so the Symbol's
        // bounds reflect the lookahead's position, not where inner
        // advanced to before rollback.
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>(), lexer.Input.AsMemory(transaction.StartPosition, 0), lexer.Context)
            : Symbol.Discarded;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.NeverAdvances;
}
