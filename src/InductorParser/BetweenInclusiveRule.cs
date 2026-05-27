using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

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

    internal override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
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
                    return effectiveFlattenType == FlattenType.Preserve
                        ? new Symbol(Id, FlattenType, Array.Empty<Symbol>(), lexer.Input.AsMemory(startPosition, 0), lexer.Context)
                        : Symbol.Discarded;
                }
                TraceFailure(lexer, $"count= 0");
                // Shortcut path: we never advanced, so lexer.Position
                // equals startPosition, and the rule never entered Inner,
                // so the subtree extent is still empty. Record at
                // lexer.Position (plain RecordFailure, not the composite
                // anchor) so the cursor lands where the user needs to fix
                // the input. See docs/ErrorArchitecture.md.
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
            // Record this rule's own .WithError, floored at the failing
            // iteration's start (lexer.Position, where that iteration
            // rolled back). See docs/ErrorArchitecture.md for how
            // RecordCompositeFailure anchors it.
            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"count= {count}");
        int matchLength = lexer.Position - startPosition;
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, outputSymbols, lexer.Input.AsMemory(startPosition, matchLength), lexer.Context)
            : Symbol.Discarded;
    }

    // Fast path for scanner-style loops shaped like
    //   ZeroOrMore(Or(real1, ..., realN, AnyToken().Delete()))
    // The loop walks a run of input, matching a "real" alternative where it
    // can and throwing away everything else one token at a time through the
    // deleted AnyToken fallback. Rather than invoke the Or once per thrown-
    // away token, we jump the cursor straight to the next token a real
    // alternative could start at and skip everything before.
    //
    // We can only skip a token if the tree wouldn't have gained a node for
    // it. That requires the AnyToken fallback to be Delete (so it
    // contributes no leaf) and the Or to not be Preserve (so it doesn't
    // wrap the empty fallback match in its own node). The gates below
    // enforce both.
    // The real alternatives never run on a skipped token, so their own output
    // and flatten policy don't matter here. They run only at the candidate
    // positions the skip stops at, exactly as they would without the skip,
    // which is also why a custom Rule alternative is safe as long as the
    // first-token metadata it publishes is correct.
    private ScannerSkip? TryCreateScannerSkip(Lexer lexer)
    {
        // Only ZeroOrMore-shaped loops qualify (atLeast 0, atMost unbounded); other bounds aren't this loop shape.
        if (AtLeast != 0 || AtMost != int.MaxValue)
            return null;
        // PreserveAllSymbols wants every grammar node in the tree, so we can't drop the ones the skip would jump over.
        if (lexer.PreserveAllSymbols)
            return null;
        // Tracing wants every per-token Or invocation visible in the trace, which a silent cursor jump would hide.
        if (lexer.IsTracing())
            return null;
        // Inner has to be an Or; that's the only loop shape this fast path models.
        if (Inner is not OrRule)
            return null;
        // A .WithError on the Or is user-attached error reporting, which only the slow path honors.
        if (Inner.ErrorMessage != null)
            return null;
        // A Preserve Or wraps every match in its own node, so even fallback matches leave empty nodes the skip would drop.
        if (Inner.FlattenType == FlattenType.Preserve)
            return null;

        var alternatives = Inner.Children;
        // Need at least one real alternative in addition to the fallback.
        if (alternatives.Count < 2)
            return null;
        // The last alternative has to be the throw-away branch (a deleted AnyToken with no .WithError) the skip stands in for.
        if (!IsDeletedAnyTokenFallback(alternatives[alternatives.Count - 1]))
            return null;

        // A non-fallback alternative was disqualified, or no useful candidate set emerged; either way the skip can't help here.
        if (!TryCollectScannerCandidates(alternatives, out TokenSet candidates, out LiteralScannerCandidate[]? literals))
            return null;

        candidates.TryGetBmpChars(maxChars: 256, out var bmpCandidates);
        return new ScannerSkip(
            candidates,
            bmpCandidates.Length == 0 ? null : bmpCandidates,
            literals,
            // A single literal uses the substring-search cache, which jumps
            // straight to the next hit and amortizes across iterations.
            // Multiple literals fall to the IndexOfAny path instead (the cache
            // is much slower for them, because each consumed match restales
            // the others' cached positions, and the BCL's IndexOfAny is
            // SIMD-tuned for "any of these chars").
            literals is { Length: 1 } ? CreateUnknownPositions(literals.Length) : null);
    }

    // The Or's last alternative is the scanner-skip's "throw this token away"
    // fallback. It only stays transparent when it emits nothing and records
    // no failure, which means exactly AnyToken().Delete() with no .WithError.
    private static bool IsDeletedAnyTokenFallback(Rule rule) =>
        rule is AnyTokenRule
        && rule.FlattenType == FlattenType.Delete
        && rule.ErrorMessage == null;

    // Collect the runes a real (non-fallback) alternative could start with;
    // the scanner jumps to the next position holding one of them. Returns
    // false (and the caller runs the slow loop) if any non-fallback
    // alternative isn't a plain positive, always-advancing matcher, or if the
    // collected set is empty or everything, where the skip can't help.
    private static bool TryCollectScannerCandidates(
        IReadOnlyList<Rule> alternatives,
        out TokenSet candidates,
        out LiteralScannerCandidate[]? literals)
    {
        candidates = TokenSet.Empty;
        literals = null;

        var literalCandidates = new List<LiteralScannerCandidate>();
        bool allCandidatesAreLiterals = true;
        for (int index = 0; index < alternatives.Count - 1; index++)
        {
            Rule alternative = alternatives[index];
            if (alternative.ErrorMessage != null || alternative.Advance != Advance.Always)
                return false;
            // MustNotBeIn polarity inverts the meaning of FirstConsumedTokens:
            // the published set is the rule's FAIL-set, not its match-set.
            // Unioning it into the candidates would point the scanner at FAIL
            // positions and skip past every match position (e.g.
            // Or(NoneOf("xy"), AnyToken().Delete()) would jump to 'x' / 'y'
            // and have the fallback consume them, while the runes NoneOf
            // wanted are never read). A sound candidate set under MustNotBeIn
            // would need the complement of the fail-set, which is unbounded
            // under grapheme tokenization, so bail to the slow path instead.
            if (alternative.Polarity != Polarity.MustBeIn)
                return false;
            // AdvanceUntilRuneIn needs a rune-only candidate set (multi-rune
            // entries are invisible to its IndexOfAny / Contains paths), so
            // project through LookaheadFirstRunes: each multi-rune entry's
            // first rune still pulls the scanner to a real candidate position.
            candidates |= alternative.FirstConsumedTokens.LookaheadFirstRunes;

            // Optional stronger prefilter: when every real alternative is a
            // literal, the scanner can also skip false first-rune hits (the
            // first-rune set for an ASCII ignore-case search like 'S' or 's'
            // is broad and common in normal text). If any branch isn't a
            // literal, drop the literal set and keep the generic first-rune
            // skip, which is less aggressive but safe for any grammar shape.
            if (allCandidatesAreLiterals
                && !TryCollectLiteralScannerCandidates(alternative, literalCandidates))
            {
                allCandidatesAreLiterals = false;
                literalCandidates.Clear();
            }
        }

        if (candidates.IsEmpty || candidates == TokenSet.Universe)
            return false;

        literals = allCandidatesAreLiterals && literalCandidates.Count > 0
            ? literalCandidates.ToArray()
            : null;
        return true;
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
