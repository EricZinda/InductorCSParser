using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches the first child that succeeds. Tries
// children left-to-right, committing to whichever one matches first.
// If none match, the FirstOf fails. Each child attempt runs in its own
// transaction so a failed alternative leaves the lexer where it was
// before FirstOf was called.
internal sealed class FirstOfRule : Rule
{
    public FirstOfRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        string input = lexer.Input;
        int pos = lexer.Position;
        // Peek the next rune once for the skip shortcut. At EOF or on a
        // malformed surrogate, use -1 (a value no FirstConsumedTokens can
        // contain) so an Always child is still correctly skipped: it would
        // need to read a rune and there isn't one.
        int peekValue = -1;
        if (pos < input.Length)
            Lexer.TryPeekRune(input, pos, out peekValue, out _);

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
            if (child.CannotMatchLookahead(peekValue) && child.ErrorMessage == null)
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

    // FirstOf matches exactly one branch on success. If every branch
    // has at least one required literal (single or its own multi-literal
    // set), the union of those literals is required by FirstOf as a
    // whole: any successful match is guaranteed to contain one of them
    // as a substring. If even one branch has no analyzable literal, the
    // union doesn't hold and we return null.
    internal override IReadOnlyList<(string Text, bool IgnoreCase)>? ComputeRequiredLiteralAlternatives()
    {
        if (Children.Count == 0) return null;
        var union = new List<(string Text, bool IgnoreCase)>();
        foreach (var child in Children)
        {
            var childSet = child.ComputeRequiredLiteralAlternatives();
            if (childSet != null && childSet.Count > 0)
            {
                union.AddRange(childSet);
                continue;
            }
            var single = child.ComputeRequiredLiteral();
            if (single == null || single.Value.Text.Length == 0)
                return null;
            union.Add(single.Value);
        }
        return union.Count == 0 ? null : union;
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (TokenSet.Empty when Advance.Never. TokenSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // FirstOf matches any of its children, so its FirstConsumedTokens is the
        // union of children's FirstConsumedTokens.
        //
        // Advance:
        //   Always:    If every child advances then FirstOf always advances too
        //   Never:     If no child advances then FirstOf never advances
        //   Sometimes: If mixed (FirstOf matches are in different classes) then FirstOf
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
