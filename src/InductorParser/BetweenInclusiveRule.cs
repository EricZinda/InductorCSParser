using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;

namespace InductorParser;

// BetweenInclusive(atLeast, atMost, inner) is the
// composite every other count rule reduces to. It greedily matches
// `inner` between `atLeast` and `atMost` times (both inclusive), fails
// if it can't reach `atLeast`, and stops once it reaches `atMost`.
//
// The named factories are thin wrappers:
//   OneOrMore(inner)      == BetweenInclusive(1, int.MaxValue, inner)
//   ZeroOrMore(inner)     == BetweenInclusive(0, int.MaxValue, inner)
//   Optional(inner)       == BetweenInclusive(0, 1, inner)
//   AtLeast(n, inner)     == BetweenInclusive(n, int.MaxValue, inner)
//   AtMost(n, inner)      == BetweenInclusive(0, n, inner)
//   Exactly(n, inner)     == BetweenInclusive(n, n, inner)
//
// Tests live in src/InductorParser.Tests/Rules/BetweenInclusiveRuleTests.cs.
// See docs/TestArchitecture.md for the per-rule test conventions
// (success, failure position, WithError propagation, positional fallback,
// sealed-rule rejection).
internal sealed class BetweenInclusiveRule : Rule
{
    internal int AtLeast { get; }
    internal int AtMost { get; }

    private Rule Inner => Children[0];

    public BetweenInclusiveRule(Rule inner, int atLeast, int atMost, string? traceName = null)
        : base(FlattenType.Flatten, inner)
    {
        if (inner == null)
            throw new ArgumentNullException(nameof(inner));
        if (atLeast < 0)
            throw new ArgumentOutOfRangeException(nameof(atLeast), atLeast,
                "atLeast must be non-negative.");
        if (atMost < atLeast)
            throw new ArgumentOutOfRangeException(nameof(atMost), atMost,
                "atMost must be greater than or equal to atLeast.");
        AtLeast = atLeast;
        AtMost = atMost;
        // When the caller supplies a friendly name (OneOrMore, ZeroOrMore,
        // Optional), the trace reads as the name the grammar author chose.
        // When no name is given (the general BetweenInclusive factory),
        // render the bounds so the reader sees what the rule actually does.
        // int.MaxValue renders as an empty upper bound: "[2..]".
        SetTraceName(traceName ?? BuildBoundsLabel(atLeast, atMost));
    }

    private static string BuildBoundsLabel(int atLeast, int atMost)
    {
        string upper = atMost == int.MaxValue ? "" : atMost.ToString();
        return $"BetweenInclusive[{atLeast}..{upper}]";
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        var scannerSkip = TryCreateScannerSkip(lexer);

        // First try to shortcut and exit fast using the "Rule Skip" shortcut described
        // on RuleStartRequirements
        if (!lexer.PreserveAllSymbols && Inner.ErrorMessage == null)
        {
            string input = lexer.Input;
            int pos = lexer.Position;
            if (pos < input.Length
                && Lexer.TryPeekRune(input, pos, out int peekValue, out _)
                && Inner.CannotMatchLookahead(peekValue))
            {
                if (AtLeast == 0)
                {
                    TraceSuccess(lexer, $"count= 0");
                    transaction.Commit();
                    return effectiveFlattenType == FlattenType.Preserve
                        ? new Symbol(Id, FlattenType, Array.Empty<Symbol>())
                        : Symbol.Discarded;
                }
                TraceFailure(lexer, $"count= 0");
                lexer.RecordFailure(lexer.Position, ErrorMessage);
                return null;
            }
        }

        // If we are preserving this node, create a new list to capture its outputSymbols
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>();
        int count = 0;
        while (count < AtMost)
        {
            scannerSkip?.Advance(lexer);
            int positionBefore = lexer.Position;
            var nextSymbol = ParseChild(Inner, lexer, outputSymbols);
            if (nextSymbol == null) break;
            // Zero-width-match guard. Inner succeeded but didn't advance the
            // lexer (e.g. Optional, Peek, Not, or any composite of zero-width
            // children). Without this break the loop would spin forever on
            // ZeroOrMore(Optional(X)) and friends, incrementing count without
            // making progress. Exit with whatever count we have. The AtLeast
            // check below decides if that's enough to call the rule a success.
            if (lexer.Position == positionBefore) break;
            // Don't add child symbols if they are discarded
            if (outputSymbols != null && !ReferenceEquals(nextSymbol, Symbol.Discarded))
                outputSymbols.Add(nextSymbol);
            count++;
        }
        if (count < AtLeast)
        {
            TraceFailure(lexer, $"count= {count}");
            lexer.RecordFailure(lexer.Position, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"count= {count}");
        transaction.Commit();
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, outputSymbols)
            : Symbol.Discarded;
    }

    private ScannerSkip? TryCreateScannerSkip(Lexer lexer)
    {
        // Recognize scanner-style loops: ZeroOrMore(FirstOf(match, AnyToken.Delete)).
        // The deleted AnyToken fallback means non-matching input would be thrown
        // away one token at a time, so we can jump directly to the next rune that
        // could start a real match without changing the emitted syntax tree.
        if (AtLeast != 0 || AtMost != int.MaxValue)
            return null;
        if (lexer.PreserveAllSymbols || lexer.IsTracing(TraceLevel.Normal))
            return null;
        if (Inner is not FirstOfRule || Inner.ErrorMessage != null)
            return null;

        var alternatives = Inner.Children;
        if (alternatives.Count < 2)
            return null;

        Rule fallback = alternatives[alternatives.Count - 1];
        if (fallback is not AnyTokenRule
            || fallback.FlattenType != FlattenType.Delete
            || fallback.ErrorMessage != null)
            return null;

        RuneSet candidates = RuneSet.Empty;
        var literalCandidates = new List<LiteralScannerCandidate>();
        bool allCandidatesAreLiterals = true;
        for (int index = 0; index < alternatives.Count - 1; index++)
        {
            Rule alternative = alternatives[index];
            if (alternative.ErrorMessage != null || alternative.Advance != Advance.Always)
                return null;
            candidates |= alternative.FirstConsumedRunes;

            // Optional stronger prefilter: if the real alternatives are all
            // literals, the scanner can skip false first-rune hits too. This
            // matters for ASCII ignore-case searches where the first-rune set
            // is broad (`S` or `s`) and common in normal text. If any branch
            // is not a literal, keep the generic first-rune skip; it is less
            // aggressive but still safe for arbitrary grammar shapes.
            if (allCandidatesAreLiterals
                && !TryCollectLiteralScannerCandidates(alternative, literalCandidates))
            {
                allCandidatesAreLiterals = false;
                literalCandidates.Clear();
            }
        }

        if (candidates.IsEmpty || candidates == RuneSet.Universe)
            return null;

        candidates.TryGetBmpChars(maxChars: 256, out var bmpCandidates);
        LiteralScannerCandidate[]? literals =
            allCandidatesAreLiterals && literalCandidates.Count > 0
                ? literalCandidates.ToArray()
                : null;
        // For a single literal, the substring-search cache jumps straight
        // to the next hit and amortizes across iterations. Multi-literal
        // alternates use the IndexOfAny path below: an attempt to enable
        // the cache for them measured 23x slower on the rebar Sherlock
        // haystack than IndexOfAny + per-position MatchesAt, because each
        // match consumed forces a re-search for every literal whose cached
        // position is now stale, and the BCL's IndexOfAny is SIMD-tuned
        // for "any of these chars" while N separate IndexOf calls don't
        // benefit from that vectorization. See
        // src/Benchmarks/Rebar/results/multi-literal-cache-rebar-2026-04-28.csv.
        return new ScannerSkip(
            candidates,
            bmpCandidates.Length == 0 ? null : bmpCandidates,
            literals,
            literals is { Length: 1 } ? CreateUnknownPositions(literals.Length) : null);
    }

    private static int[] CreateUnknownPositions(int length)
    {
        // Per-parse cache for the literal prefilter. -2 means "not searched
        // from the current lexer position yet"; -1 means "not found at or
        // after the searched position"; any non-negative value is the next
        // candidate position for that literal. The cache lives on the scanner
        // instance created for this parse, so it never leaks across inputs.
        var positions = new int[length];
        for (int index = 0; index < positions.Length; index++)
            positions[index] = -2;
        return positions;
    }

    private static bool TryCollectLiteralScannerCandidates(
        Rule rule,
        List<LiteralScannerCandidate> candidates)
    {
        if (rule.ErrorMessage != null || rule.Advance != Advance.Always)
            return false;

        switch (rule)
        {
            case LiteralRule literal:
                candidates.Add(new LiteralScannerCandidate(literal.Expected, ignoreAsciiCase: false));
                return true;

            case LiteralIgnoreAsciiCaseRule literal:
                candidates.Add(new LiteralScannerCandidate(literal.Expected, ignoreAsciiCase: true));
                return true;

            case FirstOfRule firstOfRule:
                if (firstOfRule.Children.Count == 0)
                    return false;
                for (int index = 0; index < firstOfRule.Children.Count; index++)
                {
                    if (!TryCollectLiteralScannerCandidates(firstOfRule.Children[index], candidates))
                        return false;
                }
                return true;

            default:
                return false;
        }
    }

    private readonly record struct ScannerSkip(
        RuneSet Candidates,
        char[]? BmpCandidates,
        LiteralScannerCandidate[]? Literals,
        int[]? LiteralPositions)
    {
        public void Advance(Lexer lexer)
        {
            if (Literals is { Length: > 0 })
            {
                lexer.AdvanceUntilLiteralCandidateIn(Candidates, BmpCandidates, Literals, LiteralPositions);
                return;
            }
            lexer.AdvanceUntilRuneIn(Candidates, BmpCandidates);
        }
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (RuneSet.Empty when Advance.Never. RuneSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override (string Text, bool IgnoreCase)? ComputeRequiredLiteral()
    {
        // When the lower bound forces at least one inner match, inner's
        // required literal flows through. AtLeast == 0 means the rule
        // can succeed without inner ever firing, so we can't promise
        // the literal will appear and have to return null.
        return AtLeast >= 1 ? Inner.ComputeRequiredLiteral() : null;
    }

    internal override (string Text, bool IgnoreCase)? ComputeConcatenableText()
    {
        // Only Exactly(n, fixedTextChild) gives a fixed-length
        // contribution (n copies of the inner's concatenable text).
        // Loose lower or upper bounds vary in length and break
        // concatenation.
        if (AtLeast != AtMost || AtLeast == 0) return null;
        var inner = Inner.ComputeConcatenableText();
        if (inner == null || inner.Value.Text.Length == 0) return null;
        // Cap the repetition at a small budget to avoid blowing up
        // memory on Exactly(huge, ...) edge cases. Twenty is plenty
        // for realistic prefilter literals.
        if (AtLeast > 20) return null;
        var builder = new System.Text.StringBuilder(inner.Value.Text.Length * AtLeast);
        for (int i = 0; i < AtLeast; i++)
            builder.Append(inner.Value.Text);
        return (builder.ToString(), inner.Value.IgnoreCase);
    }

    internal override RuleStartRequirements ComputeRuleStart()
    {
        // We need to return *all* runes that *might* be consumed as the first rune.
        // Then, we need to say if the first rune will Always/Sometimes/Never be consumed.
        //
        // For BetweenInclusive:
        // The set of runes is defined by Inner, so we just return those.
        // Inner defines whether the initial token is Always/Sometimes/Never consumed so we use that
        // *except* if atLeast is zero, because then we can
        // succeed and not advance. In that case, we are *at best* sometimes, but it depends on what inner
        // does. If they are Never, we will never advance. If they are Sometimes, we are sometimes.
        Advance advance;
        if (AtLeast == 0)
            advance = Inner.Advance == Advance.Never ? Advance.Never : Advance.Sometimes;
        else
            advance = Inner.Advance;
        return new RuleStartRequirements(Inner.FirstConsumedRunes, advance);
    }
}
