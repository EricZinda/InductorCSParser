// End-to-end tests for the PEP 508 dependency-specifier sample.
//
// 1. Golden corpus: valid specifiers drawn from the PEP 508 examples
//    and packaging's own test suite. The Original (packaging port) and
//    the Rewrite (InductorParser) must produce equal ASTs for each one.
//
// 2. Reject corpus: malformed input. Both parsers must reject it. The
//    position-and-message detail lives in ErrorMessageTests.cs; here we
//    only check that nothing malformed slips through.
//
// 3. Side-by-side: a few bad inputs where the Original and the Rewrite
//    error renderings are printed together, to show they both produce
//    a message + source + caret a user can act on.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pep508Sample.Original;
using Pep508Sample.Rewrite;

namespace Pep508Sample.Tests;

[TestFixture]
public class RequirementGoldenTests
{
    public sealed record ValidCase(
        string Input,
        string Name,
        string[] Extras,
        string[] Operators,
        string[] Versions,
        string? Url)
    {
        public override string ToString() => $"\"{Input}\"";
    }

    public static readonly ValidCase[] ValidInputs =
    {
        new("requests", "requests", new string[0], new string[0], new string[0], null),
        new("Django", "Django", new string[0], new string[0], new string[0], null),
        new("a", "a", new string[0], new string[0], new string[0], null),
        new("a.b.c", "a.b.c", new string[0], new string[0], new string[0], null),
        new("requests-toolbelt", "requests-toolbelt", new string[0], new string[0], new string[0], null),
        new("requests>=2.8.1", "requests", new string[0], new[] { ">=" }, new[] { "2.8.1" }, null),
        new("requests >= 2.8.1", "requests", new string[0], new[] { ">=" }, new[] { "2.8.1" }, null),
        new("requests==2.28.2", "requests", new string[0], new[] { "==" }, new[] { "2.28.2" }, null),
        new("requests~=2.8", "requests", new string[0], new[] { "~=" }, new[] { "2.8" }, null),
        new("requests!=2.8.1", "requests", new string[0], new[] { "!=" }, new[] { "2.8.1" }, null),
        new("requests<3.0", "requests", new string[0], new[] { "<" }, new[] { "3.0" }, null),
        new("requests>1.0", "requests", new string[0], new[] { ">" }, new[] { "1.0" }, null),
        new("requests<=2.0", "requests", new string[0], new[] { "<=" }, new[] { "2.0" }, null),
        new("requests===2.8.1", "requests", new string[0], new[] { "===" }, new[] { "2.8.1" }, null),
        new("requests==2.28.*", "requests", new string[0], new[] { "==" }, new[] { "2.28.*" }, null),
        new("requests>=2.8.1,<3.0", "requests", new string[0],
            new[] { ">=", "<" }, new[] { "2.8.1", "3.0" }, null),
        new("requests >= 2.8.1, < 3.0", "requests", new string[0],
            new[] { ">=", "<" }, new[] { "2.8.1", "3.0" }, null),
        new("requests(>=2.8.1)", "requests", new string[0], new[] { ">=" }, new[] { "2.8.1" }, null),
        new("requests (>=2.8.1, <3.0)", "requests", new string[0],
            new[] { ">=", "<" }, new[] { "2.8.1", "3.0" }, null),
        new("requests[security]", "requests", new[] { "security" }, new string[0], new string[0], null),
        new("requests[security,socks]", "requests", new[] { "security", "socks" },
            new string[0], new string[0], null),
        new("requests[security, socks]", "requests", new[] { "security", "socks" },
            new string[0], new string[0], null),
        new("requests[]", "requests", new string[0], new string[0], new string[0], null),
        new("requests[security]>=2.8.1", "requests", new[] { "security" },
            new[] { ">=" }, new[] { "2.8.1" }, null),
        new("name[a,b,c]>=1.0,<2.0", "name", new[] { "a", "b", "c" },
            new[] { ">=", "<" }, new[] { "1.0", "2.0" }, null),
        new("  flask  >=  1.0  ", "flask", new string[0], new[] { ">=" }, new[] { "1.0" }, null),
        new("pip @ https://github.com/pypa/pip/archive/1.3.1.zip", "pip", new string[0],
            new string[0], new string[0], "https://github.com/pypa/pip/archive/1.3.1.zip"),
        new("requests[security] @ https://example.com/requests.whl", "requests",
            new[] { "security" }, new string[0], new string[0], "https://example.com/requests.whl"),
    };

    [TestCaseSource(nameof(ValidInputs))]
    public void Original_and_Rewrite_agree_on_valid_input(ValidCase validCase)
    {
        bool originalOk = OriginalRequirementParser.TryParse(validCase.Input, out var original, out var originalError);
        bool rewriteOk = RequirementParser.TryParse(validCase.Input, out var rewrite, out var rewriteError);

        Assert.That(originalOk, Is.True, $"Original rejected '{validCase.Input}': {originalError}");
        Assert.That(rewriteOk, Is.True, $"Rewrite rejected '{validCase.Input}': {rewriteError}");

        AssertExpected(validCase, original!.Name, original.Extras,
            original.Specifiers.Select(s => s.Operator), original.Specifiers.Select(s => s.Version),
            original.Url, "Original");
        AssertExpected(validCase, rewrite!.Name, rewrite.Extras,
            rewrite.Specifiers.Select(s => s.Operator), rewrite.Specifiers.Select(s => s.Version),
            rewrite.Url, "Rewrite");
    }

    private static void AssertExpected(
        ValidCase expected, string name, IEnumerable<string> extras,
        IEnumerable<string> operators, IEnumerable<string> versions, string? url, string which)
    {
        Assert.That(name, Is.EqualTo(expected.Name), $"{which} name for '{expected.Input}'");
        Assert.That(extras, Is.EqualTo(expected.Extras), $"{which} extras for '{expected.Input}'");
        Assert.That(operators, Is.EqualTo(expected.Operators), $"{which} operators for '{expected.Input}'");
        Assert.That(versions, Is.EqualTo(expected.Versions), $"{which} versions for '{expected.Input}'");
        Assert.That(url, Is.EqualTo(expected.Url), $"{which} url for '{expected.Input}'");
    }
}

[TestFixture]
public class RequirementRejectTests
{
    // Malformed input both parsers reject.
    private static readonly string[] RejectedByBoth =
    {
        "",
        "   ",
        "==1.0",
        ".foo",
        "[extra]",
        "requests<>1.0",
        "requests>=",
        "requests >= ",
        "requests[extra1 extra2]",
        "requests[extra1,]",
        "requests[extra1",
        "requests[,extra]",
        "requests(>=1.0",
        "requests@",
        "requests @ ",
        "requests==1.0 oops",
        "requests oops",
        "requests=1.0",
        "requests; python_version < \"3.8\"",
    };

    // Input the rewrite rejects but packaging accepts: packaging keeps a
    // trailing comma in the specifier string and lets a later layer
    // choke on it, while the rewrite's grammar requires a real version
    // after every comma. The rewrite is stricter here, on purpose.
    private static readonly string[] RejectedByRewriteOnly =
    {
        "requests>=1.0,",
    };

    private static readonly string[] AllInvalid =
        RejectedByBoth.Concat(RejectedByRewriteOnly).ToArray();

    [TestCaseSource(nameof(RejectedByBoth))]
    public void Original_rejects(string input)
    {
        bool ok = OriginalRequirementParser.TryParse(input, out _, out _);
        Assert.That(ok, Is.False, $"Original should reject '{input}'");
    }

    [TestCaseSource(nameof(AllInvalid))]
    public void Rewrite_rejects_with_a_positioned_error(string input)
    {
        bool ok = RequirementParser.TryParse(input, out _, out var error);
        Assert.That(ok, Is.False, $"Rewrite should reject '{input}'");
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.InRange(0, input.Length));
        Assert.That(error.Message, Is.Not.Empty);
    }
}

[TestFixture]
public class RequirementErrorRenderingTests
{
    // Prints the Original (packaging-style) and Rewrite error renderings
    // side by side. Both produce a message, the source line, and a
    // caret. The Rewrite additionally carries a token index and a
    // walkable parse tree (not shown here).
    [TestCase("requests<>1.0")]
    [TestCase("requests[extra1 extra2]")]
    [TestCase("requests(>=1.0")]
    [TestCase("requests@")]
    public void Both_parsers_render_a_caret(string input)
    {
        OriginalRequirementParser.TryParse(input, out _, out var originalError);
        RequirementParser.TryParse(input, out _, out var rewriteError);

        Assert.That(originalError, Is.Not.Null);
        Assert.That(rewriteError, Is.Not.Null);

        TestContext.Out.WriteLine($"input: {input}");
        TestContext.Out.WriteLine("Original (packaging port):");
        TestContext.Out.WriteLine(originalError!.ToString());
        TestContext.Out.WriteLine("Rewrite (InductorParser):");
        TestContext.Out.WriteLine(rewriteError!.ToString());
        TestContext.Out.WriteLine(string.Empty);
    }
}
