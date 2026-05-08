// Test fixture commit type. Mirrors the TestCommit in upstream Versionize's
// test suite: subclasses LibGit2Sharp.Commit (the local stub under Original/)
// and overrides Message and Sha so the parser sees concrete values without a
// real git repository.

using LibGit2Sharp;

namespace Versionize.E2ESample.Tests;

public class TestCommit : Commit
{
    public TestCommit(string sha, string message)
    {
        Sha = sha;
        Message = message;
    }
}
