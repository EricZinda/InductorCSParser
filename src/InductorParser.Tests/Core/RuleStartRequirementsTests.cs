using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

// Unit tests for the RuleStartRequirements helpers
// (NeverAdvances / AlwaysAdvancesByOneToken / MayAdvanceByAnyTokens
// constants, FirstTokenMustBeInSet / FirstTokenMustNotBeInSet /
// FirstTokenMustBeFirstGraphemeOf factories, PassesThroughTo,
// WithAdvance, MatchesAnyOf, MatchesAllOf). These tests target the
// helpers in isolation and don't exercise any consumer rule, so they
// stay valid even as individual rules migrate to call the helpers.
[TestFixture]
public class RuleStartRequirementsTests
{
    private static Rule Compiled(Rule rule) { rule.Compile(); return rule; }

    // ===== Constants =====

    [Test]
    public void NeverAdvances_publishes_Empty_Never_MustBeIn()
    {
        var requirements = RuleStartRequirements.NeverAdvances;
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Empty));
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Never));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
    }

    [Test]
    public void AlwaysAdvancesByOneToken_publishes_Universe_Always_MustBeIn()
    {
        var requirements = RuleStartRequirements.AlwaysAdvancesByOneToken;
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Universe));
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
    }

    [Test]
    public void MayAdvanceByAnyTokens_publishes_Universe_Sometimes_MustBeIn()
    {
        var requirements = RuleStartRequirements.MayAdvanceByAnyTokens;
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Universe));
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Sometimes));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
    }

    // ===== First-token-must-be... factories =====

    [Test]
    public void FirstTokenMustBeInSet_wraps_set_with_Always_MustBeIn()
    {
        var set = TokenSet.Runes("ab");
        var requirements = RuleStartRequirements.FirstTokenMustBeInSet(set);
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(set));
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
    }

    [Test]
    public void FirstTokenMustNotBeInSet_wraps_set_with_Always_MustNotBeIn()
    {
        var set = TokenSet.Runes("ab");
        var requirements = RuleStartRequirements.FirstTokenMustNotBeInSet(set);
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(set));
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustNotBeIn));
    }

    [Test]
    public void FirstTokenMustBeFirstGraphemeOf_single_rune_string_yields_singleton_set()
    {
        var requirements = RuleStartRequirements.FirstTokenMustBeFirstGraphemeOf("hello");
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Runes("h")));
    }

    [Test]
    public void FirstTokenMustBeFirstGraphemeOf_empty_string_falls_back_to_AlwaysAdvancesByOneToken()
    {
        var requirements = RuleStartRequirements.FirstTokenMustBeFirstGraphemeOf("");
        Assert.That(requirements, Is.EqualTo(RuleStartRequirements.AlwaysAdvancesByOneToken));
    }

    [Test]
    public void FirstTokenMustBeFirstGraphemeOf_combining_mark_grapheme_yields_multi_rune_entry()
    {
        // "éllo" — first grapheme is "e" + combining acute (two runes,
        // one user-visible character). Should land in the set as a multi-rune
        // entry, not as the rune 'e'.
        var requirements = RuleStartRequirements.FirstTokenMustBeFirstGraphemeOf($"e{UnicodeExamples.CombiningAcuteText}llo");
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens.HasMultiRuneGraphemes, Is.True);
    }

    [Test]
    public void FirstTokenMustBeFirstGraphemeOf_unpaired_high_surrogate_falls_back()
    {
        // A bare high surrogate isn't a valid grapheme. Grapheme/Literal
        // factories handle this case by treating the input as "I don't know
        // what the first token is" — same fallback as an empty string.
        string unpairedHighSurrogate = UnicodeExamples.HighSurrogateMinText;
        var requirements =
            RuleStartRequirements.FirstTokenMustBeFirstGraphemeOf(unpairedHighSurrogate);
        Assert.That(requirements, Is.EqualTo(RuleStartRequirements.AlwaysAdvancesByOneToken));
    }

    // ===== Pass-through with Advance adjustment =====

    [Test]
    public void PassesThroughTo_copies_source_rule_triple()
    {
        var source = Compiled(NoneOf(TokenSet.Runes("ab")));
        var requirements = RuleStartRequirements.PassesThroughTo(source);
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(source.FirstConsumedTokens));
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustNotBeIn));
    }

    [Test]
    public void WithAdvance_keeps_polarity_when_new_advance_is_Always()
    {
        var requirements = RuleStartRequirements
            .FirstTokenMustNotBeInSet(TokenSet.Runes("ab"))
            .WithAdvance(Advance.Always);
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustNotBeIn));
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
    }

    [Test]
    public void WithAdvance_drops_MustNotBeIn_to_MustBeIn_when_new_advance_is_Sometimes()
    {
        // Advance.Sometimes can't carry MustNotBeIn (the shortcut requires
        // Advance.Always to act on a fail-set), so WithAdvance has to fall
        // back to MustBeIn to keep the invariant.
        var requirements = RuleStartRequirements
            .FirstTokenMustNotBeInSet(TokenSet.Runes("ab"))
            .WithAdvance(Advance.Sometimes);
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Sometimes));
    }

    [Test]
    public void WithAdvance_drops_MustNotBeIn_to_MustBeIn_when_new_advance_is_Never()
    {
        var requirements = RuleStartRequirements
            .FirstTokenMustNotBeInSet(TokenSet.Runes("ab"))
            .WithAdvance(Advance.Never);
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Never));
    }

    // ===== MatchesAnyOf (Or shape) =====

    [Test]
    public void MatchesAnyOf_two_MustBeIn_Always_unions_sets()
    {
        var children = new[]
        {
            Compiled(OneOf(TokenSet.Runes("a"))),
            Compiled(OneOf(TokenSet.Runes("b"))),
        };
        var requirements = RuleStartRequirements.MatchesAnyOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Runes("ab")));
    }

    [Test]
    public void MatchesAnyOf_two_MustNotBeIn_Always_intersects_sets()
    {
        // Both children fail on different sets. The composite fails only
        // when both fail, i.e. when the peek is in the intersection.
        var children = new[]
        {
            Compiled(NoneOf(TokenSet.Runes("ab"))),
            Compiled(NoneOf(TokenSet.Runes("bc"))),
        };
        var requirements = RuleStartRequirements.MatchesAnyOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustNotBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Runes("b")));
    }

    [Test]
    public void MatchesAnyOf_mixed_polarity_with_Always_collapses_to_MustNotBeIn_difference()
    {
        // OneOf("a") has MustBeIn={a}. NoneOf("ab") has MustNotBeIn={a,b}.
        // Composite "matches positive OR doesn't fail negative" = peek IN {a}
        // OR peek NOT IN {a,b} = peek NOT IN {b}.
        var children = new[]
        {
            Compiled(OneOf(TokenSet.Runes("a"))),
            Compiled(NoneOf(TokenSet.Runes("ab"))),
        };
        var requirements = RuleStartRequirements.MatchesAnyOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustNotBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Runes("b")));
    }

    [Test]
    public void MatchesAnyOf_drops_MustNotBeIn_to_MustBeIn_when_Advance_drops_below_Always()
    {
        // Mix one Always-MustNotBeIn child and one Sometimes child. The
        // composite Advance becomes Sometimes (not all Always), and the
        // running polarity is MustNotBeIn. Since MustNotBeIn requires
        // Advance.Always, the helper falls back to MustBeIn. The set
        // becomes Universe rather than the original fail-set: under
        // MustBeIn semantics the set means "tokens this rule might
        // consume first," so the original MustNotBeIn fail-set would
        // mean exactly the opposite of the rule's actual behavior.
        // Universe is the noncommittal fallback that keeps parent
        // composition sound.
        var children = new[]
        {
            Compiled(NoneOf(TokenSet.Runes("ab"))),
            Compiled(Optional(OneOf(TokenSet.Runes("c")))),
        };
        var requirements = RuleStartRequirements.MatchesAnyOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Sometimes));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Universe));
    }

    [Test]
    public void MatchesAnyOf_all_Never_children_publishes_Never_advance()
    {
        var children = new[]
        {
            Compiled(Eof()),
            Compiled(Peek(OneOf(TokenSet.Runes("a")))),
        };
        var requirements = RuleStartRequirements.MatchesAnyOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Never));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
    }

    [Test]
    public void MatchesAnyOf_mixed_Always_and_Sometimes_publishes_Sometimes()
    {
        var children = new[]
        {
            Compiled(OneOf(TokenSet.Runes("a"))),
            Compiled(Optional(OneOf(TokenSet.Runes("b")))),
        };
        var requirements = RuleStartRequirements.MatchesAnyOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Sometimes));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Runes("ab")));
    }

    [Test]
    public void MatchesAnyOf_four_child_mix_collapses_to_safe_fallback()
    {
        // Plan's regression scenario:
        // [OneOf(A), NoneOf(B), Optional(OneOf(C)), AnyToken].
        // Mixed polarities + non-Always advance => fall back to
        // (Empty, Sometimes, MustBeIn).
        var children = new[]
        {
            Compiled(OneOf(TokenSet.Runes("a"))),
            Compiled(NoneOf(TokenSet.Runes("b"))),
            Compiled(Optional(OneOf(TokenSet.Runes("c")))),
            Compiled(AnyToken()),
        };
        var requirements = RuleStartRequirements.MatchesAnyOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Sometimes));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
    }

    // ===== MatchesAllOf (And shape) =====

    [Test]
    public void MatchesAllOf_first_Always_child_stops_the_walk()
    {
        // Regression test for the original Phase 5 hole. The first Always
        // child claims the lookahead; later siblings can't influence the
        // composite's first-token set even if their first-set differs.
        var children = new[]
        {
            Compiled(OneOf(TokenSet.Runes("a"))),
            Compiled(OneOf(TokenSet.Runes("xyz"))),
        };
        var requirements = RuleStartRequirements.MatchesAllOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Runes("a")));
    }

    [Test]
    public void MatchesAllOf_Sometimes_then_Always_unions_both_first_sets()
    {
        // Optional(a) might match zero, in which case the next sibling
        // claims the lookahead. Composite has to admit both first-sets.
        var children = new[]
        {
            Compiled(Optional(OneOf(TokenSet.Runes("a")))),
            Compiled(OneOf(TokenSet.Runes("b"))),
        };
        var requirements = RuleStartRequirements.MatchesAllOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Runes("ab")));
    }

    [Test]
    public void MatchesAllOf_Never_child_does_not_contribute_to_first_set()
    {
        // Peek doesn't read the lookahead, so its first-set (Empty) is
        // skipped and the next sibling's first-set is what the composite
        // publishes.
        var children = new[]
        {
            Compiled(Peek(OneOf(TokenSet.Runes("xyz")))),
            Compiled(OneOf(TokenSet.Runes("a"))),
        };
        var requirements = RuleStartRequirements.MatchesAllOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Always));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Runes("a")));
    }

    [Test]
    public void MatchesAllOf_all_Never_children_publishes_Never_advance()
    {
        var children = new[]
        {
            Compiled(Eof()),
            Compiled(Peek(OneOf(TokenSet.Runes("a")))),
        };
        var requirements = RuleStartRequirements.MatchesAllOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Never));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Empty));
    }

    [Test]
    public void MatchesAllOf_all_Sometimes_children_publishes_Sometimes_advance()
    {
        var children = new[]
        {
            Compiled(Optional(OneOf(TokenSet.Runes("a")))),
            Compiled(Optional(OneOf(TokenSet.Runes("b")))),
        };
        var requirements = RuleStartRequirements.MatchesAllOf(children);
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Sometimes));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Runes("ab")));
    }

    [Test]
    public void MatchesAllOf_empty_children_publishes_Never_advance()
    {
        var requirements = RuleStartRequirements.MatchesAllOf(System.Array.Empty<Rule>());
        Assert.That(requirements.Advance, Is.EqualTo(Advance.Never));
        Assert.That(requirements.Polarity, Is.EqualTo(Polarity.MustBeIn));
        Assert.That(requirements.FirstConsumedTokens, Is.EqualTo(TokenSet.Empty));
    }
}
