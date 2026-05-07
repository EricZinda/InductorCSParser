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
    private static Rule StopOnPipe() => ScanUntil(TokenSet.Runes("|"));

    // A ScanUntil with the JSON-style shape: stop at ", escape start
    // \, escape end is one of "/\bfnrt. Matches the grammar the
    // benchmark uses in InductorJsonParser, minus the delimiters.
    private static Rule JsonLike()
    {
        var escapeEnd = OneOf(TokenSet.Runes("\"\\/bfnrt"));
        return ScanUntil(TokenSet.Runes("\""), new Rune('\\'), escapeEnd);
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
        // matches is still a successful parse. Wrap it with Token('|')
        // so the outer rule consumes the full input and the EOF check
        // passes.
        var rule = InductorParser.Rules.And(StopOnPipe(), Token('|'));
        var result = rule.Parse("|");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // The ScanUntil leaf contributes an empty range. Token('|')
        // is Delete so it drops out of the tree. Concatenated text is
        // "".
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""));
    }

    [Test]
    public void ScanUntil_stops_at_first_stopper_rune_without_consuming_it()
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
    public void ScanUntil_escape_start_check_is_grapheme_scoped()
    {
        // Parser-wide invariant: a token is one user-perceived character
        // (one UAX #29 grapheme cluster). Token('\\'), OneOf("\\"),
        // and Literal("\\") all refuse to match a '\<combining mark>'
        // cluster because the cluster as a whole isn't a single-char '\'.
        // ScanUntil's single-rune escape-start fast path follows the
        // same rule: the escape rune only triggers when the next token
        // is exactly that one rune, with no extending characters glued
        // onto it.
        //
        // For '\<U+0301>' (COMBINING ACUTE ACCENT, one two-rune cluster
        // by GB9), the cluster isn't the escape rune. It falls through
        // to body and is consumed wholesale. The remaining 'n' is also
        // body. The parse succeeds with the whole input as the body.
        var rule = JsonLike();
        var result = rule.Parse("\\́n");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("\\́n"),
            "the cluster '\\<U+0301>' is not the single-rune escape '\\', so it's body, " +
            "matching how Token('\\\\') and OneOf(\"\\\\\") would treat the same cluster.");
    }

    [Test]
    public void ScanUntil_stopper_check_is_grapheme_scoped()
    {
        // Companion to ScanUntil_escape_start_check_is_grapheme_scoped.
        // The stopper TokenSet membership runs against the whole next
        // token, not just its first rune. A '"<U+0301>' cluster is one
        // user-perceived character that isn't equal to '"' alone, so
        // ScanUntil(Runes("\"")) treats it as body and keeps scanning,
        // matching how OneOf("\"") refuses the same cluster.
        var rule = ScanUntil(TokenSet.Runes("\""));
        var result = rule.Parse("ab\"́cd");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("ab\"́cd"),
            "the '\"<U+0301>' cluster is not the single-rune stopper '\"', " +
            "so the scan continues past it as body.");
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
        var end = OneOf(TokenSet.Ascii.Letters);
        var rule = ScanUntil(TokenSet.Runes("|"), start, end);

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
        var end = OneOf(TokenSet.Ascii.Letters);
        var body = ScanUntil(TokenSet.Runes("|"), start, end);
        var rule = InductorParser.Rules.And(body, Token('|'));

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
        var end = OneOf(TokenSet.Ascii.Letters);
        var rule = ScanUntil(TokenSet.Runes("|"), start, end);

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
        var zeroWidthStart = Optional(Token('z'));  // matches empty if 'z' isn't next
        var zeroWidthEnd = Optional(Token('z'));
        var rule = ScanUntil(TokenSet.Runes("|"), zeroWidthStart, zeroWidthEnd);

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
        var start = Or(Token('$'), Token('?'));
        var end = OneOf(TokenSet.Ascii.Letters);
        var rule = ScanUntil(TokenSet.Runes("|"), start, end);

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
        var rule = InductorParser.Rules.And(body, Literal("\"\"\""));

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
        var rule = InductorParser.Rules.And(body, Literal("END"));

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
        var escapeEnd = OneOf(TokenSet.Runes("\"\\nt"));
        var body = ScanUntil(stopper, new Rune('\\'), escapeEnd);
        var rule = InductorParser.Rules.And(body, Literal("\"\"\""));

        var result = rule.Parse("a\\nb\"c\"\"\"");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("a\\nb\"c"));
    }

    // Malformed-UTF-16 cases. IL2CPP sanitizes lone-surrogate code
    // units in string *literals* to U+FFFD at compile time (see
    // TokenSetTests.Runes_with_lone_surrogate_throws for the same
    // pattern), so these tests build the malformed input at runtime
    // to make sure the lone-surrogate path actually runs.
    //
    // The default Compile uses FormC and string.Normalize would itself
    // throw on malformed UTF-16 before ScanUntil ever sees the input.
    // Compile each rule with null first to skip normalization and
    // deliver the surrogate through to the rule unchanged.
    //
    // The behavior under the grapheme-scoped design: the lexer
    // surfaces a lone surrogate as a one-char token with no
    // RuneValue. That token isn't in any TokenSet (entries are
    // valid Unicode scalars) and isn't the escape-start rune, so
    // ScanUntil consumes it as body, the same way
    // ZeroOrMore(NoneOf(stopAt)) would. See UnexpectedUnicodeTests
    // for the parser-wide story on lone surrogates.

    [TestCase((char)0xD800, TestName = "lone high surrogate (first)")]
    [TestCase((char)0xDBFF, TestName = "lone high surrogate (last)")]
    [TestCase((char)0xDC00, TestName = "lone low surrogate (first)")]
    [TestCase((char)0xDFFF, TestName = "lone low surrogate (last)")]
    public void ScanUntil_consumes_isolated_surrogate_half_as_body(char loneSurrogate)
    {
        // "abc" + <surrogate> + "xyz|"
        // ScanUntil consumes 'a', 'b', 'c', the lone surrogate, 'x',
        // 'y', 'z' as body and stops at the '|' stopper. Outer
        // Token('|') then matches the '|' and the parse succeeds.
        // The body Symbol's text equals the input slice byte-for-
        // byte, lone surrogate included.
        string input = "abc" + new string(loneSurrogate, 1) + "xyz|";
        var rule = InductorParser.Rules.And(StopOnPipe(), Token('|'));
        rule.Compile(null);

        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc" + new string(loneSurrogate, 1) + "xyz"),
            "ScanUntil body should include the lone surrogate as one body token.");
    }

    [Test]
    public void ScanUntil_consumes_lone_high_surrogate_at_end_of_input_as_body()
    {
        // Surrogate at the very end of input, nothing to pair with.
        // ScanUntil consumes 'a', 'b', 'c', then the lone surrogate
        // as body, then hits EOF and exits the loop normally.
        string input = "abc" + new string((char)0xD800, 1);
        var rule = StopOnPipe();
        rule.Compile(null);

        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(input),
            "Body should include the trailing lone surrogate.");
    }

    [Test]
    public void ScanUntil_lone_surrogate_round_trips_through_ToString()
    {
        // Round-trip property: the leaf Symbol's Memory is a zero-copy
        // slice of the input string, so whatever code units were in
        // the input come out of ToString() unchanged. This includes
        // unpaired surrogate halves, which .NET's System.String holds
        // verbatim (a String is any sequence of UTF-16 code units, no
        // well-formedness validation). UnexpectedUnicodeTests pins the
        // same property for AnyToken / Token(string) / OneOf / etc.;
        // this test pins it for ScanUntil specifically.
        string input = "before" + new string((char)0xD83D, 1) + "after|";
        // ScanUntil doesn't consume the stopper, so the parse leaves
        // trailing '|' input. AllowTrailingInput keeps Parse from
        // failing on the unconsumed pipe.
        var rule = InductorParser.Rules.And(StopOnPipe(), Token('|'));
        rule.Compile(null);

        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        string body = result.Tree!.ToString();

        // Round-trip: ToString() reproduces the input slice exactly.
        Assert.That(body, Is.EqualTo("before" + new string((char)0xD83D, 1) + "after"));
        // Length and the specific code unit at each position survive
        // unchanged. The lone-surrogate code unit at position 6 still
        // reads as 0xD83D.
        Assert.That(body.Length, Is.EqualTo(12));
        Assert.That(body[6], Is.EqualTo((char)0xD83D));
        // Reading back into the original input produces the same chars.
        Assert.That(body, Is.EqualTo(input.Substring(0, body.Length)));
    }

    [Test]
    public void Sealed_ScanUntil_rejects_Flatten()
    {
        var rule = ScanUntil(TokenSet.Runes("|"));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
    }

    [Test]
    public void Sealed_ScanUntil_rejects_WithError()
    {
        var rule = ScanUntil(TokenSet.Runes("|"));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.WithError("late"));
    }

    [Test]
    public void Sealed_ScanUntil_rejects_As()
    {
        var rule = ScanUntil(TokenSet.Runes("|"));
        rule.Compile();
        Assert.Throws<InvalidOperationException>(() => rule.As("late"));
    }

    // Multi-rune grapheme stopper -----------------------------------------

    [Test]
    public void ScanUntil_with_multi_rune_stopper_stops_at_the_Token()
    {
        // Stopper set has only the USFlag multi-rune entry. Body is
        // any single-rune token plus any non-USFlag grapheme. Scan
        // should consume "ab" + WomanShrugging and stop at the
        // following USFlag without consuming it.
        var stopOnFlag = ScanUntil(TokenSet.Runes(USFlagGrapheme));
        var rule = And(stopOnFlag, OneOf(TokenSet.Runes(USFlagGrapheme)));

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
        var stopper = TokenSet.Single('\n') | TokenSet.Runes(USFlagGrapheme);
        var rule = ScanUntil(stopper);

        var newlineCase = rule.Parse("hello\nrest", new ParseOptions { AllowTrailingInput = true });
        Assert.That(newlineCase.Success, Is.True, newlineCase.ErrorMessage);
        Assert.That(newlineCase.Tree!.ToString(), Is.EqualTo("hello"));

        var flagCase = rule.Parse("hello" + USFlagGrapheme + "rest", new ParseOptions { AllowTrailingInput = true });
        Assert.That(flagCase.Success, Is.True, flagCase.ErrorMessage);
        Assert.That(flagCase.Tree!.ToString(), Is.EqualTo("hello"));
    }
}
