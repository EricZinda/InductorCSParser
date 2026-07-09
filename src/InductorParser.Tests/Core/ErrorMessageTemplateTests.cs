using System;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
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
        // And(Token('a'), Token('b')) consumes 'a' then fails on
        // 'x' at offset 1. No WithError on either child, so the failure
        // path goes through the positional template and the parser
        // substitutes {charIndex} and {character}.
        var rule = And(Token('a'), Token('b'));
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
        // Input "a" runs out before Token('b') runs, so the
        // failure position equals input.Length and the EOF template
        // path fires. Only {charIndex} is meaningful here.
        var rule = And(Token('a'), Token('b'));
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
        // Recursive balanced-parens grammar from BudgetTests. It recurses
        // once per open paren until the depth budget trips.
        var nested = new LateBoundRule("nested");
        nested.Bind(Or(
            And(Token('('), nested, Token(')')),
            Token('x')));

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
        // column 1 (Language Server Protocol zero-based).
        var rule = And(
            Token(0x1D400),
            Token('\n'),
            Token('b'),
            Token('y'));
        var options = new ParseOptions
        {
            PositionalErrorTemplate =
                "char={charIndex} grapheme={tokenIndex} line={line} col={charColumn}",
        };
        var result = rule.Parse(UnicodeExamples.MathematicalBoldCapitalAGrapheme + "\nbx", options);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("char=4 grapheme=3 line=1 col=1"));
        // Sanity check: the placeholder values match the ParseResult
        // properties they're supposed to mirror.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(4));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(3));
        Assert.That(result.ErrorLine, Is.EqualTo(1));
        Assert.That(result.ErrorCharColumn, Is.EqualTo(1));
    }

    [Test]
    public void One_based_placeholders_are_line_and_column_plus_one()
    {
        // {lineNumber} / {charColumnNumber} are the human-facing one-based
        // counterparts of the zero-based {line} / {charColumn}. Same failure
        // point as the test above: char 4, line 1, column 1 (zero-based), so
        // lineNumber 2, charColumnNumber 2.
        var rule = And(
            Token(0x1D400),
            Token('\n'),
            Token('b'),
            Token('y'));
        var options = new ParseOptions
        {
            PositionalErrorTemplate =
                "line={line} charColumn={charColumn} lineNumber={lineNumber} charColumnNumber={charColumnNumber}",
        };
        var result = rule.Parse(UnicodeExamples.MathematicalBoldCapitalAGrapheme + "\nbx", options);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo("line=1 charColumn=1 lineNumber=2 charColumnNumber=2"));
        // The one-based placeholders are exactly the zero-based fields + 1.
        Assert.That(result.ErrorLine + 1, Is.EqualTo(2));
        Assert.That(result.ErrorCharColumn + 1, Is.EqualTo(2));
    }

    [Test]
    public void Default_message_column_counts_graphemes_not_chars()
    {
        // Bold-A (U+1D400) is one grapheme but two UTF-16 chars. Token(0x1D400)
        // matches it, then Token('a') fails on 'x' at char index 2. Counting
        // chars, 'x' sits at column 3 (one-based). Counting what a person sees,
        // it's the 2nd character, column 2. The default message reports the
        // grapheme column, so it says column 2.
        var rule = And(Token(0x1D400), Token('a'));
        var result = rule.Parse(UnicodeExamples.MathematicalBoldCapitalAGrapheme + "x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Unexpected 'x' at line 1, column 2."));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
        Assert.That(result.ErrorCharColumn, Is.EqualTo(2), "char column: the bold-A is two UTF-16 code units");
        Assert.That(result.ErrorTokenColumn, Is.EqualTo(1), "grapheme column: the bold-A is one character");
    }

    [Test]
    public void Char_and_token_column_placeholders_diverge_on_a_wide_character()
    {
        // Same bold-A setup. {charColumn} / {charColumnNumber} count UTF-16 chars.
        // {tokenColumn} / {tokenColumnNumber} count graphemes. With the two-char
        // bold-A ahead of the failing 'x', the two units disagree.
        var rule = And(Token(0x1D400), Token('a'));
        var options = new ParseOptions
        {
            PositionalErrorTemplate =
                "charColumn={charColumn} charColumnNumber={charColumnNumber} tokenColumn={tokenColumn} tokenColumnNumber={tokenColumnNumber}",
        };
        var result = rule.Parse(UnicodeExamples.MathematicalBoldCapitalAGrapheme + "x", options);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo("charColumn=2 charColumnNumber=3 tokenColumn=1 tokenColumnNumber=2"));
        // The placeholders mirror the ParseResult fields they're named for.
        Assert.That(result.ErrorCharColumn, Is.EqualTo(2));
        Assert.That(result.ErrorTokenColumn, Is.EqualTo(1));
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
        var rule = And(Token('a'), Token('b'));
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
        var rule = And(Token('a'), Token('b'));
        var options = new ParseOptions
        {
            PositionalErrorTemplate = "{charIndex} and {charIndex} again",
        };
        var result = rule.Parse("ax", options);

        Assert.That(result.ErrorMessage, Is.EqualTo("1 and 1 again"));
    }

    [Test]
    public void Character_placeholder_renders_full_supplementary_plane_rune()
    {
        // Bold-A (U+1D400) is one rune but two UTF-16 chars (a high
        // surrogate at offset 0, a low surrogate at offset 1). The
        // {character} placeholder is supposed to render the unexpected
        // user-perceived character. If the substitution only takes
        // parseInput[pos] it grabs a lone surrogate half, which is
        // malformed Unicode that displays as garbage. The right answer
        // is the full rune, "𝐀".
        string boldA = UnicodeExamples.MathematicalBoldCapitalAGrapheme;
        var rule = Token('a');
        var result = rule.Parse(boldA);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo($"Unexpected '{boldA}' at line 1, column 1."));
    }

    [Test]
    public void Character_placeholder_renders_full_grapheme_cluster_under_no_normalization()
    {
        // Compile(null) keeps the input verbatim, so a decomposed
        // grapheme like "e" + combining acute survives to the parser
        // as two chars / one grapheme cluster. The user perceives one
        // character ("é") and the {character} placeholder should match
        // what they see, not the bare 'e' before the combining mark.
        string eAcute = UnicodeExamples.LatinEAcuteGrapheme;
        var rule = Token('a');
        rule.Compile(null);
        var result = rule.Parse(eAcute);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo($"Unexpected '{eAcute}' at line 1, column 1."));
    }

    [Test]
    public void Setting_template_to_null_throws_ArgumentNullException()
    {
        var options = new ParseOptions();

        Assert.Throws<ArgumentNullException>(() => options.WithErrorTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.PositionalErrorTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.EndOfInputErrorTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.TimeoutAbortTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.RuleCountLimitAbortTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.DepthLimitAbortTemplate = null!);
        Assert.Throws<ArgumentNullException>(() => options.CancellationAbortTemplate = null!);
    }

    [Test]
    public void Default_templates_match_expected_strings()
    {
        // Locks in the exact default wording so callers who depend on the
        // current strings (and the other tests in the suite that assert on
        // them with Does.StartWith / Is.EqualTo) notice if the defaults ever
        // drift. The positional, end-of-input, and malformed defaults report
        // the failure as one-based line/column so the out-of-the-box message
        // reads the way a person counts position in an editor.
        var options = new ParseOptions();

        Assert.That(options.WithErrorTemplate,
            Is.EqualTo("{message} at line {lineNumber}, column {tokenColumnNumber}."));
        Assert.That(options.PositionalErrorTemplate,
            Is.EqualTo("Unexpected '{character}' at line {lineNumber}, column {tokenColumnNumber}."));
        Assert.That(options.EndOfInputErrorTemplate,
            Is.EqualTo("Unexpected end of input at line {lineNumber}, column {tokenColumnNumber}."));
        Assert.That(options.MalformedInputTemplate,
            Is.EqualTo("Malformed input at line {lineNumber}, column {tokenColumnNumber}: '{character}' isn't valid Unicode and can't be normalized."));
        Assert.That(options.TimeoutAbortTemplate,
            Is.EqualTo("Parse aborted: timeout exceeded."));
        Assert.That(options.RuleCountLimitAbortTemplate,
            Is.EqualTo("Parse aborted: rule-count limit exceeded."));
        Assert.That(options.DepthLimitAbortTemplate,
            Is.EqualTo("Parse aborted: maximum recursion depth exceeded."));
        Assert.That(options.CancellationAbortTemplate,
            Is.EqualTo("Parse aborted: cancellation requested."));
    }

    [Test]
    public void WithError_message_carries_position_by_default()
    {
        // A .WithError message goes through WithErrorTemplate, which by default
        // appends the failure position, so a custom message reads the same
        // shape as the mechanical default. Token('b') fails on 'x' at column 2.
        var rule = And(Token('a'), Token('b').WithError("expected a 'b' here"));
        var result = rule.Parse("ax");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("expected a 'b' here at line 1, column 2."));
    }

    [Test]
    public void Custom_WithErrorTemplate_reshapes_or_strips_the_position()
    {
        var rule = And(Token('a'), Token('b').WithError("expected a 'b' here"));

        // Position-first custom shape, where {message} is the author's text.
        var reshaped = rule.Parse("ax", new ParseOptions
        {
            WithErrorTemplate = "line {lineNumber} col {tokenColumnNumber}: {message}",
        });
        // "{message}" alone hands back the raw .WithError string with no position.
        var raw = rule.Parse("ax", new ParseOptions { WithErrorTemplate = "{message}" });

        Assert.That(reshaped.ErrorMessage, Is.EqualTo("line 1 col 2: expected a 'b' here"));
        Assert.That(raw.ErrorMessage, Is.EqualTo("expected a 'b' here"));
    }

    [Test]
    public void Character_placeholder_renders_user_typed_character_under_NFKC()
    {
        // Under FormKC, fullwidth "１" (U+FF11) folds to ASCII "1" before
        // the lexer sees it. Token('x') is stable under FormKC and rejects
        // '1' on the normalized side. The {character} placeholder in the
        // default PositionalErrorTemplate should show what the user typed
        // ('１'), not the normalized character ('1') the parser saw
        // internally. Otherwise the message contradicts the source code
        // the user is looking at.
        var rule = Token('x');
        rule.Compile(System.Text.NormalizationForm.FormKC);
        var result = rule.Parse(UnicodeExamples.FullwidthDigitOneGrapheme);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo($"Unexpected '{UnicodeExamples.FullwidthDigitOneGrapheme}' at line 1, column 1."));
    }

    [Test]
    public void Character_placeholder_renders_user_typed_ligature_under_NFKC()
    {
        // U+FB01 LATIN SMALL LIGATURE FI folds to "fi" under FormKC.
        // Token('a') won't accept 'f', so this fails at offset 0. The
        // message should report the user's ligature, not the unfolded
        // "f" the parser saw internally.
        var rule = Token('a');
        rule.Compile(System.Text.NormalizationForm.FormKC);
        var result = rule.Parse(UnicodeExamples.FiLigaturePlusOoText);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo($"Unexpected '{UnicodeExamples.FiLigatureGrapheme}' at line 1, column 1."));
    }

    [Test]
    public void Character_placeholder_escapes_control_character()
    {
        // The {character} placeholder splices the unexpected input
        // character into the message verbatim. For a printable character
        // that's right (see the supplementary-plane / NFKC tests above),
        // but a control character like LF injects a raw newline into the
        // one-line error message, splitting it across two lines in a log
        // or terminal. Every other diagnostic-render site in the parser
        // (TokenSet.ToString, PrintTree, the Lexer.Read trace) routes
        // user-bearing text through DisplayEscape so Cc / Zl / Zp render
        // as U+XXXX. The error message has to do the same.
        var rule = And(Token('a'), Token('b'));
        var result = rule.Parse("a\nb");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Unexpected 'U+000A' at line 1, column 2."));
    }

    [Test]
    public void Character_placeholder_escapes_line_separator()
    {
        // U+2028 LINE SEPARATOR is category Zl, not Cc, so a
        // char.IsControl-only check would miss it. DisplayEscape covers
        // Cc / Zl / Zp, so the error message escapes it to U+2028 rather
        // than embedding the raw separator (which terminates a logical
        // line the same way LF does). Built via (char)0x2028 so the
        // source file itself has no literal line separator.
        var rule = And(Token('a'), Token('b'));
        string input = "a" + (char)0x2028 + "b";
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Unexpected 'U+2028' at line 1, column 2."));
    }

    [Test]
    public void Character_placeholder_escapes_lone_surrogate()
    {
        // Compile(null) is the documented opt-in path for malformed
        // UTF-16 / WTF-8 round-tripping. A lone surrogate can therefore
        // reach the normal error-message path, but the message itself
        // should contain a readable code-unit escape rather than a raw
        // unpaired surrogate that output encoders may replace or reject.
        var rule = Token('a');
        rule.Compile(null);
        var result = rule.Parse(UnicodeExamples.HighSurrogateMinText);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage,
            Is.EqualTo("Unexpected 'U+D800' at line 1, column 1."));
    }

    [Test]
    public void Character_placeholder_escapes_control_character_in_custom_template()
    {
        // The escape lives in the {character} value provider, not in the
        // default template text, so a caller who swaps in their own
        // PositionalErrorTemplate with {character} in it still gets a
        // control character rendered as U+XXXX rather than a raw newline
        // splitting their message. Here the parse fails on a bare LF at
        // offset 1.
        var rule = And(Token('a'), Token('b'));
        var options = new ParseOptions
        {
            PositionalErrorTemplate = "boom at {charIndex}: '{character}'",
        };
        var result = rule.Parse("a\nb", options);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("boom at 1: 'U+000A'"));
    }
}
