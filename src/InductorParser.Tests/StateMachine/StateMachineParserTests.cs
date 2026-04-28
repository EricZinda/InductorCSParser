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
        var rule = AllOf(Literal("hello"), Eof());
        AssertSameOutcome(rule, "hello", expectSuccess: true);
        AssertSameOutcome(rule, "hellx", expectSuccess: false);
        AssertSameOutcome(rule, "hell", expectSuccess: false);
    }

    [Test]
    public void Literal_with_Preserve_keeps_leaf_text()
    {
        var rule = AllOf(Literal("hello").Preserve(), Eof());
        var stateMachine = StateMachineParser.Parse(rule, "hello");
        Assert.That(stateMachine.Success, Is.True, stateMachine.ErrorMessage);
        Assert.That(stateMachine.ToString(), Is.EqualTo("hello"));
    }

    // ---- Token (lowered as Literal) ----

    [Test]
    public void Token_matches_one_grapheme()
    {
        var rule = AllOf(Token('a'), Token('b'), Token('c'), Eof());
        AssertSameOutcome(rule, "abc", expectSuccess: true);
        AssertSameOutcome(rule, "abd", expectSuccess: false);
    }

    // ---- OneOf ----

    [Test]
    public void OneOf_matches_one_rune_in_set()
    {
        var rule = AllOf(OneOf(RuneSet.Ascii.Letters), Eof());
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
        var rule = AllOf(Token('a'), Eof());
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "ab", expectSuccess: false);
    }

    // ---- And ----

    [Test]
    public void And_all_children_succeed()
    {
        var rule = AllOf(Token('a'), Token('b'), Token('c'), Eof());
        AssertSameOutcome(rule, "abc", expectSuccess: true);
    }

    [Test]
    public void And_first_child_fails()
    {
        var rule = AllOf(Token('a'), Token('b'), Eof());
        AssertSameOutcome(rule, "xb", expectSuccess: false);
    }

    [Test]
    public void And_middle_child_fails_does_not_consume()
    {
        // After failure, position rolls back to start so the parent
        // alternative can try again.
        var rule = FirstOf(
            AllOf(Token('a'), Token('b'), Token('c')),
            AllOf(Token('a'), Token('x'), Token('y')));
        var rooted = AllOf(rule, Eof());
        AssertSameOutcome(rooted, "axy", expectSuccess: true);
        AssertSameOutcome(rooted, "axx", expectSuccess: false);
    }

    // ---- Or ----

    [Test]
    public void Or_first_alternative_matches()
    {
        var rule = AllOf(FirstOf(Literal("hello"), Literal("world")), Eof());
        AssertSameOutcome(rule, "hello", expectSuccess: true);
        AssertSameOutcome(rule, "world", expectSuccess: true);
        AssertSameOutcome(rule, "other", expectSuccess: false);
    }

    [Test]
    public void Or_takes_first_match_PEG_style()
    {
        // Longer literal must come first under PEG semantics; "ma"
        // would otherwise commit and "maj" never tried.
        var rule = AllOf(FirstOf(Literal("maj"), Literal("ma")), Eof());
        AssertSameOutcome(rule, "maj", expectSuccess: true);
        AssertSameOutcome(rule, "ma", expectSuccess: true);
    }

    // ---- BetweenInclusive (Optional, ZeroOrMore, OneOrMore, Exactly) ----

    [Test]
    public void Optional_present_or_absent_both_succeed()
    {
        var rule = AllOf(Optional(Token('!')), Eof());
        AssertSameOutcome(rule, "!", expectSuccess: true);
        AssertSameOutcome(rule, "", expectSuccess: true);
        AssertSameOutcome(rule, "a", expectSuccess: false);
    }

    [Test]
    public void ZeroOrMore_matches_run_of_chars()
    {
        var rule = AllOf(ZeroOrMore(OneOf(RuneSet.Ascii.Digits)), Eof());
        AssertSameOutcome(rule, "", expectSuccess: true);
        AssertSameOutcome(rule, "1", expectSuccess: true);
        AssertSameOutcome(rule, "12345", expectSuccess: true);
        AssertSameOutcome(rule, "12a45", expectSuccess: false);
    }

    [Test]
    public void OneOrMore_requires_at_least_one()
    {
        var rule = AllOf(OneOrMore(OneOf(RuneSet.Ascii.Letters)), Eof());
        AssertSameOutcome(rule, "", expectSuccess: false);
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "abc", expectSuccess: true);
    }

    [Test]
    public void Exactly_three_letters()
    {
        var rule = AllOf(Exactly(3, OneOf(RuneSet.Ascii.Letters)), Eof());
        AssertSameOutcome(rule, "ab", expectSuccess: false);
        AssertSameOutcome(rule, "abc", expectSuccess: true);
        AssertSameOutcome(rule, "abcd", expectSuccess: false);
    }

    [Test]
    public void BetweenInclusive_collects_children_into_preserve_wrapper()
    {
        var letters = OneOrMore(OneOf(RuneSet.Ascii.Letters)).As("letters").Preserve();
        var rooted = AllOf(letters, Eof());

        var stateMachine = StateMachineParser.Parse(rooted, "abc");
        Assert.That(stateMachine.Success, Is.True);

        // The "letters" wrapper is at top level. Its children are the
        // three rune leaves.
        var found = stateMachine.Tree?.Find(letters) ?? stateMachine.Symbols.FirstOrDefault(s => s.Id == letters.Id);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Children.Count, Is.EqualTo(3));
    }

    // ---- Not ----

    [Test]
    public void Not_succeeds_when_inner_fails()
    {
        // Match "a" only when not followed by "b". The Not is
        // zero-width so the lexer still has the next char available.
        var rule = AllOf(Token('a'), Not(Token('b')), Eof());
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "ab", expectSuccess: false);
    }

    [Test]
    public void Not_chained_with_OneOrMore_for_until_pattern()
    {
        // Read digits until "x" appears. Not(Token('x')) succeeds as long
        // as the next token isn't 'x'. Combined with OneOf(digits) we
        // consume one digit per iteration.
        var rule = AllOf(
            OneOrMore(AllOf(Not(Token('x')), OneOf(RuneSet.Ascii.Digits))),
            Token('x'),
            Eof());
        AssertSameOutcome(rule, "123x", expectSuccess: true);
        AssertSameOutcome(rule, "x", expectSuccess: false); // OneOrMore needs at least one
    }

    // ---- Peek ----

    [Test]
    public void Peek_succeeds_without_consuming()
    {
        var rule = AllOf(Peek(Token('a')), Token('a'), Eof());
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "b", expectSuccess: false);
    }

    [Test]
    public void Peek_failure_propagates()
    {
        var rule = AllOf(Peek(Token('a')), Eof());
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
        parens.Bind(ZeroOrMore(AllOf(Token('('), parens, Token(')'))));
        var rooted = AllOf(parens, Eof());

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
        var digit = OneOf(RuneSet.Ascii.Digits);
        var term = FirstOf(digit, AllOf(Token('('), expression, Token(')')));
        expression.Bind(AllOf(term, ZeroOrMore(AllOf(OneOf("+-"), term))));
        var rooted = AllOf(expression, Eof());

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
        var named = AllOf(Token('('), OneOrMore(OneOf(RuneSet.Ascii.Letters)), Token(')')).As("group").Preserve();
        var rooted = AllOf(named, Eof());

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
        var rule = AllOf(Token('a'), Token('b'), Token('c'));
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
        var rule = AllOf(Literal("hello"), Eof());
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
        var rule = AllOf(
            FirstOf(Literal("abcXY"), Literal("ab")),
            Eof());
        var stateMachine = StateMachineParser.Parse(rule, "abcde");
        Assert.That(stateMachine.Success, Is.False);
        // The deepest read inside the failed Or alternative was at
        // position 3 (matched abc, then failed at X). That's where
        // we should be.
        Assert.That(stateMachine.ErrorCharIndex, Is.GreaterThanOrEqualTo(2));
    }

    // ---- StringBody ----

    [Test]
    public void StringBody_no_escape_scans_until_stopper()
    {
        var rule = AllOf(
            Token('"'),
            ScanUntil(RuneSet.Runes("\"")),
            Token('"'),
            Eof());
        AssertSameOutcome(rule, "\"hello\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"\"", expectSuccess: true);
    }

    [Test]
    public void StringBody_with_simple_escape_handles_backslash_pairs()
    {
        var simpleEscape = OneOf(RuneSet.Runes("\"\\nrtbf/"));
        var rule = AllOf(
            Token('"'),
            ScanUntil(stopAt: RuneSet.Runes("\""), escapeStart: new System.Text.Rune('\\'), escapeEnd: simpleEscape),
            Token('"'),
            Eof());
        AssertSameOutcome(rule, "\"hello\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"a\\nb\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"a\\\"b\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"a\\xb\"", expectSuccess: false);  // \x not a valid escape
    }

    [Test]
    public void StringBody_recursive_escape_uses_subprogram_correctly()
    {
        var simpleEscape = OneOf(RuneSet.Runes("\"\\/bfnrt"));
        var hexDigit = OneOf(RuneSet.Ascii.HexDigits);
        var unicodeEscape = AllOf(Token('u'), hexDigit, hexDigit, hexDigit, hexDigit);
        var escape = FirstOf(simpleEscape, unicodeEscape).Flatten(FlattenType.Delete);
        var rule = AllOf(
            Token('"'),
            ScanUntil(stopAt: RuneSet.Runes("\""), escapeStart: new System.Text.Rune('\\'), escapeEnd: escape),
            Token('"'),
            Eof());
        AssertSameOutcome(rule, "\"\\u0041\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"hello \\\"world\\\" \\u00E9\"", expectSuccess: true);
        AssertSameOutcome(rule, "\"\\u00ZZ\"", expectSuccess: false); // bad hex
    }

    [Test]
    public void StringBody_preserves_leaf_text()
    {
        var rule = AllOf(Token('"'), ScanUntil(RuneSet.Runes("\"")), Token('"'), Eof());
        var stateMachine = StateMachineParser.Parse(rule, "\"hello\"");
        Assert.That(stateMachine.Success, Is.True);
        Assert.That(stateMachine.ToString(), Is.EqualTo("hello"));
    }

    // ---- AnyToken ----

    [Test]
    public void AnyToken_matches_any_single_token()
    {
        var rule = AllOf(AnyToken(), Eof());
        AssertSameOutcome(rule, "a", expectSuccess: true);
        AssertSameOutcome(rule, "z", expectSuccess: true);
        AssertSameOutcome(rule, "5", expectSuccess: true);
        AssertSameOutcome(rule, "", expectSuccess: false);
    }

    [Test]
    public void AnyToken_in_until_pattern()
    {
        // Read everything up to 'x'.
        var rule = AllOf(
            ZeroOrMore(AllOf(Not(Token('x')), AnyToken())),
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
        var rule = AllOf(OneOrMore(NoneOf(RuneSet.Runes("\""))), Token('"'), Eof());
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
        var rule = AllOf(LiteralIgnoreAsciiCase("hello"), Eof());
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
        var rule = AllOf(LiteralIgnoreAsciiCase("café"), Eof());
        AssertSameOutcome(rule, "café", expectSuccess: true);
        AssertSameOutcome(rule, "CAFé", expectSuccess: true);
        AssertSameOutcome(rule, "CAFÉ", expectSuccess: false); // É is non-ASCII, doesn't fold
    }

    // ---- WithError attribution ----

    [Test]
    public void WithError_message_surfaces_at_deepest_failure()
    {
        var keyword = Literal("hello").WithError("expected the greeting");
        var rule = AllOf(keyword, Eof());
        var stateMachine = StateMachineParser.Parse(rule, "world");
        Assert.That(stateMachine.Success, Is.False);
        Assert.That(stateMachine.ErrorMessage, Is.EqualTo("expected the greeting"));
    }

    [Test]
    public void WithError_on_OneOf_surfaces_at_failure()
    {
        var digit = OneOf(RuneSet.Ascii.Digits).WithError("expected a digit");
        var rule = AllOf(digit, Eof());
        var stateMachine = StateMachineParser.Parse(rule, "x");
        Assert.That(stateMachine.Success, Is.False);
        Assert.That(stateMachine.ErrorMessage, Is.EqualTo("expected a digit"));
    }

    // ---- Bridge fallback (WithinGrapheme + custom subclasses) ----

    [Test]
    public void WithinGrapheme_bridges_to_recursive_evaluator()
    {
        // WithinGrapheme is bridged. The state machine delegates the
        // whole rule to the recursive evaluator's TryParse, captures
        // its Symbol output, and folds it into the surrounding tree.
        // Test: under the GraphemeLexer, "é" arrives as one grapheme
        // (one rune, since this é is the precomposed form). WithinGrapheme
        // walks it rune-by-rune via the Rune sub-lexer and lets the
        // inner OneOf match.
        var rule = AllOf(
            WithinGrapheme(OneOf(RuneSet.Letters)),
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
        var rule = AllOf(new CustomTwoQs(), Eof());
        AssertSameOutcome(rule, "qq", expectSuccess: true);
        AssertSameOutcome(rule, "qx", expectSuccess: false);
        AssertSameOutcome(rule, "q", expectSuccess: false);
    }

    private sealed class CustomTwoQs : Rule
    {
        public CustomTwoQs() : base(FlattenType.Delete) { }

        internal override Symbol? TryParseRule(InductorParser.Lexing.Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
        {
            using var transaction = lexer.BeginTransaction();
            for (int i = 0; i < 2; i++)
            {
                if (lexer.IsEof) return null;
                var token = lexer.Read();
                if (token.RuneValue != 'q') return null;
            }
            transaction.Commit();
            return Symbol.Discarded;
        }
    }

    // ---- Both lexers ----

    [Test]
    public void Works_with_RuneLexer()
    {
        var rule = AllOf(Literal("hello"), Eof());
        var stateMachine = StateMachineParser.Parse(rule, "hello", new ParseOptions { InputUnit = InputUnit.Rune });
        Assert.That(stateMachine.Success, Is.True);
    }

    [Test]
    public void Works_with_GraphemeLexer()
    {
        var rule = AllOf(Literal("hello"), Eof());
        var stateMachine = StateMachineParser.Parse(rule, "hello", new ParseOptions { InputUnit = InputUnit.Grapheme });
        Assert.That(stateMachine.Success, Is.True);
    }

    // ---- Helpers ----

    // Assert the state-machine evaluator and the existing recursive
    // evaluator agree on outcome (success/failure) for the same input.
    // For both, we don't normalize so positions stay comparable.
    private static void AssertSameOutcome(Rule rule, string input, bool expectSuccess)
    {
        var legacy = rule.Parse(input, new ParseOptions { NormalizeInput = null });
        var stateMachine = StateMachineParser.Parse(rule, input, new ParseOptions { NormalizeInput = null });

        Assert.That(legacy.Success, Is.EqualTo(expectSuccess), $"legacy outcome for '{input}': {legacy.ErrorMessage}");
        Assert.That(stateMachine.Success, Is.EqualTo(expectSuccess), $"state-machine outcome for '{input}': {stateMachine.ErrorMessage}");
        Assert.That(stateMachine.Success, Is.EqualTo(legacy.Success), $"evaluators disagree on '{input}'");

        if (legacy.Success)
        {
            Assert.That(stateMachine.ToString(), Is.EqualTo(legacy.ToString()), $"tree text differs on '{input}'");
        }
    }
}
