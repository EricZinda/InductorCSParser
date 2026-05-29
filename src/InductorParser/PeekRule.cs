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
    public PeekRule(Rule inner)
        : base(FlattenType.Delete, inner ?? throw new ArgumentNullException(nameof(inner)))
    {
    }

    private Rule Inner => Children[0];

    protected internal override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Lookahead only: inner runs as a throwaway probe. BeginProbe
        // brackets the position, the failure tracker, and the
        // subtree-extent mark, and with no Commit restores all three on
        // Dispose, so inner's excursion leaves nothing behind. See
        // docs/ErrorArchitecture.md, "Lookahead failures are discarded".
        bool innerMatched;
        using (lexer.BeginProbe())
        {
            innerMatched = Inner.TryParse(lexer, outputSymbols: null) != null;
        }
        if (!innerMatched)
        {
            TraceFailure(lexer, $"inner didn't match");
            // Peek records one failure of its own, at the lookahead anchor
            // (its own start, where the user has to change something),
            // carrying its .WithError if it has one. This is the
            // exception to composite anchoring: there is no surviving
            // descendant failure to anchor to. See docs/ErrorArchitecture.md.
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"inner matched");
        // Peek is zero-width: the probe already rolled the lexer back to
        // startPosition, and Rule.TryParse's transaction commits that
        // unchanged position. Record a zero-length consumed span at the
        // anchor so the Symbol's bounds reflect the lookahead's position,
        // not where inner advanced to before rollback.
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>(), lexer.Input.AsMemory(startPosition, 0), lexer.Context)
            : Symbol.Discarded;
    }

}
