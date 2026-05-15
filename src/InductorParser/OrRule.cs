using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches the first child that succeeds. Tries children left-to-right,
// committing to whichever one matches first. If none match, the Or fails.
// Each child rule manages its own lexer rollback (per the TryParseRule
// contract), so a failed alternative naturally leaves the lexer where it
// was before Or called it.
internal sealed class OrRule : Rule
{
    public OrRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Outer transaction wraps every branch attempt. On success it
        // commits, clearing the rejected branches' records. On failure
        // it rolls back, keeping all records (so the Or's own failure
        // and each branch's records survive). See docs/ErrorArchitecture.md.
        using var outerTransaction = lexer.BeginTransaction();
        int startPosition = outerTransaction.StartPosition;

        // Peek the next token (one grapheme cluster, or one rune in
        // WithinToken sub-lexer mode) for the skip shortcut. peekFirstRune
        // is -1 at EOF (empty Chars) or when the cluster starts with a
        // stray surrogate; CannotMatchLookahead handles both.
        var peekToken = lexer.PeekToken();
        var peekChars = peekToken.Chars;
        int peekFirstRune = peekToken.FirstRune;

        // If we're preserving this node, create a new list to capture its outputSymbols
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>();

        for (int symbolIndex = 0; symbolIndex < Children.Count; symbolIndex++)
        {
            var child = Children[symbolIndex];
            // Skip children the shortcut proves can't match at this lookahead.
            // The HasErrorMessageInSubtree guard preserves WithError message
            // surfacing: a child whose own .WithError or any descendant's
            // .WithError would belong at the deepest-failure slot still gets
            // attempted so its failure chain can reach DeepestFailureMessage.
            // ErrorMessage == null alone would be a half-check: it would skip
            // a composite child whose own ErrorMessage is null even when a
            // deeper rule in its subtree carries the user's message, dropping
            // the message on the floor.
            if (child.CannotMatchLookahead(peekChars, peekFirstRune) && !child.HasErrorMessageInSubtree)
            {
                child.TraceShortcutSkip(lexer, peekChars);
                continue;
            }

            int matchStart = lexer.Position;
            var symbol = ParseChild(child, lexer, outputSymbols);
            if (symbol != null)
            {
                TraceSuccess(lexer, $"symbol #{symbolIndex}");
                int matchLength = lexer.Position - matchStart;
                // Or's commit clears records added during its run so
                // rejected branches' failures don't haunt later
                // failures. See docs/ErrorArchitecture.md.
                outerTransaction.Commit(clearFailureRecords: true);
                // Don't add child symbols if they're discarded
                if (outputSymbols != null && !ReferenceEquals(symbol, Symbol.Discarded))
                    outputSymbols.Add(symbol);
                return effectiveFlattenType == FlattenType.Preserve
                    ? new Symbol(Id, FlattenType, outputSymbols, lexer.Input.AsMemory(matchStart, matchLength), lexer.Context)
                    : Symbol.Discarded;
            }
        }
        TraceFailure(lexer, $"");
        lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
        return null;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.MatchesAnyOf(Children);
}
