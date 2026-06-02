# SemVer 2.0.0 sample

A worked end-to-end example of taking a real parser people actually use and rewriting it with InductorParser. Picks the SemVer 2.0.0 spec because the rules are small, well-defined, and almost every C# project on NuGet has a parser for them somewhere.

## What's the original

The "original" is the canonical regex from semver.org's spec page (CC-BY-3.0):

    ^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$

Source: https://semver.org/spec/v2.0.0.html#is-there-a-suggested-regular-expression-regex-to-check-a-semver-string

Almost every C# semver implementation in the wild (NuGet's `SemanticVersion`, WalkerCodeRanger/semver, Ruhrpottpatriot/SemanticVersion, McSherry.SemanticVersioning, the npm-flavored adamreeve/semver.net) wraps this same regex with their own AST. `Original/SemVerRegexParser.cs` is the typical shape: regex match, then turn the capture groups into a typed record (Major / Minor / Patch / pre-release identifiers / build metadata identifiers).

## What the InductorParser version adds

The regex parser tells the caller two things: did it match, and if it did, what are the four capture groups. It does not say where in the input something broke or why. `Rewrite/` keeps the same public API (Parse / TryParse / a typed record) and adds three things the regex can't:

1. Positioned errors. A failure carries a `(charIndex, line, column)` triple, so the user sees `line 1, column 1: major version must not have leading zeros` instead of `'01.2.3' is not a valid semver 2.0.0 version string.`
2. Targeted error messages. The grammar attaches a `WithError` at the spots most likely to confuse a user (the dots between major/minor/patch, the empty-after-`-` and empty-after-`+` cases, and the no-leading-zero rule on each of major/minor/patch). The post-parse validator handles the leading-zero check on numeric pre-release identifiers, which has a mixed numeric/alphanumeric rule shape that doesn't fit the grammar pattern as cleanly.
3. A walkable parse tree. Downstream tooling (a syntax highlighter, a NuGet UI, a refactoring tool) can call `Symbol.Find(SemVerGrammar.PreReleaseIdent)` or walk `tree.FindAll(...)` and operate on the tree directly. The regex parser hands you four flat strings.

## Side-by-side

Same input, both parsers.

```
01.2.3
```

Original: `'01.2.3' is not a valid semver 2.0.0 version string.`
Rewrite: `line 1, column 1: major version must not have leading zeros`

```
1.2.3-
```

Original: `'1.2.3-' is not a valid semver 2.0.0 version string.`
Rewrite: `line 1, column 7: Pre-release identifier expected`

```
1.2.3-alpha..1
```

Original: `'1.2.3-alpha..1' is not a valid semver 2.0.0 version string.`
Rewrite: `line 1, column 13: Pre-release identifier expected`

```
1.2.3-01
```

Original: `'1.2.3-01' is not a valid semver 2.0.0 version string.`
Rewrite: `line 1, column 7: Numeric pre-release identifier '01' must not have leading zeros`

## Layout

```
E2ESamples/SemVer/
  README.md              this file
  SemVer.csproj          one project, both parsers, the test corpus
  Original/
    SemVerRegexParser.cs the regex + projection version
  Rewrite/
    SemVerGrammar.cs     the InductorParser grammar
    SemVerParser.cs      the typed-AST projection
  Tests/
    SemVerTests.cs       golden corpus, reject corpus, error-position cases
```

`SemVer.csproj` is wired into `InductorParser.sln` so `dotnet build` at the repo root builds it, and `dotnet test E2ESamples/SemVer/SemVer.csproj` runs the corpus.

## Lines of code

|                                       | Lines |
| ------------------------------------- | ----- |
| Original/SemVerRegexParser.cs         |   107 |
| Rewrite/SemVerGrammar.cs              |    90 |
| Rewrite/SemVerParser.cs               |   127 |

The Rewrite (217 lines) is about 2x the lines of the regex original (107 lines). The grammar is shorter than the regex parser file. The consumer trades regex's "one match, four capture groups" for a tree walk that picks up positions on every node, plus a post-parse no-leading-zero check on the numeric pre-release identifier, which is what enables the better error messages.

## What this exercise found

Every awkward, missing, or surprising piece of API that came up while writing this got filed as its own backlog item under `backlog/`. The big ones are listed below, with their backlog filenames so you can find them.

The big patterns this rewrite surfaced:

- The natural translation of the "0 or [1-9][0-9]*" no-leading-zero rule into `Or(Token('0'), And(Range('1','9'), ZeroOrMore(Digits)))` lands the error position in the wrong spot. The `Or` commits to the `0` branch, then the next-rule failure looks like a missing `.`. The fix is the reject-first pattern: put a `Not(And(Token('0'), OneOf(Digits)))` probe in front of any consumption. The grammar message lands at the right column without needing a post-parse re-check. See [docs/Recipes.md](../../docs/Recipes.md) for the walkthrough.
- Naming a tree node uses `.As("name")`, which mutates the rule. Sharing a `var numericCore = ...` across major/minor/patch and calling `.As("major")` / `.As("minor")` / `.As("patch")` on the same instance silently makes all three "patch". Easy to miss and the build doesn't catch it.
- A consumer that wants positions for a tree node reads `symbol.SourceRange`, which returns a `SourceRange?` with char-index / line / column / token-index endpoints already translated back to the original input. It's on `Symbol` directly, so a typed-AST projection can embed positions in its records without passing the `ParseResult` around.

## Running it

    dotnet build E2ESamples/SemVer/SemVer.csproj
    dotnet test  E2ESamples/SemVer/SemVer.csproj

73 tests in three fixtures. The golden fixture asserts the Original and Rewrite produce equal AST shapes on every valid input. The reject fixture asserts both reject every invalid input. The error-position fixture asserts the Rewrite points at the right column for the most common kinds of bad SemVer.
