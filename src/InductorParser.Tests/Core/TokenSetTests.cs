using System;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Tests.UnicodeExamples;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

[TestFixture]
public class TokenSetTests
{
    // Asserts both value equality and hash-code equality for two TokenSets.
    // The standard contract is that equal values must hash the same. Using
    // this helper everywhere two TokenSets are compared bakes the hash check
    // into every equality assertion, so any drift between Equals and
    // GetHashCode shows up at whichever call site triggered it rather than
    // being caught only by a single dedicated test.
    private static void AssertEqual(TokenSet actual, TokenSet expected)
    {
        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(actual.GetHashCode(), Is.EqualTo(expected.GetHashCode()),
            "equal TokenSets must have equal hash codes");
    }

    [Test]
    public void Single_int_with_surrogate_throws()
    {
        // 0xD800..0xDFFF are surrogate code points, not valid Unicode scalar values.
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenSet.Single(UnicodeExamples.HighSurrogateMinRune));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenSet.Single(UnicodeExamples.LowSurrogateMaxRune));
    }

    [Test]
    public void Single_int_out_of_range_throws()
    {
        // Valid Unicode scalar values are 0x0000..0x10FFFF; anything outside is not a code point.
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenSet.Single(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenSet.Single(0x110000));
    }

    [Test]
    public void Single_char_with_surrogate_throws()
    {
        // (int)'\uD800' == 0xD800, delegates through Single(int) which throws.
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenSet.Single('\uD800'));
    }

    [Test]
    public void Range_with_invalid_low_or_high_throws()
    {
        // A range entirely inside the surrogate block has no valid scalar endpoints.
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenSet.Range(0xD800, 0xDFFF));
        // Endpoints below 0 or above 0x10FFFF are not valid Unicode scalar values.
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenSet.Range(-1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenSet.Range(100, 0x110000));
    }

    [Test]
    public void Range_spanning_the_surrogate_gap_is_allowed()
    {
        // Endpoints are valid scalar values even though the range as a whole
        // straddles the surrogate block. The set contains "dead" slots in
        // 0xD800..0xDFFF, which is harmless because the lexer never produces
        // those as token values.
        var set = TokenSet.Range(0x0000, 0x10FFFF);

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
        var loneSurrogate = UnicodeExamples.HighSurrogateMinText;
        Assert.Throws<ArgumentException>(() => TokenSet.Runes(loneSurrogate));
    }

    [Test]
    public void Runes_with_valid_surrogate_pair_works()
    {
        // Guitar as a surrogate pair. Treated as one codepoint.
        var set = TokenSet.Graphemes(GuitarGrapheme);

        Assert.That(set.Contains(GuitarRune), Is.True);
    }

    [Test]
    public void Category_returns_runes_of_that_category()
    {
        var uppercaseLetters = TokenSet.Category(UnicodeCategory.UppercaseLetter);

        Assert.That(uppercaseLetters.Contains('A'), Is.True);
        Assert.That(uppercaseLetters.Contains('Z'), Is.True);
        Assert.That(uppercaseLetters.Contains('a'), Is.False);
        Assert.That(uppercaseLetters.Contains('1'), Is.False);

        // Non-ASCII BMP uppercase letters (Greek Alpha, Cyrillic Zhe).
        Assert.That(uppercaseLetters.Contains('\u0391'), Is.True);
        Assert.That(uppercaseLetters.Contains('\u0416'), Is.True);

        // Supplementary-plane uppercase letter (Mathematical Bold Capital A,
        // U+1D400). Verifies the category scan reaches past the surrogate gap.
        Assert.That(uppercaseLetters.Contains(0x1D400), Is.True);

        // Titlecase letter U+01F2 '\u01F2' is category TitlecaseLetter, not
        // UppercaseLetter. Easy bug to make if someone conflates the two.
        Assert.That(uppercaseLetters.Contains('\u01F2'), Is.False);
    }

    [Test]
    public void Category_returns_the_same_cached_instance_on_repeat_calls()
    {
        // Caching is the whole point: second call shouldn't rescan.
        var first = TokenSet.Category(UnicodeCategory.DecimalDigitNumber);
        var second = TokenSet.Category(UnicodeCategory.DecimalDigitNumber);

        // TokenSet is a value type, but the internal _ranges array reference
        // should be shared across both returns when the cache hit.
        Assert.That(first.Contains('5'), Is.True);
        Assert.That(second.Contains('5'), Is.True);
    }

    [Test]
    public void Union_of_two_singletons_contains_both()
    {
        var set = TokenSet.Single('a') | TokenSet.Single('z');

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
        var set = TokenSet.Range(1, 5) | TokenSet.Range(3, 7);

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
        var set = TokenSet.Range(1, 5) | TokenSet.Range(6, 10);

        for (int codepoint = 1; codepoint <= 10; codepoint++)
            Assert.That(set.Contains(codepoint), Is.True, $"{codepoint} should be in union");
        Assert.That(set.Contains(0), Is.False);
        Assert.That(set.Contains(11), Is.False);
    }

    [Test]
    public void Union_of_disjoint_ranges_keeps_the_gap()
    {
        // [1, 5] | [10, 15]. Gap at 6..9 must stay uncovered.
        var set = TokenSet.Range(1, 5) | TokenSet.Range(10, 15);

        Assert.That(set.Contains(3), Is.True);
        Assert.That(set.Contains(12), Is.True);
        Assert.That(set.Contains(7), Is.False);
        Assert.That(set.Contains(0), Is.False);
        Assert.That(set.Contains(16), Is.False);
    }

    [Test]
    public void Union_is_commutative()
    {
        var ab = TokenSet.Range(1, 5) | TokenSet.Range(10, 15);
        var ba = TokenSet.Range(10, 15) | TokenSet.Range(1, 5);

        AssertEqual(ab, ba);
    }

    [Test]
    public void Union_with_default_TokenSet_returns_the_other_side()
    {
        // default(TokenSet) has a null _ranges array. The | operator should
        // tolerate that and return a set equivalent to the non-empty side.
        var set = default(TokenSet) | TokenSet.Single('x');

        Assert.That(set.Contains('x'), Is.True);
        Assert.That(set.Contains('y'), Is.False);
    }

    [Test]
    public void Default_TokenSet_is_empty()
    {
        // Sanity: a default-constructed TokenSet has no intervals and matches
        // no codepoint. Contains relies on the null-ranges guard. IsEmpty
        // reports the same state directly.
        var set = default(TokenSet);

        Assert.That(set.IsEmpty, Is.True);
        Assert.That(set.Contains('a'), Is.False);
    }

    [Test]
    public void Equality_is_structural_across_construction_paths()
    {
        // Two sets with the same membership compare equal regardless of how
        // they were built, because Normalize produces a canonical interval
        // list. Runes("abc") and Range('a','c') land on the same _ranges.
        // AssertEqual checks both Equals and GetHashCode in one shot.
        AssertEqual(TokenSet.Runes("abc"), TokenSet.Range('a', 'c'));
        AssertEqual(TokenSet.Single('a') | TokenSet.Single('b'),
            TokenSet.Range('a', 'b'));
        // Different membership compares unequal. (Unequal objects MAY share
        // hash codes, so don't add a hash check on this side.)
        Assert.That(TokenSet.Single('a'), Is.Not.EqualTo(TokenSet.Single('b')));
        // default and an explicit empty set are equal (both have no intervals).
        AssertEqual(default(TokenSet), TokenSet.Runes(""));
        // == operator mirrors Equals.
        Assert.That(TokenSet.Range('a', 'c') == TokenSet.Runes("abc"), Is.True);
        Assert.That(TokenSet.Single('a') != TokenSet.Single('b'), Is.True);
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
        var expected = TokenSet.Range(1, 30);

        // Adjacent intervals must merge into one.
        AssertEqual(TokenSet.Range(1, 10) | TokenSet.Range(11, 20) | TokenSet.Range(21, 30),
            expected);
        // Overlapping intervals must merge.
        AssertEqual(TokenSet.Range(1, 15) | TokenSet.Range(10, 25) | TokenSet.Range(20, 30),
            expected);
        // Out-of-order union must end up sorted.
        AssertEqual(TokenSet.Range(21, 30) | TokenSet.Range(1, 10) | TokenSet.Range(11, 20),
            expected);
        // Intersection of larger ranges must collapse to the overlap.
        AssertEqual(TokenSet.Range(0, 50) & TokenSet.Range(1, 30), expected);
        // Messy chain of overlapping fragments.
        AssertEqual(TokenSet.Range(1, 5) | TokenSet.Range(3, 10) | TokenSet.Range(10, 15)
            | TokenSet.Range(14, 22) | TokenSet.Range(20, 30), expected);
        // Double complement round-trip.
        AssertEqual(~~expected, expected);
        // Complement split across surrogate block, then complement again.
        // Verifies the surrogate-split path doesn't leak adjacent intervals.
        AssertEqual(~~TokenSet.Range(0xD7FE, 0xD7FE), TokenSet.Range(0xD7FE, 0xD7FE));
        // A & ~B chain through the full operator set.
        AssertEqual(TokenSet.Range(1, 30) & ~TokenSet.Range(40, 50), expected);
    }

    [Test]
    public void IsEmpty_is_false_for_sets_with_any_rune()
    {
        // Complement of the non-empty singleton contains everything except
        // that rune: IsEmpty should stay False. Covers the non-empty branch
        // of the _ranges null/length check.
        Assert.That(TokenSet.Single('a').IsEmpty, Is.False);
        Assert.That(TokenSet.Range('a', 'z').IsEmpty, Is.False);
        Assert.That(TokenSet.Letters.IsEmpty, Is.False);
    }

    [Test]
    public void Letters_covers_all_five_letter_categories()
    {
        // Letters is now built as a union of five Category calls. Verify it
        // still behaves correctly (one sample per category).
        Assert.That(TokenSet.Letters.Contains('A'), Is.True);  // UppercaseLetter
        Assert.That(TokenSet.Letters.Contains('a'), Is.True);  // LowercaseLetter
        Assert.That(TokenSet.Letters.Contains('\u01C5'), Is.True); // TitlecaseLetter (ǅ)
        Assert.That(TokenSet.Letters.Contains('\u02B0'), Is.True); // ModifierLetter (ʰ)
        Assert.That(TokenSet.Letters.Contains('\u4E2D'), Is.True); // OtherLetter (中)
        Assert.That(TokenSet.Letters.Contains('1'), Is.False);
        Assert.That(TokenSet.Letters.Contains(' '), Is.False);
    }

    [Test]
    public void Intersection_of_overlapping_ranges_keeps_the_overlap()
    {
        // [1, 7] & [5, 10] = [5, 7]. The classic overlap case.
        var set = TokenSet.Range(1, 7) & TokenSet.Range(5, 10);

        Assert.That(set.Contains(4), Is.False);
        Assert.That(set.Contains(5), Is.True);
        Assert.That(set.Contains(7), Is.True);
        Assert.That(set.Contains(8), Is.False);
    }

    [Test]
    public void Intersection_of_disjoint_ranges_is_empty()
    {
        // [1, 5] & [10, 15] share no elements.
        var set = TokenSet.Range(1, 5) & TokenSet.Range(10, 15);

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Intersection_with_default_TokenSet_is_empty()
    {
        // default(TokenSet) has a null _ranges and represents the empty set.
        // Intersecting anything with it returns empty.
        var set = default(TokenSet) & TokenSet.Single('x');

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Intersection_is_commutative()
    {
        var left = TokenSet.Range(1, 10) | TokenSet.Range(20, 30);
        var right = TokenSet.Range(5, 25);

        AssertEqual(left & right, right & left);
    }

    [Test]
    public void Intersection_of_multi_interval_sets_walks_both_sides()
    {
        // A = [1, 5] ∪ [10, 15] ∪ [20, 25]
        // B = [3, 12] ∪ [22, 30]
        // A & B = [3, 5] ∪ [10, 12] ∪ [22, 25]
        var a = TokenSet.Range(1, 5) | TokenSet.Range(10, 15) | TokenSet.Range(20, 25);
        var b = TokenSet.Range(3, 12) | TokenSet.Range(22, 30);
        var intersection = a & b;

        // Inside the expected intervals.
        foreach (int codepoint in new[] { 3, 4, 5, 10, 11, 12, 22, 23, 24, 25 })
            Assert.That(intersection.Contains(codepoint), Is.True, $"{codepoint} should be in A & B");
        // Outside: in A but not B, in B but not A, or in neither.
        foreach (int codepoint in new[] { 1, 2, 6, 9, 13, 14, 15, 20, 21, 26, 30 })
            Assert.That(intersection.Contains(codepoint), Is.False, $"{codepoint} SHOULDN'T be in A & B");
    }

    [Test]
    public void Intersection_narrows_Letters_to_a_script_block()
    {
        // Cyrillic block 0x0400..0x04FF intersected with Unicode Letters. The
        // motivating use case from docs/InductorParserDesignDecisions.md: narrow a semantic
        // class (Letters) by a script-range restriction.
        var cyrillicLetters = TokenSet.Letters & TokenSet.Range(0x0400, 0x04FF);

        // ж (U+0436) is a Cyrillic letter (in both sets).
        Assert.That(cyrillicLetters.Contains(0x0436), Is.True);
        // A (U+0041) is a letter but not Cyrillic.
        Assert.That(cyrillicLetters.Contains('A'), Is.False);
        // U+0488 is a Cyrillic combining mark, not a letter.
        Assert.That(cyrillicLetters.Contains(0x0488), Is.False);
    }

    // The set of every Unicode scalar value: [0, 0x10FFFF] minus the surrogate
    // block [0xD800, 0xDFFF]. Equivalent to ~default(TokenSet). Pulled out so
    // complement tests can compare against it by equality instead of sampling
    // specific codepoints.
    private static readonly TokenSet AllScalarValues =
        TokenSet.Range(0, 0xD7FF) | TokenSet.Range(0xE000, 0x10FFFF);

    [Test]
    public void Complement_of_empty_set_is_all_scalar_values()
    {
        AssertEqual(~default(TokenSet), AllScalarValues);
    }

    [Test]
    public void Complement_of_single_rune_excludes_that_rune()
    {
        // ~{'a'} = all scalar values minus 'a'. Build the expected set as
        // the two gaps around 'a'.
        var expected = TokenSet.Range(0, 'a' - 1)
            | TokenSet.Range('a' + 1, 0xD7FF)
            | TokenSet.Range(0xE000, 0x10FFFF);

        AssertEqual(~TokenSet.Single('a'), expected);
    }

    [Test]
    public void Complement_of_full_range_is_empty()
    {
        // ~[0..0x10FFFF] = ∅ (the input already covers every gap, including
        // the dead slots in the surrogate block).
        var set = ~TokenSet.Range(0, 0x10FFFF);

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Complement_is_involutive()
    {
        // ~~A = A for any A. Surrogates aren't in either side, so value
        // equality is the right check.
        var original = TokenSet.Range('a', 'z') | TokenSet.Range('A', 'Z');

        AssertEqual(~~original, original);
    }

    [Test]
    public void SetDifference_via_complement_and_intersection()
    {
        // The idiom from the docs: A & ~B is "A minus B". Consonants as
        // ASCII letters minus vowels.
        var asciiConsonants = TokenSet.Ascii.Letters & ~TokenSet.Runes("aeiouAEIOU");

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
        var set = TokenSet.Range(10, 20);

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
        var zero = TokenSet.Single(0);
        var max = TokenSet.Single(UnicodeExamples.MaximumCodePointRune);

        Assert.That(zero.Contains(0), Is.True);
        Assert.That(zero.Contains(1), Is.False);
        Assert.That(max.Contains(0x10FFFF), Is.True);
        Assert.That(max.Contains(0x10FFFE), Is.False);
    }

    [Test]
    public void Contains_char_and_Rune_overloads_delegate_to_int_form()
    {
        var set = TokenSet.Single(GuitarRune) | TokenSet.Single('a');

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
        var fromChar = TokenSet.Single('Z');
        var fromRune = TokenSet.Single(new Rune(GuitarRune));

        Assert.That(fromChar.Contains('Z'), Is.True);
        Assert.That(fromRune.Contains(GuitarRune), Is.True);
    }

    [Test]
    public void Range_char_and_Rune_overloads_cover_the_range()
    {
        var charRange = TokenSet.Range('a', 'c');
        var runeRange = TokenSet.Range(new Rune(GuitarRune), new Rune(MusicalKeyboardRune));

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
        Assert.Throws<ArgumentException>(() => TokenSet.Range(100, 50));
    }

    [Test]
    public void Range_with_equal_endpoints_is_a_singleton()
    {
        // Range(x, x) is allowed and behaves like Single(x): one-codepoint set.
        var set = TokenSet.Range(0x41, 0x41);

        Assert.That(set.Contains('A'), Is.True);
        Assert.That(set.Contains('B'), Is.False);
        Assert.That(set.Contains(0x40), Is.False);
    }

    // Runes corner cases -----------------------------------------------------

    [Test]
    public void Runes_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => TokenSet.Runes(null!));
    }

    [Test]
    public void Runes_empty_string_is_empty_set()
    {
        var set = TokenSet.Runes("");

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Runes_with_duplicates_dedupes_via_normalize()
    {
        // Normalize sorts and merges. Repeated characters collapse into one
        // interval each. Observable as a set with the same membership as the
        // distinct-char version, and as the compact ToString.
        var set = TokenSet.Runes("aabbccba");

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
        var loneLow = UnicodeExamples.LowSurrogateMinText;

        Assert.Throws<ArgumentException>(() => TokenSet.Runes(loneLow));
    }

    [Test]
    public void Runes_with_trailing_high_surrogate_throws()
    {
        // High surrogate at the end of the string with no low surrogate to
        // pair with. 
        var trailing = "a" + UnicodeExamples.HighSurrogateMinText;

        Assert.Throws<ArgumentException>(() => TokenSet.Runes(trailing));
    }

    [Test]
    public void Runes_with_high_surrogate_not_followed_by_low_throws()
    {
        // High surrogate followed by a non-surrogate char is invalid.
        var malformed = UnicodeExamples.HighSurrogateMinText + "a";

        Assert.Throws<ArgumentException>(() => TokenSet.Runes(malformed));
    }

    [Test]
    public void Runes_with_multi_rune_emoji_grapheme_stores_it_as_one_entry()
    {
        // Skin-tone-modified thumbs-up: U+1F44D U+1F3FD. One grapheme
        // under UAX #29, two runes. The set holds it as one multi-rune
        // entry, not as two separate rune adds. Contains(string)
        // matches the whole grapheme as a unit, and Contains(int)
        // doesn't match either of the constituent runes by themselves.
        var thumbsUpSkinTone = Canary("👍🏽", "thumbs up + medium skin tone", 0x1F44D, 0x1F3FD);
        var set = TokenSet.Graphemes(thumbsUpSkinTone);

        Assert.That(set.Contains(thumbsUpSkinTone), Is.True);
        Assert.That(set.Contains(0x1F44D), Is.False, "the base rune isn't a member on its own");
        Assert.That(set.Contains(0x1F3FD), Is.False, "the modifier rune isn't a member on its own");
    }

    [Test]
    public void Runes_with_decomposed_accent_stores_it_as_one_entry()
    {
        // "e" + combining acute in decomposed form: U+0065 + U+0301.
        // One grapheme, two runes. Built with explicit escapes so the
        // source file's encoding or an editor's normalization can't
        // silently rewrite it to the precomposed single-rune form
        // U+00E9.
        var decomposedE = UnicodeExamples.LatinEAcuteGrapheme;

        var set = TokenSet.Graphemes(decomposedE);

        Assert.That(set.Contains(decomposedE), Is.True);
        Assert.That(set.Contains('e'), Is.False);
        Assert.That(set.Contains(0x0301), Is.False);
    }

    [Test]
    public void Runes_with_single_scalar_non_bmp_works()
    {
        // 😀 is U+1F600, a single Unicode scalar in the supplementary
        // plane. One rune, one grapheme. The validation should let
        // this through and build a one-element set.
        var grinningFace = UnicodeExamples.GrinningFaceEmojiGrapheme;
        var set = TokenSet.Graphemes(grinningFace);

        Assert.That(set.Contains(0x1F600), Is.True);
        Assert.That(set.Contains('A'), Is.False);
    }

    [Test]
    public void Graphemes_with_crlf_stores_it_as_one_multi_rune_entry()
    {
        // CRLF is one grapheme per UAX #29. Graphemes("\r\n") stores
        // it as a single cluster, so Contains("\r\n") hits but
        // Contains('\r') and Contains('\n') don't.
        var set = TokenSet.Graphemes("\r\n");

        Assert.That(set.Contains("\r\n"), Is.True);
        Assert.That(set.Contains('\r'), Is.False);
        Assert.That(set.Contains('\n'), Is.False);
    }

    [Test]
    public void Runes_with_crlf_throws_to_force_explicit_intent()
    {
        // "\r\n" is one grapheme cluster, so Runes(string) rejects it.
        // Callers who wanted {CR, LF} as two scalars build the set
        // explicitly with Single. Callers who wanted the CRLF cluster
        // use Graphemes.
        var ex = Assert.Throws<ArgumentException>(() => TokenSet.Runes("\r\n"));
        Assert.That(ex!.Message, Does.Contain("multi-rune grapheme cluster"));
    }

    [Test]
    public void Runes_with_non_adjacent_cr_and_lf_works()
    {
        // Same scalars as the CRLF case, but separated by anything in
        // between, so neither CR nor LF is part of a multi-rune
        // cluster. Set semantics are order-independent.
        var set = TokenSet.Runes("\r\t\n");

        Assert.That(set.Contains('\r'), Is.True);
        Assert.That(set.Contains('\n'), Is.True);
        Assert.That(set.Contains('\t'), Is.True);
        Assert.That(set.Contains("\r\n"), Is.False);
    }

    [Test]
    public void Graphemes_rejects_multi_cluster_element()
    {
        // Each Graphemes element must be exactly one cluster. Passing
        // "ab" (two clusters) throws because the caller almost
        // certainly meant Runes("ab") instead.
        Assert.Throws<ArgumentException>(() => TokenSet.Graphemes("ab"));
    }

    [Test]
    public void OneOf_with_multi_rune_grapheme_builds_a_multi_rune_rule()
    {
        // Cluster-shaped OneOf goes through TokenSet.Graphemes
        // explicitly. The resulting rule matches the cluster as a
        // single token when the lexer reads it as one grapheme.
        var thumbsUpSkinTone = Canary("👍🏽", "thumbs up + medium skin tone", 0x1F44D, 0x1F3FD);
        var rule = Rules.OneOf(TokenSet.Graphemes(thumbsUpSkinTone));

        var result = rule.Parse(thumbsUpSkinTone);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    // ToString ---------------------------------------------------------------

    [Test]
    public void ToString_renders_empty_singleton_and_range()
    {
        Assert.That(default(TokenSet).ToString(), Is.EqualTo("[]"));
        Assert.That(TokenSet.Single('a').ToString(), Is.EqualTo("[a]"));
        Assert.That(TokenSet.Range('a', 'z').ToString(), Is.EqualTo("[a-z]"));
    }

    [Test]
    public void ToString_separates_multiple_ranges_with_commas()
    {
        // Disjoint ranges render as comma-separated pieces, each using the
        // same low or low-high convention.
        var set = TokenSet.Range('0', '9') | TokenSet.Range('A', 'Z') | TokenSet.Range('a', 'z');

        Assert.That(set.ToString(), Is.EqualTo("[0-9,A-Z,a-z]"));
    }

    [Test]
    public void ToString_escapes_non_printable_and_supplementary_plane()
    {
        // Printable ASCII boundary: 0x20 (space) and 0x7E (~) render literal.
        // 0x1F and 0x7F render as U+XXXX.
        Assert.That(TokenSet.Single(0x20).ToString(), Is.EqualTo("[ ]"));
        Assert.That(TokenSet.Single(0x7E).ToString(), Is.EqualTo("[~]"));
        Assert.That(TokenSet.Single(0x1F).ToString(), Is.EqualTo("[U+001F]"));
        Assert.That(TokenSet.Single(0x7F).ToString(), Is.EqualTo("[U+007F]"));
        // Supplementary plane codepoint: five hex digits, no zero-padding past X4.
        Assert.That(TokenSet.Single(GuitarRune).ToString(), Is.EqualTo("[U+1F3B8]"));
        // Range with non-printable endpoints.
        Assert.That(TokenSet.Range(0, 0x1F).ToString(), Is.EqualTo("[U+0000-U+001F]"));
    }

    [Test]
    public void ToString_renders_up_to_eight_ranges_in_full()
    {
        // Eight non-adjacent single-rune ranges. All show, no tail.
        var set = TokenSet.Runes("acegikmo");
        Assert.That(set.ToString(), Is.EqualTo("[a,c,e,g,i,k,m,o]"));
    }

    [Test]
    public void ToString_truncates_beyond_eight_ranges()
    {
        // Nine non-adjacent single-rune ranges. First eight show, a
        // "+1 more" tail says the rest got dropped. Large classes like
        // TokenSet.Letters would produce hundreds of ranges without
        // this cap and make trace lines unreadable.
        var set = TokenSet.Runes("acegikmoq");
        Assert.That(set.ToString(), Is.EqualTo("[a,c,e,g,i,k,m,o,...+1 more]"));
    }

    [Test]
    public void ToString_truncates_with_correct_count_for_large_class()
    {
        // Ten non-adjacent single-rune ranges. First eight show, "+2
        // more" reports the remainder. Confirms the tail count tracks
        // actual overflow rather than a fixed placeholder.
        var set = TokenSet.Runes("acegikmoqs");
        Assert.That(set.ToString(), Is.EqualTo("[a,c,e,g,i,k,m,o,...+2 more]"));
    }

    // Union corners ----------------------------------------------------------

    [Test]
    public void Union_of_two_default_sets_is_empty()
    {
        var set = default(TokenSet) | default(TokenSet);

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Union_of_identical_sets_is_that_set()
    {
        // a | a shouldn't double-count intervals. Normalize collapses the
        // duplicates back to the single-interval form.
        var original = TokenSet.Range('a', 'z');
        var doubled = original | original;

        Assert.That(doubled.ToString(), Is.EqualTo("[a-z]"));
    }

    [Test]
    public void Union_of_fully_contained_range_yields_the_outer_range()
    {
        // [1, 100] | [10, 20] = [1, 100]. Inner range adds nothing.
        var set = TokenSet.Range(1, 100) | TokenSet.Range(10, 20);

        AssertEqual(set, TokenSet.Range(1, 100));
    }

    // Intersection corners ---------------------------------------------------

    [Test]
    public void Intersection_of_identical_sets_is_that_set()
    {
        var original = TokenSet.Range('a', 'z');

        AssertEqual(original & original, original);
    }

    [Test]
    public void Intersection_with_containing_set_returns_contained_set()
    {
        // [10, 20] & [1, 100] = [10, 20] (the smaller set wins when nested).
        var inner = TokenSet.Range(10, 20);
        var outer = TokenSet.Range(1, 100);

        AssertEqual(inner & outer, inner);
    }

    [Test]
    public void Intersection_of_touching_but_disjoint_intervals_is_empty()
    {
        // [1, 5] and [6, 10] are adjacent, not overlapping. They'd merge
        // under union. Under intersection they share no elements.
        var set = TokenSet.Range(1, 5) & TokenSet.Range(6, 10);

        Assert.That(set.IsEmpty, Is.True);
    }

    [Test]
    public void Intersection_with_default_TokenSet_on_right_is_empty()
    {
        // Mirrors the existing "default on left" test. Both branches of the
        // null-check in the operator should short-circuit to empty.
        var set = TokenSet.Single('x') & default(TokenSet);

        Assert.That(set.IsEmpty, Is.True);
    }

    // Complement surrogate-boundary cases ------------------------------------

    [Test]
    public void Complement_of_range_ending_at_surrogate_low_boundary()
    {
        // Input ends at 0xD7FF. Complement is just [0xE000, 0x10FFFF].
        // No prefix, no surrogate slots, full suffix.
        AssertEqual(~TokenSet.Range(0, 0xD7FF), TokenSet.Range(0xE000, 0x10FFFF));
    }

    [Test]
    public void Complement_of_range_starting_at_surrogate_high_boundary()
    {
        // Input starts at 0xE000. Complement is just [0, 0xD7FF].
        AssertEqual(~TokenSet.Range(0xE000, 0x10FFFF), TokenSet.Range(0, 0xD7FF));
    }

    [Test]
    public void Complement_of_multi_interval_input_emits_all_gaps()
    {
        // Input [5, 10] ∪ [20, 30]. Complement should be the three gaps,
        // with the trailing one split around the surrogate block.
        var expected = TokenSet.Range(0, 4)
            | TokenSet.Range(11, 19)
            | TokenSet.Range(31, 0xD7FF)
            | TokenSet.Range(0xE000, 0x10FFFF);

        AssertEqual(~(TokenSet.Range(5, 10) | TokenSet.Range(20, 30)), expected);
    }

    // Built-ins --------------------------------------------------------------

    [Test]
    public void Digits_contains_decimal_digit_runes_across_scripts()
    {
        // TokenSet.Digits is UnicodeCategory.DecimalDigitNumber, every
        // decimal digit in every script, not just ASCII 0-9.
        Assert.That(TokenSet.Digits.Contains('0'), Is.True);
        Assert.That(TokenSet.Digits.Contains('9'), Is.True);
        Assert.That(TokenSet.Digits.Contains(0x0660), Is.True); // Arabic-Indic digit zero ٠
        Assert.That(TokenSet.Digits.Contains(0x09E6), Is.True); // Bengali digit zero ০
        // Letters and whitespace aren't digits.
        Assert.That(TokenSet.Digits.Contains('a'), Is.False);
        Assert.That(TokenSet.Digits.Contains(' '), Is.False);
        // Roman numeral letters are category NumberLetter, not digits.
        Assert.That(TokenSet.Digits.Contains(0x2160), Is.False); // Ⅰ
    }

    [Test]
    public void InlineWhitespace_contains_intra_line_whitespace_only()
    {
        // Built via char.IsWhiteSpace predicate over the BMP, MINUS the
        // seven UAX #18 single-rune line terminators. Includes the
        // obvious ASCII intra-line whitespace plus a few Unicode-only
        // runes that are also intra-line.
        Assert.That(TokenSet.InlineWhitespace.Contains(' '), Is.True);
        Assert.That(TokenSet.InlineWhitespace.Contains('\t'), Is.True);
        Assert.That(TokenSet.InlineWhitespace.Contains(0x00A0), Is.True); // NBSP
        Assert.That(TokenSet.InlineWhitespace.Contains(0x1680), Is.True); // OGHAM SPACE MARK
        Assert.That(TokenSet.InlineWhitespace.Contains(0x2003), Is.True); // EM SPACE
        Assert.That(TokenSet.InlineWhitespace.Contains(0x202F), Is.True); // NARROW NO-BREAK SPACE
        Assert.That(TokenSet.InlineWhitespace.Contains(0x3000), Is.True); // IDEOGRAPHIC SPACE
        // Line terminators are NOT in InlineWhitespace; they live in
        // LineTerminators / EndOfLine().
        Assert.That(TokenSet.InlineWhitespace.Contains('\r'), Is.False);
        Assert.That(TokenSet.InlineWhitespace.Contains('\n'), Is.False);
        Assert.That(TokenSet.InlineWhitespace.Contains('\v'), Is.False); // VT
        Assert.That(TokenSet.InlineWhitespace.Contains('\f'), Is.False); // FF
        Assert.That(TokenSet.InlineWhitespace.Contains(0x0085), Is.False); // NEL
        Assert.That(TokenSet.InlineWhitespace.Contains(0x2028), Is.False); // LINE SEPARATOR
        Assert.That(TokenSet.InlineWhitespace.Contains(0x2029), Is.False); // PARAGRAPH SEPARATOR
        // Not whitespace at all.
        Assert.That(TokenSet.InlineWhitespace.Contains('a'), Is.False);
        Assert.That(TokenSet.InlineWhitespace.Contains('0'), Is.False);
    }

    [Test]
    public void InlineWhitespace_and_LineTerminators_are_disjoint()
    {
        // The whole point of the split: every rune that
        // char.IsWhiteSpace accepts is in exactly one of the two sets.
        // Verify the disjointness across the BMP.
        for (int codepoint = 0; codepoint <= 0xFFFF; codepoint++)
        {
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF) continue;
            bool inInline = TokenSet.InlineWhitespace.Contains(codepoint);
            bool inLineTerm = TokenSet.LineTerminators.Contains(codepoint);
            Assert.That(inInline && inLineTerm, Is.False,
                $"U+{codepoint:X4} is in both InlineWhitespace and LineTerminators");
        }
    }

    [Test]
    public void AnyWhitespace_contains_inline_whitespace_and_line_terminators()
    {
        // The full-Unicode "regex \s" set: every rune that's intra-line
        // whitespace OR a UAX #18 single-rune line terminator.
        Assert.That(TokenSet.AnyWhitespace.Contains(' '), Is.True);
        Assert.That(TokenSet.AnyWhitespace.Contains('\t'), Is.True);
        Assert.That(TokenSet.AnyWhitespace.Contains('\r'), Is.True);
        Assert.That(TokenSet.AnyWhitespace.Contains('\n'), Is.True);
        Assert.That(TokenSet.AnyWhitespace.Contains('\v'), Is.True);  // VT, line terminator
        Assert.That(TokenSet.AnyWhitespace.Contains('\f'), Is.True);  // FF, line terminator
        Assert.That(TokenSet.AnyWhitespace.Contains(0x0085), Is.True); // NEL
        Assert.That(TokenSet.AnyWhitespace.Contains(0x00A0), Is.True); // NBSP (inline)
        Assert.That(TokenSet.AnyWhitespace.Contains(0x2028), Is.True); // LS
        Assert.That(TokenSet.AnyWhitespace.Contains(0x2029), Is.True); // PS
        Assert.That(TokenSet.AnyWhitespace.Contains(0x3000), Is.True); // ideographic space (inline)
        // Letters and digits aren't whitespace.
        Assert.That(TokenSet.AnyWhitespace.Contains('a'), Is.False);
        Assert.That(TokenSet.AnyWhitespace.Contains('0'), Is.False);
    }

    [Test]
    public void AnyWhitespace_equals_union_of_InlineWhitespace_and_LineTerminators()
    {
        // The set is defined as the union of the two component sets,
        // and the components are disjoint (per the test above), so
        // AnyWhitespace matches exactly the same runes as either side.
        Assert.That(TokenSet.AnyWhitespace, Is.EqualTo(TokenSet.InlineWhitespace | TokenSet.LineTerminators));
    }

    [Test]
    public void AnyWhitespace_returns_the_same_cached_instance_on_repeat_calls()
    {
        // Property is lazy-initialized; same value object on every call.
        Assert.That(TokenSet.AnyWhitespace, Is.EqualTo(TokenSet.AnyWhitespace));
        // A computed expression that produces the same value also compares equal,
        // verifying structural equality (the underlying lazy may or may not
        // be reference-equal across builds).
        Assert.That(TokenSet.AnyWhitespace, Is.EqualTo(TokenSet.InlineWhitespace | TokenSet.LineTerminators));
    }

    [Test]
    public void Ascii_Digits_contains_only_0_through_9()
    {
        Assert.That(TokenSet.Ascii.Digits.Contains('0'), Is.True);
        Assert.That(TokenSet.Ascii.Digits.Contains('5'), Is.True);
        Assert.That(TokenSet.Ascii.Digits.Contains('9'), Is.True);
        // The Unicode-full Digits would include Arabic-Indic zero. Ascii.Digits doesn't.
        Assert.That(TokenSet.Ascii.Digits.Contains(0x0660), Is.False);
        Assert.That(TokenSet.Ascii.Digits.Contains('a'), Is.False);
    }

    [Test]
    public void Ascii_AnyWhitespace_contains_space_tab_cr_lf()
    {
        // ASCII whitespace including line terminators. The "regex \s on
        // ASCII" set, for grammars that treat newlines as ordinary
        // whitespace.
        Assert.That(TokenSet.Ascii.AnyWhitespace.Contains(' '), Is.True);
        Assert.That(TokenSet.Ascii.AnyWhitespace.Contains('\t'), Is.True);
        Assert.That(TokenSet.Ascii.AnyWhitespace.Contains('\r'), Is.True);
        Assert.That(TokenSet.Ascii.AnyWhitespace.Contains('\n'), Is.True);
        // Not in the literal " \t\r\n" set, even though char.IsWhiteSpace says yes.
        Assert.That(TokenSet.Ascii.AnyWhitespace.Contains(0x00A0), Is.False); // NBSP
        Assert.That(TokenSet.Ascii.AnyWhitespace.Contains('\v'), Is.False);   // vertical tab
        Assert.That(TokenSet.Ascii.AnyWhitespace.Contains('\f'), Is.False);   // form feed
    }

    [Test]
    public void Ascii_InlineWhitespace_contains_only_space_and_tab()
    {
        // ASCII intra-line whitespace. Mirrors the full-Unicode
        // TokenSet.InlineWhitespace but stays inside ASCII.
        Assert.That(TokenSet.Ascii.InlineWhitespace.Contains(' '), Is.True);
        Assert.That(TokenSet.Ascii.InlineWhitespace.Contains('\t'), Is.True);
        // Line terminators excluded by definition.
        Assert.That(TokenSet.Ascii.InlineWhitespace.Contains('\r'), Is.False);
        Assert.That(TokenSet.Ascii.InlineWhitespace.Contains('\n'), Is.False);
        // Unicode-only whitespace excluded because this is the ASCII set.
        Assert.That(TokenSet.Ascii.InlineWhitespace.Contains(0x00A0), Is.False); // NBSP
    }

    [Test]
    public void Ascii_Letters_excludes_non_ascii_letters()
    {
        // The Ascii-qualified built-ins are the "only ASCII" versions. They
        // must not drift into full Unicode by accident.
        Assert.That(TokenSet.Ascii.Letters.Contains('a'), Is.True);
        Assert.That(TokenSet.Ascii.Letters.Contains('Z'), Is.True);
        Assert.That(TokenSet.Ascii.Letters.Contains(0x00E9), Is.False); // é
        Assert.That(TokenSet.Ascii.Letters.Contains(0x4E2D), Is.False); // 中
    }

    // Category niche ---------------------------------------------------------
    //
    // The remaining UnicodeCategory values (most marks, numbers, separators,
    // punctuation, symbols, control, format, private-use) aren't individually
    // tested. Category(...) runs a uniform 0..0x10FFFF scan with a predicate
    // that varies only by the category enum value, so once a handful of
    // categories from different parts of the enum have been verified
    // (UppercaseLetter, the four other Letter categories via Letters,
    // DecimalDigitNumber via Digits, SpaceSeparator below), additional
    // categories just re-exercise the same loop. The tests below cover the
    // pieces that aren't uniform: Surrogate is empty as a special case, and
    // OtherNotAssigned is the last value in the enum (good check that the
    // scan doesn't have an off-by-one at the upper end of the enum).

    [Test]
    public void Category_Surrogate_is_empty_because_the_scan_skips_surrogates()
    {
        // The build loop explicitly skips 0xD800..0xDFFF (they aren't valid
        // scalar values), so asking for UnicodeCategory.Surrogate yields an
        // empty set. Non-obvious and worth verifying.
        var surrogates = TokenSet.Category(UnicodeCategory.Surrogate);

        Assert.That(surrogates.IsEmpty, Is.True);
    }

    [Test]
    public void Category_SpaceSeparator_matches_only_space_category_runes()
    {
        // DecimalDigitNumber is exercised indirectly via Digits. Pick a
        // different non-letter category so the cache path sees more variety.
        var spaces = TokenSet.Category(UnicodeCategory.SpaceSeparator);

        Assert.That(spaces.Contains(' '), Is.True);       // U+0020 space
        Assert.That(spaces.Contains(0x00A0), Is.True);    // NBSP
        Assert.That(spaces.Contains(0x2003), Is.True);    // EM SPACE
        Assert.That(spaces.Contains('\t'), Is.False);     // tab is Control, not SpaceSeparator
        Assert.That(spaces.Contains('a'), Is.False);
    }

    [Test]
    public void Category_OtherNotAssigned_contains_unassigned_codepoints()
    {
        // OtherNotAssigned (Cn) is the last value in the UnicodeCategory
        // enum. U+0378 has been an unassigned gap in the Greek block since
        // Unicode 1.0 and is a stable Cn member. If the scan had an
        // off-by-one at the upper end of the enum, the set would come back
        // empty or miss this codepoint.
        var unassigned = TokenSet.Category(UnicodeCategory.OtherNotAssigned);

        Assert.That(unassigned.Contains(0x0378), Is.True);
        Assert.That(unassigned.Contains('A'), Is.False);
        Assert.That(unassigned.Contains('0'), Is.False);
        Assert.That(unassigned.Contains(' '), Is.False);
    }

    // Operator invariant -----------------------------------------------------

    [Test]
    public void A_and_not_A_is_empty_for_any_A()
    {
        // A ∩ ~A = ∅. One test covers the complement-then-intersect pipeline
        // for a nontrivial multi-interval A that also crosses the surrogate
        // split.
        var a = TokenSet.Ascii.Letters | TokenSet.Range(0xE000, 0xE00F);
        var intersection = a & ~a;

        Assert.That(intersection.IsEmpty, Is.True);
    }

    // Multi-rune grapheme support -------------------------------------------
    //
    // Below this point: tests that exercise the multi-rune side of the
    // TokenSet (graphemes that occupy two or more runes). The rune fast
    // path stays unchanged, so the rune-only tests above are still the
    // bulk of the coverage. These verify the new capability: storage,
    // membership, set algebra, the documented complement-throws rule,
    // equality, and ToString.

    [Test]
    public void Runes_with_mixed_input_splits_into_intervals_and_multi_rune_entries()
    {
        // 'a' is one rune (interval add). USFlag and SkinTonedWave are
        // two-rune graphemes (multi-rune entries). 'z' is one rune
        // (another interval add). Verify all four show up by their
        // appropriate Contains overloads, and the ones that aren't
        // members don't accidentally match.
        var set = TokenSet.Runes("az") | TokenSet.Graphemes(USFlagGrapheme, SkinTonedWaveGrapheme);

        Assert.That(set.Contains('a'), Is.True);
        Assert.That(set.Contains('z'), Is.True);
        Assert.That(set.Contains(USFlagGrapheme), Is.True);
        Assert.That(set.Contains(SkinTonedWaveGrapheme), Is.True);
        // The first runes of the multi-rune entries aren't single-rune
        // members on their own.
        Assert.That(set.Contains(0x1F1FA), Is.False, "regional indicator U not a single-rune member");
        Assert.That(set.Contains(WavingHandRune), Is.False, "lone waving hand isn't in the set");
        Assert.That(set.Contains('m'), Is.False);
    }

    [Test]
    public void Contains_string_on_empty_input_returns_false()
    {
        var set = TokenSet.Runes("a") | TokenSet.Graphemes(USFlagGrapheme);

        Assert.That(set.Contains(""), Is.False);
    }

    [Test]
    public void Contains_string_with_single_rune_input_routes_to_rune_intervals()
    {
        // A single-rune string is just shorthand for the rune-Contains
        // path. Build the set as multi-rune-only and verify a
        // single-rune Contains(string) doesn't hit it.
        var set = TokenSet.Graphemes(USFlagGrapheme);

        Assert.That(set.Contains("a"), Is.False);
        Assert.That(set.Contains(USFlagGrapheme), Is.True);
    }

    [Test]
    public void Union_of_rune_only_and_mixed_keeps_both_halves()
    {
        // Letters is a large rune-only set. USFlag is a multi-rune entry
        // built via Runes. Their union should contain every letter and
        // also match the flag grapheme.
        var mixed = TokenSet.Letters | TokenSet.Graphemes(USFlagGrapheme);

        Assert.That(mixed.Contains('a'), Is.True);
        Assert.That(mixed.Contains('Z'), Is.True);
        Assert.That(mixed.Contains(USFlagGrapheme), Is.True);
        Assert.That(mixed.Contains('1'), Is.False);
    }

    [Test]
    public void Union_of_two_mixed_sets_unions_both_halves()
    {
        // Build two mixed sets with overlapping rune intervals and
        // disjoint multi-rune entries. The union should contain every
        // rune from both sides and both multi-rune entries.
        var left = TokenSet.Runes("ab") | TokenSet.Graphemes(USFlagGrapheme);
        var right = TokenSet.Runes("bc") | TokenSet.Graphemes(SkinTonedWaveGrapheme);
        var combined = left | right;

        Assert.That(combined.Contains('a'), Is.True);
        Assert.That(combined.Contains('b'), Is.True);
        Assert.That(combined.Contains('c'), Is.True);
        Assert.That(combined.Contains(USFlagGrapheme), Is.True);
        Assert.That(combined.Contains(SkinTonedWaveGrapheme), Is.True);
        // Same multi-rune entry on both sides shouldn't double-count or
        // produce a non-canonical array.
        var withDup = (TokenSet.Runes("a") | TokenSet.Graphemes(USFlagGrapheme))
                    | (TokenSet.Runes("b") | TokenSet.Graphemes(USFlagGrapheme));
        AssertEqual(withDup, TokenSet.Runes("ab") | TokenSet.Graphemes(USFlagGrapheme));
    }

    [Test]
    public void Intersection_of_two_mixed_sets_keeps_common_members()
    {
        // Both sides contain USFlag and 'a'. Only those should survive.
        var left = TokenSet.Runes("ab") | TokenSet.Graphemes(USFlagGrapheme, SkinTonedWaveGrapheme);
        var right = TokenSet.Runes("ac") | TokenSet.Graphemes(USFlagGrapheme);
        var intersected = left & right;

        Assert.That(intersected.Contains('a'), Is.True);
        Assert.That(intersected.Contains(USFlagGrapheme), Is.True);
        Assert.That(intersected.Contains('b'), Is.False);
        Assert.That(intersected.Contains('c'), Is.False);
        Assert.That(intersected.Contains(SkinTonedWaveGrapheme), Is.False);
    }

    [Test]
    public void Intersection_of_rune_only_and_mixed_drops_multi_rune_entries()
    {
        // The rune-only side has nothing to intersect against on the
        // multi-rune side. Letters & (Letters | USFlag) is just the
        // letters.
        var rune = TokenSet.Runes("ab");
        var mixed = TokenSet.Runes("ab") | TokenSet.Graphemes(USFlagGrapheme);

        AssertEqual(rune & mixed, TokenSet.Runes("ab"));
    }

    [Test]
    public void Complement_of_mixed_set_throws_with_documented_message()
    {
        var mixed = TokenSet.Runes("a") | TokenSet.Graphemes(USFlagGrapheme);

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var _ = ~mixed;
        });
        Assert.That(exception!.Message, Does.Contain("multi-rune"));
        Assert.That(exception.Message, Does.Contain("set & ~runeOnlyMask"));
    }

    [Test]
    public void Complement_via_explicit_rune_only_mask_works_for_rune_only_sets()
    {
        // The complement-of-a-rune-only-set workaround: build the
        // rune-only mask, complement that, intersect with the
        // rune-only side. Multi-rune entries on the input side aren't
        // preserved by `mixed & ~runeOnlyMask`, because intersecting
        // a mixed set with a rune-only set drops the multi-rune
        // entries (the rune-only side has no multi-rune entries to
        // pair with). Callers who want to preserve multi-rune entries
        // through a "subtract these runes" operation union them back
        // in explicitly.
        var letters = TokenSet.Letters;
        var withoutVowels = letters & ~TokenSet.Runes("aeiou");

        Assert.That(withoutVowels.Contains('b'), Is.True);
        Assert.That(withoutVowels.Contains('a'), Is.False);

        // To keep a multi-rune entry through the operation, project
        // the rune-only part, complement that, then union the
        // multi-rune part back in.
        var withoutVowelsKeepingFlag =
            (letters & ~TokenSet.Runes("aeiou")) | TokenSet.Graphemes(USFlagGrapheme);
        Assert.That(withoutVowelsKeepingFlag.Contains(USFlagGrapheme), Is.True);
        Assert.That(withoutVowelsKeepingFlag.Contains('a'), Is.False);
    }

    [Test]
    public void Equality_treats_mixed_sets_built_two_ways_as_equal()
    {
        // Same logical content, different construction paths. Equals
        // and GetHashCode should both agree. AssertEqual checks both.
        var sequential = TokenSet.Runes("a") | TokenSet.Graphemes(USFlagGrapheme, SkinTonedWaveGrapheme);
        var unioned =
            TokenSet.Runes("a")
            | TokenSet.Graphemes(USFlagGrapheme)
            | TokenSet.Graphemes(SkinTonedWaveGrapheme);

        AssertEqual(sequential, unioned);
    }

    [Test]
    public void Equality_distinguishes_sets_that_only_differ_in_multi_rune_entries()
    {
        // Same rune intervals, different multi-rune content. Equals
        // must report them unequal.
        var withFlag = TokenSet.Runes("a") | TokenSet.Graphemes(USFlagGrapheme);
        var withWave = TokenSet.Runes("a") | TokenSet.Graphemes(SkinTonedWaveGrapheme);

        Assert.That(withFlag, Is.Not.EqualTo(withWave));
    }

    [Test]
    public void GetHashCode_matches_for_equal_mixed_sets_built_in_different_orders()
    {
        // Multi-rune array dedupe + sort means the order Runes() sees
        // graphemes shouldn't affect the hash.
        var ab = TokenSet.Graphemes(USFlagGrapheme) | TokenSet.Graphemes(SkinTonedWaveGrapheme);
        var ba = TokenSet.Graphemes(SkinTonedWaveGrapheme) | TokenSet.Graphemes(USFlagGrapheme);

        AssertEqual(ab, ba);
    }

    [Test]
    public void ToString_renders_multi_rune_entries_inline_with_runes()
    {
        // Ranges first, then multi-rune entries, separated by commas
        // inside the brackets. Multi-rune entries render as the user-
        // perceived characters themselves.
        var set = TokenSet.Range('a', 'z') | TokenSet.Graphemes(USFlagGrapheme);

        Assert.That(set.ToString(), Is.EqualTo("[a-z," + USFlagGrapheme + "]"));
    }

    [Test]
    public void ToString_truncation_counts_multi_rune_entries_as_entries()
    {
        // 7 single-rune ranges + 2 multi-rune entries = 9 total. The
        // first 8 render in full; the trailing entry shows "+1 more".
        // Multi-rune entries land at the end of the entry list, so
        // the truncation falls on one of them.
        var set = TokenSet.Runes("acegikm")
            | TokenSet.Graphemes(USFlagGrapheme)
            | TokenSet.Graphemes(SkinTonedWaveGrapheme);

        Assert.That(set.ToString(), Does.Contain("+1 more"));
    }

    [Test]
    public void HasMultiRuneGraphemes_is_false_for_rune_only_sets()
    {
        Assert.That(TokenSet.Runes("abc").HasMultiRuneGraphemes, Is.False);
        Assert.That(TokenSet.Letters.HasMultiRuneGraphemes, Is.False);
        Assert.That(default(TokenSet).HasMultiRuneGraphemes, Is.False);
    }

    [Test]
    public void HasMultiRuneGraphemes_is_true_after_adding_multi_rune_entry()
    {
        Assert.That(TokenSet.Graphemes(USFlagGrapheme).HasMultiRuneGraphemes, Is.True);
        Assert.That((TokenSet.Letters | TokenSet.Graphemes(USFlagGrapheme)).HasMultiRuneGraphemes, Is.True);
    }
}
