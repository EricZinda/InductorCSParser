# Versionize: ConventionalCommit parser, rewritten with InductorParser

This sample takes the conventional-commits parser embedded in
[Versionize](https://github.com/versionize/versionize), a real CLI tool for
automating semantic versioning and CHANGELOG generation, and rewrites it on
top of `InductorParser`. Versionize itself is not a parser library.
Parsing commit messages is one piece of a larger workflow that also bumps
versions in csproj/json files, writes changelog entries, and tags git.
That makes it a fit for the "parsing as an implementation detail" angle.

Upstream commit fetched: 2026-05-08.
Upstream license: MIT (see [LICENSE-Versionize](LICENSE-Versionize), Copyright 2018 Christoph Walcher).

## What's here

```
Versionize.E2ESample.csproj
LICENSE-Versionize
Original/
  ConventionalCommitParser.cs   verbatim from upstream
  ConventionalCommit.cs         verbatim from upstream
  CommitParserOptions.cs        verbatim from upstream
  LibGit2SharpStub.cs           the only non-upstream file (see below)
Rewrite/
  ConventionalCommitParserRewrite.cs   InductorParser version
Tests/
  TestCommit.cs                 mirrors upstream's TestCommit fixture
  OriginalParserTests.cs        ported from upstream's xUnit suite
  RewriteParserTests.cs         same scenarios, exercising the rewrite
  ErrorMessageComparisonTests.cs   side-by-side error reporting comparison
```

The Original/ folder is otherwise byte-identical to the upstream files at
the time of the fetch. The one stub file replaces the `LibGit2Sharp.Commit`
base type with a minimal stand-in carrying just `Sha` and `Message`. That
keeps the sample from pulling in `LibGit2Sharp`'s native git binaries
(several megabytes per platform) just to expose two strings the parser
reads. The upstream test fixture already subclasses `Commit` to override
those two properties, so the swap is transparent.

The `Rewrite/` folder skips two upstream features:

- Custom `HeaderPatterns` and `IssuesPatterns`. These are user-supplied
  regex strings the original passes through to `Regex.Match`. The
  InductorParser equivalent would take a `Rule` instead, but designing
  the public API for that is out of scope for the sample.
- The original returns Type / Scope as `null` when the header doesn't
  match the structured form (the record's defaults, never assigned).
  The rewrite returns empty strings in that case. Tests cover both.

## Line count comparison

The rewrite is slightly longer than the original, mostly because it adds
a `ParseHeader` entry point that surfaces positional errors the original
can't:

|                                  | Lines | Code-only |
|----------------------------------|-------|-----------|
| Original `ConventionalCommitParser.cs` | 130   | 107       |
| Rewrite `ConventionalCommitParserRewrite.cs` | 190   | 121       |

"Code-only" excludes blank lines and lines starting with `//`. The
~13 percent gap is the diagnostic entry point. The grammar core itself
(rules + Parse method body) is comparable to the regex-plus-handler in
the original.

## What the rewrite gets that the original doesn't

The original parser is regex-driven. When the header doesn't match the
structured form (`<type>(<scope>)?<!>?: <subject>`), the regex silently
returns no match and the code falls through to "use the whole header as
the subject." The caller never finds out the input was structurally
invalid, only that there's no `Type` or `Scope`.

The rewrite's grammar is the same shape, but `ParseHeader(line)` returns
a `ParseResult` with `Success`, `ErrorCharIndex`, and an error message.
For `feat:foo` (missing the space after the colon), the rewrite
reports failure at offset 5: that's the position of the `f` in `foo`,
where `Literal(": ")` had read the `:` and then expected a space. The
original swallows that case as "subject = `feat:foo`."

`Tests/ErrorMessageComparisonTests.cs` pins this difference for three
malformed inputs.

## Worked example

```csharp
var commit = new TestCommit("abc123", "feat(scope)!: broadcast $destroy event\nBREAKING CHANGE: this will break rc1");

var parsed = ConventionalCommitParserRewrite.Parse(commit);

parsed.Type;        // "feat"
parsed.Scope;       // "scope"
parsed.Subject;     // "broadcast $destroy event"
parsed.IsBreakingChange;  // true
parsed.Notes[0];    // { Title = "BREAKING CHANGE", Text = "" }    (from "!")
parsed.Notes[1];    // { Title = "BREAKING CHANGE", Text = "this will break rc1" }
```

For a malformed header:

```csharp
var result = ConventionalCommitParserRewrite.ParseHeader("feat broadcast $destroy");

result.Success;          // false
result.ErrorCharIndex;   // 4 — position of the space, where ": " was expected
result.ErrorMessage;     // "Parse failed at offset 4: unexpected ' '."
```

## Running the tests

From the repo root:

```
dotnet test E2ESamples/Versionize/Versionize.E2ESample.csproj
```

32 tests, no skips. The OriginalParserTests fixture exercises the upstream
parser, RewriteParserTests exercises the rewrite against the same shapes,
and ErrorMessageComparisonTests pins the diagnostic improvement.

## Friction surfaced during the rewrite

Each item below was filed as a separate backlog entry. They're the kinds
of things "real-world parser rewrite" rounds are meant to surface.

- **Default-Flatten root produces null `result.Tree`**. `And(...)` returns
  `FlattenType.Flatten` by default, which lifts every named child into a
  flat top-level list and leaves `result.Tree == null`. The first attempt
  at the rewrite hit a `NullReferenceException` from `Tree!.Find(...)`
  before this was understood. A grammar that wants to use `result.Tree`
  has to remember to add `.Flatten(FlattenType.Preserve)` to the root.
  See [backlog/dvbq-default-flatten-root-produces-null-tree.md](../../backlog/dvbq-default-flatten-root-produces-null-tree.md).
- **`Tree.Find(rule)` only works when `rule.FlattenType == Preserve`**.
  For a rule that defaults to `Delete` (every `Token`, `Literal`,
  `Eof`, `Not`, `Peek`) or `Flatten` (every count rule), `Tree.Find`
  returns `null` because the rule's wrapper Symbol was filtered out
  during tree assembly. This is correct given the FlattenType semantics,
  but it's surprising for a grammar author writing
  `BreakingMarker = Token('!').As("breaking")` and expecting `Find`
  to confirm the marker matched. See
  [backlog/4hwn-find-only-works-on-preserve-rules.md](../../backlog/4hwn-find-only-works-on-preserve-rules.md).
- **`ScanUntil(TokenSet.Empty)` for "match rest of input"**. The
  Subject grammar wants "consume everything to end of input." The
  natural way is `ScanUntil(emptyStopperSet)`, which works because
  `ScanUntil` falls out at EOF, but reading the rule it's not obvious
  that an empty stop-set means "scan to EOF." A `ScanToEof()` /
  `RestOfInput()` factory would document the intent better.
  See [backlog/m9k4-scantoeof-factory-missing.md](../../backlog/m9k4-scantoeof-factory-missing.md).
