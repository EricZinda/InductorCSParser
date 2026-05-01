using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

[TestFixture]
public class ScanUntilRuleTests
{
    // "Scan until pipe": the simplest stopper-style setup. Body is
    // any rune other than '|'. Scan stops at the first '|' without
    // consuming it. Mirrors the common "scan until the delimiter"
    // idiom that the stopper-based API is designed for.
    private static Rule StopOnPipe() => ScanUntil(RuneSet.Runes("|"));

    // A ScanUntil with the JSON-style shape: stop at ", escape start
    // \, escape end is one of "/\bfnrt. Matches the grammar the
    // benchmark uses in InductorJsonParser, minus the delimiters.
    private static Rule JsonLike()
    {
        var escapeEnd = OneOf(RuneSet.Runes("\"\\/bfnrt"));
        return ScanUntil(RuneSet.Runes("\""), new Rune('\\'), escapeEnd);
    }

    [Test]
    public void ScanUntil_matches_a_run_of_body_chars_into_one_leaf()
    {
        // "abcXYZ" has no '|' anywhere, so the whole input is body.
        var result = StopOnPipe().Parse("abcXYZ");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abcXYZ"));
        // One leaf Symbol for the whole run, not one per character. The
        // whole point of this rule.
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0));
    }

    [Test]
    public void ScanUntil_empty_match_is_success()
    {
        // First rune is the stopper, so the scan exits immediately
        // without consuming. Same semantics as ZeroOrMore: zero
        // matches is still a successful parse. Wrap it with Grapheme('|')
        // so the outer rule consumes the full input and the EOF check
        // passes.
        var rule = InductorParser.Rules.AllOf(StopOnPipe(), Grapheme('|'));
        var result = rule.Parse("|");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // The ScanUntil leaf contributes an empty range. Grapheme('|')
        // is Delete so it drops out of the tree. Concatenated text is
        // "".
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""));
    }

    [Test]
    public void ScanUntil_stops_at_first_stopper_rune_without_consuming_it()
    {
        // "abc|rest" scans 'a', 'b', 'c' as body, stops at '|' without
        // consuming it. The surrounding Grapheme('|') then consumes the
        // '|' itself. Tree text is "abc" (the body) plus "" (the
        // FlattenType.Delete delimiter) = "abc".
        var rule = InductorParser.Rules.AllOf(StopOnPipe(), Grapheme('|'));
        var result = rule.Parse("abc|");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void ScanUntil_escape_start_triggers_escape_end()
    {
        var rule = JsonLike();
        var result = rule.Parse(@"hello\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // The leaf carries the raw source text, backslash and all.
        // Lazy decode is deliberate: the primitive doesn't materialize
        // the decoded form.
        Assert.That(result.Tree!.ToString(), Is.EqualTo(@"hello\n"));
    }

    [Test]
    public void ScanUntil_back_to_back_escapes_all_match()
    {
        var rule = JsonLike();
        var result = rule.Parse(@"\n\t\\");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(@"\n\t\\"));
    }

    [Test]
    public void ScanUntil_escape_at_end_of_run_matches()
    {
        var rule = JsonLike();
        var result = rule.Parse(@"abc\t");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(@"abc\t"));
    }

    [Test]
    public void ScanUntil_bad_escape_end_fails_the_whole_match()
    {
        // 'q' isn't in the escape-end set. The whole ScanUntil
        // fails, and the outer transaction rolls the lexer back to
        // the start of the string body.
        var rule = JsonLike();
        var result = rule.Parse(@"abc\q");

        Assert.That(result.Success, Is.False);
        // Error position points at 'q' (offset 4), the rune that
        // tried and failed to match the escape end.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(4));
    }

    [Test]
    public void ScanUntil_unterminated_escape_at_eof_fails()
    {
        // Backslash with nothing after it. The escape end sees EOF
        // and fails, which fails the whole ScanUntil.
        var rule = JsonLike();
        var result = rule.Parse(@"abc\");

        Assert.That(result.Success, Is.False);
        // Error position is at the EOF offset, where the end tried
        // to read and couldn't.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(4));
    }

    [Test]
    public void ScanUntil_body_consumes_backslash_when_no_escape_configured()
    {
        // No-escape form, stopper is '|'. A '\' isn't a stopper and
        // there's no escape path, so it's consumed as body.
        var result = StopOnPipe().Parse(@"abc\");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(@"abc\"));
    }

    [Test]
    public void ScanUntil_returns_single_leaf_covering_whole_match()
    {
        // Tree shape matters because the performance win of this
        // primitive is "one Symbol per run, not one per rune." Lock
        // in the shape so a future change that accidentally splits
        // the leaf back into per-rune pieces fails loudly.
        var result = StopOnPipe().Parse("hello");
        Assert.That(result.Success, Is.True);

        Symbol tree = result.Tree!;
        Assert.That(tree.Children.Count, Is.EqualTo(0), "ScanUntil should produce a leaf, not a composite");
        Assert.That(tree.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void ScanUntil_multi_rune_start_triggers_escape_end()
    {
        // Start is the two-rune sequence "$$". Stopper is '|'. End
        // is one letter. Matches "abc$$X" up through the end.
        var start = Literal("$$");
        var end = OneOf(RuneSet.Ascii.Letters);
        var rule = ScanUntil(RuneSet.Runes("|"), start, end);

        var result = rule.Parse("abc$$X");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc$$X"));
    }

    [Test]
    public void ScanUntil_multi_rune_start_falls_through_when_only_first_char_matches()
    {
        // Start is "$$". Input has a bare '$' (not followed by another
        // '$'), so the start rule fails via its own transaction rollback
        // and the '$' is consumed as body. The scan continues past it
        // until the pipe stopper.
        var start = Literal("$$");
        var end = OneOf(RuneSet.Ascii.Letters);
        var body = ScanUntil(RuneSet.Runes("|"), start, end);
        var rule = InductorParser.Rules.AllOf(body, Grapheme('|'));

        var result = rule.Parse("abc$xyz|");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc$xyz"));
    }

    [Test]
    public void ScanUntil_multi_rune_start_then_bad_end_fails_whole_match()
    {
        // Start matches "$$", end requires a letter. Input "abc$$1"
        // has a digit where the end expects a letter: whole
        // ScanUntil fails, outer transaction rolls the position
        // back.
        var start = Literal("$$");
        var end = OneOf(RuneSet.Ascii.Letters);
        var rule = ScanUntil(RuneSet.Runes("|"), start, end);

        var result = rule.Parse("abc$$1");
        Assert.That(result.Success, Is.False);
        // Error position is where the end started trying, i.e.
        // after the consumed "$$".
        Assert.That(result.ErrorCharIndex, Is.EqualTo(5));
    }

    [Test]
    public void ScanUntil_zero_width_escape_does_not_loop_forever()
    {
        // Pathological grammar: both the start and the end match
        // zero-width. A naive scan would treat that as "matched an
        // escape, continue" and spin forever on the same rune. The
        // zero-width guard in the hot loop catches it and breaks
        // the scan cleanly, same pattern as BetweenInclusiveRule
        // uses for the same reason. A per-case timeout would be a
        // belt-and-suspenders backstop, but the core assertion here
        // is just "Parse returns in bounded time."
        var zeroWidthStart = Optional(Grapheme('z'));  // matches empty if 'z' isn't next
        var zeroWidthEnd = Optional(Grapheme('z'));
        var rule = ScanUntil(RuneSet.Runes("|"), zeroWidthStart, zeroWidthEnd);

        // Input contains no 'z' and no '|'. Every iteration would see
        // 'a' as non-stopper, zeroWidthStart matches empty, zeroWidthEnd
        // matches empty. The guard fires and we break without consuming.
        var result = rule.Parse("aaa");

        // Result: zero-char match, EOF check fails because 'aaa' is
        // unconsumed. We don't care about Success here, we care that
        // Parse returned at all.
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void ScanUntil_rule_based_start_supports_alternative_starts()
    {
        // Starts are '$' OR '?'. Stopper is '|'. Demonstrates that
        // escapeStart can be a sub-rule, not just a fixed literal.
        var start = FirstOf(Grapheme('$'), Grapheme('?'));
        var end = OneOf(RuneSet.Ascii.Letters);
        var rule = ScanUntil(RuneSet.Runes("|"), start, end);

        var r1 = rule.Parse("abc$X");
        Assert.That(r1.Success, Is.True, r1.ErrorMessage);
        Assert.That(r1.Tree!.ToString(), Is.EqualTo("abc$X"));

        var r2 = rule.Parse("abc?Y");
        Assert.That(r2.Success, Is.True, r2.ErrorMessage);
        Assert.That(r2.Tree!.ToString(), Is.EqualTo("abc?Y"));
    }

    [Test]
    public void ScanUntil_rule_based_stopper_stops_at_multi_rune_boundary()
    {
        // C++ raw-string / Python triple-quote flavor: the stop
        // condition is a multi-rune sequence. The stopper rule runs
        // in a peek transaction, so the stopper isn't consumed by
        // ScanUntil. The surrounding grammar matches it after.
        var stopper = Literal("\"\"\"");
        var body = ScanUntil(stopper);
        var rule = InductorParser.Rules.AllOf(body, Literal("\"\"\""));

        var result = rule.Parse("hello \"world\" yes\"\"\"");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // Body includes single '"' chars. Only the triple-quote
        // sequence terminates the scan.
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello \"world\" yes"));
    }

    [Test]
    public void ScanUntil_rule_based_stopper_does_not_consume_the_boundary()
    {
        // Verify the peek-semantic: after ScanUntil succeeds, the
        // lexer position must sit at the START of the stopper, so
        // the surrounding grammar can match it.
        var stopper = Literal("END");
        var body = ScanUntil(stopper);
        var rule = InductorParser.Rules.AllOf(body, Literal("END"));

        // If the stopper had been consumed by ScanUntil, the
        // outer Literal("END") would fail (EOF or leftover garbage).
        var result = rule.Parse("somecontentEND");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("somecontent"));
    }

    [Test]
    public void ScanUntil_rule_based_stopper_with_escape_start()
    {
        // C++-raw-string wouldn't have an escape, but Python
        // triple-quote DOES process escapes. Verify the combination
        // works: multi-rune stop boundary AND a backslash escape.
        var stopper = Literal("\"\"\"");
        var escapeEnd = OneOf(RuneSet.Runes("\"\\nt"));
        var body = ScanUntil(stopper, new Rune('\\'), escapeEnd);
        var rule = InductorParser.Rules.AllOf(body, Literal("\"\"\""));

        var result = rule.Parse("a\\nb\"c\"\"\"");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a\\nb\"c"));
    }

    // Malformed-UTF-16 cases. IL2CPP sanitizes lone-surrogate code
    // units in string *literals* to U+FFFD at compile time (see
    // RuneSetTests.Runes_with_lone_surrogate_throws for the same
    // pattern), so these tests build the malformed input at runtime
    // to make sure the TryPeekRune surrogate branch actually runs.
    //
    // The default ParseOptions.NormalizeInput calls string.Normalize,
    // which itself throws on malformed UTF-16 before ScanUntil ever
    // sees the input. Pass NormalizeInput = null to skip normalization
    // and deliver the surrogate through to the rule unchanged.
    private static readonly ParseOptions NoNormalize = new() { NormalizeInput = null };

    [TestCase((char)0xD800, TestName = "lone high surrogate (first)")]
    [TestCase((char)0xDBFF, TestName = "lone high surrogate (last)")]
    [TestCase((char)0xDC00, TestName = "lone low surrogate (first)")]
    [TestCase((char)0xDFFF, TestName = "lone low surrogate (last)")]
    public void ScanUntil_stops_at_isolated_surrogate_half(char loneSurrogate)
    {
        // "abc" + <surrogate> + "xyz|"
        // ScanUntil scans 'a', 'b', 'c' as body. At position 3 it
        // peeks the surrogate: TryPeekRune returns false, the scan
        // breaks without throwing or looping. Outer Grapheme('|') then
        // tries to match at position 3, can't match a surrogate, so
        // the whole parse fails with ErrorCharIndex pointing at 3.
        string input = "abc" + new string(loneSurrogate, 1) + "xyz|";
        var rule = InductorParser.Rules.AllOf(StopOnPipe(), Grapheme('|'));

        var result = rule.Parse(input, NoNormalize);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3),
            "ScanUntil should stop exactly at the surrogate, not before or after");
    }

    [Test]
    public void ScanUntil_stops_at_lone_high_surrogate_at_end_of_input()
    {
        // Surrogate at the very end of input, nothing to pair with.
        // Hits the branch of TryPeekRune where pos+1 >= inputLen so
        // the pair-decode short-circuit is skipped and the IsSurrogate
        // fallback catches it.
        string input = "abc" + new string((char)0xD800, 1);

        var result = StopOnPipe().Parse(input, NoNormalize);

        // ScanUntil matches "abc" and stops at position 3. The
        // outer Parse's EOF check fails because the surrogate is
        // unconsumed.
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
    }

    [Test]
    public void Sealed_ScanUntil_rejects_Flatten()
    {
        var rule = ScanUntil(RuneSet.Runes("|"));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_ScanUntil_rejects_WithError()
    {
        var rule = ScanUntil(RuneSet.Runes("|"));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_ScanUntil_rejects_As()
    {
        var rule = ScanUntil(RuneSet.Runes("|"));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // Multi-rune grapheme stopper -----------------------------------------

    [Test]
    public void ScanUntil_with_multi_rune_stopper_stops_at_the_grapheme()
    {
        // Stopper set has only the USFlag multi-rune entry. Body is
        // any single-rune token plus any non-USFlag grapheme. Scan
        // should consume "ab" + WomanShrugging and stop at the
        // following USFlag without consuming it.
        var stopOnFlag = ScanUntil(RuneSet.Runes(USFlagGrapheme));
        var rule = AllOf(stopOnFlag, OneOf(RuneSet.Runes(USFlagGrapheme)));

        var input = "ab" + WomanShruggingGrapheme + USFlagGrapheme;
        var result = rule.Parse(input,
            new ParseOptions { PreserveAllSymbols = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(input));
    }

    [Test]
    public void ScanUntil_with_mixed_stopper_stops_at_first_matching_token()
    {
        // Mixed stopper: rune-only newline plus a multi-rune flag.
        // The body should consume letters until it hits either a
        // newline rune or the flag grapheme. Use AllowTrailingInput
        // because ScanUntil doesn't consume the stopper, so the parse
        // wouldn't reach EOF on its own.
        var stopper = RuneSet.Single('\n') | RuneSet.Runes(USFlagGrapheme);
        var rule = ScanUntil(stopper);

        var newlineCase = rule.Parse("hello\nrest", new ParseOptions { AllowTrailingInput = true });
        Assert.That(newlineCase.Success, Is.True, newlineCase.ErrorMessage);
        Assert.That(newlineCase.Tree!.ToString(), Is.EqualTo("hello"));

        var flagCase = rule.Parse("hello" + USFlagGrapheme + "rest", new ParseOptions { AllowTrailingInput = true });
        Assert.That(flagCase.Success, Is.True, flagCase.ErrorMessage);
        Assert.That(flagCase.Tree!.ToString(), Is.EqualTo("hello"));
    }
}
