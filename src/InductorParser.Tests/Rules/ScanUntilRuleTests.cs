using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

[TestFixture]
public class ScanUntilRuleTests
{
    // "Scan until pipe": the simplest stopper-style setup. Body is
    // any rune other than '|'. Scan stops at the first '|' without
    // consuming it. Mirrors the common "scan until the delimiter"
    // idiom that the stopper-based API is designed for. Strict by
    // default: fails if EOF is reached without a '|' in the input.
    private static Rule StopOnPipe() => ScanUntil(TokenSet.Runes("|"));

    // Tolerant sibling: EOF is also a valid end of the scan. Used by
    // tests that intentionally scan inputs containing no '|', where
    // the assertion is about the body content (escape handling, lone
    // surrogates, leaf shape) rather than the stopper requirement.
    private static Rule StopOnPipeOrEof() => ScanUntil(TokenSet.Runes("|"), eofIsTerminator: true);

    // A ScanUntil with the JSON-style shape: stop at ", escape start
    // \, escape end is one of "/\bfnrt. Matches the grammar the
    // benchmark uses in InductorJsonParser, minus the delimiters.
    // eofIsTerminator: true so these tests can drive bodies that don't
    // include the closing quote; the focus is the escape handling and
    // body composition, not the stopper-required check (which has its
    // own dedicated tests).
    private static Rule JsonLike()
    {
        var escapeEnd = OneOf(TokenSet.Runes("\"\\/bfnrt"));
        return ScanUntil(TokenSet.Runes("\""), new Rune('\\'), escapeEnd, eofIsTerminator: true);
    }

    [Test]
    public void ScanUntil_with_precomposed_stopper_set_stops_at_decomposed_input_under_FormD()
    {
        // Stopper set: precomposed U+00E9. Under FormD the lexer
        // decomposes input "é" to a multi-rune cluster "e + combining
        // acute". Without compile-time set projection, _stopperSet
        // stays as the rune-only U+00E9 entry and ContainsToken on
        // the cluster returns false: ScanUntil consumes the cluster
        // as body and runs to EOF instead of stopping where the user
        // expected. OneOf with the same set / same input matches the
        // cluster, so the asymmetry is the bug.
        var rule = InductorParser.Rules.And(
            ScanUntil(TokenSet.Graphemes(LatinEAcutePrecomposedGrapheme)),
            Token(LatinEAcutePrecomposedGrapheme));
        rule.Compile(System.Text.NormalizationForm.FormD);

        var result = rule.Parse("abc" + LatinEAcutePrecomposedGrapheme);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void ScanUntil_with_precomposed_escape_start_rune_fires_on_decomposed_input_under_FormD()
    {
        // Escape start: U+00E9 (precomposed LATIN SMALL LETTER E WITH
        // ACUTE). Under FormD the lexer decomposes input for that
        // character to the two-rune cluster "e + combining acute". The
        // escape-start fast path compares the next token's single rune
        // against _escapeStartRune. The decomposed cluster's first rune
        // is 'e' (0x65), not 0xE9, and tokenLen (2) doesn't equal
        // runeLen (1) either, so the fast path's check rejects every
        // cluster and the escape never fires.
        //
        // OneOf and ScanUntil's stopper set project their TokenSet
        // entries through OneOfRule.NormalizeAndValidate at Compile
        // time so the same precomposed rune written in a stopper set
        // recognizes the decomposed cluster. _escapeStartRune sits in
        // the same Compile-time pass but isn't projected, so the fix
        // is to convert the rune to its FormD-normalized cluster and
        // span-compare against it.
        //
        // Asserts the user-visible consequence: the escape should fire
        // on the decomposed cluster and run the escape-end on the next
        // token. Here escape end is Token('x'); the input has 'Y'
        // after the e-acute, so when the escape fires the escape-end
        // fails and the whole ScanUntil fails. Without the fix the
        // escape is silently skipped, the cluster is consumed as body,
        // and ScanUntil reaches '|' and succeeds.
        var rule = InductorParser.Rules.And(
            ScanUntil(TokenSet.Runes("|"), new Rune(LatinEAcuteRune), Token('x')),
            Token('|'));
        rule.Compile(System.Text.NormalizationForm.FormD);

        var result = rule.Parse("abc" + LatinEAcutePrecomposedGrapheme + "Yhello|");

        Assert.That(result.Success, Is.False,
            "escape start (precomposed e-acute) should recognize the decomposed cluster under FormD; "
            + "the trailing 'Y' isn't 'x', so escape-end Token('x') should fail "
            + "and the whole ScanUntil should fail. Without the fix the escape is "
            + "silently skipped, ScanUntil consumes the whole pre-'|' span as body, "
            + "and the outer And succeeds.");
    }

    [Test]
    public void ScanUntil_with_precomposed_escape_start_rune_consumes_escape_on_decomposed_input_under_FormD()
    {
        // Happy-path sibling of the test above: same shape, but the
        // input has the matching 'x' after the e-acute so the escape
        // sequence completes. Without the fix the escape silently
        // never fires and the cluster + 'x' are consumed as body too;
        // with the fix the escape fires, escape-end matches 'x', and
        // scanning resumes for the rest of the body. Either way the
        // outer And succeeds (the cursor reaches the same '|'), so
        // success alone isn't the discriminator. The trace is.
        var sink = TraceTestHelpers.NewSink();
        var rule = InductorParser.Rules.And(
            ScanUntil(TokenSet.Runes("|"), new Rune(LatinEAcuteRune), Token('x')),
            Token('|'));
        rule.Compile(System.Text.NormalizationForm.FormD);

        var result = rule.Parse(
            "abc" + LatinEAcutePrecomposedGrapheme + "xhello|",
            new ParseOptions { TraceSink = sink });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // Token('x') is Delete by default, so a successfully-fired
        // escape produces a Token SUCC trace line for the 'x'. Without
        // the fix, the escape never fires and no such trace line
        // exists: the 'x' is consumed as body alongside the e-acute
        // cluster.
        Assert.That(sink.ToString(), Does.Contain("Token: found 'x'"),
            "the escape-end Token('x') should run after the escape start "
            + "fires on the decomposed cluster; without the fix the escape "
            + "is silently skipped and Token('x') is never invoked.");
    }

    [Test]
    public void ScanUntil_matches_a_run_of_body_chars_into_one_leaf()
    {
        // "abcXYZ|" scans the whole "abcXYZ" run as body up to the
        // stopper. The trailing '|' is required because StopOnPipe is
        // strict and would otherwise fail at EOF.
        var rule = InductorParser.Rules.And(StopOnPipe(), Token('|'));
        var result = rule.Parse("abcXYZ|");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abcXYZ"));
        // One leaf Symbol for the whole run, not one per character. The
        // whole point of this rule. The Token('|') is Delete so it does
        // not add children either.
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0));
    }

    [Test]
    public void ScanUntil_strict_fails_when_EOF_reached_without_stopper()
    {
        // Strict default: a scan that runs off the end without matching
        // the stopper fails the rule, with the failure recorded at the
        // EOF position the scan reached. No WithError on ScanUntil here,
        // so the mechanical failure lands where the scan got stuck.
        var result = StopOnPipe().Parse("abcXYZ");

        Assert.That(result.Success, Is.False,
            "strict ScanUntil should fail when no '|' appears in the input");
        Assert.That(result.ErrorCharIndex, Is.EqualTo("abcXYZ".Length),
            "failure should be recorded at the EOF position the scan reached");
    }

    [Test]
    public void ScanUntil_tolerant_succeeds_at_EOF_without_stopper()
    {
        // eofIsTerminator: true admits EOF as a valid end of the scan.
        // The whole input becomes the body leaf.
        var result = StopOnPipeOrEof().Parse("abcXYZ");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abcXYZ"));
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
        var result = rule.Parse(Canary("\\́n", "reverse solidus + combining acute accent + latin small letter n", 0x005C, 0x0301, 0x006E));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(Canary("\\́n", "reverse solidus + combining acute accent + latin small letter n", 0x005C, 0x0301, 0x006E)),
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
        var rule = ScanUntil(TokenSet.Runes("\""), eofIsTerminator: true);
        var result = rule.Parse($"ab\"{UnicodeExamples.CombiningAcuteText}cd");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo($"ab\"{UnicodeExamples.CombiningAcuteText}cd"),
            "the '\"<U+0301>' cluster is not the single-rune stopper '\"', " +
            "so the scan continues past it as body.");
    }

    [Test]
    public void ScanUntil_body_consumes_backslash_when_no_escape_configured()
    {
        // No-escape form, stopper is '|'. A '\' isn't a stopper and
        // there's no escape path, so it's consumed as body. The input
        // has no '|', so the tolerant variant runs the whole body to
        // EOF.
        var result = StopOnPipeOrEof().Parse(@"abc\");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(@"abc\"));
    }

    [Test]
    public void ScanUntil_with_explicit_SymbolId_uses_explicit_id_for_body_leaf()
    {
        // Sibling of the OneOf / NoneOf / AnyToken / WithinToken explicit-
        // SymbolId tests added in p1nd. ScanUntil emits one leaf per
        // body run with the rule's Id directly (no rune-as-leaf-id
        // shortcut, since a body of multiple tokens doesn't have one
        // distinguished rune to carry). .As(SymbolId) writes the user's
        // explicit value into Id, so the leaf carries it by construction.
        // Test locks in the matrix so a future leaf-id refactor that
        // routes ScanUntil through ResolveLeafId or a similar helper has
        // to keep .As(SymbolId) honored.
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 105);
        var rule = ScanUntil(TokenSet.Runes("|"), eofIsTerminator: true).As(explicitId);
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.Id, Is.EqualTo(explicitId),
            "leaf carries the user's explicit SymbolId");
        Assert.That(result.Tree!.Is(rule), Is.True);
        Assert.That(result.Tree!.Find(rule), Is.Not.Null);
    }

    [Test]
    public void ScanUntil_returns_single_leaf_covering_whole_match()
    {
        // Tree shape matters because the performance win of this
        // primitive is "one Symbol per run, not one per rune." Lock
        // in the shape so a future change that accidentally splits
        // the leaf back into per-rune pieces fails loudly. Tolerant
        // variant so the no-pipe input still parses; the leaf shape
        // is the same under either eofIsTerminator setting.
        var result = StopOnPipeOrEof().Parse("hello");
        Assert.That(result.Success, Is.True);

        Symbol tree = result.Tree!;
        Assert.That(tree.Children.Count, Is.EqualTo(0), "ScanUntil should produce a leaf, not a composite");
        Assert.That(tree.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void ScanUntil_multi_rune_start_triggers_escape_end()
    {
        // Start is the two-rune sequence "$$". Stopper is '|'. End
        // is one letter. Matches "abc$$X" up through the end. The
        // input has no '|' so eofIsTerminator: true lets the body run
        // to EOF; this test exercises escape behavior, not the
        // stopper-required check.
        var start = Literal("$$");
        var end = OneOf(TokenSet.Ascii.Letters);
        var rule = ScanUntil(TokenSet.Runes("|"), start, end, eofIsTerminator: true);

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
        // Tolerant variant because the inputs run past the escape end
        // to EOF without ever encountering '|'.
        var start = Or(Token('$'), Token('?'));
        var end = OneOf(TokenSet.Ascii.Letters);
        var rule = ScanUntil(TokenSet.Runes("|"), start, end, eofIsTerminator: true);

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
    public void ScanUntil_rule_stopper_probe_does_not_leak_a_failure_past_the_body()
    {
        // The rule-valued stopper runs as a pure lookahead at every
        // scanned position: a peek transaction always rolls the lexer
        // back, so the stopper is never consumed. But a transaction
        // rolls back the POSITION, not the failure tracker, so a stopper
        // probe's RecordFailure survives the rollback. Peek / Not avoid
        // this by snapshotting and restoring the failure state around
        // their inner probe; the stopper probe has to do the same.
        //
        // Stopper "-->" scanning "----->" (five dashes then '>'): the
        // stopper matches at offset 3. While scanning, the probe at
        // offset 2 reads "---" before failing on the '-' at offset 4,
        // recording a mechanical failure there - past the body, which
        // ends at offset 3. Eof() then fails at offset 3 (ScanUntil
        // stops at the stopper without consuming it). The reported
        // error must point at 3, not at the stray offset 4 the stopper
        // probe left behind.
        var rule = InductorParser.Rules.And(ScanUntil(Literal("-->")), Eof());

        var result = rule.Parse("----->");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3),
            "the rule-stopper lookahead probe leaked a failure past the body");
    }

    [Test]
    public void ScanUntil_rule_escape_start_probe_does_not_leak_a_failure_past_the_body()
    {
        // The rule-valued escape-start is also a lookahead probe: it is
        // tried at every position, and when it doesn't match the scan
        // falls through to the stopper check. A mismatching escape-start
        // rule rolls its own position back, but (like the stopper probe)
        // its RecordFailure calls survive, so its failures have to be
        // discarded too.
        //
        // Escape-start Literal("xy"), stopper {'x'}, scanning "abxz": the
        // stopper 'x' is at offset 2, so the body is "ab". At offset 2 the
        // escape-start probe reads 'x' (matches) then 'z' (wanted 'y') and
        // records a failure at offset 3 - past the body. Eof() then fails
        // at offset 2. The reported error must be 2, not the stray 3.
        var body = ScanUntil(TokenSet.Runes("x"), Literal("xy"), AnyToken());
        var rule = InductorParser.Rules.And(body, Eof());

        var result = rule.Parse("abxz");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2),
            "the rule-escape-start lookahead probe leaked a failure past the body");
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

    [TestCase((char)UnicodeExamples.HighSurrogateMinRune, TestName = "lone high surrogate (first)")]
    [TestCase((char)UnicodeExamples.HighSurrogateMaxRune, TestName = "lone high surrogate (last)")]
    [TestCase((char)UnicodeExamples.LowSurrogateMinRune, TestName = "lone low surrogate (first)")]
    [TestCase((char)UnicodeExamples.LowSurrogateMaxRune, TestName = "lone low surrogate (last)")]
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
        // as body, then hits EOF. With eofIsTerminator: true the
        // tolerant variant succeeds with the whole input as the leaf.
        string input = "abc" + UnicodeExamples.HighSurrogateMinText;
        var rule = StopOnPipeOrEof();
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
        // well-formedness validation). UnexpectedUnicodeTests verifies
        // the same property for AnyToken / Token(string) / OneOf / etc.
        // This test verifies it for ScanUntil specifically.
        string input = "before" + UnicodeExamples.EmojiStartHighSurrogateText + "after|";
        // ScanUntil doesn't consume the stopper, so the parse leaves
        // trailing '|' input. AllowTrailingInput keeps Parse from
        // failing on the unconsumed pipe.
        var rule = InductorParser.Rules.And(StopOnPipe(), Token('|'));
        rule.Compile(null);

        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        string body = result.Tree!.ToString();

        // Round-trip: ToString() reproduces the input slice exactly.
        Assert.That(body, Is.EqualTo("before" + UnicodeExamples.EmojiStartHighSurrogateText + "after"));
        // Length and the specific code unit at each position survive
        // unchanged. The lone-surrogate code unit at position 6 still
        // reads as 0xD83D.
        Assert.That(body.Length, Is.EqualTo(12));
        Assert.That(body[6], Is.EqualTo((char)UnicodeExamples.EmojiStartHighSurrogateRune));
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
        var stopOnFlag = ScanUntil(TokenSet.Graphemes(USFlagGrapheme));
        var rule = And(stopOnFlag, OneOf(TokenSet.Graphemes(USFlagGrapheme)));

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
        var stopper = TokenSet.Single('\n') | TokenSet.Graphemes(USFlagGrapheme);
        var rule = ScanUntil(stopper);

        var newlineCase = rule.Parse("hello\nrest", new ParseOptions { AllowTrailingInput = true });
        Assert.That(newlineCase.Success, Is.True, newlineCase.ErrorMessage);
        Assert.That(newlineCase.Tree!.ToString(), Is.EqualTo("hello"));

        var flagCase = rule.Parse("hello" + USFlagGrapheme + "rest", new ParseOptions { AllowTrailingInput = true });
        Assert.That(flagCase.Success, Is.True, flagCase.ErrorMessage);
        Assert.That(flagCase.Tree!.ToString(), Is.EqualTo("hello"));
    }

    // -----------------------------------------------------------------
    // Compile-form normalization matrix
    //
    // See GraphemeRuleTests for the full matrix rationale. ScanUntil's
    // _stopperSet routes through the same OneOfRule.NormalizeAndValidate
    // helper as OneOf / NoneOf / ScanWhile.
    //
    // ScanUntil has no minimum count, so the bare-leaf and Or-with-
    // fallback shapes can't distinguish the stale-set bug from correct
    // behavior via Success alone. The three shapes that DO surface the
    // bug all involve a follow-up consumer that depends on ScanUntil
    // stopping at the right place:
    //   * And(ScanUntil, Token(source)): assert success. ScanUntil
    //     should stop at offset 0 (input starts with stopper) so the
    //     trailing Token can consume the source. Stale set means
    //     ScanUntil eats everything and either runs off the end
    //     (strict ScanUntil fails on EOF) or hands an empty residue to
    //     Token, which fails. Either way the And fails.
    //   * OneOrMore(ScanUntil): assert FAILURE. ScanUntil makes 0
    //     progress so OneOrMore can't get its minimum count. Stale set
    //     means ScanUntil consumes everything in one round, OneOrMore
    //     wrongly succeeds.
    //   * And(ScanUntil, Eof()): assert FAILURE. ScanUntil should stop
    //     at offset 0 leaving the source for Eof to fail on. Stale set
    //     means ScanUntil eats everything and Eof wrongly succeeds.
    // -----------------------------------------------------------------

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void ScanUntil_in_AllOf_with_trailing_token_matches_input_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Graphemes(row.Source));
            return;
        }

        var rule = And(ScanUntil(TokenSet.Graphemes(row.Source)), Token(row.Source));

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        Assert.That(result.Success, Is.True,
            $"And(ScanUntil(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")), Token(...)).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should succeed. Error was: {result.ErrorMessage}");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void ScanUntil_in_OneOrMore_does_not_match_source_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Graphemes(row.Source));
            return;
        }

        var rule = OneOrMore(ScanUntil(TokenSet.Graphemes(row.Source)));

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        // ScanUntil sees the stopper at offset 0, consumes 0 chars,
        // and succeeds. OneOrMore can't get its minimum count off a
        // zero-progress inner match and fails. A stale stopper-set
        // would let ScanUntil consume the whole source in round 1
        // and OneOrMore would wrongly succeed.
        Assert.That(result.Success, Is.False,
            $"OneOrMore(ScanUntil(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\"))).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should fail. A pass means ScanUntil's stopper-set didn't get rewritten under {form}.");
    }

    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void ScanUntil_in_AllOf_with_Eof_does_not_match_source_under_form(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
        {
            Assert.Throws<ArgumentException>(() => TokenSet.Graphemes(row.Source));
            return;
        }

        var rule = And(ScanUntil(TokenSet.Graphemes(row.Source)), Eof());

        if (!NormalizationExamples.PostFormIsSingleGrapheme(row, form))
        {
            Assert.Throws<InvalidOperationException>(() => rule.Compile(form));
            return;
        }

        rule.Compile(form);
        var result = rule.Parse(row.Source);
        // ScanUntil sees the stopper at offset 0 and consumes 0 chars.
        // Eof then sees the source still there and fails. A stale
        // stopper-set would let ScanUntil consume everything and Eof
        // would wrongly succeed.
        Assert.That(result.Success, Is.False,
            $"And(ScanUntil(TokenSet.Runes(\"{NormalizationExamples.Hex(row.Source)}\")), Eof()).Compile({form}).Parse(\"{NormalizationExamples.Hex(row.Source)}\") " +
            $"should fail. A pass means ScanUntil's stopper-set didn't get rewritten under {form}.");
    }

    // Matrix-driven SourceRange test. See docs/TestArchitecture.md
    // "Per-rule SourceRange-matrix tests live in each rule's own
    // test file." Shared scaffold lives in SourceRangeMatrixHelper.
    [Test, TestCaseSource(typeof(NormalizationExamples), nameof(NormalizationExamples.RowFormPairs))]
    public void SourceRange_for_ScanUntil_target_after_normalized_Literal_prefix_uses_original_coords(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form)
    {
        SourceRangeMatrixHelper.AssertTargetAfterLiteralPrefix(
            row, form,
            target: ScanUntil(TokenSet.Runes("!")).As("body"),
            targetText: "XYZ",
            extraInput: "!",
            afterTarget: Token('!'));
    }

    [Test]
    public void SourceText_on_ScanUntil_returns_matched_text_under_every_FlattenType()
    {
        // ScanUntilEof matches the whole input as a single leaf. Use
        // that rather than ScanUntil + stopper because strict-default
        // ScanUntil fails when no stopper is found in the input, and
        // we want the test to be about the SourceText invariant, not
        // about stopper search.
        SourceTextFlattenTypeMatrixHelper.AssertSourceTextUnderEveryFlattenType(
            ruleBuilder: () => ScanUntilEof(),
            input: "XYZ",
            expectedSourceText: "XYZ");
    }

    [Test]
    public void ScanUntil_standalone_zero_width_match_reports_position_via_SourceRange()
    {
        // ScanUntil whose stopper sits at the cursor matches a
        // zero-width body and emits a leaf Symbol with empty memory.
        // The leaf still has a well-defined position (the offset where
        // the stopper sat). SourceRange reports a zero-width range at
        // that position, not null. AllowTrailingInput lets the parse
        // succeed with the trailing 'X' unconsumed.
        var rule = ScanUntil(TokenSet.Runes("X")).Preserve();
        var options = new ParseOptions { AllowTrailingInput = true };
        var result = rule.Parse("X", options);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""));
        var range = result.Tree!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(0));
        Assert.That(range.End.CharIndex, Is.EqualTo(0));
    }

    [Test]
    public void ScanUntil_empty_string_body_in_composite_reports_position_between_delimiters()
    {
        // The user-visible canonical case: a JSON-style string grammar
        // with ScanUntil for the body. Empty input "" between the
        // delimiters means the body is zero-width AT offset 1.
        // Consumers that highlight bodies or read offsets need a
        // position even when the body is empty.
        var body = ScanUntil(TokenSet.Runes("\"")).As("body");
        var rule = And(Token('"'), body, Token('"'));
        var result = rule.Parse("\"\"");

        Assert.That(result.Success, Is.True);
        var bodySymbol = result.Tree!.Find(body)!;
        Assert.That(bodySymbol.ToString(), Is.EqualTo(""));
        var range = bodySymbol.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(1));
        Assert.That(range.End.CharIndex, Is.EqualTo(1));
    }

    // -----------------------------------------------------------------
    // Strict-vs-tolerant EOF and ScanUntilEof
    // -----------------------------------------------------------------

    [Test]
    public void ScanUntil_strict_with_rule_stopper_fails_on_EOF()
    {
        // Rule-stopper overload: the close marker "]]>" is never present
        // in the input. Strict semantics fail the rule at EOF. No
        // WithError on ScanUntil, so it records at the EOF position the
        // scan reached.
        var rule = ScanUntil(Literal("]]>"));
        var result = rule.Parse("plain text");

        Assert.That(result.Success, Is.False,
            "strict ScanUntil(Rule) should fail when the stopper never matches");
        Assert.That(result.ErrorCharIndex, Is.EqualTo("plain text".Length));
    }

    [Test]
    public void ScanUntil_strict_inside_outer_And_attributes_failure_to_inner_scan()
    {
        // Unterminated string body: an outer And(Token('"'), ScanUntil('"'),
        // Token('"')) fails on input '"hello'. Under strict ScanUntil the
        // inner scan fails at EOF (offset 6), so the deepest failure
        // recorded is the body's, not the missing close quote's.
        var body = ScanUntil(TokenSet.Runes("\""));
        var rule = InductorParser.Rules.And(Token('"'), body, Token('"'));

        var result = rule.Parse("\"hello");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo("\"hello".Length),
            "failure depth should be at EOF, where the body ran out without finding the closer");
    }

    [Test]
    public void ScanUntil_strict_with_WithError_surfaces_custom_message_at_EOF()
    {
        var rule = ScanUntil(TokenSet.Runes("|")).WithError("expected pipe before end");
        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("expected pipe before end"));
    }

    [Test]
    public void ScanUntil_inner_escapeEnd_WithError_surfaces_when_ScanUntil_fails()
    {
        // ScanUntil doesn't clear inner failures on success, and when
        // it fails on a bad escape end, the inner escapeEnd rule's
        // failure survives (rollback keeps). If escapeEnd has
        // a .WithError and ScanUntil doesn't, the inner WithError is
        // what the user sees. Verifies ScanUntil doesn't accidentally
        // scrub inner failures.
        var escapeEnd = OneOf(TokenSet.Runes("nrt\\\""))
            .WithError("invalid escape character");
        var rule = ScanUntil(TokenSet.Runes("\""), new Rune('\\'), escapeEnd);
        var result = rule.Parse("\\x\"");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("invalid escape character"));
    }

    [Test]
    public void ScanUntil_WithError_anchors_at_deepest_subtree_position_when_fast_path_escape_end_fails()
    {
        // Literal("uX") on "\uY" matches 'u' then mismatches 'Y' against 'X',
        // leaving a mechanical failure at offset 2 even though the escape-end
        // itself rolled back to 1. ScanUntil's .WithError has to anchor at
        // the deeper subtree position or it loses the deepest-wins tiebreaker.
        var rule = ScanUntil(TokenSet.Runes("\""), new Rune('\\'), Literal("uX"))
            .WithError("bad string body");
        var result = rule.Parse("\\uY\"");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("bad string body"));
    }

    [Test]
    public void ScanUntil_WithError_anchors_at_deepest_subtree_position_when_rule_form_escape_end_fails()
    {
        // Same shape as the fast-path test above, but with the rule-form
        // escape-start: a multi-char Literal triggers the escape, and a
        // multi-char Literal escape-end records its failure deeper than
        // where the escape-end started.
        var rule = ScanUntil(TokenSet.Runes("\""), Literal("$$"), Literal("uX"))
            .WithError("bad string body");
        var result = rule.Parse("$$uY\"");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("bad string body"));
    }

    [Test]
    public void ScanUntil_strict_fails_on_EOF_even_with_escape_support()
    {
        // Escape-having variant: even when escapes are configured, a
        // scan that runs off the end without matching the stopper has
        // to fail under strict semantics. The escape path doesn't
        // accidentally consume the EOF branch. No WithError on
        // ScanUntil, so the mechanical failure lands at the EOF position
        // the scan reached.
        var escapeEnd = OneOf(TokenSet.Runes("nrt\\\""));
        var rule = ScanUntil(TokenSet.Runes("\""), new Rune('\\'), escapeEnd);

        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.False,
            "strict ScanUntil with escape support should still fail when EOF is reached without the stopper");
        Assert.That(result.ErrorCharIndex, Is.EqualTo("abc".Length));
    }

    [Test]
    public void ScanUntilEof_consumes_remainder_in_a_single_leaf()
    {
        var rule = ScanUntilEof().As("rest");
        rule.Compile();

        var result = rule.Parse("hello world");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello world"));
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0),
            "ScanUntilEof should emit one leaf, same as ScanUntil");
    }

    [Test]
    public void ScanUntilEof_succeeds_on_empty_input()
    {
        var rule = ScanUntilEof();
        rule.Compile();

        var result = rule.Parse("");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""));
    }

    [Test]
    public void ScanUntilEof_round_trips_lone_surrogate_at_end_of_input()
    {
        // The whole-input-as-one-leaf path needs to carry through
        // unpaired surrogates the same way the stoppered variant does
        // (matches the round-trip property already verified for
        // StopOnPipe with a trailing surrogate).
        string input = "abc" + UnicodeExamples.EmojiStartHighSurrogateText;
        var rule = ScanUntilEof();
        rule.Compile(null);

        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(input));
    }

    // Escape start vs. stopper ordering ------------------------------------

    [Test]
    public void ScanUntil_single_rune_escape_fires_when_the_escape_rune_is_also_a_stopper()
    {
        // Regression: ScanUntil used to check the stopper set before the
        // escape start. When the escape-start rune is also a member of
        // the stopper set, that ordering made the escape unreachable —
        // the scan terminated at the escape character instead of
        // consuming the escape sequence. This is the exact grammar shape
        // the Rules.cs XML-doc example for the single-rune-escape
        // ScanUntil overload shows (the escape rune '\' listed in the
        // stopAt set alongside the closing quote).
        var body = ScanUntil(
            stopAt: TokenSet.Runes("\"\\"),
            escapeStart: new Rune('\\'),
            escapeEnd: OneOf("ntr\"\\"));

        // One escape sequence \n, then the closing quote. The body has
        // to consume \n as an escape and stop at the quote, leaving the
        // unconsumed quote for AllowTrailingInput to tolerate.
        var result = body.Parse("\\n\"", new ParseOptions { AllowTrailingInput = true });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("\\n"),
            "the '\\' must trigger the escape, not terminate the body at offset 0");
    }

    [Test]
    public void ScanUntil_rule_escape_start_fires_when_its_first_rune_is_also_a_stopper()
    {
        // The Rules.cs XML-doc example for the rule-valued-escape-start
        // ScanUntil overload: a shell-style string body that stops at "
        // or $, where "${...}" is an interpolation escape and a bare '$'
        // (not followed by '{') is a stopper. The escape start
        // Literal("${") shares its first rune '$' with the '$' stopper,
        // so the escape has to be tried before the stopper.
        var body = ScanUntil(
            stopAt: TokenSet.Runes("\"$"),
            escapeStart: Literal("${"),
            escapeEnd: And(OneOrMore(NoneOf("}")), Token('}')));

        // "${name}" is consumed as an escape; the body runs to the quote.
        var withInterpolation = body.Parse("ab${name}cd\"",
            new ParseOptions { AllowTrailingInput = true });
        Assert.That(withInterpolation.Success, Is.True, withInterpolation.ErrorMessage);
        Assert.That(withInterpolation.Tree!.ToString(), Is.EqualTo("ab${name}cd"),
            "${name} must be consumed as an escape, not stop the body at the '$'");

        // A bare '$' not followed by '{' still stops: the escape
        // Literal("${") fails to match, so the scan falls through to the
        // '$' stopper. This is the "bare $ falls through" behavior the
        // doc example promises.
        var bareDollar = body.Parse("ab$cd\"",
            new ParseOptions { AllowTrailingInput = true });
        Assert.That(bareDollar.Success, Is.True, bareDollar.ErrorMessage);
        Assert.That(bareDollar.Tree!.ToString(), Is.EqualTo("ab"),
            "a bare '$' falls through to the stopper and ends the body");
    }

    [Test]
    public void ScanUntil_with_Eof_rule_stopper_stops_at_end_of_input()
    {
        // ScanUntil(Eof()) reads as "scan until end of input": Eof() is
        // a rule that matches only at EOF, so ScanUntil should stop the
        // body there and match the whole input, exactly like the
        // library's own ScanUntilEof() helper. The Rule-stopper loop
        // only tests the stopper at non-EOF positions, so a stopper
        // that matches at EOF is never recognised and the rule fails.
        var rule = ScanUntil(Eof());

        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }

    [Test]
    public void ScanUntil_with_rule_stopper_that_admits_Eof_stops_when_input_runs_out()
    {
        // Or(Literal("END"), Eof()) is the natural way to write
        // "stop at END or at end of input." On input with no "END",
        // the body should run to EOF where the Eof() alternative
        // matches. Instead ScanUntil fails because the stopper rule
        // is never tried at the EOF position.
        var stopper = Or(Literal("END"), Eof());
        var rule = ScanUntil(stopper);

        var result = rule.Parse("abc");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("abc"));
    }
}
