// Side-by-side error reporting comparison. The upstream regex parser has no
// notion of "this commit is malformed at offset N" — when the header doesn't
// match the structured form it silently falls through to "treat the whole
// line as the subject," and the caller never finds out something looked
// almost-but-not-quite right. The InductorParser rewrite exposes a
// ParseHeader entry point that returns a ParseResult with a position and
// (optionally) a WithError-driven message, which is the diagnostic
// improvement the rewrite buys us. These tests pin a few representative
// malformed inputs and the position the rewrite reports for each.

using NUnit.Framework;
using Versionize.ConventionalCommits;
using Versionize.ConventionalCommits.Rewrite;

namespace Versionize.E2ESample.Tests;

[TestFixture]
public class ErrorMessageComparisonTests
{
    private const string Sha = "0000000000000000000000000000000000000000";

    [Test]
    public void Original_silently_drops_malformed_header_to_subject()
    {
        // No colon-space at all. Original treats the whole header as subject
        // with no signal that the input was structurally invalid.
        var commit = new TestCommit(Sha, "feat broadcast $destroy");

        var parsed = ConventionalCommitParser.Parse(commit);

        // Type / Scope stay at their record defaults (null) because the
        // upstream parser never enters the structured branch when the
        // header regex doesn't match, so it never sets them at all.
        Assert.That(parsed.Type, Is.Null);
        Assert.That(parsed.Scope, Is.Null);
        Assert.That(parsed.Subject, Is.EqualTo("feat broadcast $destroy"));
        // No error, no position, no diagnostic. The caller can't tell this
        // input from a deliberately-untyped commit.
    }

    [Test]
    public void Rewrite_reports_position_for_missing_colon_space()
    {
        var result = ConventionalCommitParserRewrite.ParseHeader("feat broadcast $destroy");

        Assert.That(result.Success, Is.False);
        // Type matched "feat" (4 chars); the parser then expected "(", "!", or
        // ": " and saw a space at offset 4.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(4));
    }

    [Test]
    public void Rewrite_reports_position_for_unclosed_scope()
    {
        var result = ConventionalCommitParserRewrite.ParseHeader("feat(scope: broadcast");

        Assert.That(result.Success, Is.False);
        // ScanUntil(')') keeps scanning past ':' since neither ':' nor any
        // later char is ')'. It runs to EOF, then the closing Token(')')
        // fails at the end-of-input position. Report ErrorCharIndex
        // somewhere inside / at the end of the input rather than the
        // generic "string did not match" the regex would give.
        Assert.That(result.ErrorCharIndex, Is.GreaterThan(0));
    }

    [Test]
    public void Rewrite_reports_position_for_colon_without_space()
    {
        // "feat: foo" matches; "feat:foo" (no space after colon) does not
        // match the upstream regex either. Both engines fail. The rewrite
        // gives a position; the original silently falls back to subject-only.
        var commit = new TestCommit(Sha, "feat:foo");

        var originalResult = ConventionalCommitParser.Parse(commit);
        var rewriteResult = ConventionalCommitParserRewrite.ParseHeader("feat:foo");

        // Original: no signal of malformedness. Type stays null (default
        // record value) because the regex didn't match, the structured
        // branch never ran, and Subject is the whole header.
        Assert.That(originalResult.Type, Is.Null);
        Assert.That(originalResult.Subject, Is.EqualTo("feat:foo"));

        // Rewrite: failure with a position. Type matched "feat" (4 chars),
        // then Literal(": ") read the ':' and failed on 'f' at offset 5
        // where it wanted a space. The position points at the offending
        // character, which is the diagnostic the original silently swallows.
        Assert.That(rewriteResult.Success, Is.False);
        Assert.That(rewriteResult.ErrorCharIndex, Is.EqualTo(5));
    }
}
