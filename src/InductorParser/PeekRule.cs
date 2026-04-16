using System;
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

    internal override Symbol? TryParseRule(Lexer lexer)
    {
        using var transaction = lexer.BeginTransaction();
        var innerResult = Inner.TryParse(lexer);
        if (innerResult == null)
        {
            TraceFailure(lexer, $"inner did not match");
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"inner matched");
        // The `using` rolls back the lexer on exit (no Commit), so even
        // though inner advanced the cursor during its TryParse, we're
        // back where we started. Zero-width success.
        return new Symbol(Id, FlattenType, Array.Empty<Symbol>());
    }
}
