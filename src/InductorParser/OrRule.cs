using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches the first child that succeeds. Tries
// children left-to-right, committing to whichever one matches first.
// If none match, the Or fails. Each child attempt runs in its own
// transaction so a failed alternative leaves the lexer where it was
// before Or was called.
internal sealed class OrRule : Rule
{
    public OrRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        string input = lexer.Input;
        int pos = lexer.Position;
        // Peek the next rune once for the skip shortcut. Two cases get the
        // peekValue = -1 marker: EOF (no rune to read) and a lone surrogate
        // (TryPeekRune returns false because a surrogate half isn't a valid
        // scalar). The shortcut is sound for EOF: an Always child there has
        // nothing to read and is correctly skipped. It is NOT sound for a
        // lone surrogate, which is still a one-char token a wildcard child
        // like AnyToken can match. Track which case we're in so the shortcut
        // applies at EOF or on a real rune but not on a lone surrogate.
        // Mirrors BetweenInclusiveRule, which short-circuits on the same
        // TryPeekRune bool return.
        int peekValue = -1;
        bool loneSurrogate = false;
        if (pos < input.Length)
            loneSurrogate = !Lexer.TryPeekRune(input, pos, out peekValue, out _);

        // If we're preserving this node, create a new list to capture its outputSymbols
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>();

        for (int symbolIndex = 0; symbolIndex < Children.Count; symbolIndex++)
        {
            var child = Children[symbolIndex];
            // Skip children the shortcut proves can't match at this lookahead.
            // The ErrorMessage == null guard preserves WithError message
            // surfacing: a child with a friendly message still gets attempted
            // so its failure can reach DeepestFailureMessage.
            if (!loneSurrogate && child.CannotMatchLookahead(peekValue) && child.ErrorMessage == null)
            {
                continue;
            }

            using var transaction = lexer.BeginTransaction();
            var symbol = ParseChild(child, lexer, outputSymbols);
            if (symbol != null)
            {
                TraceSuccess(lexer, $"symbol #{symbolIndex}");
                transaction.Commit();
                // Don't add child symbols if they're discarded
                if (outputSymbols != null && !ReferenceEquals(symbol, Symbol.Discarded))
                    outputSymbols.Add(symbol);
                return effectiveFlattenType == FlattenType.Preserve
                    ? new Symbol(Id, FlattenType, outputSymbols)
                    : Symbol.Discarded;
            }
        }
        TraceFailure(lexer, $"");
        lexer.RecordFailure(lexer.Position, ErrorMessage);
        return null;
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (TokenSet.Empty when Advance.Never. TokenSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // Or matches any of its children, so its FirstConsumedTokens is the
        // union of children's FirstConsumedTokens.
        //
        // Advance:
        //   Always:    If every child advances then Or always advances too
        //   Never:     If no child advances then Or never advances
        //   Sometimes: If mixed (Or matches are in different classes) then Or
        //               might or might not advance depending on branch.
        TokenSet union = TokenSet.Empty;
        bool allAlways = Children.Count > 0;
        bool allNever = Children.Count > 0;
        foreach (var child in Children)
        {
            union |= child.FirstConsumedTokens;
            if (child.Advance != Advance.Always) allAlways = false;
            if (child.Advance != Advance.Never) allNever = false;
        }
        Advance advance = allAlways
            ? Advance.Always
            : allNever ? Advance.Never : Advance.Sometimes;
        return new RuleStartRequirements(union, advance);
    }
}
