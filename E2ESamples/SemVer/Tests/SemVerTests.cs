// End-to-end tests for the SemVer 2.0.0 sample. Three sets:
//
// 1. Golden corpus: every valid input from the semver.org spec, plus
//    a handful of edge cases (long numeric, hyphens-in-prerelease,
//    long alphanumeric build metadata). The Original and Rewrite
//    parsers must produce equal AST shapes for every one. This is the
//    "behavioral parity" check.
//
// 2. Reject corpus: every kind of bad input the spec calls out
//    (leading zeros, missing parts, empty identifiers, non-ASCII).
//    Both parsers must reject. The Rewrite parser additionally has to
//    point at the right position. This is the "errors are useful"
//    check.
//
// 3. Side-by-side: a small set of bad inputs where we explicitly
//    compare the error messages from Original and Rewrite. Original
//    only knows "didn't match". Rewrite gives line, column, and a
//    targeted message. This is the "value-add" check that proves
//    porting was worth doing.
//
// Test names follow the existing repo convention (snake_case sentence,
// describes the invariant being checked).

using System.Linq;
using NUnit.Framework;
using SemVerSample.Original;
using SemVerSample.Rewrite;

namespace SemVerSample.Tests;

[TestFixture]
public class SemVerGoldenTests
{
    // The full set of valid inputs from semver.org's "Backus-Naur Form
    // Grammar for Valid SemVer Versions" plus the FAQ examples.
    private static readonly string[] ValidInputs = {
        "0.0.0",
        "1.0.0",
        "1.2.3",
        "10.20.30",
        "1.1.2-prerelease+meta",
        "1.1.2+meta",
        "1.1.2+meta-valid",
        "1.0.0-alpha",
        "1.0.0-beta",
        "1.0.0-alpha.beta",
        "1.0.0-alpha.beta.1",
        "1.0.0-alpha.1",
        "1.0.0-alpha0.valid",
        "1.0.0-alpha.0valid",
        "1.0.0-alpha-a.b-c-somethinglong+build.1-aef.1-its-okay",
        "1.0.0-rc.1+build.1",
        "2.0.0-rc.1+build.123",
        "1.2.3-beta",
        "10.2.3-DEV-SNAPSHOT",
        "1.2.3-SNAPSHOT-123",
        "1.0.0",
        "2.0.0",
        "1.1.7",
        "2.0.0+build.1848",
        "2.0.1-alpha.1227",
        "1.0.0-alpha+beta",
        "1.2.3----RC-SNAPSHOT.12.9.1--.12+788",
        "1.2.3----R-S.12.9.1--.12+meta",
        "1.2.3----RC-SNAPSHOT.12.9.1--.12",
        "1.0.0+0.build.1-rc.10000aaa-kk-0.1",
        "99999999999999999999.999999999999999999.99999999999999999",
    };

    [TestCaseSource(nameof(ValidInputs))]
    public void Original_and_Rewrite_agree_on_valid_input(string input)
    {
        bool originalOk = OriginalSemVerParser.TryParse(input, out var originalVersion, out string originalError);
        bool rewriteOk = SemVerParser.TryParse(input, out var rewriteVersion, out var rewriteError);

        if (!originalOk)
        {
            // Spec edge: the regex parser uses Int32 for major/minor/patch so
            // it bails on the 99999999999999999999... case. Rewrite has the
            // same Int32 limitation, so they should agree even there.
            Assert.That(rewriteOk, Is.False,
                $"Original rejected '{input}' ({originalError}) but Rewrite accepted.");
            return;
        }

        Assert.That(rewriteOk, Is.True,
            $"Original accepted '{input}' but Rewrite rejected: {rewriteError}");

        Assert.That(rewriteVersion!.Major, Is.EqualTo(originalVersion!.Major));
        Assert.That(rewriteVersion.Minor, Is.EqualTo(originalVersion.Minor));
        Assert.That(rewriteVersion.Patch, Is.EqualTo(originalVersion.Patch));

        Assert.That(rewriteVersion.PreRelease.Count, Is.EqualTo(originalVersion.PreRelease.Count),
            $"Pre-release length mismatch on '{input}'");
        for (int identifierIndex = 0; identifierIndex < rewriteVersion.PreRelease.Count; identifierIndex++)
        {
            Assert.That(rewriteVersion.PreRelease[identifierIndex].Text, Is.EqualTo(originalVersion.PreRelease[identifierIndex].Text));
            Assert.That(rewriteVersion.PreRelease[identifierIndex].IsNumeric, Is.EqualTo(originalVersion.PreRelease[identifierIndex].IsNumeric));
        }

        Assert.That(rewriteVersion.BuildMetadata, Is.EqualTo(originalVersion.BuildMetadata).AsCollection,
            $"Build metadata mismatch on '{input}'");

        Assert.That(rewriteVersion.ToString(), Is.EqualTo(input),
            "Round-trip ToString should equal the input.");
    }
}

[TestFixture]
public class SemVerRejectTests
{
    // Every category of bad input the spec calls out, plus a handful
    // of common typos. The Original (regex) parser should reject all
    // of these. The Rewrite parser should additionally produce a
    // useful position.
    public record RejectCase(string Input, string DescribesProblem);

    private static readonly RejectCase[] InvalidInputs = {
        new("",                      "empty"),
        new("1",                     "missing minor and patch"),
        new("1.2",                   "missing patch"),
        new("01.2.3",                "leading zero on major"),
        new("1.02.3",                "leading zero on minor"),
        new("1.2.03",                "leading zero on patch"),
        new("1.2.3-01",              "leading zero on numeric pre-release identifier"),
        new("1.2.3-",                "empty pre-release after '-'"),
        new("1.2.3+",                "empty build metadata after '+'"),
        new("1.2.3-alpha..1",        "empty identifier from double dot in pre-release"),
        new("1.2.3-alpha.",          "trailing dot in pre-release"),
        new("1.2.3+build..1",        "empty identifier from double dot in build metadata"),
        new("1.2.3-α",               "non-ASCII rune in pre-release"),
        new("1.2.3 ",                "trailing whitespace"),
        new("v1.2.3",                "leading 'v'"),
        new("1.2.3.4",               "too many dotted parts"),
        new("1.2.-1",                "negative patch"),
    };

    [TestCaseSource(nameof(InvalidInputs))]
    public void Original_rejects(RejectCase reject)
    {
        bool ok = OriginalSemVerParser.TryParse(reject.Input, out _, out _);
        Assert.That(ok, Is.False, $"Original should reject '{reject.Input}' ({reject.DescribesProblem})");
    }

    [TestCaseSource(nameof(InvalidInputs))]
    public void Rewrite_rejects_with_positioned_error(RejectCase reject)
    {
        bool ok = SemVerParser.TryParse(reject.Input, out _, out var error);
        Assert.That(ok, Is.False, $"Rewrite should reject '{reject.Input}' ({reject.DescribesProblem})");
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(error.CharIndex, Is.LessThanOrEqualTo(reject.Input.Length));
        Assert.That(error.Message, Is.Not.Empty);
    }
}

[TestFixture]
public class SemVerErrorPositionTests
{
    // Side-by-side comparison: each case names the input, the column
    // (0-based) the Rewrite parser should point at, and a substring
    // the error message should contain. The Original/regex parser
    // produces "'<input>' is not a valid semver 2.0.0 version string."
    // for every one of these (no position, no specifics).

    [Test]
    public void Leading_zero_on_major_is_pointed_at_column_zero()
    {
        SemVerParser.TryParse("01.2.3", out _, out var error);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Column, Is.EqualTo(0));
        Assert.That(error.Message, Does.Contain("leading zero").IgnoreCase);
    }

    [Test]
    public void Leading_zero_on_minor_is_pointed_at_the_minor_position()
    {
        // "1.02.3" -> minor "02" starts at offset 2 (the '0' after the first '.').
        SemVerParser.TryParse("1.02.3", out _, out var error);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Column, Is.EqualTo(2));
        Assert.That(error.Message, Does.Contain("leading zero").IgnoreCase);
        Assert.That(error.Message, Does.Contain("minor").IgnoreCase);
    }

    [Test]
    public void Leading_zero_on_patch_is_pointed_at_the_patch_position()
    {
        // "1.2.03" -> patch "03" starts at offset 4.
        SemVerParser.TryParse("1.2.03", out _, out var error);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Column, Is.EqualTo(4));
        Assert.That(error.Message, Does.Contain("leading zero").IgnoreCase);
        Assert.That(error.Message, Does.Contain("patch").IgnoreCase);
    }

    [Test]
    public void Non_digit_first_character_is_not_mislabeled_as_a_leading_zero()
    {
        // "v1.2.3" fails at the major position because 'v' is not a
        // digit, not because of a leading zero. The leading-zero
        // WithError sits on the Not probe, which fails only on the
        // "0[digit]" shape. On 'v' the Not succeeds and the failure is
        // the OneOrMore's generic one. This is why the WithError is on
        // the Not and not on the major/minor/patch rule as a whole:
        // moving it outward (onto an alias wrapping the whole number,
        // say) would fire it on every failure and mislabel this 'v'.
        SemVerParser.TryParse("v1.2.3", out _, out var error);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Column, Is.EqualTo(0));
        Assert.That(error.Message, Does.Not.Contain("leading zero").IgnoreCase);
    }

    [Test]
    public void Empty_pre_release_after_dash_is_pointed_at_the_dash_position()
    {
        // "1.2.3-" -> the deepest the parser got is offset 6 (just past
        // the '-'), where it expected an identifier and found EOF.
        SemVerParser.TryParse("1.2.3-", out _, out var error);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.EqualTo(6));
        Assert.That(error.Message.ToLowerInvariant(),
            Does.Contain("pre-release").Or.Contain("identifier"));
    }

    [Test]
    public void Empty_identifier_inside_pre_release_is_pointed_at_the_double_dot()
    {
        // "1.2.3-alpha..1" -> the empty identifier is at offset 12.
        SemVerParser.TryParse("1.2.3-alpha..1", out _, out var error);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.EqualTo(12));
    }

    [Test]
    public void Numeric_pre_release_with_leading_zero_is_pointed_at_the_identifier()
    {
        // "1.2.3-01" -> "01" starts at offset 6.
        SemVerParser.TryParse("1.2.3-01", out _, out var error);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.EqualTo(6));
        Assert.That(error.Message, Does.Contain("01"));
        Assert.That(error.Message, Does.Contain("leading zero").IgnoreCase);
    }

    [Test]
    public void Trailing_text_after_valid_version_is_pointed_at_the_first_extra_char()
    {
        // "1.2.3xyz" -> after "1.2.3" the Eof rule fires at offset 5.
        SemVerParser.TryParse("1.2.3xyz", out _, out var error);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.CharIndex, Is.EqualTo(5));
    }

    [Test]
    public void Original_does_not_carry_a_position_on_failure()
    {
        // Sanity check on the value-add: the Original parser's error
        // string is the same shape regardless of where the failure was.
        OriginalSemVerParser.TryParse("01.2.3", out _, out string errorA);
        OriginalSemVerParser.TryParse("1.2.3-", out _, out string errorB);
        OriginalSemVerParser.TryParse("1.2.3-01", out _, out string errorC);

        // All three are the same regex-match-failed message. No position
        // info, no per-cause hint. That's the gap the Rewrite closes.
        Assert.That(errorA, Does.Contain("not a valid semver"));
        Assert.That(errorB, Does.Contain("not a valid semver"));
        Assert.That(errorC, Does.Contain("not a valid semver"));
    }
}
