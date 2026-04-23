using System;
using System.Globalization;
using System.Text;
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
        // lone-surrogate code units in string *constants* to U+FFFD. By
        // the time a literal "\uD800" reaches the test body under IL2CPP,
        // the char has already been replaced with the Unicode replacement
        // character and the test never exercises the surrogate path.
        // Runtime-constructed strings preserve the char value the caller
        // passed, which is what we want to verify here.
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
        var ab = RuneSet.Range(1, 5) | RuneSet.Range(10, 15);
        var ba = RuneSet.Range(10, 15) | RuneSet.Range(1, 5);

        Assert.That(ab, Is.EqualTo(ba));
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
    public void Default_RuneSet_is_empty()
    {
        // Sanity: a default-constructed RuneSet has no intervals and matches
        // no codepoint. Contains relies on the null-ranges guard. IsEmpty
        // reports the same state directly.
        var set = default(RuneSet);

        Assert.That(set.IsEmpty, Is.True);
        Assert.That(set.Contains('a'), Is.False);
    }

    [Test]
    public void Equality_is_structural_across_construction_paths()
    {
        // Two sets with the same membership compare equal regardless of how
        // they were built, because Normalize produces a canonical interval
        // list. Runes("abc") and Range('a','c') land on the same _ranges.
        Assert.That(RuneSet.Runes("abc"), Is.EqualTo(RuneSet.Range('a', 'c')));
        Assert.That(RuneSet.Single('a') | RuneSet.Single('b'),
            Is.EqualTo(RuneSet.Range('a', 'b')));
        // Different membership compares unequal.
        Assert.That(RuneSet.Single('a'), Is.Not.EqualTo(RuneSet.Single('b')));
        // default and an explicit empty set are equal (both have no intervals).
        Assert.That(default(RuneSet), Is.EqualTo(RuneSet.Runes("")));
        // == operator mirrors Equals.
        Assert.That(RuneSet.Range('a', 'c') == RuneSet.Runes("abc"), Is.True);
        Assert.That(RuneSet.Single('a') != RuneSet.Single('b'), Is.True);
    }

    [Test]
    public void Equal_RuneSets_have_equal_hash_codes()
    {
        // Two sets that compare equal must hash to the same value. The reverse
        // isn't required but equal sets must not drift apart.
        var left = RuneSet.Runes("abc");
        var right = RuneSet.Range('a', 'c');

        Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
    }

    [Test]
    public void Operations_always_produce_normalized_representation()
    {
        // Every result of |, &, ~, and Runes(...) should be normalized
        // (sorted, non-overlapping, non-adjacent intervals). If any operator
        // left the output un-normalized, value equality would fail for two
        // sets with the same membership because Equals compares _ranges
        // element-by-element.
        //
        // Each case below builds the same logical set a different way. They
        // all must compare equal to the straightforwardly-built canonical form.
        var expected = RuneSet.Range(1, 30);

        // Adjacent intervals must merge into one.
        Assert.That(RuneSet.Range(1, 10) | RuneSet.Range(11, 20) | RuneSet.Range(21, 30),
            Is.EqualTo(expected));
        // Overlapping intervals must merge.
        Assert.That(RuneSet.Range(1, 15) | RuneSet.Range(10, 25) | RuneSet.Range(20, 30),
            Is.EqualTo(expected));
        // Out-of-order union must end up sorted.
        Assert.That(RuneSet.Range(21, 30) | RuneSet.Range(1, 10) | RuneSet.Range(11, 20),
            Is.EqualTo(expected));
        // Intersection of larger ranges must collapse to the overlap.
        Assert.That(RuneSet.Range(0, 50) & RuneSet.Range(1, 30), Is.EqualTo(expected));
        // Messy chain of overlapping fragments.
        Assert.That(RuneSet.Range(1, 5) | RuneSet.Range(3, 10) | RuneSet.Range(10, 15)
            | RuneSet.Range(14, 22) | RuneSet.Range(20, 30), Is.EqualTo(expected));
        // Double complement round-trip.
        Assert.That(~~expected, Is.EqualTo(expected));
        // Complement split across surrogate block, then complement again.
        // Verifies the surrogate-split path doesn't leak adjacent intervals.
        Assert.That(~~RuneSet.Range(0xD7FE, 0xD7FE), Is.EqualTo(RuneSet.Range(0xD7FE, 0xD7FE)));
        // A & ~B chain through the full operator set.
        Assert.That(RuneSet.Range(1, 30) & ~RuneSet.Range(40, 50), Is.EqualTo(expected));
    }

    [Test]
    public void IsEmpty_is_false_for_sets_with_any_rune()
    {
        // Complement of the non-empty singleton contains everything except
        // that rune: IsEmpty should stay False. Covers the non-empty branch
        // of the _ranges null/length check.
        Assert.That(RuneSet.Single('a').IsEmpty, Is.False);
        Assert.That(RuneSet.Range('a', 'z').IsEmpty, Is.False);
        Assert.That(RuneSet.Letters.IsEmpty, Is.False);
    }

    [Test]
    public void Letters_covers_all_five_letter_categories()
    {
        // Letters is now built as a union of five Category calls. Verify it
        // still behaves correctly (one sample per category).
        Assert.That(RuneSet.Letters.Contains('A'), Is.True);  // UppercaseLetter
        Assert.That(RuneSet.Letters.Contains('a'), Is.True);  // LowercaseLetter
        Assert.That(RuneSet.Letters.Contains('\u01C5'), Is.True); // TitlecaseLetter (ǅ)
        Assert.That(RuneSet.Letters.Contains('\u02B0'), Is.True); // ModifierLetter (ʰ)
        Assert.That(RuneSet.Letters.Contains('\u4E2D'), Is.True); // OtherLetter (中)
        Assert.That(RuneSet.Letters.Contains('1'), Is.False);
        Assert.That(RuneSet.Letters.Contains(' '), Is.False);
    }

    [Test]
    public void Intersection_of_overlapping_ranges_keeps_the_overlap()
    {
        // [1, 7] & [5, 10] = [5, 7]. The classic overlap case.
        var set = RuneSet.Range(1, 7) & RuneSet.Range(5, 10);

        Assert.That(set.Contains(4), Is.False);
        Assert.That(set.Contains(5), Is.True);
        Assert.That(set.Contains(7), Is.True);
        Assert.That(set.Contains(8), Is.False);
    }

    [Test]
    public void Intersection_of_disjoint_ranges_is_empty()
    {
        // [1, 5] & [10, 15] share no elements.
        var set = RuneSet.Range(1, 5) & RuneSet.Range(10, 15);

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Intersection_with_default_RuneSet_is_empty()
    {
        // default(RuneSet) has a null _ranges and represents the empty set.
        // Anything intersected with it is empty.
        var set = default(RuneSet) & RuneSet.Single('x');

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Intersection_is_commutative()
    {
        var left = RuneSet.Range(1, 10) | RuneSet.Range(20, 30);
        var right = RuneSet.Range(5, 25);

        Assert.That(left & right, Is.EqualTo(right & left));
    }

    [Test]
    public void Intersection_of_multi_interval_sets_walks_both_sides()
    {
        // A = [1, 5] ∪ [10, 15] ∪ [20, 25]
        // B = [3, 12] ∪ [22, 30]
        // A & B = [3, 5] ∪ [10, 12] ∪ [22, 25]
        var a = RuneSet.Range(1, 5) | RuneSet.Range(10, 15) | RuneSet.Range(20, 25);
        var b = RuneSet.Range(3, 12) | RuneSet.Range(22, 30);
        var intersection = a & b;

        // Inside the expected intervals.
        foreach (int codepoint in new[] { 3, 4, 5, 10, 11, 12, 22, 23, 24, 25 })
            Assert.That(intersection.Contains(codepoint), Is.True, $"{codepoint} should be in A & B");
        // Outside: in A but not B, in B but not A, or in neither.
        foreach (int codepoint in new[] { 1, 2, 6, 9, 13, 14, 15, 20, 21, 26, 30 })
            Assert.That(intersection.Contains(codepoint), Is.False, $"{codepoint} should NOT be in A & B");
    }

    [Test]
    public void Intersection_narrows_Letters_to_a_script_block()
    {
        // Cyrillic block 0x0400..0x04FF intersected with Unicode Letters. The
        // motivating use case from docs/ProgrammingModel.md: narrow a semantic
        // class (Letters) by a script-range restriction.
        var cyrillicLetters = RuneSet.Letters & RuneSet.Range(0x0400, 0x04FF);

        // ж (U+0436) is a Cyrillic letter (in both sets).
        Assert.That(cyrillicLetters.Contains(0x0436), Is.True);
        // A (U+0041) is a letter but not Cyrillic.
        Assert.That(cyrillicLetters.Contains('A'), Is.False);
        // U+0488 is a Cyrillic combining mark, not a letter.
        Assert.That(cyrillicLetters.Contains(0x0488), Is.False);
    }

    // The set of every Unicode scalar value: [0, 0x10FFFF] minus the surrogate
    // block [0xD800, 0xDFFF]. Equivalent to ~default(RuneSet). Pulled out so
    // complement tests can compare against it by equality instead of sampling
    // specific codepoints.
    private static readonly RuneSet AllScalarValues =
        RuneSet.Range(0, 0xD7FF) | RuneSet.Range(0xE000, 0x10FFFF);

    [Test]
    public void Complement_of_empty_set_is_all_scalar_values()
    {
        Assert.That(~default(RuneSet), Is.EqualTo(AllScalarValues));
    }

    [Test]
    public void Complement_of_single_rune_excludes_that_rune()
    {
        // ~{'a'} = all scalar values minus 'a'. Build the expected set as
        // the two gaps around 'a'.
        var expected = RuneSet.Range(0, 'a' - 1)
            | RuneSet.Range('a' + 1, 0xD7FF)
            | RuneSet.Range(0xE000, 0x10FFFF);

        Assert.That(~RuneSet.Single('a'), Is.EqualTo(expected));
    }

    [Test]
    public void Complement_of_full_range_is_empty()
    {
        // ~[0..0x10FFFF] = ∅ (the input already covers every gap, including
        // the dead slots in the surrogate block).
        var set = ~RuneSet.Range(0, 0x10FFFF);

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Complement_is_involutive()
    {
        // ~~A = A for any A. Surrogates aren't in either side, so value
        // equality is the right check.
        var original = RuneSet.Range('a', 'z') | RuneSet.Range('A', 'Z');

        Assert.That(~~original, Is.EqualTo(original));
    }

    [Test]
    public void SetDifference_via_complement_and_intersection()
    {
        // The idiom from the docs: A & ~B is "A minus B". Consonants as
        // ASCII letters minus vowels.
        var asciiConsonants = RuneSet.Ascii.Letters & ~RuneSet.Runes("aeiouAEIOU");

        // Consonants: in.
        Assert.That(asciiConsonants.Contains('b'), Is.True);
        Assert.That(asciiConsonants.Contains('z'), Is.True);
        Assert.That(asciiConsonants.Contains('B'), Is.True);
        Assert.That(asciiConsonants.Contains('Z'), Is.True);
        // Vowels: out.
        Assert.That(asciiConsonants.Contains('a'), Is.False);
        Assert.That(asciiConsonants.Contains('e'), Is.False);
        Assert.That(asciiConsonants.Contains('i'), Is.False);
        Assert.That(asciiConsonants.Contains('o'), Is.False);
        Assert.That(asciiConsonants.Contains('u'), Is.False);
        Assert.That(asciiConsonants.Contains('A'), Is.False);
        Assert.That(asciiConsonants.Contains('U'), Is.False);
        // Non-letters: out.
        Assert.That(asciiConsonants.Contains('0'), Is.False);
        Assert.That(asciiConsonants.Contains(' '), Is.False);
    }

    // Contains boundaries ----------------------------------------------------

    [Test]
    public void Contains_hits_exact_low_and_high_endpoints()
    {
        // Endpoints are inclusive on both sides. Lock it in so a future
        // off-by-one in the < vs <= choice in Contains breaks loudly.
        var set = RuneSet.Range(10, 20);

        Assert.That(set.Contains(9), Is.False);
        Assert.That(set.Contains(10), Is.True);
        Assert.That(set.Contains(20), Is.True);
        Assert.That(set.Contains(21), Is.False);
    }

    [Test]
    public void Contains_at_scalar_value_extremes()
    {
        // 0 and 0x10FFFF are valid scalar values. Make sure the boundary
        // codepoints don't get accidentally excluded.
        var zero = RuneSet.Single(0);
        var max = RuneSet.Single(0x10FFFF);

        Assert.That(zero.Contains(0), Is.True);
        Assert.That(zero.Contains(1), Is.False);
        Assert.That(max.Contains(0x10FFFF), Is.True);
        Assert.That(max.Contains(0x10FFFE), Is.False);
    }

    [Test]
    public void Contains_char_and_Rune_overloads_delegate_to_int_form()
    {
        var set = RuneSet.Single(GuitarRune) | RuneSet.Single('a');

        // char overload: BMP only.
        Assert.That(set.Contains('a'), Is.True);
        Assert.That(set.Contains('b'), Is.False);
        // Rune overload: any scalar value, including supplementary plane.
        Assert.That(set.Contains(new Rune(GuitarRune)), Is.True);
        Assert.That(set.Contains(new Rune(MusicalKeyboardRune)), Is.False);
    }

    // Single / Range corner cases --------------------------------------------

    [Test]
    public void Single_char_and_Rune_overloads_contain_that_rune()
    {
        // Happy-path overloads weren't directly exercised before. char takes
        // the BMP path, Rune takes the supplementary-plane-capable path.
        var fromChar = RuneSet.Single('Z');
        var fromRune = RuneSet.Single(new Rune(GuitarRune));

        Assert.That(fromChar.Contains('Z'), Is.True);
        Assert.That(fromRune.Contains(GuitarRune), Is.True);
    }

    [Test]
    public void Range_char_and_Rune_overloads_cover_the_range()
    {
        var charRange = RuneSet.Range('a', 'c');
        var runeRange = RuneSet.Range(new Rune(GuitarRune), new Rune(MusicalKeyboardRune));

        Assert.That(charRange.Contains('a'), Is.True);
        Assert.That(charRange.Contains('b'), Is.True);
        Assert.That(charRange.Contains('c'), Is.True);
        Assert.That(charRange.Contains('d'), Is.False);
        Assert.That(runeRange.Contains(GuitarRune), Is.True);
        Assert.That(runeRange.Contains(MusicalKeyboardRune), Is.True);
    }

    [Test]
    public void Range_with_low_greater_than_high_throws()
    {
        // Endpoints are valid scalar values but inverted. Not an
        // ArgumentOutOfRangeException (the endpoints themselves are fine).
        // It's an ArgumentException because the pair is inconsistent.
        Assert.Throws<ArgumentException>(() => RuneSet.Range(100, 50));
    }

    [Test]
    public void Range_with_equal_endpoints_is_a_singleton()
    {
        // Range(x, x) is allowed and behaves like Single(x): one-codepoint set.
        var set = RuneSet.Range(0x41, 0x41);

        Assert.That(set.Contains('A'), Is.True);
        Assert.That(set.Contains('B'), Is.False);
        Assert.That(set.Contains(0x40), Is.False);
    }

    // Runes corner cases -----------------------------------------------------

    [Test]
    public void Runes_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => RuneSet.Runes(null!));
    }

    [Test]
    public void Runes_empty_string_is_empty_set()
    {
        var set = RuneSet.Runes("");

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Runes_with_duplicates_dedupes_via_normalize()
    {
        // Normalize sorts and merges. Repeated characters collapse into one
        // interval each. Observable as a set with the same membership as the
        // distinct-char version, and as the compact ToString.
        var set = RuneSet.Runes("aabbccba");

        Assert.That(set.Contains('a'), Is.True);
        Assert.That(set.Contains('b'), Is.True);
        Assert.That(set.Contains('c'), Is.True);
        Assert.That(set.Contains('d'), Is.False);
        // Adjacent 'a','b','c' merge into one interval a-c.
        Assert.That(set.ToString(), Is.EqualTo("[a-c]"));
    }

    [Test]
    public void Runes_with_lone_low_surrogate_throws()
    {
        // Low surrogate not preceded by a high surrogate. Built at runtime
        // for the same IL2CPP-literal-sanitization reason as the lone-high
        // test: "\uDC00" in a string constant becomes U+FFFD under IL2CPP.
        var loneLow = new string((char)0xDC00, 1);

        Assert.Throws<ArgumentException>(() => RuneSet.Runes(loneLow));
    }

    [Test]
    public void Runes_with_trailing_high_surrogate_throws()
    {
        // High surrogate at the end of the string with no low surrogate to
        // pair with. 
        var trailing = "a" + new string((char)0xD800, 1);

        Assert.Throws<ArgumentException>(() => RuneSet.Runes(trailing));
    }

    [Test]
    public void Runes_with_high_surrogate_not_followed_by_low_throws()
    {
        // High surrogate followed by a non-surrogate char is invalid.
        var malformed = new string((char)0xD800, 1) + "a";

        Assert.Throws<ArgumentException>(() => RuneSet.Runes(malformed));
    }

    // ToString ---------------------------------------------------------------

    [Test]
    public void ToString_renders_empty_singleton_and_range()
    {
        Assert.That(default(RuneSet).ToString(), Is.EqualTo("[]"));
        Assert.That(RuneSet.Single('a').ToString(), Is.EqualTo("[a]"));
        Assert.That(RuneSet.Range('a', 'z').ToString(), Is.EqualTo("[a-z]"));
    }

    [Test]
    public void ToString_separates_multiple_ranges_with_commas()
    {
        // Disjoint ranges render as comma-separated pieces, each using the
        // same low or low-high convention.
        var set = RuneSet.Range('0', '9') | RuneSet.Range('A', 'Z') | RuneSet.Range('a', 'z');

        Assert.That(set.ToString(), Is.EqualTo("[0-9,A-Z,a-z]"));
    }

    [Test]
    public void ToString_escapes_non_printable_and_supplementary_plane()
    {
        // Printable ASCII boundary: 0x20 (space) and 0x7E (~) render literal.
        // 0x1F and 0x7F render as U+XXXX.
        Assert.That(RuneSet.Single(0x20).ToString(), Is.EqualTo("[ ]"));
        Assert.That(RuneSet.Single(0x7E).ToString(), Is.EqualTo("[~]"));
        Assert.That(RuneSet.Single(0x1F).ToString(), Is.EqualTo("[U+001F]"));
        Assert.That(RuneSet.Single(0x7F).ToString(), Is.EqualTo("[U+007F]"));
        // Supplementary plane codepoint: five hex digits, no zero-padding past X4.
        Assert.That(RuneSet.Single(GuitarRune).ToString(), Is.EqualTo("[U+1F3B8]"));
        // Range with non-printable endpoints.
        Assert.That(RuneSet.Range(0, 0x1F).ToString(), Is.EqualTo("[U+0000-U+001F]"));
    }

    [Test]
    public void ToString_renders_up_to_eight_ranges_in_full()
    {
        // Eight non-adjacent single-rune ranges. All show, no tail.
        var set = RuneSet.Runes("acegikmo");
        Assert.That(set.ToString(), Is.EqualTo("[a,c,e,g,i,k,m,o]"));
    }

    [Test]
    public void ToString_truncates_beyond_eight_ranges()
    {
        // Nine non-adjacent single-rune ranges. First eight show, a
        // "+1 more" tail says the rest got dropped. Large classes like
        // RuneSet.Letters would produce hundreds of ranges without
        // this cap and make trace lines unreadable.
        var set = RuneSet.Runes("acegikmoq");
        Assert.That(set.ToString(), Is.EqualTo("[a,c,e,g,i,k,m,o,...+1 more]"));
    }

    [Test]
    public void ToString_truncates_with_correct_count_for_large_class()
    {
        // Ten non-adjacent single-rune ranges. First eight show, "+2
        // more" reports the remainder. Confirms the tail count tracks
        // actual overflow rather than a fixed placeholder.
        var set = RuneSet.Runes("acegikmoqs");
        Assert.That(set.ToString(), Is.EqualTo("[a,c,e,g,i,k,m,o,...+2 more]"));
    }

    // Union corners ----------------------------------------------------------

    [Test]
    public void Union_of_two_default_sets_is_empty()
    {
        var set = default(RuneSet) | default(RuneSet);

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Union_of_identical_sets_is_that_set()
    {
        // a | a shouldn't double-count intervals. Normalize collapses the
        // duplicates back to the single-interval form.
        var original = RuneSet.Range('a', 'z');
        var doubled = original | original;

        Assert.That(doubled.ToString(), Is.EqualTo("[a-z]"));
    }

    [Test]
    public void Union_of_fully_contained_range_yields_the_outer_range()
    {
        // [1, 100] | [10, 20] = [1, 100]. Inner range adds nothing.
        var set = RuneSet.Range(1, 100) | RuneSet.Range(10, 20);

        Assert.That(set, Is.EqualTo(RuneSet.Range(1, 100)));
    }

    // Intersection corners ---------------------------------------------------

    [Test]
    public void Intersection_of_identical_sets_is_that_set()
    {
        var original = RuneSet.Range('a', 'z');

        Assert.That(original & original, Is.EqualTo(original));
    }

    [Test]
    public void Intersection_with_containing_set_returns_contained_set()
    {
        // [10, 20] & [1, 100] = [10, 20] (the smaller set wins when nested).
        var inner = RuneSet.Range(10, 20);
        var outer = RuneSet.Range(1, 100);

        Assert.That(inner & outer, Is.EqualTo(inner));
    }

    [Test]
    public void Intersection_of_touching_but_disjoint_intervals_is_empty()
    {
        // [1, 5] and [6, 10] are adjacent, not overlapping. They'd merge
        // under union. Under intersection they share no elements.
        var set = RuneSet.Range(1, 5) & RuneSet.Range(6, 10);

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Intersection_with_default_RuneSet_on_right_is_empty()
    {
        // Mirrors the existing "default on left" test. Both branches of the
        // null-check in the operator should short-circuit to empty.
        var set = RuneSet.Single('x') & default(RuneSet);

        Assert.That(set.IsEmpty, Is.True);
    }

    // Complement surrogate-boundary cases ------------------------------------

    [Test]
    public void Complement_of_range_ending_at_surrogate_low_boundary()
    {
        // Input ends at 0xD7FF. Complement is just [0xE000, 0x10FFFF].
        // No prefix, no surrogate slots, full suffix.
        Assert.That(~RuneSet.Range(0, 0xD7FF), Is.EqualTo(RuneSet.Range(0xE000, 0x10FFFF)));
    }

    [Test]
    public void Complement_of_range_starting_at_surrogate_high_boundary()
    {
        // Input starts at 0xE000. Complement is just [0, 0xD7FF].
        Assert.That(~RuneSet.Range(0xE000, 0x10FFFF), Is.EqualTo(RuneSet.Range(0, 0xD7FF)));
    }

    [Test]
    public void Complement_of_multi_interval_input_emits_all_gaps()
    {
        // Input [5, 10] ∪ [20, 30]. Complement should be the three gaps,
        // with the trailing one split around the surrogate block.
        var expected = RuneSet.Range(0, 4)
            | RuneSet.Range(11, 19)
            | RuneSet.Range(31, 0xD7FF)
            | RuneSet.Range(0xE000, 0x10FFFF);

        Assert.That(~(RuneSet.Range(5, 10) | RuneSet.Range(20, 30)), Is.EqualTo(expected));
    }

    // Built-ins --------------------------------------------------------------

    [Test]
    public void Digits_contains_decimal_digit_runes_across_scripts()
    {
        // RuneSet.Digits is UnicodeCategory.DecimalDigitNumber, every
        // decimal digit in every script, not just ASCII 0-9.
        Assert.That(RuneSet.Digits.Contains('0'), Is.True);
        Assert.That(RuneSet.Digits.Contains('9'), Is.True);
        Assert.That(RuneSet.Digits.Contains(0x0660), Is.True); // Arabic-Indic digit zero ٠
        Assert.That(RuneSet.Digits.Contains(0x09E6), Is.True); // Bengali digit zero ০
        // Letters and whitespace aren't digits.
        Assert.That(RuneSet.Digits.Contains('a'), Is.False);
        Assert.That(RuneSet.Digits.Contains(' '), Is.False);
        // Roman numeral letters are category NumberLetter, not digits.
        Assert.That(RuneSet.Digits.Contains(0x2160), Is.False); // Ⅰ
    }

    [Test]
    public void Whitespace_contains_ascii_and_unicode_whitespace()
    {
        // Built via char.IsWhiteSpace predicate over the BMP. Contains the
        // obvious ASCII whitespace plus a few Unicode-only runes.
        Assert.That(RuneSet.Whitespace.Contains(' '), Is.True);
        Assert.That(RuneSet.Whitespace.Contains('\t'), Is.True);
        Assert.That(RuneSet.Whitespace.Contains('\r'), Is.True);
        Assert.That(RuneSet.Whitespace.Contains('\n'), Is.True);
        Assert.That(RuneSet.Whitespace.Contains(0x00A0), Is.True); // NBSP
        Assert.That(RuneSet.Whitespace.Contains(0x2028), Is.True); // LINE SEPARATOR
        // Not whitespace.
        Assert.That(RuneSet.Whitespace.Contains('a'), Is.False);
        Assert.That(RuneSet.Whitespace.Contains('0'), Is.False);
    }

    [Test]
    public void Ascii_Digits_contains_only_0_through_9()
    {
        Assert.That(RuneSet.Ascii.Digits.Contains('0'), Is.True);
        Assert.That(RuneSet.Ascii.Digits.Contains('5'), Is.True);
        Assert.That(RuneSet.Ascii.Digits.Contains('9'), Is.True);
        // The Unicode-full Digits would include Arabic-Indic zero. Ascii.Digits doesn't.
        Assert.That(RuneSet.Ascii.Digits.Contains(0x0660), Is.False);
        Assert.That(RuneSet.Ascii.Digits.Contains('a'), Is.False);
    }

    [Test]
    public void Ascii_Whitespace_contains_only_space_tab_cr_lf()
    {
        Assert.That(RuneSet.Ascii.Whitespace.Contains(' '), Is.True);
        Assert.That(RuneSet.Ascii.Whitespace.Contains('\t'), Is.True);
        Assert.That(RuneSet.Ascii.Whitespace.Contains('\r'), Is.True);
        Assert.That(RuneSet.Ascii.Whitespace.Contains('\n'), Is.True);
        // Not in the literal " \t\r\n" set, even though char.IsWhiteSpace says yes.
        Assert.That(RuneSet.Ascii.Whitespace.Contains(0x00A0), Is.False); // NBSP
        Assert.That(RuneSet.Ascii.Whitespace.Contains('\v'), Is.False);   // vertical tab
        Assert.That(RuneSet.Ascii.Whitespace.Contains('\f'), Is.False);   // form feed
    }

    [Test]
    public void Ascii_Letters_excludes_non_ascii_letters()
    {
        // The Ascii-qualified built-ins are the "only ASCII" versions. They
        // must not drift into full Unicode by accident.
        Assert.That(RuneSet.Ascii.Letters.Contains('a'), Is.True);
        Assert.That(RuneSet.Ascii.Letters.Contains('Z'), Is.True);
        Assert.That(RuneSet.Ascii.Letters.Contains(0x00E9), Is.False); // é
        Assert.That(RuneSet.Ascii.Letters.Contains(0x4E2D), Is.False); // 中
    }

    // Category niche ---------------------------------------------------------

    [Test]
    public void Category_Surrogate_is_empty_because_the_scan_skips_surrogates()
    {
        // The build loop explicitly skips 0xD800..0xDFFF (they aren't valid
        // scalar values), so asking for UnicodeCategory.Surrogate yields an
        // empty set. Non-obvious and worth verifying.
        var surrogates = RuneSet.Category(UnicodeCategory.Surrogate);

        Assert.That(surrogates.IsEmpty, Is.True);
    }

    [Test]
    public void Category_SpaceSeparator_matches_only_space_category_runes()
    {
        // DecimalDigitNumber is exercised indirectly via Digits. Pick a
        // different non-letter category so the cache path sees more variety.
        var spaces = RuneSet.Category(UnicodeCategory.SpaceSeparator);

        Assert.That(spaces.Contains(' '), Is.True);       // U+0020 space
        Assert.That(spaces.Contains(0x00A0), Is.True);    // NBSP
        Assert.That(spaces.Contains(0x2003), Is.True);    // EM SPACE
        Assert.That(spaces.Contains('\t'), Is.False);     // tab is Control, not SpaceSeparator
        Assert.That(spaces.Contains('a'), Is.False);
    }

    // Operator invariant -----------------------------------------------------

    [Test]
    public void A_and_not_A_is_empty_for_any_A()
    {
        // A ∩ ~A = ∅. One test covers the complement-then-intersect pipeline
        // for a nontrivial multi-interval A that also crosses the surrogate
        // split.
        var a = RuneSet.Ascii.Letters | RuneSet.Range(0xE000, 0xE00F);
        var intersection = a & ~a;

        Assert.That(intersection.IsEmpty, Is.True);
    }
}
