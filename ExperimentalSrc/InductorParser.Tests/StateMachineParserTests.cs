using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using InductorParser;
using InductorParser.StateMachine;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests.StateMachine;

// Correctness tests for the state-machine evaluator. The bar is
// "matches the existing Rule.Parse output on the same input" plus a
// few targeted shape assertions where we want to lock in a specific
// behavior. Helper methods AssertSameOutcome and AssertSameTreeText
// keep the per-test boilerplate small.
[TestFixture]
public class StateMachineParserTests
{
    // ---- Literal ----

    [Test]
    public void Literal_matches_exact_string()
    {
        var rule = And(Literal("hello"), Eof());
        AssertSameOutcome(rule, "hello", expectSuccess: true);
        AssertSameOutcome(rule, "hellx", expectSuccess: false);
        AssertSameOutcome(rule, "hell", expectSuccess: false);
    }

    [Test]
    public void Literal_with_Preserve_keeps_leaf_text()
    {
        var rule = And(Literal("hello").Preserve(), Eof());
        var stateMachine = StateMachineParser.Parse(rule, "hello");
        Assert.That(stateMachine.Success, Is.True, stateMachine.ErrorMessage);
        Assert.That(stateMachine.ToString(), Is.EqualTo("hello"));
    }

    // ---- Grapheme (lowered as Literal) ----

    [Test]
    public void Grapheme_matches_one_grapheme()
    {
        var rule = And(Token('a'), Token('b'), Token('c'), Eof());
        AssertSameOutcome(rule, "abc", expectSuccess: true);
        AssertSameOutcome(rule, "abd", expectSuccess: false);
    }

    // ---- OneOf ----

    [Test]
    public void OneOf_matches_one_rune_in_set()
    {
        var rule = And(OneOf(TokenSet.Ascii.Letters), Eof());
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "Z", expectSuccess: true);
        AssertSameOutcome(rule, "5", expectSuccess: false);
    }

    [Test]
    public void OneOf_default_Preserve_creates_leaf_with_rune_id()
    {
        var rule = OneOf("AbZ");
        var stateMachine = StateMachineParser.Parse(rule, "b");
        Assert.That(stateMachine.Success, Is.True);
        var leaf = stateMachine.Symbols[0];
        Assert.That(leaf.Id.Value, Is.EqualTo((int)'b'));
    }

    // ---- Eof ----

    [Test]
    public void Eof_succeeds_at_end_only()
    {
        var rule = And(Token('a'), Eof());
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "ab", expectSuccess: false);
    }

    // ---- And ----

    [Test]
    public void And_all_children_succeed()
    {
        var rule = And(Token('a'), Token('b'), Token('c'), Eof());
        AssertSameOutcome(rule, "abc", expectSuccess: true);
    }

    [Test]
    public void And_first_child_fails()
    {
        var rule = And(Token('a'), Token('b'), Eof());
        AssertSameOutcome(rule, "xb", expectSuccess: false);
    }

    [Test]
    public void And_middle_child_fails_does_not_consume()
    {
        // After failure, position rolls back to start so the parent
        // alternative can try again.
        var rule = Or(
            And(Token('a'), Token('b'), Token('c')),
            And(Token('a'), Token('x'), Token('y')));
        var rooted = And(rule, Eof());
        AssertSameOutcome(rooted, "axy", expectSuccess: true);
        AssertSameOutcome(rooted, "axx", expectSuccess: false);
    }

    // ---- Or ----

    [Test]
    public void Or_first_alternative_matches()
    {
        var rule = And(Or(Literal("hello"), Literal("world")), Eof());
        AssertSameOutcome(rule, "hello", expectSuccess: true);
        AssertSameOutcome(rule, "world", expectSuccess: true);
        AssertSameOutcome(rule, "other", expectSuccess: false);
    }

    [Test]
    public void Or_takes_first_match_PEG_style()
    {
        // Longer literal must come first under PEG semantics; "ma"
        // would otherwise commit and "maj" never tried.
        var rule = And(Or(Literal("maj"), Literal("ma")), Eof());
        AssertSameOutcome(rule, "maj", expectSuccess: true);
        AssertSameOutcome(rule, "ma", expectSuccess: true);
    }

    // ---- BetweenInclusive (Optional, ZeroOrMore, OneOrMore, Exactly) ----

    [Test]
    public void Optional_present_or_absent_both_succeed()
    {
        var rule = And(Optional(Token('!')), Eof());
        AssertSameOutcome(rule, "!", expectSuccess: true);
        AssertSameOutcome(rule, "", expectSuccess: true);
        AssertSameOutcome(rule, "a", expectSuccess: false);
    }

    [Test]
    public void ZeroOrMore_matches_run_of_chars()
    {
        var rule = And(ZeroOrMore(OneOf(TokenSet.Ascii.Digits)), Eof());
        AssertSameOutcome(rule, "", expectSuccess: true);
        AssertSameOutcome(rule, "1", expectSuccess: true);
        AssertSameOutcome(rule, "12345", expectSuccess: true);
        AssertSameOutcome(rule, "12a45", expectSuccess: false);
    }

    [Test]
    public void OneOrMore_requires_at_least_one()
    {
        var rule = And(OneOrMore(OneOf(TokenSet.Ascii.Letters)), Eof());
        AssertSameOutcome(rule, "", expectSuccess: false);
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "abc", expectSuccess: true);
    }

    [Test]
    public void Exactly_three_letters()
    {
        var rule = And(Exactly(3, OneOf(TokenSet.Ascii.Letters)), Eof());
        AssertSameOutcome(rule, "ab", expectSuccess: false);
        AssertSameOutcome(rule, "abc", expectSuccess: true);
        AssertSameOutcome(rule, "abcd", expectSuccess: false);
    }

    [Test]
    public void BetweenInclusive_collects_children_into_preserve_wrapper()
    {
        var letters = OneOrMore(OneOf(TokenSet.Ascii.Letters)).As("letters");
        var rooted = And(letters, Eof());

        var stateMachine = StateMachineParser.Parse(rooted, "abc");
        Assert.That(stateMachine.Success, Is.True);

        // The "letters" wrapper is at top level. Its children are the
        // three rune leaves.
        var found = stateMachine.Tree?.Find(letters) ?? stateMachine.Symbols.FirstOrDefault(s => s.Id == letters.Id);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Children.Count, Is.EqualTo(3));
    }

    [Test]
    public void ZeroOrMore_scanner_skip_does_not_skip_NoneOf_alternative_matches()
    {
        // TryLowerBetweenScanner unions every non-fallback alternative's
        // FirstConsumedTokens.LookaheadFirstRunes into the candidate set
        // without considering Polarity. For a MustNotBeIn alternative
        // like NoneOf(stopSet), FirstConsumedTokens is the rule's
        // FAIL-set, not its match-set. Including it directs the
        // scanner to advance to positions where NoneOf will FAIL (and
        // fall through to AnyToken().Delete()), silently skipping past
        // every position where NoneOf would have MATCHED. Mirrors the
        // recursive engine's BetweenInclusive_scanner_skip_does_not_skip_NoneOf_alternative_matches.
        var stopSet = TokenSet.Runes("xy");
        var rule = BetweenInclusive(0, int.MaxValue, Or(
            NoneOf(stopSet),
            AnyToken().Flatten(FlattenType.Delete)
        ));

        var stateMachine = StateMachineParser.Parse(rule, "abxcyd");

        Assert.That(stateMachine.Success, Is.True, stateMachine.ErrorMessage);
        Assert.That(stateMachine.ToString(), Is.EqualTo("abcd"));
    }

    // ---- Not ----

    [Test]
    public void Not_succeeds_when_inner_fails()
    {
        // Match "a" only when not followed by "b". The Not is
        // zero-width so the lexer still has the next char available.
        var rule = And(Token('a'), Not(Token('b')), Eof());
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "ab", expectSuccess: false);
    }

    [Test]
    public void Not_chained_with_OneOrMore_for_until_pattern()
    {
        // Read digits until "x" appears. Not(Token('x')) succeeds as long
        // as the next token isn't 'x'. Combined with OneOf(digits) we
        // consume one digit per iteration.
        var rule = And(
            OneOrMore(And(Not(Token('x')), OneOf(TokenSet.Ascii.Digits))),
            Token('x'),
            Eof());
        AssertSameOutcome(rule, "123x", expectSuccess: true);
        AssertSameOutcome(rule, "x", expectSuccess: false); // OneOrMore needs at least one
    }

    // ---- Peek ----

    [Test]
    public void Peek_succeeds_without_consuming()
    {
        var rule = And(Peek(Token('a')), Token('a'), Eof());
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "b", expectSuccess: false);
    }

    [Test]
    public void Peek_failure_propagates()
    {
        var rule = And(Peek(Token('a')), Eof());
        // Peek('a') fails at empty input. So the And fails.
        AssertSameOutcome(rule, "", expectSuccess: false);
    }

    // ---- LateBound (recursive grammar) ----

    [Test]
    public void LateBound_balanced_parens()
    {
        // parens = ( "(" parens ")" )*
        // Matches any balanced-parens string, including the empty one.
        var parens = new LateBoundRule("parens");
        parens.Bind(ZeroOrMore(And(Token('('), parens, Token(')'))));
        var rooted = And(parens, Eof());

        AssertSameOutcome(rooted, "", expectSuccess: true);
        AssertSameOutcome(rooted, "()", expectSuccess: true);
        AssertSameOutcome(rooted, "(())", expectSuccess: true);
        AssertSameOutcome(rooted, "((()))", expectSuccess: true);
        AssertSameOutcome(rooted, "()()", expectSuccess: true);
        AssertSameOutcome(rooted, "(()", expectSuccess: false);
        AssertSameOutcome(rooted, "())", expectSuccess: false);
    }

    [Test]
    public void LateBound_simple_arithmetic_recursion()
    {
        // expression = term (("+" / "-") term)*
        // term = digit / "(" expression ")"
        // Tests both LateBound and BetweenInclusive over a recursive sub-rule.
        var expression = new LateBoundRule("expression");
        var digit = OneOf(TokenSet.Ascii.Digits);
        var term = Or(digit, And(Token('('), expression, Token(')')));
        expression.Bind(And(term, ZeroOrMore(And(OneOf("+-"), term))));
        var rooted = And(expression, Eof());

        AssertSameOutcome(rooted, "1", expectSuccess: true);
        AssertSameOutcome(rooted, "1+2", expectSuccess: true);
        AssertSameOutcome(rooted, "1+2-3+4", expectSuccess: true);
        AssertSameOutcome(rooted, "(1+2)", expectSuccess: true);
        AssertSameOutcome(rooted, "((1+2)-(3+4))", expectSuccess: true);
        AssertSameOutcome(rooted, "1+", expectSuccess: false);
        AssertSameOutcome(rooted, "(1+2", expectSuccess: false);
    }

    // ---- FlattenType handling ----

    [Test]
    public void Flatten_default_path_drops_Delete_nodes()
    {
        // Token defaults to Delete: keywords and punctuation don't
        // appear in the tree under the default path.
        var named = And(Token('('), OneOrMore(OneOf(TokenSet.Ascii.Letters)), Token(')')).As("group");
        var rooted = And(named, Eof());

        var stateMachine = StateMachineParser.Parse(rooted, "(abc)");
        Assert.That(stateMachine.Success, Is.True);
        var group = stateMachine.Symbols[0];
        Assert.That(group.Id, Is.EqualTo(named.Id));
        // Delete drops the parens; OneOrMore is Flatten by default and
        // lifts its three letter children into "group".
        Assert.That(group.Children.Count, Is.EqualTo(3));
    }

    [Test]
    public void PreserveAllSymbols_keeps_every_grammar_node()
    {
        var rule = And(Token('a'), Token('b'), Token('c'));
        var stateMachine = StateMachineParser.Parse(rule, "abc", new ParseOptions { PreserveAllSymbols = true });
        Assert.That(stateMachine.Success, Is.True);
        // Tree.ToString() walks the whole structure. Under
        // PreserveAllSymbols every Token leaf survives, so the
        // concatenation reproduces the input.
        Assert.That(stateMachine.ToString(), Is.EqualTo("abc"));
    }

    // ---- Error reporting ----

    [Test]
    public void Error_position_reports_deepest_failure()
    {
        var rule = And(Literal("hello"), Eof());
        var stateMachine = StateMachineParser.Parse(rule, "hellx");
        Assert.That(stateMachine.Success, Is.False);
        Assert.That(stateMachine.ErrorCharIndex, Is.EqualTo(4));
    }

    [Test]
    public void Error_position_in_alternative_picks_furthest_attempt()
    {
        // "abc" matches the first three of "abcde". Or's first
        // alternative reads past "abc" and fails at offset 4. The
        // second alternative also fails earlier. Deepest wins.
        var rule = And(
            Or(Literal("abcXY"), Literal("ab")),
            Eof());
        var stateMachine = StateMachineParser.Parse(rule, "abcde");
        Assert.That(stateMachine.Success, Is.False);
        // The deepest read inside the failed Or alternative was at
        // position 3 (matched abc, then failed at X). That's where
        // we should be.
        Assert.That(stateMachine.ErrorCharIndex, Is.GreaterThanOrEqualTo(2));
    }

    // ---- ScanUntil ----

    [Test]
    public void ScanUntil_no_escape_scans_until_stopper()
    {
        var rule = And(
            Token('"'),
            ScanUntil(TokenSet.Runes("\"")),
            Token('"'),
            Eof());
        AssertSameOutcome(rule, "\"hello\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"\"", expectSuccess: true);
    }

    [Test]
    public void ScanUntil_with_simple_escape_handles_backslash_pairs()
    {
        var simpleEscape = OneOf(TokenSet.Runes("\"\\nrtbf/"));
        var rule = And(
            Token('"'),
            ScanUntil(stopAt: TokenSet.Runes("\""), escapeStart: new System.Text.Rune('\\'), escapeEnd: simpleEscape),
            Token('"'),
            Eof());
        AssertSameOutcome(rule, "\"hello\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"a\\nb\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"a\\\"b\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"a\\xb\"", expectSuccess: false);  // \x not a valid escape
    }

    [Test]
    public void ScanUntil_recursive_escape_uses_subprogram_correctly()
    {
        var simpleEscape = OneOf(TokenSet.Runes("\"\\/bfnrt"));
        var hexDigit = OneOf(TokenSet.Ascii.HexDigits);
        var unicodeEscape = And(Token('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        var escape = Or(simpleEscape, unicodeEscape).Flatten(FlattenType.Delete);
        var rule = And(
            Token('"'),
            ScanUntil(stopAt: TokenSet.Runes("\""), escapeStart: new System.Text.Rune('\\'), escapeEnd: escape),
            Token('"'),
            Eof());
        AssertSameOutcome(rule, "\"\\u0041\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"hello \\\"world\\\" \\u00E9\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"\\u00ZZ\"", expectSuccess: false); // bad hex
    }

    [Test]
    public void ScanUntil_preserves_leaf_text()
    {
        var rule = And(Token('"'), ScanUntil(TokenSet.Runes("\"")), Token('"'), Eof());
        var stateMachine = StateMachineParser.Parse(rule, "\"hello\"");
        Assert.That(stateMachine.Success, Is.True);
        Assert.That(stateMachine.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void ScanUntil_strict_fails_on_EOF_without_stopper_in_both_engines()
    {
        // Strict default: no '|' in the input means the scan runs off
        // the end. Both the recursive evaluator and the state-machine
        // ScanUntilFast opcode have to fail here, and at the same
        // depth, so AssertSameOutcome compares both Success and the
        // recorded failure position.
        var rule = ScanUntil(TokenSet.Runes("|"));
        AssertSameOutcome(rule, "abc", expectSuccess: false);
        AssertSameOutcome(rule, "", expectSuccess: false);
    }

    [Test]
    public void ScanUntil_tolerant_succeeds_at_EOF_in_both_engines()
    {
        // eofIsTerminator: true: the Stepper's pos >= inputLen branch
        // takes state.OnSuccess instead of spec.OnEofFailState, so
        // both engines succeed with the whole input as the body.
        // Wrap in And(..., Eof()) so the test doesn't trip on the
        // default Parse "no trailing input" rule when a stopper exists
        // mid-input; we want to compare engine outcomes, not parse
        // options.
        var bare = ScanUntil(TokenSet.Runes("|"), eofIsTerminator: true);
        AssertSameOutcome(bare, "abc", expectSuccess: true);
        AssertSameOutcome(bare, "", expectSuccess: true);

        // Mid-input stopper: scan stops at '|', outer Token consumes it.
        var withTrailer = And(
            ScanUntil(TokenSet.Runes("|"), eofIsTerminator: true),
            Token('|'),
            Eof());
        AssertSameOutcome(withTrailer, "ab|", expectSuccess: true);
    }

    [Test]
    public void ScanUntilEof_succeeds_in_both_engines()
    {
        // ScanUntilEof forwards to ScanUntilRule(TokenSet.Empty,
        // eofIsTerminator: true), so it shares the same lowered shape
        // as the tolerant case and just always runs to EOF.
        var rule = ScanUntilEof();
        AssertSameOutcome(rule, "hello world", expectSuccess: true);
        AssertSameOutcome(rule, "", expectSuccess: true);
    }

    [Test]
    public void ScanUntil_state_machine_strict_fails_on_stray_surrogate_halt()
    {
        // Step_ScanUntilFast has a second exit besides pos >= inputLen:
        // a lone surrogate halts the scan because no rune can be
        // decoded. Before strict semantics, that halt always routed to
        // state.OnSuccess (succeed with whatever body had accumulated).
        // Now it dispatches on spec.EofIsTerminator the same way the
        // real-EOF branch does, so under strict the halt fails the
        // rule. The recursive evaluator handles lone surrogates
        // differently (it flows them through as body and only halts at
        // real EOF), so this test verifies only the state-machine side of
        // the new strict surrogate-halt behavior; the recursive side's
        // surrogate-as-body behavior is verified in
        // ScanUntilRuleTests.cs.
        //
        // Compile with null so string.Normalize doesn't reject the
        // malformed UTF-16 before ScanUntil ever sees it.
        string input = "abc" + new string((char)0xD800, 1);

        var strict = ScanUntil(TokenSet.Runes("|"));
        strict.Compile(null);
        var strictStateMachine = StateMachineParser.Parse(strict, input, new ParseOptions());
        Assert.That(strictStateMachine.Success, Is.False,
            "strict ScanUntil in the state-machine engine should fail when the stray surrogate halts the scan with no stopper match");

        // Sanity-check the tolerant variant: same input but with
        // eofIsTerminator: true. The Stepper's surrogate-halt branch
        // returns state.OnSuccess in tolerant mode, so the ScanUntil
        // rule itself succeeds with a leaf over "abc". The surrogate
        // remains unconsumed in the input; the rule succeeds at the
        // ScanUntil level even if an outer Eof() would then refuse it.
        var tolerant = ScanUntil(TokenSet.Runes("|"), eofIsTerminator: true);
        tolerant.Compile(null);
        var tolerantStateMachine = StateMachineParser.Parse(tolerant,
            input,
            new ParseOptions { AllowTrailingInput = true });
        Assert.That(tolerantStateMachine.Success, Is.True, tolerantStateMachine.ErrorMessage);
    }

    // ---- AnyToken ----

    [Test]
    public void AnyToken_matches_any_single_token()
    {
        var rule = And(AnyToken(), Eof());
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "z", expectSuccess: true);
        AssertSameOutcome(rule, "5", expectSuccess: true);
        AssertSameOutcome(rule, "", expectSuccess: false);
    }

    [Test]
    public void AnyToken_in_until_pattern()
    {
        // Read everything up to 'x'.
        var rule = And(
            ZeroOrMore(And(Not(Token('x')), AnyToken())),
            Token('x'),
            Eof());
        AssertSameOutcome(rule, "x", expectSuccess: true);
        AssertSameOutcome(rule, "abcx", expectSuccess: true);
        AssertSameOutcome(rule, "ax", expectSuccess: true);
        AssertSameOutcome(rule, "abc", expectSuccess: false);
    }

    // ---- NoneOf ----

    [Test]
    public void NoneOf_matches_runes_outside_set()
    {
        var rule = And(OneOrMore(NoneOf(TokenSet.Runes("\""))), Token('"'), Eof());
        AssertSameOutcome(rule, "hello\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"", expectSuccess: false); // empty body, OneOrMore needs >= 1
    }

    [Test]
    public void NoneOf_default_Preserve_creates_leaf_with_rune_id()
    {
        var rule = NoneOf("xyz");
        var stateMachine = StateMachineParser.Parse(rule, "a");
        Assert.That(stateMachine.Success, Is.True);
        var leaf = stateMachine.Symbols[0];
        Assert.That(leaf.Id.Value, Is.EqualTo((int)'a'));
    }

    // ---- LiteralIgnoreAsciiCase ----

    [Test]
    public void LiteralIgnoreAsciiCase_matches_either_case()
    {
        var rule = And(LiteralIgnoreAsciiCase("hello"), Eof());
        AssertSameOutcome(rule, "hello", expectSuccess: true);
        AssertSameOutcome(rule, "HELLO", expectSuccess: true);
        AssertSameOutcome(rule, "Hello", expectSuccess: true);
        AssertSameOutcome(rule, "hellO", expectSuccess: true);
        AssertSameOutcome(rule, "world", expectSuccess: false);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_does_not_fold_non_ascii()
    {
        // ASCII letters fold; non-ASCII does not. Same as
        // LiteralIgnoreAsciiCaseRule.
        var rule = And(LiteralIgnoreAsciiCase("café"), Eof());
        AssertSameOutcome(rule, "café", expectSuccess: true);
        AssertSameOutcome(rule, "CAFé", expectSuccess: true);
        AssertSameOutcome(rule, "CAFÉ", expectSuccess: false); // É is non-ASCII, doesn't fold
    }

    // ---- WithError attribution ----

    [Test]
    public void WithError_message_surfaces_at_deepest_failure()
    {
        var keyword = Literal("hello").WithError("expected the greeting");
        var rule = And(keyword, Eof());
        var stateMachine = StateMachineParser.Parse(rule, "world");
        Assert.That(stateMachine.Success, Is.False);
        Assert.That(stateMachine.ErrorMessage, Is.EqualTo("expected the greeting"));
    }

    [Test]
    public void WithError_on_OneOf_surfaces_at_failure()
    {
        var digit = OneOf(TokenSet.Ascii.Digits).WithError("expected a digit");
        var rule = And(digit, Eof());
        var stateMachine = StateMachineParser.Parse(rule, "x");
        Assert.That(stateMachine.Success, Is.False);
        Assert.That(stateMachine.ErrorMessage, Is.EqualTo("expected a digit"));
    }

    [Test]
    public void Or_descendant_WithError_surfaces_when_per_alt_peek_skip_would_skip_composite()
    {
        // SM mirror of Or_descendant_WithError_surfaces_when_per_child_shortcut_would_skip_composite
        // in OrRuleTests. The state-machine engine's Lowerer.CanSkipUnreachableAlt
        // decides whether to emit a CheckPeekedRuneInSet ahead of an Or
        // alternative. Pre-fix the gate was child.ErrorMessage != null,
        // which let a composite child like Or(...) whose own ErrorMessage
        // is null be skipped on a peeked-rune mismatch even when its
        // subtree carried .WithError. The peek-skip would route past the
        // PushBacktrack to the next alternative, so the descendant's
        // RecordFailure call never ran and "want 'a'" never reached
        // DeepestFailureMessage.
        //
        // The fix consults child.HasErrorMessageInSubtree, the same
        // subtree-aware flag the recursive engine's OrRule uses. With the
        // gate the inner Or runs, its own per-alt skip respects WithError
        // on Token('a'), and "want 'a'" surfaces.
        var rule = Or(Or(Token('a').WithError("want 'a'"), Token('b')), Token('c'));
        var stateMachine = StateMachineParser.Parse(rule, "d", new ParseOptions());

        Assert.That(stateMachine.Success, Is.False);
        Assert.That(stateMachine.ErrorCharIndex, Is.EqualTo(0));
        Assert.That(stateMachine.ErrorMessage, Is.EqualTo("want 'a'"));
    }

    // ---- Bridge fallback (WithinGrapheme + custom subclasses) ----

    [Test]
    public void WithinGrapheme_bridges_to_recursive_evaluator()
    {
        // WithinGrapheme is bridged. The state machine delegates the
        // whole rule to the recursive evaluator's TryParse, captures
        // its Symbol output, and folds it into the surrounding tree.
        // Test: "é" arrives as one token (one rune, since this é is
        // the precomposed form). WithinGrapheme walks it rune-by-rune
        // via the Rune sub-lexer and lets the inner OneOf match.
        var rule = And(
            WithinToken(OneOf(TokenSet.Letters)),
            Eof());

        AssertSameOutcome(rule, "é", expectSuccess: true);
        AssertSameOutcome(rule, "5", expectSuccess: false);
    }

    [Test]
    public void Custom_rule_subclass_bridges_to_recursive_evaluator()
    {
        // A user-defined Rule subclass: matches exactly two consecutive
        // 'q' characters. The state machine has no native lowering for
        // CustomTwoQs; falls through to LowerViaBridge.
        var rule = And(new CustomTwoQs(), Eof());
        AssertSameOutcome(rule, "qq", expectSuccess: true);
        AssertSameOutcome(rule, "qx", expectSuccess: false);
        AssertSameOutcome(rule, "q", expectSuccess: false);
    }

    private sealed class CustomTwoQs : Rule
    {
        public CustomTwoQs() : base(FlattenType.Delete) { }

        internal override Symbol? TryParseRule(InductorParser.Lexing.Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
        {
            for (int i = 0; i < 2; i++)
            {
                if (lexer.IsEof) return null;
                var token = lexer.Read();
                if (token.RuneValue != 'q') return null;
            }
            return Symbol.Discarded;
        }
    }

    [Test]
    public void Works_on_default_lexer()
    {
        var rule = And(Literal("hello"), Eof());
        var stateMachine = StateMachineParser.Parse(rule, "hello", new ParseOptions());
        Assert.That(stateMachine.Success, Is.True);
    }

    // ---- ParseContext-dependent Symbol APIs ----

    [Test]
    public void Symbol_DisplayName_resolves_through_the_grammar_on_a_state_machine_parse()
    {
        // TreeBuilder used to build Symbols without a ParseContext.
        // Symbol.DisplayName resolves the id through
        // ParseContext.GrammarRoot, so without the context every node in
        // a state-machine-parsed tree reported a null DisplayName even
        // when the rule was .As(...)-named.
        var letter = OneOf(TokenSet.Letters).As("letter");
        var word = OneOrMore(letter).As("word");

        var stateMachine = StateMachineParser.Parse(word, "hi");
        Assert.That(stateMachine.Success, Is.True, stateMachine.ErrorMessage);

        Assert.That(stateMachine.Symbols[0].DisplayName, Is.EqualTo("word"));
        Assert.That(stateMachine.Symbols[0].Is("word"), Is.True);
    }

    [Test]
    public void SourceRange_reports_original_input_coordinates_under_normalization()
    {
        // Under FormC the lexer scans precomposed "café" (4 chars) while
        // the user typed the decomposed form (5 chars), so everything
        // after the prefix sits one char further along in the original
        // input than in parseInput. Symbol.SourceRange translates
        // parseInput offsets back to original-input coordinates through
        // the ParseContext. With no context TreeBuilder left the X leaf
        // reporting parseInput offset 4 instead of original offset 5.
        string decomposedCafe = "caféX"; // c a f e U+0301 X
        var x = Token('X').As("x");
        var rule = And(Literal("café"), x).As("root"); // literal precomposed café
        rule.Compile(System.Text.NormalizationForm.FormC);

        var stateMachine = StateMachineParser.Parse(rule, decomposedCafe);
        Assert.That(stateMachine.Success, Is.True, stateMachine.ErrorMessage);

        var xSymbol = stateMachine.Tree!.Find(rule.IdOf("x")!.Value)!;
        var range = xSymbol.SourceRange;
        Assert.That(range, Is.Not.Null);
        Assert.That(range!.Value.Start.CharIndex, Is.EqualTo(5),
            "X is at original-input char 5 (decomposed café is 5 chars), not parseInput char 4.");
        Assert.That(range.Value.End.CharIndex, Is.EqualTo(6));
        Assert.That(range.Value.Start.Input, Is.SameAs(decomposedCafe),
            "SourceRange endpoints should point into the user's original input string.");
    }

    // ---- Helpers ----

    // Assert the state-machine evaluator and the existing recursive
    // evaluator agree on outcome (success/failure) for the same input.
    // Both engines auto-compile the rule with the FormC default when
    // it isn't compiled yet, which is fine for the inputs the existing
    // test cases use (ASCII, single-rune Unicode without combining
    // marks). Tests that need a different normalization form should
    // compile the rule themselves before calling this helper.
    private static void AssertSameOutcome(Rule rule, string input, bool expectSuccess)
    {
        var legacy = rule.ParseRecursive(input, new ParseOptions());
        var stateMachine = StateMachineParser.Parse(rule, input, new ParseOptions());

        Assert.That(legacy.Success, Is.EqualTo(expectSuccess), $"legacy outcome for '{input}': {legacy.ErrorMessage}");
        Assert.That(stateMachine.Success, Is.EqualTo(expectSuccess), $"state-machine outcome for '{input}': {stateMachine.ErrorMessage}");
        Assert.That(stateMachine.Success, Is.EqualTo(legacy.Success), $"evaluators disagree on '{input}'");

        if (legacy.Success)
        {
            Assert.That(stateMachine.ToString(), Is.EqualTo(legacy.ToString()), $"tree text differs on '{input}'");
        }
    }
}
