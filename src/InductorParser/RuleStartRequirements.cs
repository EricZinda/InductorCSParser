using System;
using System.Collections.Generic;
using System.Globalization;

namespace InductorParser;

// RuleStarRequirements is the Return type of Rule.ComputeRuleStart. 
// It carries the three compile-time
// fields (FirstConsumedTokens, Advance, Polarity) that power the "can I
// skip this rule?" shortcut.
//
// ===== Definitive reference for the "can I skip this rule?" shortcut =====
//
// Before dispatching into a child rule, an enclosing rule can peek the next
// TOKEN (one grapheme cluster, the same unit the lexer hands out) and use
// these three fields to decide whether the child could possibly succeed. If
// not, it skips dispatch entirely, avoiding a wasted transaction/read/
// rollback cycle.
//
//   FirstConsumedTokens: The TokenSet of tokens (grapheme clusters)
//       the rule's first-token check refers to. Interpretation depends
//       on Polarity:
//
//         * MustBeIn: tokens this rule might consume as its first token.
//           For an Advance.Always rule, "peek is not in the set" implies
//           "rule definitely fails," so the shortcut skips it. "Peek is
//           in the set" is necessary but not sufficient: Literal "hello"
//           has 'h' in its set but still fails on "hxxx".
//         * MustNotBeIn: tokens this rule will definitely FAIL on. For
//           an Advance.Always rule, "peek is in the set" implies "rule
//           definitely fails," so the shortcut skips it. "Peek is not
//           in the set" is necessary but not sufficient: NoneOf might still
//           fail at EOF, on a longer multi-rune match, etc.
//
//       For Sometimes/Never rules the field is only compositional info
//       (see below). The shortcut requires Advance.Always to fire.
//
//   Advance: whether this rule moves past the lookahead token on
//       success: Always / Sometimes / Never (see Advance.cs).
//
//   Polarity: see the enum below. Default is MustBeIn.
//
// Callers that want to see if they can skip a rule because it can't possibly
// succeed don't inspect these fields directly. They call
// Rule.CannotMatchLookahead(peekTokenChars), which combines them as:
//
//     Advance == Always
//         && (Polarity == MustBeIn
//             ? !FirstConsumedTokens.ContainsToken(peek)
//             :  FirstConsumedTokens.ContainsToken(peek))
//
// Only Always makes the filter sound: Sometimes rules (e.g. ZeroOrMore)
// can sometimes (e.g. Optional) succeed without consuming lookahead,
// and Never rules (e.g. Peek, Not, Eof) never consume anything. In both
// cases CannotMatchLookahead returns false and the rule gets attempted.
//
// Advance serves two equally necessary roles:
//
//   1. Ensures we only skip rules that would truly fail. Without it,
//      Sometimes rules (which can succeed on any lookahead via zero-
//      match) and Never rules (whose Empty set would always exclude
//      the lookahead) would be wrongly skipped.
//
//   2. Composition. Parent rules (And/Or/BetweenInclusive) look at
//      their children's Advance when computing their own
//      FirstConsumedTokens. The three-way distinction tells a
//      composite "always claims the lookahead" (Always) vs. "might let the
//      next sibling claim it" (Sometimes) vs. "examines the lookahead without
//      claiming it" (Never):
//
//        * Always child. Say X.Advance == Always in And(X, Y): X
//          definitely reads the lookahead, Y reads later. The
//          composite stops at X since Y's FirstConsumedTokens is
//          irrelevant to the And's own.
//
//        * Sometimes child. Say Optional in And(Optional(X), Y):
//          Optional might match X or match zero. If it matches zero,
//          Y reads the lookahead. The composite has to combine both
//          X's and Y's FirstConsumedTokens (polarity-aware).
//
//        * Never child. Say Peek in And(Peek(X), Y): Peek never
//          consumes, so Y reads the lookahead. Peek's FirstConsumedTokens
//          is Empty and contributes nothing. Never tells the
//          composite "don't union my set into yours, move to the
//          next sibling."
//
// Notes:
//   * A superset of actual first-consumed tokens is safe under MustBeIn
//     (just slower). A subset would cause enclosing rules to wrongly
//     skip a rule that could succeed. TokenSet.Universe with MustBeIn
//     means "I don't know, don't filter me."
//   * Under MustNotBeIn, a subset of actual fail-tokens is safe (just
//     skips fewer than possible). A superset would cause enclosing
//     rules to wrongly skip a rule that could succeed. TokenSet.Empty
//     with MustNotBeIn means "I don't know, don't filter me."
//   * Advance.Never requires FirstConsumedTokens == TokenSet.Empty
//     (Compile enforces this). A rule that never consumes can't have
//     a set of "tokens it would consume first."
//   * MustNotBeIn requires Advance.Always (Compile enforces this). The
//     "peek IS in fail-set => skip" logic relies on the rule actually
//     attempting a consume on success.
//
// Why this algebra? It's essentially a tailored variant of LL(1)
// FIRST-set analysis from classical parser theory, extended with
// negation. In LL(1), every grammar symbol publishes a FIRST set
// (terminals that can begin any derivation from it) and a nullable
// flag. A predictive parser uses FIRST to dispatch. Nullability tells
// FIRST-composition "keep unioning past me." FIRST(XY) = FIRST(X) if
// X isn't nullable, else FIRST(X) ∪ FIRST(Y). Polarity lets us encode
// the dual ("FAIL set") for negation rules without losing the
// shortcut's precision.


// Membership polarity for FirstConsumedTokens. Most rules want
// MustBeIn: "the rule might consume only when the lookahead token is
// in this set." Negative rules like NoneOf want MustNotBeIn: "the
// rule will fail when the lookahead token IS in this set." The
// shortcut consults polarity to decide which direction the membership
// test should flip.
internal enum Polarity { MustBeIn, MustNotBeIn }

// Populated at Compile time. Rule's pessimistic defaults (Universe,
// Sometimes, MustBeIn) mean any user-defined Rule subclass that doesn't
// override ComputeRuleStart is safe: it'll never be shortcutted out.
internal readonly record struct RuleStartRequirements(
    TokenSet FirstConsumedTokens,
    Advance Advance,
    Polarity Polarity = Polarity.MustBeIn)
{
    // ===== Constants =====
    //
    // Each constant completes the sentence "my rule ___" and packages
    // the (set, advance, polarity) triple a rule with that shape would
    // publish. 
    //
    // "My rule NEVER ADVANCES" — examines the lookahead but doesn't
    // consume it. Advance.Never excludes the rule from the shortcut
    // by construction. Use for: Eof, Not, Peek.
    public static readonly RuleStartRequirements NeverAdvances =
        new(TokenSet.Empty, Advance.Never, Polarity.MustBeIn);

    // "My rule ALWAYS ADVANCES BY ONE TOKEN" — consumes exactly one
    // token, no constraint on which. Universe + Always + MustBeIn:
    // the shortcut never filters this rule because Universe accepts
    // every peek. Use for: AnyToken.
    public static readonly RuleStartRequirements AlwaysAdvancesByOneToken =
        new(TokenSet.Universe, Advance.Always, Polarity.MustBeIn);

    // "My rule MAY ADVANCE BY ANY TOKENS" — may consume zero or more
    // tokens, no upfront filter on what's accepted. Advance.Sometimes
    // excludes the rule from the shortcut by construction. Use for:
    // ScanUntil.
    public static readonly RuleStartRequirements MayAdvanceByAnyTokens =
        new(TokenSet.Universe, Advance.Sometimes, Polarity.MustBeIn);

    // "My rule['s] FIRST TOKEN MUST BE IN [this] SET". Consumes one
    // token on success (Advance.Always), polarity MustBeIn. The
    // shortcut skips this rule when the peek isn't in the set. Use
    // for: OneOf, ScanWhile.
    public static RuleStartRequirements FirstTokenMustBeInSet(TokenSet set) =>
        new(set, Advance.Always, Polarity.MustBeIn);

    // "My rule['s] FIRST TOKEN MUST NOT BE IN [this] SET". Consumes
    // one token on success (Advance.Always), polarity MustNotBeIn. The
    // shortcut skips this rule when the peek IS in the set. Use for:
    // NoneOf.
    public static RuleStartRequirements FirstTokenMustNotBeInSet(TokenSet set) =>
        new(set, Advance.Always, Polarity.MustNotBeIn);

    // "My rule['s] FIRST TOKEN MUST BE [the] FIRST GRAPHEME OF [this]
    // string". Extracts the first grapheme of `expected` and publishes
    // a TokenSet containing it (single-rune graphemes land in the
    // rune intervals, multi-rune graphemes in the multi-rune entries).
    // Falls back to AlwaysAdvancesByOneToken for surrogate-half
    // starts that StringInfo can't decode as a valid grapheme — same
    // fallback as the open-coded versions in
    // GraphemeRule/LiteralRule/LiteralIgnoreAsciiCase. Use for:
    // Grapheme, Literal.
    public static RuleStartRequirements FirstTokenMustBeFirstGraphemeOf(string expected)
    {
        try
        {
            string first = StringInfo.GetNextTextElement(expected, 0);
            if (first.Length == 0) return AlwaysAdvancesByOneToken;
            return FirstTokenMustBeInSet(TokenSet.Graphemes(first));
        }
        catch (ArgumentException)
        {
            return AlwaysAdvancesByOneToken;
        }
    }

    // "My rule PASSES THROUGH TO [this rule]" — propagates the source
    // rule's published (set, advance, polarity) triple unchanged. Use
    // for transparent proxy rules whose lookahead behavior mirrors
    // their target. Reads at the call site as "my rule passes through
    // to inner."
    public static RuleStartRequirements PassesThroughTo(Rule source) =>
        new(source.FirstConsumedTokens, source.Advance, source.Polarity);

    // Builder-style modifier: "...with [this] Advance instead." Drops
    // polarity to MustBeIn when newAdvance becomes non-Always
    // (MustNotBeIn requires Always per the invariant — a Sometimes/
    // Never rule with a fail-set doesn't make sense, see
    // ComputeRuleStartAll). Reads as a continuation of
    // PassesThroughTo: "my rule passes through to inner, with this
    // advance."
    public RuleStartRequirements WithAdvance(Advance newAdvance)
    {
        var polarity = newAdvance == Advance.Always ? Polarity : Polarity.MustBeIn;
        return new RuleStartRequirements(FirstConsumedTokens, newAdvance, polarity);
    }

    // These two map directly to the two core composition
    // operators. Each absorbs the polarity composition logic that was
    // previously open-coded in OrRule and AndRule.

    // "My rule MATCHES ANY OF [these children]" — composite that
    // succeeds when any child does (Or shape). Unions every
    // child's first-set, combining polarity-aware via the Combine
    // helper below. Advance derives from "all children Always" / "all
    // children Never" / mixed. Falls back to MustBeIn when Advance
    // drops below Always (MustNotBeIn requires Advance.Always per the
    // invariant). Use for: Or.
    public static RuleStartRequirements MatchesAnyOf(IReadOnlyList<Rule> children)
    {
        TokenSet runningSet = TokenSet.Empty;
        Polarity runningPolarity = Polarity.MustBeIn;
        bool started = false;
        bool allAlways = children.Count > 0;
        bool allNever = children.Count > 0;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (!started)
            {
                runningSet = child.FirstConsumedTokens;
                runningPolarity = child.Polarity;
                started = true;
            }
            else
            {
                (runningSet, runningPolarity) = Combine(
                    runningSet, runningPolarity,
                    child.FirstConsumedTokens, child.Polarity);
            }
            if (child.Advance != Advance.Always) allAlways = false;
            if (child.Advance != Advance.Never) allNever = false;
        }
        Advance advance = allAlways
            ? Advance.Always
            : allNever ? Advance.Never : Advance.Sometimes;
        if (advance != Advance.Always && runningPolarity == Polarity.MustNotBeIn)
            return new RuleStartRequirements(TokenSet.Empty, advance, Polarity.MustBeIn);
        return new RuleStartRequirements(runningSet, advance, runningPolarity);
    }

    // "My rule MATCHES ALL OF [these children, in order]" — composite
    // that runs children sequentially, all must succeed (And shape).
    // The first-set is the union of leading children's first-sets up
    // to (and including) the first child whose Advance is Always
    // (since later children can't influence the lookahead the
    // composite consumes). Children whose Advance is Never (Peek,
    // Not, Eof) don't contribute. Same MustNotBeIn-to-MustBeIn
    // fallback as MatchesAnyOf. Use for: And.
    public static RuleStartRequirements MatchesAllOf(IReadOnlyList<Rule> children)
    {
        TokenSet runningSet = TokenSet.Empty;
        Polarity runningPolarity = Polarity.MustBeIn;
        bool started = false;
        bool anyMightConsume = false;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Advance != Advance.Never)
            {
                if (!started)
                {
                    runningSet = child.FirstConsumedTokens;
                    runningPolarity = child.Polarity;
                    started = true;
                }
                else
                {
                    (runningSet, runningPolarity) = Combine(
                        runningSet, runningPolarity,
                        child.FirstConsumedTokens, child.Polarity);
                }
                anyMightConsume = true;
            }
            if (child.Advance == Advance.Always)
                return new RuleStartRequirements(runningSet, Advance.Always, runningPolarity);
        }
        Advance advance = anyMightConsume ? Advance.Sometimes : Advance.Never;
        if (advance != Advance.Always && runningPolarity == Polarity.MustNotBeIn)
            return new RuleStartRequirements(TokenSet.Empty, advance, Polarity.MustBeIn);
        return new RuleStartRequirements(runningSet, advance, runningPolarity);
    }

    // OR-combine two children's "might match" descriptions into a
    // single "might match EITHER child" description. Used by Or
    // (which unions over all alternatives because the Or
    // succeeds when any child does) and And when an early child's
    // Advance is Sometimes (the lookahead might be claimed by THIS
    // child via a non-zero match OR by the next sibling via this
    // child's zero-match, so the composite has to admit either).
    //
    // The result describes the disjunction of the two predicates:
    // "could A's predicate fire OR could B's predicate fire at this
    // peek?" Not conjunction. Neither operator currently needs a
    // polarity-aware AND-composition: And's first Always-child
    // stops the walk, so post-Always siblings never AND in.
    //
    // Polarity cases (writing P_x for a MustBeIn match-set, N_x for
    // a MustNotBeIn fail-set):
    //
    //   * Both MustBeIn: aSet = P_a, bSet = P_b. "peek matches
    //     either" iff peek IN P_a ∪ P_b. Returns (aSet | bSet,
    //     MustBeIn).
    //
    //   * Both MustNotBeIn: aSet = N_a, bSet = N_b. "peek doesn't
    //     fail either" iff peek NOT IN N_a OR NOT IN N_b,
    //     equivalently peek NOT IN (N_a ∩ N_b). Returns
    //     (aSet & bSet, MustNotBeIn).
    //
    //   * Mixed (one MustBeIn, one MustNotBeIn): name the negative
    //     side's set N and the positive side's set P. "peek matches
    //     positive OR doesn't fail negative" = peek IN P OR peek
    //     NOT IN N = peek NOT IN (N \ P). Returns (N \ P,
    //     MustNotBeIn).
    //
    // The mixed-case N \ P drops the multi-rune clusters from P
    // before computing, because TokenSet's ~ can't complement them.
    // The smaller result is sound under MustNotBeIn: less aggressive
    // skipping, never wrong skips. (See the body for mechanics.)
    //
    // Advance composition is left to the caller because it differs
    // between And (stop at first Always, otherwise union past
    // Sometimes / skip Never) and Or (Always iff every child is
    // Always; Never iff every child is Never; else Sometimes). Only
    // FirstConsumedTokens / Polarity get OR-combined here.
    internal static (TokenSet Set, Polarity Polarity) Combine(
        TokenSet aSet, Polarity aPolarity,
        TokenSet bSet, Polarity bPolarity)
    {
        if (aPolarity == Polarity.MustBeIn && bPolarity == Polarity.MustBeIn)
            return (aSet | bSet, Polarity.MustBeIn);
        if (aPolarity == Polarity.MustNotBeIn && bPolarity == Polarity.MustNotBeIn)
            return (aSet & bSet, Polarity.MustNotBeIn);
        // Mixed: pick the negative side as N and the positive as P.
        var negativeSet = aPolarity == Polarity.MustNotBeIn ? aSet : bSet;
        var positiveSet = aPolarity == Polarity.MustBeIn ? aSet : bSet;
        // N \ P = N & ~P. ~ throws on multi-rune entries; project P
        // down to its rune-only part first. Result is sound (a subset
        // of the actual fail-set) at the cost of less precise skipping
        // when P has multi-rune members.
        TokenSet positiveRunes = positiveSet.HasMultiRuneGraphemes
            ? positiveSet.RunesOnlyPart
            : positiveSet;
        TokenSet difference = negativeSet & ~positiveRunes;
        return (difference, Polarity.MustNotBeIn);
    }
}
