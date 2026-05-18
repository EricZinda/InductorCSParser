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
        // on RuleStartRequirements.
        //
        // Gated on !Inner.HasErrorMessageInSubtree, not just Inner.ErrorMessage == null.
        // A descendant rule with .WithError has the same role as Inner's own .WithError:
        // its message belongs at the deepest-failure slot when the overall parse fails
        // here. Bypassing Inner.TryParse would skip past every RecordFailure call in
        // Inner's subtree, so the descendant's message never reaches DeepestFailureMessage
        // and the user sees the generic positional template instead of the grammar
        // author's text. This is true on both paths: the failure path drops the message
        // outright, and the success path (AtLeast == 0) silently swallows it because
        // Inner is never entered and so no failure is ever recorded for the descendant
        // to claim.
        if (!lexer.PreserveAllSymbols && !Inner.HasErrorMessageInSubtree)
        {
            var peekToken = lexer.PeekToken();
            if (Inner.CannotMatchLookahead(peekToken.Chars, peekToken.FirstRune))
            {
                Inner.TraceShortcutSkip(lexer, peekToken.Chars);
                if (AtLeast == 0)
                {
                    TraceSuccess(lexer, $"count= 0");
                    int emptyStart = transaction.StartPosition;
                    transaction.Commit();
                    return effectiveFlattenType == FlattenType.Preserve
                        ? new Symbol(Id, FlattenType, Array.Empty<Symbol>(), lexer.Input.AsMemory(emptyStart, 0), lexer.Context)
                        : Symbol.Discarded;
                }
                TraceFailure(lexer, $"count= 0");
                // Shortcut path: we never advanced, so lexer.Position
                // equals transaction.StartPosition. Record at lexer.Position
                // so the cursor lands where the user needs to fix the input.
                // See docs/ErrorArchitecture.md.
                lexer.RecordFailure(lexer.Position, ErrorMessage, ErrorForced);
                return null;
            }
        }

        // If we're preserving this node, create a new list to capture its outputSymbols
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>();
        int count = 0;
        while (count < AtMost)
        {
            scannerSkip?.Advance(lexer);
            int positionBefore = lexer.Position;
            var nextSymbol = ParseChild(Inner, lexer, outputSymbols);
            if (nextSymbol == null) break;
            bool zeroWidthMatch = lexer.Position == positionBefore;
            // Add the matched child to outputSymbols before deciding
            // whether to continue. Zero-width Inner that returns a real
            // wrapper Symbol (e.g. Not(X).Preserve() succeeding when X
            // fails, or any Preserve'd lookahead) would otherwise have
            // its wrapper silently dropped: the wrapper sits in
            // nextSymbol but never reaches the parent's children list.
            // AndRule adds children unconditionally for the same reason;
            // BetweenInclusive matches that behavior here.
            if (outputSymbols != null && !ReferenceEquals(nextSymbol, Symbol.Discarded))
                outputSymbols.Add(nextSymbol);
            // Zero-width-match guard. Inner succeeded but didn't advance the
            // lexer (e.g. Optional, Peek, Not, or any composite of zero-width
            // children). Without this break the loop would spin forever on
            // ZeroOrMore(Optional(X)) and friends, incrementing count without
            // making progress. Exit with whatever count we have. The AtLeast
            // check below decides if that's enough to call the rule a success.
            if (zeroWidthMatch) break;
            count++;
        }
        if (count < AtLeast)
        {
            TraceFailure(lexer, $"count= {count}");
            // Anchor a .WithError on this rule at the deepest position
            // its subtree reached. lexer.Position is the failing
            // iteration's start (the matched iterations advanced the
            // cursor, the failing one rolled back there) and is the
            // floor; an iteration that probed deeper before failing
            // pushes SubtreeDeepestFailure past it. See
            // docs/ErrorArchitecture.md.
            int anchor = Math.Max(lexer.SubtreeDeepestFailure, lexer.Position);
            lexer.RecordFailure(anchor, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"count= {count}");
        int matchStart = transaction.StartPosition;
        int matchLength = lexer.Position - matchStart;
        transaction.Commit();
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, outputSymbols, lexer.Input.AsMemory(matchStart, matchLength), lexer.Context)
            : Symbol.Discarded;
    }

    private ScannerSkip? TryCreateScannerSkip(Lexer lexer)
    {
        // Recognize scanner-style loops: ZeroOrMore(Or(match, AnyToken.Delete)).
        // The deleted AnyToken fallback means non-matching input would be thrown
        // away one token at a time, so we can jump directly to the next rune that
        // could start a real match without changing the emitted syntax tree.
        if (AtLeast != 0 || AtMost != int.MaxValue)
            return null;
        if (lexer.PreserveAllSymbols || lexer.IsTracing(TraceLevel.Normal))
            return null;
        if (Inner is not OrRule || Inner.ErrorMessage != null)
            return null;

        var alternatives = Inner.Children;
        if (alternatives.Count < 2)
            return null;

        Rule fallback = alternatives[alternatives.Count - 1];
        if (fallback is not AnyTokenRule
            || fallback.FlattenType != FlattenType.Delete
            || fallback.ErrorMessage != null)
            return null;

        TokenSet candidates = TokenSet.Empty;
        var literalCandidates = new List<LiteralScannerCandidate>();
        bool allCandidatesAreLiterals = true;
        for (int index = 0; index < alternatives.Count - 1; index++)
        {
            Rule alternative = alternatives[index];
            if (alternative.ErrorMessage != null || alternative.Advance != Advance.Always)
                return null;
            // MustNotBeIn polarity inverts the meaning of
            // FirstConsumedTokens: the published set is the rule's
            // FAIL-set, not its match-set. Unioning it into `candidates`
            // would direct the scanner to FAIL positions and silently
            // skip past every match position (e.g.
            // `Or(NoneOf("xy"), AnyToken().Delete())` would have the
            // scanner jump to 'x' / 'y' and the AnyToken fallback consume
            // them, while the runes the user wanted captured by NoneOf
            // are never read). Bail out so the slow path runs. Building
            // a sound candidate set under MustNotBeIn would need the
            // complement of the fail-set, which under grapheme
            // tokenization is unbounded.
            if (alternative.Polarity != Polarity.MustBeIn)
                return null;
            // AdvanceUntilRuneIn requires a rune-only candidate set
            // (multi-rune entries are silently invisible to its
            // IndexOfAny / Contains paths). Flatten via the
            // TokenSet.LookaheadFirstRunes view so each multi-rune
            // entry's first rune still pulls the scanner to a real
            // candidate position.
            candidates |= alternative.FirstConsumedTokens.LookaheadFirstRunes;

            // Optional stronger prefilter: if the real alternatives are all
            // literals, the scanner can skip false first-rune hits too. This
            // matters for ASCII ignore-case searches where the first-rune set
            // is broad (`S` or `s`) and common in normal text. If any branch
            // isn't a literal, keep the generic first-rune skip. It's less
            // aggressive but still safe for arbitrary grammar shapes.
            if (allCandidatesAreLiterals
                && !TryCollectLiteralScannerCandidates(alternative, literalCandidates))
            {
                allCandidatesAreLiterals = false;
                literalCandidates.Clear();
            }
        }

        if (candidates.IsEmpty || candidates == TokenSet.Universe)
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
                candidates.Add(new LiteralScannerCandidate(literal.ExpectedText!, ignoreAsciiCase: false));
                return true;

            case LiteralIgnoreAsciiCaseRule literal:
                candidates.Add(new LiteralScannerCandidate(literal.ExpectedText!, ignoreAsciiCase: true));
                return true;

            case OrRule orRule:
                if (orRule.Children.Count == 0)
                    return false;
                for (int index = 0; index < orRule.Children.Count; index++)
                {
                    if (!TryCollectLiteralScannerCandidates(orRule.Children[index], candidates))
                        return false;
                }
                return true;

            default:
                return false;
        }
    }

    private readonly record struct ScannerSkip(
        TokenSet Candidates,
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

    internal override RuleStartRequirements ComputeRuleStart()
    {
        // Inner defines the first-token set. Advance follows Inner's,
        // except AtLeast == 0 (Optional / ZeroOrMore) lets us succeed
        // without advancing, downgrading Inner.Always to Sometimes. An
        // Inner.Never stays Never since zero matches plus a non-
        // advancing inner still consumes nothing.
        Advance advance = AtLeast == 0
            ? (Inner.Advance == Advance.Never ? Advance.Never : Advance.Sometimes)
            : Inner.Advance;
        return RuleStartRequirements.PassesThroughTo(Inner).WithAdvance(advance);
    }
}
