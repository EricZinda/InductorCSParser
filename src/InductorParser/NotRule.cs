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
            // Inner's contribution to the deepest marker -- including
            // orphan records from non-taken Or alternatives explored
            // before a later alternative succeeded -- is from a path
            // the lookahead never committed to, so it shouldn't outvote
            // the anchored WithError. force: true unconditionally pins
            // the deepest position to Not's anchor and the user's
            // message into the message slot. Without it, an Or whose
            // first alternative consumes some input before failing
            // leaves the deepest at the orphan position and the user's
            // "did not want X here" is silently dropped, with the
            // reported position pointing somewhere inside the X that
            // inner went hunting for.
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage, force: ErrorMessage != null);
            return null;
        }
        TraceSuccess(lexer, $"inner didn't match");
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>())
            : Symbol.Discarded;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.NeverAdvances;
}
