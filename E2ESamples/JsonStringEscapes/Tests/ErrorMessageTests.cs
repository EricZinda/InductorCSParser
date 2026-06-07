// The heart of this sample: the error-message exercise.
//
// serde_json reports six distinct, positioned errors while parsing a JSON
// string. Each case below is a malformed literal plus the position and the
// words a user would want to see. The cases define the target behavior from
// serde_json (see ../Original/), independent of the grammar's own `.WithError`
// messages. README.md walks through how far the natural grammar got on its own.
//
// `ExpectedCharIndex` is the 0-based char offset the caret should land on.
// `MessageMustContain` is the set of substrings the message must include
// (case-insensitive) to count as pointing the user at the real problem.

using System;
using NUnit.Framework;
using JsonStringEscapes.Rewrite;

namespace JsonStringEscapes.Tests;

[TestFixture]
public class ErrorMessageTests
{
    public sealed record ErrorCase(
        string Input,
        int ExpectedCharIndex,
        string[] MessageMustContain,
        string Describes)
    {
        public override string ToString() => Describes;
    }

    // A bare lone surrogate code unit sitting literally in the input (not the
    // text "\uD800" but an actual U+D800 char). Built here so the array
    // initializer below stays readable.
    private const string LoneSurrogateCodeUnitInput = "\"\uD800\"";

    public static readonly ErrorCase[] AllCases =
    {
        new("\"abc", 4, new[] { "EOF" },
            "unterminated string, no closing quote"),
        new("\"a\tb\"", 2, new[] { "control character" },
            "a raw TAB control character inside the string"),
        new("\"\\x\"", 2, new[] { "invalid escape" },
            "an unknown escape letter, \\x"),
        new("\"\\u12\"", 3, new[] { "four hex" },
            "a \\u escape with only two hex digits"),
        new("\"\\uXY12\"", 3, new[] { "four hex" },
            "a \\u escape whose digits aren't hex"),
        new("\"\\uD800\"", 7, new[] { "surrogate", "unexpected end of hex escape" },
            "a lone leading surrogate, nothing after it"),
        new("\"\\uD800\\u0041\"", 9, new[] { "surrogate" },
            "a leading surrogate followed by a non-surrogate \\u escape"),
        new("\"\\uD800\\uD800\"", 10, new[] { "surrogate" },
            "a leading surrogate followed by another leading surrogate"),
        new("\"\\uDC00\"", 7, new[] { "surrogate" },
            "a lone trailing surrogate, nothing before it"),
        new(LoneSurrogateCodeUnitInput, 1, new[] { "unicode code point", "surrogate" },
            "a bare unpaired surrogate code unit in the input itself"),
    };

    [TestCaseSource(nameof(AllCases))]
    public void Rewrite_rejects_with_the_right_position(ErrorCase errorCase)
    {
        bool ok = JsonStringParser.TryParse(errorCase.Input, out _, out var error);

        Assert.That(ok, Is.False, $"expected [{errorCase.Describes}] to be rejected");
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.EqualTo(errorCase.ExpectedCharIndex),
            $"caret position for [{errorCase.Describes}]\n  message was: {error.Message}");
    }

    [TestCaseSource(nameof(AllCases))]
    public void Rewrite_rejects_with_a_helpful_message(ErrorCase errorCase)
    {
        bool ok = JsonStringParser.TryParse(errorCase.Input, out _, out var error);

        Assert.That(ok, Is.False, $"expected [{errorCase.Describes}] to be rejected");
        Assert.That(error, Is.Not.Null);
        foreach (var fragment in errorCase.MessageMustContain)
        {
            Assert.That(error!.Message.ToLowerInvariant(), Does.Contain(fragment.ToLowerInvariant()),
                $"message for [{errorCase.Describes}] should mention '{fragment}'\n  message was: {error.Message}");
        }
    }

    // Not an assertion, just a diagnostic. Dumps the position + message the
    // grammar actually produces for every case, so a `dotnet test` run
    // records the current behavior in one place.
    [Test]
    public void Dump_actual_error_output()
    {
        foreach (var errorCase in AllCases)
        {
            JsonStringParser.TryParse(errorCase.Input, out _, out var error);
            string shown = error is null
                ? "  (parsed OK)"
                : $"  char {error.CharIndex,2}  {error.Message}";
            TestContext.Out.WriteLine(errorCase.Describes);
            TestContext.Out.WriteLine(shown);
        }
    }
}
