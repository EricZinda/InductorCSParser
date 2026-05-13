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
            // The lexer position rolls back via the `using` transaction,
            // but the deepest-failure marker normally survives rule
            // rollback (Or's failed alternatives contribute to it on
            // purpose). Inner is conceptually rolled back along with
            // the lexer, so its contribution should not outvote the
            // anchored WithError. force: true unconditionally pins the
            // deepest position to our anchor and the user's message into
            // the message slot, regardless of how far inner ran or what
            // it recorded. Without it, a multi-token inner like
            // Literal("ab") leaves the deepest at offset 1 and the
            // user's "expected 'ab' ahead" is silently dropped, with
            // the reported position pointing past where the lookahead
            // was anchored.
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage, force: ErrorMessage != null);
            return null;
        }
        TraceSuccess(lexer, $"inner matched");
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>())
            : Symbol.Discarded;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.NeverAdvances;
}
