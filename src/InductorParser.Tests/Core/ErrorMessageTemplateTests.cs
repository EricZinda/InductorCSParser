using System;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Templates on ParseOptions let callers swap out the parser's default error
// messages while keeping the placeholders the parser fills in. Each test
// pairs a custom template against the failure shape that triggers it and
// checks both the substituted output and the unsubstituted defaults.
[TestFixture]
public class ErrorMessageTemplateTests
{
    [Test]
    public void PositionalErrorTemplate_custom_substitutes_charIndex_and_character()
    {
        // AllOf(Grapheme('a'), Grapheme('b')) consumes 'a' then fails on
        // 'x' at offset 1. No WithError on either child, so the failure
        // path goes through the positional template and the parser
        // substitutes {charIndex} and {character}.
        var rule = AllOf(Grapheme('a'), Grapheme('b'));
        var options = new ParseOptions
        {
            PositionalErrorTemplate = "want X at {charIndex}, got '{character}'",
        };
        var result = rule.Parse("ax", options);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("want X at 1, got 'x'"));
    }

    [Test]
    public void EndOfInputErrorTemplate_custom_substitutes_charIndex()
    {
        // Input "a" runs out before Grapheme('b') runs, so the
        // failure position equals input.Length and the EOF template
        // path fires. Only {charIndex} is meaningful here.
        var rule = AllOf(Grapheme('a'), Grapheme('b'));
        var options = new ParseOptions
        {
            EndOfInputErrorTemplate = "ran out of input at {charIndex}",
        };
        var result = rule.Parse("a", options);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("ran out of input at 1"));
    }

    [Test]
    public void TimeoutAbortTemplate_custom_substitutes_timeout()
    {
        // Same shape as BudgetTests.Timeout_aborts_with_Timeout_outcome:
        // tiny timeout plus enough work to reach the first periodic
        // budget check. The custom template should surface in
        // ParseResult.ErrorMessage with the configured timeout
        // substituted.
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        var timeout = TimeSpan.FromTicks(1);
        var options = new ParseOptions
        {
            Timeout = timeout,
            RuleCountLimit = 0,
            TimeoutAbortTemplate = "took too long ({timeout})",
        };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Timeout));
        Assert.That(result.ErrorMessage, Is.EqualTo("took too long (" + timeout.ToString() + ")"));
    }

    [Test]
    public void RuleCountLimitAbortTemplate_custom_substitutes_limit()
    {
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        var options = new ParseOptions
        {
            RuleCountLimit = 10,
            RuleCountLimitAbortTemplate = "too many invocations (cap was {limit})",
        };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.RuleCountLimitExceeded));
        Assert.That(result.ErrorMessage, Is.EqualTo("too many invocations (cap was 10)"));
    }

    [Test]
    public void DepthLimitAbortTemplate_custom_substitutes_limit()
    {
        // Recursive balanced-parens grammar from BudgetTests; recurses
        // once per open paren until the depth budget trips.
        var nested = new LateBoundRule("nested");
        nested.Bind(FirstOf(
            AllOf(Grapheme('('), nested, Grapheme(')')),
            Grapheme('x')));

        string input = new string('(', 100) + "x" + new string(')', 100);
        var options = new ParseOptions
        {
            MaxDepth = 10,
            DepthLimitAbortTemplate = "stack too deep (max was {limit})",
        };
        var result = nested.Parse(input, options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.DepthLimitExceeded));
        Assert.That(result.ErrorMessage, Is.EqualTo("stack too deep (max was 10)"));
    }

    [Test]
    public void CancellationAbortTemplate_custom_renders_on_cancel()
    {
        var cancellation = new ParseCancellation();
        cancellation.Cancel();

        var rule = OneOrMore(OneOf(TokenSet.Letters));
        var options = new ParseOptions
        {
            Cancellation = cancellation,
            RuleCountLimit = 0,
            CancellationAbortTemplate = "user pulled the plug at {charIndex}",
        };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.Canceled));
        Assert.That(result.ErrorMessage, Does.StartWith("user pulled the plug at "));
    }

    [Test]
    public void All_position_placeholders_substitute_values_matching_ParseResult()
    {
        // Input lays out a clean test of every position unit in one shot:
        //   bold-A (U+1D400, supplementary plane, 2 UTF-16 chars / 1 grapheme)
        //   \n (newline, 1 char / 1 grapheme)
        //   b  (1 char / 1 grapheme)
        //   x  (the failing character)
        // The grammar matches the bold-A then \n then b, then asks for
        // 'y' and gets 'x'. Failure point: char 4, grapheme 3, line 1
        // column 1 (LSP zero-based).
        var rule = AllOf(
            Grapheme(0x1D400),
            Grapheme('\n'),
            Grapheme('b'),
            Grapheme('y'));
        var options = new ParseOptions
        {
            PositionalErrorTemplate =
                "char={charIndex} grapheme={graphemeIndex} line={line} col={column}",
        };
        var result = rule.Parse(char.ConvertFromUtf32(0x1D400) + "\nbx", options);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("char=4 grapheme=3 line=1 col=1"));
        // Sanity check: the placeholder values match the ParseResult
        // properties they're supposed to mirror.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(4));
        Assert.That(result.ErrorGraphemeIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorColumn, Is.EqualTo(1));
    }

    [Test]
    public void Position_placeholders_work_on_budget_abort_templates()
    {
        // The same five position placeholders are wired through every
        // budget template, not just the grammar-mismatch ones. A custom
        // RuleCountLimitAbortTemplate that asks for {line} and {limit}
        // should get both substituted.
        var rule = OneOrMore(OneOf(TokenSet.Letters));
        var options = new ParseOptions
        {
            RuleCountLimit = 10,
            RuleCountLimitAbortTemplate = "stopped at line {line} (limit was {limit})",
        };
        var result = rule.Parse(new string('a', 5000), options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.RuleCountLimitExceeded));
        Assert.That(result.ErrorMessage, Does.StartWith("stopped at line 0 (limit was 10)"));
    }

    [Test]
    public void Template_with_unknown_placeholder_passes_through_verbatim()
    {
        // {bogus} isn't one of the documented placeholders for the
        // positional template. The parser leaves it alone rather than
        // throwing, so a typo is visible in the output instead of
        // crashing every parse.
        var rule = AllOf(Grapheme('a'), Grapheme('b'));
        var options = new ParseOptions
        {
            PositionalErrorTemplate = "got {bogus} at {charIndex}",
        };
        var result = rule.Parse("ax", options);

        Assert.That(result.ErrorMessage, Is.EqualTo("got {bogus} at 1"));
    }

    [Test]
    public void Template_with_repeated_placeholder_substitutes_all_occurrences()
    {
        var rule = AllOf(Grapheme('a'), Grapheme('b'));
        var options = new ParseOptions
        {
            PositionalErrorTemplate = "{charIndex} and {charIndex} again",
        };
        var result = rule.Parse("ax", options);

        Assert.That(result.ErrorMessage, Is.EqualTo("1 and 1 again"));
    }

    [Test]
    public void Setting_template_to_null_throws_ArgumentNullException()
    {
        var options = new ParseOptions();

        Assert.Throws<ArgumentNullException>(() => options.PositionalErrorTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.EndOfInputErrorTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.TimeoutAbortTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.RuleCountLimitAbortTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.DepthLimitAbortTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.CancellationAbortTemplate = null!);
    }

    [Test]
    public void Default_templates_match_pre_change_strings()
    {
        // Locks in the exact default wording so callers who depend on the
        // current strings (and the existing tests scattered through the
        // suite that assert on them with Does.StartWith / Is.EqualTo)
        // notice if the defaults ever drift.
        var options = new ParseOptions();

        Assert.That(options.PositionalErrorTemplate,
            Is.EqualTo("Parse failed at offset {charIndex}: unexpected '{character}'."));
        Assert.That(options.EndOfInputErrorTemplate,
            Is.EqualTo("Unexpected end of input."));
        Assert.That(options.TimeoutAbortTemplate,
            Is.EqualTo("Parse aborted: timeout exceeded."));
        Assert.That(options.RuleCountLimitAbortTemplate,
            Is.EqualTo("Parse aborted: rule-count limit exceeded."));
        Assert.That(options.DepthLimitAbortTemplate,
            Is.EqualTo("Parse aborted: maximum recursion depth exceeded."));
        Assert.That(options.CancellationAbortTemplate,
            Is.EqualTo("Parse aborted: cancellation requested."));
    }
}
