# Rewrite a real-world GitHub parser using InductorParser (add new backlog items at the top)

**Read this first:** every path you touch as part of this task must be under the worktree root you started in. Backlog item, the new sample directory, any new tests, source fixes, scratch notes. All of it. Details in the "All edits stay inside the worktree you are running in" section below.

This is a real backlog item, if you reach it, do it!

This item should always be the second-to-last item in the backlog, sitting right before the find-a-new-bug item. New backlog items spun off from this work go at the **top** of the backlog with the smallest alpha prefix (e.g. `0001-` or `0a00-`), not somewhere alphabetically before this one. Putting new items at the top keeps the most recent work visible and matches the newest-on-top layout already used by `docs/BugSearchLog/` and `docs/PotentialBugSources/`. The VS Code backlog viewer renumbers on next open if you pick a prefix that needs adjusting. Both this item and find-a-new-bug stay permanently. Don't delete either when you finish a round.

## All edits stay inside the worktree you are running in

Every file you write or modify as part of this task goes inside the worktree you started in. That includes:

- the new sample directory under `E2ESamples/<projectname>/`
- any new test files added under `src/InductorParser.Tests/`
- any new backlog items you spin off in `backlog/`
- any source changes in `src/InductorParser/` if you decide to fix something on the way
- any other notes, scratch files, or run logs you produce while working

If you are running under `InductorCSParserWorktrees/<branch>/`, every path you touch must be under that same `InductorCSParserWorktrees/<branch>/` root. Don't write into the main `InductorCSParser/` checkout. Don't edit files in a sibling worktree. The branch this worktree owns has to carry the complete change, so splitting edits across trees breaks the work and leaves orphaned files in the wrong place.

Before you save anything, double-check the absolute path starts with the worktree root you started in.

## Why this exists

The parser already has a healthy set of internal grammars under `src/InductorParser.Tests/E2EExamples/` (JSON, HTML, CSS, Prolog, Backlog, Chord, Arithmetic). Those are useful for regression testing, but they're written by people who already know the API. They don't tell us what happens when someone takes a parser they wrote (or someone else wrote) for a different framework and has to translate it to InductorParser without prior context. That translation step is where the real friction lives. Awkward APIs, missing convenience methods, error-message gaps, things you wish were documented but aren't, things that take five lines when they should take one. Each round of this task is a fresh opportunity to surface that friction by walking through it with a project we didn't write.

The bar is "real world". We want code that someone actually shipped, not a tutorial example. Bonus points if the original project has tests we can borrow from.

## How to pick a project

Search GitHub for parser implementations and pick one that has all three of these traits:

1. Builds an AST or some structured tree, not just a yes/no recognizer or a tokenizer that returns tokens.
2. Does error handling on invalid input. The original code distinguishes "this input doesn't parse" from "this input does parse" and acts on that distinction.
3. Reports error messages back to a user, ideally with positions. "Expected ',' at line 3, column 12" beats "throw new Exception()".

Good hunting grounds:

- Configuration-file parsers: TOML, INI variants, custom config DSLs, lockfile formats.
- Query-language parsers: small SQL subsets, GraphQL fragments, JMESPath, JSONPath.
- Domain DSLs: a templating language, a markup variant, a build-system file format, a pattern-matching DSL.
- Existing parser-combinator or parser-generator examples from Sprache, Pidgin, Superpower, Parlot, ANTLR, FParsec, Pegasus, nom, lark, or Tree-sitter grammar repos. Picking from these libraries makes the comparison apples to apples.
- Awesome-list aggregators (awesome-csharp, awesome-rust, awesome-haskell) under their parsing sections.

Avoid:

- Toys, course assignments, "calculator in 50 lines" examples. We already have an arithmetic evaluator.
- Formats we already cover (JSON, HTML, CSS, Prolog, Chord, Backlog).
- Massive grammars (a full SQL dialect, a full programming language). Pick something a single person could rewrite in a sitting. If the original is huge, pick a self-contained piece of it that still builds an AST and reports errors.
- Code without a clear license. Stick to MIT, BSD, Apache, or similar permissive licenses, and record the license + upstream URL in the sample's README.

When in doubt, pick the smallest project that still has all three traits. Surfaces friction faster.

Pick something different from prior rounds. List the existing entries under `E2ESamples/` before you start so you don't pick the same kind of thing twice.

## Where to put the sample

Create a new top-level directory `E2ESamples/` at the repo root, sibling to `src/`. Inside it, give your project its own subdirectory.

```
backlog/
  E2ESamples/
    <projectname>/
      README.md
      Original/        <- copied verbatim from upstream, license preserved
      Rewrite/         <- the InductorParser version
      Tests/           <- xUnit/NUnit tests covering both
  src/
  ...
```

The sample is a self-contained .NET project. Wire it into `InductorParser.sln` so it builds with the rest of the repo. Reference `src/InductorParser/InductorParser.csproj` directly via project reference, not via NuGet. We want the sample to break visibly when the library breaks.

`README.md` for the sample should answer:

- What is the original project, who wrote it, what does it parse, and where does it live on GitHub. Link the upstream commit hash you copied from so a future reader can diff.
- What does the original do that we kept. What did we change. What did we drop and why.
- The license of the original code and any attribution it requires.
- A side-by-side line count: original vs. InductorParser rewrite. This is the headline metric of the example.
- A short worked example: input string, AST shape, what the error message looks like for a bad input.

## How to do the rewrite

1. Get the original parser building locally. Read it end to end. Understand the AST, the entry point, the error reporting, and the test corpus before you touch InductorParser.
2. Write the InductorParser grammar in `Rewrite/`. Keep the AST shape as close to the original as reasonable so the comparison stays apples to apples. If the original uses positional error messages, hook up `WithError` and the symbol-position machinery so the rewrite produces comparable diagnostics.
3. Port a representative portion of the original tests into `Tests/`. They should pass against both the original and the rewrite, modulo error-message wording. If the original has no tests, write a small corpus of golden inputs (valid + invalid) before you start the grammar so you have something to verify against.
4. Add a side-by-side error-message comparison test. Pick three or four bad inputs and assert that the rewrite produces an error with a sensible position and a sensible expected-thing. The exact wording can differ from the original, but the position has to be right and the message has to point a user at the actual problem.

## Track everything that hurt

This is the most important part. As you do the rewrite, you will hit things that are unintuitive, surprisingly verbose, missing, buggy, or just slower than they should be. Each one becomes a new backlog item.

Things to watch for and write up:

- A grammar shape that took five or more lines in InductorParser when one line would have been clearer. (Possible factory method missing.)
- An error message we produced that's worse than what the original produced. (Possible `WithError` ergonomics gap or default-message bug.)
- An AST traversal that took repeated `Find`/`Walk` boilerplate. (Possible Symbol convenience method missing.)
- A behavior that surprised us by silently doing the wrong thing on edge input (empty string, all whitespace, BOM, mixed line endings, multi-byte runes, very deep nesting). Write up a failing test under `src/InductorParser.Tests/` and link from the backlog item.
- A piece of the public API where the IntelliSense tooltip didn't tell us what we needed to know. (Doc-comment gap.)
- A piece of the docs where we expected an example and didn't find one. (Doc gap with a concrete example to add.)
- Anything where we had to read the source of `src/InductorParser/` to figure out how to do something a regular user shouldn't need to.

For each one, drop a new `.md` in `backlog/` following the existing format. One issue per backlog item. Title clearly, describe the friction concretely (use the sample's code as the example), and propose what would have made it easy. If you can sketch the API change in three or four lines, do it. Name the file with a prefix that sorts at the **top** of the backlog (smaller than the current top entry's prefix). `0001-` or `0a00-` is fine for the first ones.

If you spot a real bug (not just friction) you can fix it on the spot in `src/InductorParser/`, add a regression test, and reference the fix from the sample's README. That's a strictly better outcome than just filing a backlog item.

## Done when

- `E2ESamples/<projectname>/` exists with `README.md`, `Original/`, `Rewrite/`, and `Tests/` populated.
- The sample is in `InductorParser.sln` and `dotnet build` succeeds at the repo root.
- The rewrite passes the ported test corpus, including at least three error-message cases with positions verified.
- The README includes the line-count comparison and a worked example.
- Every piece of friction that came up during the rewrite is filed as a backlog item, or fixed in place with a regression test, or both.

## Important

*Don't* delete this backlog item when you're done. It stays in the backlog permanently so we keep adding real-world examples and surfacing friction. If you finish a round, the next person picks a different project.
