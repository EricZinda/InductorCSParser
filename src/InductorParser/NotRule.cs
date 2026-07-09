using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Negative lookahead. Runs its inner rule against the current input,
// rolls back the lexer regardless of what inner did, and succeeds iff
// inner failed. Consumes no input on either path.
internal sealed class NotRule : Rule
{
    private Rule Inner => Children[0];

    // FlattenType.Delete because Not is a zero-width lookahead: it
    // contributes no text to the parse tree. 
    public NotRule(Rule inner)
        : base(FlattenType.Delete, emitsLeaf: false, inner ?? throw new ArgumentNullException(nameof(inner)))
    {
    }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Lookahead only: inner's result is thrown away either way, so pass
        // null for outputSymbols. BeginProbe brackets the position, the failure
        // tracker, and the subtree-extent mark and, with no Commit, restores
        // all three on Dispose, so inner's probe leaves nothing behind whether
        // it matched or not. See docs/ErrorArchitecture.md, "Lookahead failures
        // are discarded".
        bool innerMatched;
        using (lexer.BeginProbe())
        {
            innerMatched = ParseChild(Inner, lexer, outputSymbols: null) != null;
        }
        if (innerMatched)
        {
            TraceFailure(lexer, $"inner matched");
            // Not is failing here, because inner matched. A composite
            // normally anchors its error at the deepest failure among its
            // children, but Not has none to use: inner succeeded, so it left
            // no failure, and BeginProbe discarded its internal ones anyway.
            // So Not records its own failure at startPosition, the lookahead
            // point where the user would change the input to make the Not
            // succeed, including its .WithError if it has one. See
            // docs/ErrorArchitecture.md.
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"inner didn't match");
        // Not is zero-width. Inner failed, so its transaction already
        // rolled the cursor back to startPosition. Returning non-null
        // makes Rule.TryParse commit that unchanged position.
        return effectiveFlattenType == FlattenType.Preserve
            ? CreateCompositeFromOwnedChildren(Array.Empty<Symbol>(), lexer.Input.AsMemory(startPosition, 0), lexer.Context)
            : Symbol.Discarded;
    }

}
