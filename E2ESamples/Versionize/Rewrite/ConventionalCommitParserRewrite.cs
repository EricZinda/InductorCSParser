// InductorParser rewrite of Versionize's ConventionalCommitParser.
// The Original parser under ../Original/ uses regex; this version expresses
// the same grammar with InductorParser rules so we can compare the two
// approaches and surface friction. The result type (ConventionalCommit,
// ConventionalCommitNote, ConventionalCommitIssue) is shared with Original/
// so existing tests assert against the same shape.
//
// What the rewrite covers vs. the upstream parser:
//   * Default header pattern (type, scope, breaking-change marker, subject).
//   * Default issue pattern (#<digits> in subject).
//   * BREAKING CHANGE: notes on body lines.
// Custom HeaderPatterns / IssuesPatterns from CommitParserOptions are NOT
// supported here because they're regex strings; the InductorParser equivalent
// would take a Rule and the API surface is out of scope for this sample.
// The README discusses what that would look like.

using InductorParser;
using InductorParser.SyntaxTree;
using LibGit2Sharp;
using static InductorParser.Rules;

namespace Versionize.ConventionalCommits.Rewrite;

public static class ConventionalCommitParserRewrite
{
    // \w in the upstream regex: ASCII letters, digits, underscore.
    private static readonly TokenSet WordChars =
        TokenSet.Ascii.Letters | TokenSet.Ascii.Digits | TokenSet.Single('_');

    // Each named rule is FlattenType.Preserve so Tree.Find can locate it
    // by reference after the parse. Without Preserve a Flatten / Delete
    // rule's Symbol disappears during tree assembly and Find returns null
    // even though the rule matched.
    public static readonly Rule Type = ZeroOrMore(OneOf(WordChars))
        .As("type")
        .Flatten(FlattenType.Preserve);

    public static readonly Rule Scope = ScanUntil(TokenSet.Single(')'))
        .As("scope");

    public static readonly Rule BreakingMarker = Token('!')
        .As("breaking")
        .Flatten(FlattenType.Preserve);

    // Header is a single line by the time we parse it (we split on newlines
    // first, matching the upstream parser's behavior). ScanUntil with an
    // empty stop set runs to EOF, capturing the whole rest of the input.
    public static readonly Rule Subject = ScanUntil(TokenSet.Empty)
        .As("subject");

    // Preserve so result.Tree returns the single root wrapper Symbol that
    // Tree.Find can walk. The default for And is Flatten, which would lift
    // every named child into a flat top-level Symbols list and leave
    // result.Tree as null per ParseResult.Tree's "exactly one Symbol" rule.
    public static readonly Rule Header = And(
        Type,
        Optional(And(Token('('), Scope, Token(')'))),
        Optional(BreakingMarker),
        Literal(": "),
        Subject,
        Eof()
    ).Flatten(FlattenType.Preserve);

    // Issue extraction grammar applied to the subject text after a header
    // parse succeeds. ZeroOrMore(Or(realMatch, AnyToken-as-Delete)) is the
    // scan-and-collect pattern: keep moving forward, capture every IssueRef,
    // ignore everything else.
    public static readonly Rule IssueId = ScanWhile(TokenSet.Ascii.Digits)
        .As("issueId");

    public static readonly Rule IssueRef = And(Token('#'), IssueId)
        .As("issueRef")
        .Flatten(FlattenType.Preserve);

    public static readonly Rule IssueScanner = And(
        ZeroOrMore(Or(IssueRef, AnyToken().Flatten(FlattenType.Delete))),
        Eof()
    ).Flatten(FlattenType.Preserve);

    static ConventionalCommitParserRewrite()
    {
        Header.Compile();
        IssueScanner.Compile();
    }

    public static ConventionalCommit Parse(Commit commit) => Parse(commit, null);

    public static ConventionalCommit Parse(Commit commit, object? options)
    {
        // options is unused; left in the signature so the test fixture can
        // share assertions with the regex-based original. CommitParserOptions
        // (custom HeaderPatterns / IssuesPatterns) is not implemented here.
        _ = options;

        var conventionalCommit = new ConventionalCommit
        {
            Sha = commit.Sha
        };

        var commitMessageLines = commit.Message
            .Split(["\r\n", "\r", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var headerLine = commitMessageLines.FirstOrDefault();
        if (headerLine == null)
        {
            return conventionalCommit;
        }

        var headerResult = Header.Parse(headerLine);
        if (headerResult.Success)
        {
            string typeText = headerResult.Tree!.Find(Type)?.ToString() ?? string.Empty;
            string scopeText = headerResult.Tree!.Find(Scope)?.ToString() ?? string.Empty;
            string subjectText = headerResult.Tree!.Find(Subject)?.ToString() ?? string.Empty;
            bool hasBreakingMarker = headerResult.Tree!.Find(BreakingMarker) != null;

            conventionalCommit = conventionalCommit with
            {
                Type = typeText,
                Scope = scopeText,
                Subject = subjectText
            };

            if (hasBreakingMarker)
            {
                conventionalCommit.Notes.Add(new ConventionalCommitNote
                {
                    Title = "BREAKING CHANGE",
                    Text = string.Empty
                });
            }

            ExtractIssues(subjectText, conventionalCommit);
        }
        else
        {
            // Same fallback the upstream parser uses when the header doesn't
            // match the structured form: keep the whole header as the subject.
            conventionalCommit = conventionalCommit with
            {
                Subject = headerLine
            };
        }

        for (int i = 1; i < commitMessageLines.Count; i++)
        {
            string line = commitMessageLines[i];
            const string noteKeyword = "BREAKING CHANGE";
            if (line.StartsWith($"{noteKeyword}:"))
            {
                conventionalCommit.Notes.Add(new ConventionalCommitNote
                {
                    Title = noteKeyword,
                    Text = line[$"{noteKeyword}:".Length..].TrimStart()
                });
            }
        }

        return conventionalCommit;
    }

    /// <summary>
    /// Parse the header alone and return a ParseResult with positional error
    /// information. Intended for callers that want to surface "this commit
    /// message is malformed at offset N" diagnostics, which the upstream
    /// regex-based parser can't do.
    /// </summary>
    public static ParseResult ParseHeader(string headerLine) => Header.Parse(headerLine);

    private static void ExtractIssues(string subject, ConventionalCommit commit)
    {
        if (string.IsNullOrEmpty(subject)) return;

        var result = IssueScanner.Parse(subject);
        if (!result.Success || result.Tree == null) return;

        foreach (var issueRefSymbol in result.Tree.FindAll(IssueRef))
        {
            string id = issueRefSymbol.Find(IssueId)?.ToString() ?? string.Empty;
            commit.Issues.Add(new ConventionalCommitIssue
            {
                Token = "#" + id,
                Id = id
            });
        }
    }
}
