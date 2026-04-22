using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches the first child that succeeds (PEG ordered choice). Tries
// children left-to-right, committing to whichever one matches first;
// if none match the Or fails. Each child attempt runs in its own
// transaction so a failed alternative leaves the lexer where it was
// before Or was called.
internal sealed class OrRule : Rule
{
    public OrRule(Rule[] children) : base(FlattenType.Flatten, children) { }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        string input = lexer.Input;
        int pos = lexer.Position;
        // Peek the next rune once for the skip shortcut. At EOF or on a
        // malformed surrogate, use -1 as a sentinel that no FirstConsumedRunes
        // contains, so an Always child is still correctly skipped (it would
        // need to read a rune and there isn't one).
        int peekValue = -1;
        if (pos < input.Length)
            Lexer.TryPeekRune(input, pos, out peekValue, out _);

        // If we are preserving this node, create a new list to capture its outputSymbols
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
                // Don't add child symbols if they are discarded
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

    // See the FirstConsumedRunes / Advance field docs on Rule for more information on what this does.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // Or matches any of its children, so its FirstConsumedRunes is the
        // union of children's FirstConsumedRunes.
        //
        // Advance:
        //   Always    — every child advances. Or always advances too.
        //   Never     — no child advances. Or never advances.
        //   Sometimes — mixed (or matches are in different classes). Or
        //               might or might not advance depending on branch.
        RuneSet union = RuneSet.Empty;
        bool allAlways = Children.Count > 0;
        bool allNever = Children.Count > 0;
        foreach (var child in Children)
        {
            union |= child.FirstConsumedRunes;
            if (child.Advance != Advance.Always) allAlways = false;
            if (child.Advance != Advance.Never) allNever = false;
        }
        Advance advance = allAlways
            ? Advance.Always
            : allNever ? Advance.Never : Advance.Sometimes;
        return new RuleStartRequirements(union, advance);
    }
}
