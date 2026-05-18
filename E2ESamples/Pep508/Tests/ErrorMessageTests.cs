// The heart of this sample: error-message tests.
//
// Each case below is a piece of malformed input plus the position and
// message a user would WANT to see. The cases were written first,
// against the natural-first grammar (RequirementGrammar.cs with no
// `.WithError` anywhere), to find out how far the obvious grammar gets
// on its own. README.md walks through that analysis. The grammar then
// had diagnostics layered on until these tests pass.
//
// `ExpectedCharIndex` is the 0-based char offset the caret should land
// on. `MessageMustContain` is the set of substrings the message must
// include (case-insensitive) to count as pointing the user at the real
// problem.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pep508Sample.Rewrite;

namespace Pep508Sample.Tests;

[TestFixture]
public class ErrorMessageTests
{
    public sealed record ErrorCase(
        string Input,
        int ExpectedCharIndex,
        string[] MessageMustContain,
        string Describes)
    {
        public override string ToString() => $"{Describes}: \"{Input}\"";
    }

    public static readonly ErrorCase[] AllCases =
    {
        new("", 0, new[] { "package name" },
            "empty input"),
        new("==1.0", 0, new[] { "package name" },
            "starts with an operator, no name"),
        new(".foo", 0, new[] { "package name" },
            "name starts with a dot"),
        new("requests<>1.0", 9, new[] { "version" },
            "bad operator '<>' leaves a stray '>'"),
        new("requests>=", 10, new[] { "version" },
            "operator with no version"),
        new("requests >= ", 12, new[] { "version" },
            "operator and whitespace, no version"),
        new("requests[extra1 extra2]", 16, new[] { "]" },
            "two extras with no comma between them"),
        new("requests[extra1,]", 16, new[] { "extra name" },
            "trailing comma in the extras list"),
        new("requests[extra1", 15, new[] { "]" },
            "extras list with no closing bracket"),
        new("requests(>=1.0", 14, new[] { ")" },
            "version specifier with no closing paren"),
        new("requests@", 9, new[] { "URL" },
            "'@' with no URL after it"),
        new("requests @ ", 11, new[] { "URL" },
            "'@' and whitespace with no URL"),
        new("requests==1.0 oops", 14, new[] { "end" },
            "trailing junk after a complete specifier"),
        // The deepest the parser gets is the version-spec attempt, which
        // reaches the comparison operator. The caret lands on the 'o'
        // with the operator message rather than the end-of-specifier one.
        new("requests oops", 9, new[] { "operator" },
            "trailing junk after a bare name"),
        // The '==' / '===' branches consume the lone '=' and fail one
        // char in, so depth-primary ranking puts the caret at char 9
        // (the '1') with the comparison-operator message. Under the old
        // tier model a named WithError pulled the caret back to char 8;
        // depth-primary keeps it at the parser's furthest progress.
        new("requests=1.0", 9, new[] { "operator" },
            "single '=' where '==' was meant"),
        // The trailing comma starts another version constraint that
        // never arrives: the comparison operator is expected at char 14
        // (EOF) and ComparisonOperator's WithError surfaces there.
        new("requests>=1.0,", 14, new[] { "operator" },
            "trailing comma in the version list"),
    };

    [TestCaseSource(nameof(AllCases))]
    public void Rewrite_rejects_with_the_right_position(ErrorCase errorCase)
    {
        bool ok = RequirementParser.TryParse(errorCase.Input, out _, out var error);

        Assert.That(ok, Is.False, $"expected '{errorCase.Input}' to be rejected");
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.EqualTo(errorCase.ExpectedCharIndex),
            $"caret position for [{errorCase.Describes}]\n  message was: {error.Message}");
    }

    [TestCaseSource(nameof(AllCases))]
    public void Rewrite_rejects_with_a_helpful_message(ErrorCase errorCase)
    {
        bool ok = RequirementParser.TryParse(errorCase.Input, out _, out var error);

        Assert.That(ok, Is.False, $"expected '{errorCase.Input}' to be rejected");
        Assert.That(error, Is.Not.Null);
        foreach (var fragment in errorCase.MessageMustContain)
        {
            Assert.That(error!.Message.ToLowerInvariant(), Does.Contain(fragment.ToLowerInvariant()),
                $"message for [{errorCase.Describes}] should mention '{fragment}'\n  message was: {error.Message}");
        }
    }

    // Not an assertion, just a diagnostic. Dumps the position + message the
    // grammar actually produces for every case, so a `dotnet test`
    // run records the current behavior in one place. Handy when
    // re-running this exercise on a future grammar change.
    [Test]
    public void Dump_actual_error_output()
    {
        foreach (var errorCase in AllCases)
        {
            RequirementParser.TryParse(errorCase.Input, out _, out var error);
            string line = error is null
                ? $"  (parsed OK)            <- {errorCase.Describes}"
                : $"  char {error.CharIndex,2}  {error.Message}";
            TestContext.Out.WriteLine($"\"{errorCase.Input}\"");
            TestContext.Out.WriteLine(line);
        }
    }
}
