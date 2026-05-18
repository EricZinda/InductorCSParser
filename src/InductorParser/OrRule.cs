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
        // Outer transaction wraps every branch attempt. Whether the Or
        // succeeds or fails, the failures its branches produced are kept:
        // a rejected branch's failure is a real near-miss, ranked by depth
        // like any other failure (see docs/ErrorArchitecture.md, Case 4).
        // The transaction also windows the subtree-extent high-water
        // mark, which the failure path reads back to anchor the Or's own
        // .WithError at the deepest position its branches reached.
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
                // Commit keeps the position the winning branch reached.
                // It doesn't clear the rejected branches' failures: a
                // rejected branch is the parser genuinely trying to read
                // the input, and its failure is a near-miss kept and ranked
                // by depth. See docs/ErrorArchitecture.md.
                outerTransaction.Commit();
                // Don't add child symbols if they're discarded
                if (outputSymbols != null && !ReferenceEquals(symbol, Symbol.Discarded))
                    outputSymbols.Add(symbol);
                return effectiveFlattenType == FlattenType.Preserve
                    ? new Symbol(Id, FlattenType, outputSymbols, lexer.Input.AsMemory(matchStart, matchLength), lexer.Context)
                    : Symbol.Discarded;
            }
        }
        TraceFailure(lexer, $"");
        // Every branch rolled back, so lexer.Position is back at the Or's
        // start. A .WithError on the Or anchors at the deepest position
        // any branch reached, not at that shallow start, so depth-primary
        // ranking can let the named failure win an exact-depth tie against
        // the branch's own deepest failure. See docs/ErrorArchitecture.md.
        int anchor = Math.Max(lexer.SubtreeDeepestFailure, startPosition);
        lexer.RecordFailure(anchor, ErrorMessage, ErrorForced);
        return null;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.MatchesAnyOf(Children);
}
