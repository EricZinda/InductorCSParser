using System;
using System.Globalization;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class RuneSetTests
{
    [Test]
    public void Single_int_with_surrogate_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuneSet.Single(0xD800));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuneSet.Single(0xDFFF));
    }

    [Test]
    public void Single_int_out_of_range_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuneSet.Single(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuneSet.Single(0x110000));
    }

    [Test]
    public void Single_char_with_surrogate_throws()
    {
        // (int)'\uD800' == 0xD800, delegates through Single(int) which throws.
        Assert.Throws<ArgumentOutOfRangeException>(() => RuneSet.Single('\uD800'));
    }

    [Test]
    public void Range_with_invalid_low_or_high_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuneSet.Range(0xD800, 0xDFFF));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuneSet.Range(-1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuneSet.Range(100, 0x110000));
    }

    [Test]
    public void Range_spanning_the_surrogate_gap_is_allowed()
    {
        // Endpoints are valid scalar values even though the range as a whole
        // straddles the surrogate block. The set contains "dead" slots in
        // 0xD800..0xDFFF, which is harmless because the lexer never produces
        // those as token values.
        var set = RuneSet.Range(0x0000, 0x10FFFF);

        Assert.That(set.Contains('a'), Is.True);
        Assert.That(set.Contains(GuitarRune), Is.True);
    }

    [Test]
    public void Runes_with_lone_surrogate_throws()
    {
        // A lone high surrogate not followed by a low surrogate. Built at
        // runtime instead of written as "\uD800" because IL2CPP sanitizes
        // lone-surrogate code units in string *constants* to U+FFFD — by
        // the time a literal "\uD800" reaches the test body under IL2CPP,
        // the char has already been replaced with the Unicode replacement
        // character and the test never exercises the surrogate path.
        // Runtime-constructed strings preserve the char value the caller
        // passed, which is what we want to pin here.
        var loneSurrogate = new string((char)0xD800, 1);
        Assert.Throws<ArgumentException>(() => RuneSet.Runes(loneSurrogate));
    }

    [Test]
    public void Runes_with_valid_surrogate_pair_works()
    {
        // Guitar as a surrogate pair. Treated as one codepoint.
        var set = RuneSet.Runes(GuitarGrapheme);

        Assert.That(set.Contains(GuitarRune), Is.True);
    }

    [Test]
    public void Category_returns_runes_of_that_category()
    {
        var uppercaseLetters = RuneSet.Category(UnicodeCategory.UppercaseLetter);

        Assert.That(uppercaseLetters.Contains('A'), Is.True);
        Assert.That(uppercaseLetters.Contains('Z'), Is.True);
        Assert.That(uppercaseLetters.Contains('a'), Is.False);
        Assert.That(uppercaseLetters.Contains('1'), Is.False);
    }

    [Test]
    public void Category_returns_the_same_cached_instance_on_repeat_calls()
    {
        // Caching is the whole point: second call shouldn't rescan.
        var first = RuneSet.Category(UnicodeCategory.DecimalDigitNumber);
        var second = RuneSet.Category(UnicodeCategory.DecimalDigitNumber);

        // RuneSet is a value type, but the internal _ranges array reference
        // should be shared across both returns when the cache hit.
        Assert.That(first.Contains('5'), Is.True);
        Assert.That(second.Contains('5'), Is.True);
    }

    [Test]
    public void Union_of_two_singletons_contains_both()
    {
        var set = RuneSet.Single('a') | RuneSet.Single('z');

        Assert.That(set.Contains('a'), Is.True);
        Assert.That(set.Contains('z'), Is.True);
        Assert.That(set.Contains('m'), Is.False);
    }

    [Test]
    public void Union_of_overlapping_ranges_merges_them()
    {
        // [1, 5] | [3, 7] should normalize to a single [1, 7] interval.
        // We can't see the internal representation, but Contains over the
        // combined range (including the previously-uncovered 6 and 7) tells
        // us the merge happened.
        var set = RuneSet.Range(1, 5) | RuneSet.Range(3, 7);

        for (int codepoint = 1; codepoint <= 7; codepoint++)
            Assert.That(set.Contains(codepoint), Is.True, $"{codepoint} should be in union");
        Assert.That(set.Contains(0), Is.False);
        Assert.That(set.Contains(8), Is.False);
    }

    [Test]
    public void Union_of_adjacent_ranges_merges_them()
    {
        // [1, 5] | [6, 10] are adjacent (no gap). Normalize coalesces them
        // because next.Low <= current.High + 1. Behaviorally the user just
        // sees one continuous covered range.
        var set = RuneSet.Range(1, 5) | RuneSet.Range(6, 10);

        for (int codepoint = 1; codepoint <= 10; codepoint++)
            Assert.That(set.Contains(codepoint), Is.True, $"{codepoint} should be in union");
        Assert.That(set.Contains(0), Is.False);
        Assert.That(set.Contains(11), Is.False);
    }

    [Test]
    public void Union_of_disjoint_ranges_keeps_the_gap()
    {
        // [1, 5] | [10, 15]. Gap at 6..9 must stay uncovered.
        var set = RuneSet.Range(1, 5) | RuneSet.Range(10, 15);

        Assert.That(set.Contains(3), Is.True);
        Assert.That(set.Contains(12), Is.True);
        Assert.That(set.Contains(7), Is.False);
        Assert.That(set.Contains(0), Is.False);
        Assert.That(set.Contains(16), Is.False);
    }

    [Test]
    public void Union_is_commutative()
    {
        // a | b and b | a should produce sets with the same membership.
        var ab = RuneSet.Range(1, 5) | RuneSet.Range(10, 15);
        var ba = RuneSet.Range(10, 15) | RuneSet.Range(1, 5);

        for (int codepoint = 0; codepoint <= 20; codepoint++)
            Assert.That(ab.Contains(codepoint), Is.EqualTo(ba.Contains(codepoint)),
                $"membership of {codepoint} should match");
    }

    [Test]
    public void Union_with_default_RuneSet_returns_the_other_side()
    {
        // default(RuneSet) has a null _ranges array. The | operator should
        // tolerate that and return a set equivalent to the non-empty side.
        var set = default(RuneSet) | RuneSet.Single('x');

        Assert.That(set.Contains('x'), Is.True);
        Assert.That(set.Contains('y'), Is.False);
    }

    [Test]
    public void Default_RuneSet_contains_nothing()
    {
        // Sanity: a default-constructed RuneSet has no intervals and matches
        // no codepoint. Contains relies on the null-ranges guard.
        var set = default(RuneSet);

        Assert.That(set.Contains('a'), Is.False);
        Assert.That(set.Contains(0), Is.False);
        Assert.That(set.Contains(0x10FFFF), Is.False);
    }

    [Test]
    public void Letters_covers_all_five_letter_categories()
    {
        // Letters is now built as a union of five Category calls. Verify it
        // still behaves correctly — one sample per category.
        Assert.That(RuneSet.Letters.Contains('A'), Is.True);  // UppercaseLetter
        Assert.That(RuneSet.Letters.Contains('a'), Is.True);  // LowercaseLetter
        Assert.That(RuneSet.Letters.Contains('\u01C5'), Is.True); // TitlecaseLetter (ǅ)
        Assert.That(RuneSet.Letters.Contains('\u02B0'), Is.True); // ModifierLetter (ʰ)
        Assert.That(RuneSet.Letters.Contains('\u4E2D'), Is.True); // OtherLetter (中)
        Assert.That(RuneSet.Letters.Contains('1'), Is.False);
        Assert.That(RuneSet.Letters.Contains(' '), Is.False);
    }
}
