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
            // The ErrorMessage == null guard preserves WithError message
            // surfacing: a child with a friendly message still gets attempted
            // so its failure can reach DeepestFailureMessage.
            if (child.CannotMatchLookahead(peekChars, peekFirstRune) && child.ErrorMessage == null)
            {
                child.TraceShortcutSkip(lexer, peekChars);
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

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.MatchesAnyOf(Children);
}
