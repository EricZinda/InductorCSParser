// Corpus tests for the JSON string-escape sample: valid inputs decode to the
// right value, invalid inputs are rejected, and the InductorParser rewrite
// agrees with the serde_json C# twin (../Original/) on both.
//
// The side-by-side error dump is a diagnostic, not an assertion: the two
// parsers position their errors differently on purpose (serde_json advances
// its cursor before recording, so several of its errors land one or more
// chars past the offending character). README.md has the table.

using System;
using NUnit.Framework;
using JsonStringEscapes.Original;
using JsonStringEscapes.Rewrite;

namespace JsonStringEscapes.Tests;

[TestFixture]
public class JsonStringTests
{
    public sealed record ValidCase(string Input, string Expected, string Describes)
    {
        public override string ToString() => Describes;
    }

    public static readonly ValidCase[] ValidCases =
    {
        new("\"\"", "", "the empty string"),
        new("\"hello\"", "hello", "plain ASCII"),
        new("\"a\\nb\\tc\"", "a\nb\tc", "simple escapes \\n and \\t"),
        new("\"\\\"\\\\\\/\"", "\"\\/", "the quote, backslash and slash escapes"),
        new("\"\\u0041\\u0042\"", "AB", "BMP \\u escapes"),
        new("\"\\u0000\"", "\0", "an escaped NUL is fine even though a raw one isn't"),
        new("\"café\"", "café", "a literal non-ASCII character in the body"),
        new("\"\\u00e9\"", "é", "the same e-acute written as a BMP escape"),
        new("\"\\uD834\\uDD1E\"", "\U0001D11E", "a surrogate pair, U+1D11E"),
        new("\"\\uDBFF\\uDFFF\"", "\U0010FFFF", "the highest surrogate pair, U+10FFFF"),
        new("\"A\\uD834\\uDD1EB\"", "A\U0001D11EB", "a pair between two ASCII letters"),
        new("\"\\uD7FF\\uE000\"", "퟿", "the BMP code points either side of the surrogate block"),
    };

    [TestCaseSource(nameof(ValidCases))]
    public void Rewrite_accepts_and_decodes(ValidCase validCase)
    {
        bool ok = JsonStringParser.TryParse(validCase.Input, out var value, out var error);

        Assert.That(ok, Is.True, $"expected [{validCase.Describes}] to parse; error: {error?.Message}");
        Assert.That(value, Is.EqualTo(validCase.Expected));
    }

    [TestCaseSource(nameof(ValidCases))]
    public void Twin_and_rewrite_decode_the_same_value(ValidCase validCase)
    {
        bool twinOk = SerdeJsonStringParser.TryParse(validCase.Input, out var twinValue, out _);
        bool rewriteOk = JsonStringParser.TryParse(validCase.Input, out var rewriteValue, out _);

        Assert.That(twinOk, Is.True, "serde_json twin should accept this input");
        Assert.That(rewriteOk, Is.True, "rewrite should accept this input");
        Assert.That(rewriteValue, Is.EqualTo(twinValue),
            $"twin and rewrite disagree on the decoded value for [{validCase.Describes}]");
    }

    // Every malformed case from ErrorMessageTests, minus the bare surrogate
    // code unit (covered on its own below: the char-based twin can't see it
    // the way byte-based serde_json does).
    public static string[] InvalidInputs() => Array.ConvertAll(
        Array.FindAll(ErrorMessageTests.AllCases, c => c.Input != "\"\uD800\""),
        c => c.Input);

    [TestCaseSource(nameof(InvalidInputs))]
    public void Both_parsers_reject_malformed_input(string input)
    {
        bool twinOk = SerdeJsonStringParser.TryParse(input, out _, out var twinError);
        bool rewriteOk = JsonStringParser.TryParse(input, out _, out var rewriteError);

        Assert.That(twinOk, Is.False, "serde_json twin should reject this input");
        Assert.That(rewriteOk, Is.False, "rewrite should reject this input");
        Assert.That(twinError, Is.Not.Null);
        Assert.That(rewriteError, Is.Not.Null);
    }

    // serde_json on bytes rejects an unpaired surrogate code unit in string
    // content (InvalidUnicodeCodePoint, raised when the bytes fail UTF-8
    // validation). The C# twin scans chars, not bytes, so it can't reach that
    // check and accepts the input, decoding to a string that holds the lone
    // surrogate. The InductorParser rewrite, compiled with Compile(null),
    // sees the bare surrogate as a token and rejects it, matching what
    // byte-based serde_json does and beating the char-based twin.
    [Test]
    public void Rewrite_rejects_a_bare_unpaired_surrogate_in_the_input()
    {
        string input = "\"\uD800\"";  // quote, lone high surrogate, quote

        bool rewriteOk = JsonStringParser.TryParse(input, out _, out var rewriteError);
        Assert.That(rewriteOk, Is.False);
        Assert.That(rewriteError!.CharIndex, Is.EqualTo(1));
        Assert.That(rewriteError.Message.ToLowerInvariant(), Does.Contain("unicode code point"));
    }

    // Diagnostic: the two parsers' error positions and messages side by side.
    [Test]
    public void Side_by_side_error_dump()
    {
        foreach (var errorCase in ErrorMessageTests.AllCases)
        {
            SerdeJsonStringParser.TryParse(errorCase.Input, out _, out var twinError);
            JsonStringParser.TryParse(errorCase.Input, out _, out var rewriteError);

            TestContext.Out.WriteLine(errorCase.Describes);
            TestContext.Out.WriteLine(twinError is null
                ? "  twin:     (accepted)"
                : $"  twin:     char {twinError.CharIndex,2}  {twinError.Message}");
            TestContext.Out.WriteLine(rewriteError is null
                ? "  rewrite:  (accepted)"
                : $"  rewrite:  char {rewriteError.CharIndex,2}  {rewriteError.Message}");
        }
    }
}
