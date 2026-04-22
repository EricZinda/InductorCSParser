using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

[TestFixture]
public class StringCharsRuleTests
{
    // "Scan until pipe": the simplest stopper-style setup. Body is
    // any rune other than '|'. Scan stops at the first '|' without
    // consuming it. Mirrors the common "scan until the delimiter"
    // idiom that the stopper-based API is designed for.
    private static Rule StopOnPipe() => StringChars(RuneSet.Runes("|"));

    // A StringChars with the JSON-style shape: stop at ", escape start
    // \, escape end is one of "/\bfnrt. Matches the grammar the
    // benchmark uses in InductorJsonParser, minus the delimiters.
    private static Rule JsonLike()
    {
        var escapeEnd = RuneIn(RuneSet.Runes("\"\\/bfnrt"));
        return StringChars(RuneSet.Runes("\""), new Rune('\\'), escapeEnd);
    }

    [Test]
    public void StringChars_matches_a_run_of_body_chars_into_one_leaf()
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
    public void StringChars_empty_match_is_success()
    {
        // First rune is the stopper, so the scan exits immediately
        // without consuming. Same semantics as ZeroOrMore: zero
        // matches is still a successful parse. Wrap it with Token('|')
        // so the outer rule consumes the full input and the EOF check
        // passes.
        var rule = InductorParser.Rules.And(StopOnPipe(), Token('|'));
        var result = rule.Parse("|");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // The StringChars leaf contributes an empty slice. Token('|')
        // is Delete so it drops out of the tree. Concatenated text is
        // "".
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""));
    }

    [Test]
    public void StringChars_stops_at_first_stopper_rune_without_consuming_it()
    {
        // "abc|rest" scans 'a', 'b', 'c' as body, stops at '|' without
        // consuming it. The surrounding Token('|') then consumes the
        // '|' itself. Tree text is "abc" (the body) plus "" (the
        // FlattenType.Delete delimiter) = "abc".
        var rule = InductorParser.Rules.And(StopOnPipe(), Token('|'));
        var result = rule.Parse("abc|");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void StringChars_escape_start_triggers_escape_end()
    {
        var rule = JsonLike();
        var result = rule.Parse(@"hello\n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // The leaf carries the raw source slice, backslash and all.
        // Lazy decode is deliberate: the primitive doesn't materialize
        // the decoded form.
        Assert.That(result.Tree!.ToString(), Is.EqualTo(@"hello\n"));
    }

    [Test]
    public void StringChars_back_to_back_escapes_all_match()
    {
        var rule = JsonLike();
        var result = rule.Parse(@"\n\t\\");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(@"\n\t\\"));
    }

    [Test]
    public void StringChars_escape_at_end_of_run_matches()
    {
        var rule = JsonLike();
        var result = rule.Parse(@"abc\t");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(@"abc\t"));
    }

    [Test]
    public void StringChars_bad_escape_end_fails_the_whole_match()
    {
        // 'q' isn't in the escape-end set. The whole StringChars
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
    public void StringChars_unterminated_escape_at_eof_fails()
    {
        // Backslash with nothing after it. The escape end sees EOF
        // and fails, which fails the whole StringChars.
        var rule = JsonLike();
        var result = rule.Parse(@"abc\");

        Assert.That(result.Success, Is.False);
        // Error position is at the EOF offset, where the end tried
        // to read and couldn't.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(4));
    }

    [Test]
    public void StringChars_body_consumes_backslash_when_no_escape_configured()
    {
        // No-escape form, stopper is '|'. A '\' isn't a stopper and
        // there's no escape path, so it's consumed as body.
        var result = StopOnPipe().Parse(@"abc\");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(@"abc\"));
    }

    [Test]
    public void StringChars_returns_single_leaf_covering_whole_match()
    {
        // Tree shape matters because the performance win of this
        // primitive is "one Symbol per run, not one per rune." Lock
        // in the shape so a future change that accidentally splits
        // the leaf back into per-rune pieces fails loudly.
        var result = StopOnPipe().Parse("hello");
        Assert.That(result.Success, Is.True);

        Symbol tree = result.Tree!;
        Assert.That(tree.Children.Count, Is.EqualTo(0), "StringChars should produce a leaf, not a composite");
        Assert.That(tree.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void StringChars_multi_rune_start_triggers_escape_end()
    {
        // Start is the two-rune sequence "$$". Stopper is '|'. End
        // is one letter. Matches "abc$$X" up through the end.
        var start = Literal("$$");
        var end = RuneIn(RuneSet.Ascii.Letters);
        var rule = StringChars(RuneSet.Runes("|"), start, end);

        var result = rule.Parse("abc$$X");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc$$X"));
    }

    [Test]
    public void StringChars_multi_rune_start_falls_through_when_only_first_char_matches()
    {
        // Start is "$$". Input has a bare '$' (not followed by another
        // '$'), so the start rule fails via its own transaction rollback
        // and the '$' is consumed as body. The scan continues past it
        // until the pipe stopper.
        var start = Literal("$$");
        var end = RuneIn(RuneSet.Ascii.Letters);
        var body = StringChars(RuneSet.Runes("|"), start, end);
        var rule = InductorParser.Rules.And(body, Token('|'));

        var result = rule.Parse("abc$xyz|");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc$xyz"));
    }

    [Test]
    public void StringChars_multi_rune_start_then_bad_end_fails_whole_match()
    {
        // Start matches "$$", end requires a letter. Input "abc$$1"
        // has a digit where the end expects a letter: whole
        // StringChars fails, outer transaction rolls the position
        // back.
        var start = Literal("$$");
        var end = RuneIn(RuneSet.Ascii.Letters);
        var rule = StringChars(RuneSet.Runes("|"), start, end);

        var result = rule.Parse("abc$$1");
        Assert.That(result.Success, Is.False);
        // Error position is where the end started trying, i.e.
        // after the consumed "$$".
        Assert.That(result.ErrorCharIndex, Is.EqualTo(5));
    }

    [Test]
    public void StringChars_zero_width_escape_does_not_loop_forever()
    {
        // Pathological grammar: both the start and the end match
        // zero-width. A naive scan would treat that as "matched an
        // escape, continue" and spin forever on the same rune. The
        // zero-width guard in the hot loop catches it and breaks
        // the scan cleanly, same pattern as BetweenInclusiveRule
        // uses for the same reason. A per-case timeout would be a
        // belt-and-suspenders backstop, but the core assertion here
        // is just "Parse returns in bounded time."
        var zeroWidthStart = Optional(Token('z'));  // matches empty if 'z' isn't next
        var zeroWidthEnd = Optional(Token('z'));
        var rule = StringChars(RuneSet.Runes("|"), zeroWidthStart, zeroWidthEnd);

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
    public void StringChars_rule_based_start_supports_alternative_starts()
    {
        // Starts are '$' OR '?'. Stopper is '|'. Demonstrates that
        // escapeStart can be a sub-rule, not just a fixed literal.
        var start = Or(Token('$'), Token('?'));
        var end = RuneIn(RuneSet.Ascii.Letters);
        var rule = StringChars(RuneSet.Runes("|"), start, end);

        var r1 = rule.Parse("abc$X");
        Assert.That(r1.Success, Is.True, r1.ErrorMessage);
        Assert.That(r1.Tree!.ToString(), Is.EqualTo("abc$X"));

        var r2 = rule.Parse("abc?Y");
        Assert.That(r2.Success, Is.True, r2.ErrorMessage);
        Assert.That(r2.Tree!.ToString(), Is.EqualTo("abc?Y"));
    }

    [Test]
    public void StringChars_rule_based_stopper_stops_at_multi_rune_boundary()
    {
        // C++ raw-string / Python triple-quote flavor: the stop
        // condition is a multi-rune sequence. The stopper rule runs
        // in a peek transaction, so the stopper isn't consumed by
        // StringChars. The surrounding grammar matches it after.
        var stopper = Literal("\"\"\"");
        var body = StringChars(stopper);
        var rule = InductorParser.Rules.And(body, Literal("\"\"\""));

        var result = rule.Parse("hello \"world\" yes\"\"\"");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // Body includes single '"' chars. Only the triple-quote
        // sequence terminates the scan.
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello \"world\" yes"));
    }

    [Test]
    public void StringChars_rule_based_stopper_does_not_consume_the_boundary()
    {
        // Verify the peek-semantic: after StringChars succeeds, the
        // lexer position must sit at the START of the stopper, so
        // the surrounding grammar can match it.
        var stopper = Literal("END");
        var body = StringChars(stopper);
        var rule = InductorParser.Rules.And(body, Literal("END"));

        // If the stopper had been consumed by StringChars, the
        // outer Literal("END") would fail (EOF or leftover garbage).
        var result = rule.Parse("somecontentEND");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("somecontent"));
    }

    [Test]
    public void StringChars_rule_based_stopper_with_escape_start()
    {
        // C++-raw-string would not have an escape, but Python
        // triple-quote DOES process escapes. Verify the combination
        // works: multi-rune stop boundary AND a backslash escape.
        var stopper = Literal("\"\"\"");
        var escapeEnd = RuneIn(RuneSet.Runes("\"\\nt"));
        var body = StringChars(stopper, new Rune('\\'), escapeEnd);
        var rule = InductorParser.Rules.And(body, Literal("\"\"\""));

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
    // which itself throws on malformed UTF-16 before StringChars ever
    // sees the input. Pass NormalizeInput = null to skip normalization
    // and deliver the surrogate through to the rule unchanged.
    private static readonly ParseOptions NoNormalize = new() { NormalizeInput = null };

    [TestCase((char)0xD800, TestName = "lone high surrogate (first)")]
    [TestCase((char)0xDBFF, TestName = "lone high surrogate (last)")]
    [TestCase((char)0xDC00, TestName = "lone low surrogate (first)")]
    [TestCase((char)0xDFFF, TestName = "lone low surrogate (last)")]
    public void StringChars_stops_at_isolated_surrogate_half(char loneSurrogate)
    {
        // "abc" + <surrogate> + "xyz|"
        // StringChars scans 'a', 'b', 'c' as body. At position 3 it
        // peeks the surrogate: TryPeekRune returns false, the scan
        // breaks without throwing or looping. Outer Token('|') then
        // tries to match at position 3, can't match a surrogate, so
        // the whole parse fails with ErrorCharIndex pointing at 3.
        string input = "abc" + new string(loneSurrogate, 1) + "xyz|";
        var rule = InductorParser.Rules.And(StopOnPipe(), Token('|'));

        var result = rule.Parse(input, NoNormalize);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3),
            "StringChars should stop exactly at the surrogate, not before or after");
    }

    [Test]
    public void StringChars_stops_at_lone_high_surrogate_at_end_of_input()
    {
        // Surrogate at the very end of input, nothing to pair with.
        // Hits the branch of TryPeekRune where pos+1 >= inputLen so
        // the pair-decode short-circuit is skipped and the IsSurrogate
        // fallback catches it.
        string input = "abc" + new string((char)0xD800, 1);

        var result = StopOnPipe().Parse(input, NoNormalize);

        // StringChars matches "abc" and stops at position 3. The
        // outer Parse's EOF check fails because the surrogate is
        // unconsumed.
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
    }

    [Test]
    public void StringChars_handles_supplementary_plane_rune()
    {
        // 'a' + '🎸' (U+1F3B8, a surrogate pair in UTF-16) + 'b'.
        // The rune-decoding path in the scan loop needs to treat
        // the pair as one rune and include both chars in the
        // matched slice.
        var rule = StringChars(RuneSet.Runes("|"));
        string input = "a\U0001F3B8b";
        var result = rule.Parse(input, new ParseOptions { InputUnit = InputUnit.Rune });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(input));
    }
}
