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
    public NotRule(Rule inner)
        : base(FlattenType.Delete, emitsLeaf: false, inner ?? throw new ArgumentNullException(nameof(inner)))
    {
    }

    private Rule Inner => Children[0];

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Lookahead only: inner's result is thrown away regardless, so we
        // pass null for outputSymbols. If inner has FlattenType.Flatten
        // (which normally writes its children into a caller-supplied
        // list), Rule.TryParse gives it a throwaway list that nobody
        // reads. BeginProbe brackets the position, the failure tracker,
        // and the subtree-extent mark, and with no Commit restores all
        // three on Dispose, so inner's probe leaves nothing behind
        // whether it matched or not. See docs/ErrorArchitecture.md,
        // "Lookahead failures are discarded".
        bool innerMatched;
        using (lexer.BeginProbe())
        {
            innerMatched = ParseRuleAgainst(Inner, lexer, outputSymbols: null) != null;
        }
        if (innerMatched)
        {
            TraceFailure(lexer, $"inner matched");
            // Not records one failure of its own, at the lookahead anchor
            // (its own start, where the user has to change something),
            // carrying its .WithError if it has one. This is the
            // exception to composite anchoring: there is no surviving
            // descendant failure to anchor to. See docs/ErrorArchitecture.md.
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"inner didn't match");
        // Not is zero-width. Inner failed, so its transaction already
        // rolled the cursor back to startPosition. Returning non-null
        // makes Rule.TryParse commit that unchanged position.
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>(), lexer.Input.AsMemory(startPosition, 0), lexer.Context)
            : Symbol.Discarded;
    }

}
