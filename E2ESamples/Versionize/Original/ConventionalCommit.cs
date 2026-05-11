// Copied verbatim from Versionize main branch (commit fetched 2026-05-08):
// https://github.com/versionize/versionize/blob/main/Versionize/ConventionalCommits/ConventionalCommit.cs
// Licensed under the MIT License. See ../LICENSE-Versionize.

namespace Versionize.ConventionalCommits;

public sealed record ConventionalCommit
{
    public string? Sha { get; init; }

    public string? Scope { get; init; }

    public string? Type { get; init; }

    public string? Subject { get; init; }

    public List<ConventionalCommitNote> Notes { get; init; } = [];

    public List<ConventionalCommitIssue> Issues { get; init; } = [];

    public bool IsFeature => Type == "feat";
    public bool IsFix => Type == "fix";
    public bool IsBreakingChange => Notes.Any(note => "BREAKING CHANGE".Equals(note.Title));
}

public sealed class ConventionalCommitNote
{
    public string? Title { get; init; }

    public string? Text { get; init; }
}

public sealed class ConventionalCommitIssue
{
    public string? Token { get; init; }

    public string? Id { get; init; }
}
