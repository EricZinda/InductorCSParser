// Stub for the LibGit2Sharp.Commit base type the upstream parser receives.
// LibGit2Sharp ships native git binaries. Pulling it as a NuGet dependency
// just to expose two strings (Message and Sha) would dwarf the sample.
// The original ConventionalCommitParser only ever reads commit.Message and
// commit.Sha, and the upstream test suite already subclasses LibGit2Sharp.Commit
// with a TestCommit that overrides those two members. So a minimal
// stand-in with the same two virtuals carries the parser unchanged.
//
// This is the ONLY edit to the upstream parser sources copied alongside.
// Everything else under Original/ is verbatim from Versionize's main branch.

namespace LibGit2Sharp;

public class Commit
{
    public virtual string Sha { get; protected set; } = string.Empty;
    public virtual string Message { get; protected set; } = string.Empty;
}
