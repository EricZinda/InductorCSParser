// Ported from upstream Versionize.Tests/ConventionalCommits/ConventionalCommitParserTests.cs
// (https://github.com/versionize/versionize/blob/main/Versionize.Tests/ConventionalCommits/ConventionalCommitParserTests.cs).
// Translated from xUnit + Shouldly to NUnit so this sample doesn't
// have to add Shouldly + Xunit dependencies on top of the harness's NUnit.
// The cases that depend on custom HeaderPatterns / IssuesPatterns are
// dropped here since the rewrite under Rewrite/ doesn't implement custom
// patterns. The default-pattern coverage is preserved so the fixture
// exercises the same shapes the upstream parser was built for.

using NUnit.Framework;
using Versionize.ConventionalCommits;

namespace Versionize.E2ESample.Tests;

[TestFixture]
public class OriginalParserTests
{
    private const string Sha = "c360d6a307909c6e571b29d4a329fd786c5d4543";

    [Test]
    public void ShouldParseTypeScopeAndSubjectFromSingleLineCommitMessage()
    {
        var commit = new TestCommit(Sha, "feat(scope): broadcast $destroy event on scope destruction");

        var parsed = ConventionalCommitParser.Parse(commit);

        Assert.That(parsed.Type, Is.EqualTo("feat"));
        Assert.That(parsed.Scope, Is.EqualTo("scope"));
        Assert.That(parsed.Subject, Is.EqualTo("broadcast $destroy event on scope destruction"));
    }

    [Test]
    public void ShouldUseFullHeaderAsSubjectIfNoTypeWasGiven()
    {
        var commit = new TestCommit(Sha, "broadcast $destroy event on scope destruction");

        var parsed = ConventionalCommitParser.Parse(commit);

        Assert.That(parsed.Subject, Is.EqualTo(commit.Message));
    }

    [Test]
    public void ShouldUseFullHeaderAsSubjectIfNoTypeWasGivenButSubjectUsesColon()
    {
        var commit = new TestCommit(Sha, "broadcast $destroy event: on scope destruction");

        var parsed = ConventionalCommitParser.Parse(commit);

        Assert.That(parsed.Subject, Is.EqualTo(commit.Message));
    }

    [Test]
    public void ShouldParseTypeScopeAndSubjectFromSingleLineCommitMessageIfSubjectUsesColon()
    {
        var commit = new TestCommit(Sha, "feat(scope): broadcast $destroy: event on scope destruction");

        var parsed = ConventionalCommitParser.Parse(commit);

        Assert.That(parsed.Type, Is.EqualTo("feat"));
        Assert.That(parsed.Scope, Is.EqualTo("scope"));
        Assert.That(parsed.Subject, Is.EqualTo("broadcast $destroy: event on scope destruction"));
    }

    [Test]
    public void ShouldExtractCommitNotes()
    {
        var commit = new TestCommit(Sha,
            "feat(scope): broadcast $destroy: event on scope destruction\nBREAKING CHANGE: this will break rc1 compatibility");

        var parsed = ConventionalCommitParser.Parse(commit);

        Assert.That(parsed.Notes, Has.Count.EqualTo(1));
        var note = parsed.Notes[0];
        Assert.That(note.Title, Is.EqualTo("BREAKING CHANGE"));
        Assert.That(note.Text, Is.EqualTo("this will break rc1 compatibility"));
    }

    [TestCase("feat!: broadcast $destroy: event on scope destruction")]
    [TestCase("feat(scope)!: broadcast $destroy: event on scope destruction")]
    public void ShouldSupportExclamationMarkSignifyingBreakingChanges(string message)
    {
        var commit = new TestCommit(Sha, message);

        var parsed = ConventionalCommitParser.Parse(commit);

        Assert.That(parsed.Notes, Has.Count.EqualTo(1));
        Assert.That(parsed.Notes[0].Title, Is.EqualTo("BREAKING CHANGE"));
        Assert.That(parsed.Notes[0].Text, Is.EqualTo(string.Empty));
    }

    [TestCase("fix: subject text #64", new[] { "64" })]
    [TestCase("fix: subject #64 text", new[] { "64" })]
    [TestCase("fix: #64 subject text", new[] { "64" })]
    [TestCase("fix: subject text. #64 #65", new[] { "64", "65" })]
    [TestCase("fix: subject text. (#64) (#65)", new[] { "64", "65" })]
    [TestCase("fix: subject text. #64#65", new[] { "64", "65" })]
    [TestCase("fix: #64 subject #65 text. (#66)", new[] { "64", "65", "66" })]
    public void ShouldExtractCommitIssues(string message, string[] expectedIssues)
    {
        var commit = new TestCommit(Sha, message);

        var parsed = ConventionalCommitParser.Parse(commit);

        Assert.That(parsed.Issues, Has.Count.EqualTo(expectedIssues.Length));
        foreach (var expectedId in expectedIssues)
        {
            var issue = parsed.Issues.SingleOrDefault(x => x.Id == expectedId);
            Assert.That(issue, Is.Not.Null);
            Assert.That(issue!.Token, Is.EqualTo("#" + expectedId));
        }
    }
}
