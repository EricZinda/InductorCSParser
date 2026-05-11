// Copied verbatim from Versionize main branch (commit fetched 2026-05-08):
// https://github.com/versionize/versionize/blob/main/Versionize/Config/CommitParserOptions.cs
// Licensed under the MIT License. See ../LICENSE-Versionize.

namespace Versionize.Config;

public sealed class CommitParserOptions
{
    public static readonly CommitParserOptions Default = new();

    public string[] HeaderPatterns { get; init; } = [];

    public string[] IssuesPatterns { get; init; } = [];

    public static CommitParserOptions MergeWithDefault(CommitParserOptions? customOptions)
    {
        if (customOptions == null)
        {
            return Default;
        }

        return new CommitParserOptions
        {
            HeaderPatterns = customOptions.HeaderPatterns ?? Default.HeaderPatterns,
            IssuesPatterns = customOptions.IssuesPatterns ?? Default.IssuesPatterns,
        };
    }
}
