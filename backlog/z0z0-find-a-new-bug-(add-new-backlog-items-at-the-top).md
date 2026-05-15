# Find a new bug (add new backlog items at the top)

**Read this first:** every path you touch as part of this task must be under the worktree root you started in. Backlog item, search log, pattern doc, failing test, source fix, scratch notes — all of it. Details in the "All edits stay inside the worktree you are running in" section below.

This is a real backlog item, if you reach it, do it!

This item should always be the last item in the backlog. New backlog items go at the **top** of the backlog with the smallest alpha prefix (e.g. `0001-` or `0a00-`), not somewhere alphabetically before this one. Putting new items at the top keeps the most recent work visible and matches the newest-on-top layout already used by `docs/BugSearchLog/` and `docs/PotentialBugSources/`. The VS Code backlog viewer renumbers on next open if you pick a prefix that needs adjusting.

## All edits stay inside the worktree you are running in

Every file you write or modify as part of this task goes inside the worktree you started in. That includes:

- the new backlog item under `backlog/`
- the search log entry under `docs/BugSearchLog/`
- the pattern doc (if any) under `docs/PotentialBugSources/`
- the failing test in the appropriate test file
- the source fix in `src/`
- any other notes, scratch files, or run logs you produce while working

If you are running under `InductorCSParserWorktrees/<branch>/`, every path you touch must be under that same `InductorCSParserWorktrees/<branch>/` root. Don't write into the main `InductorCSParser/` checkout. Don't edit files in a sibling worktree. The branch this worktree owns has to carry the complete change, so splitting edits across trees breaks the work and leaves orphaned files in the wrong place.

Before you save anything, double-check the absolute path starts with the worktree root you started in.

Before you start, read the entries under [docs/BugSearchLog/](../docs/BugSearchLog/) for the search log of which files and categories prior hunts have already swept, and the entries under [docs/PotentialBugSources/](../docs/PotentialBugSources/) for the recurring patterns those hunts surfaced. Each entry is its own `.md` file inside the folder. The `.backlog` pointer files next to those folders are just VS Code extension metadata. Don't re-tread the same files for the same patterns unless you have a specific reason (e.g. the code has changed since, or you spot something the prior sweep missed).

## Pick a random starting area before you start reading

Before opening any source file, pick a random number and use it to choose where to start. List the source files under `src/InductorParser/` (skip the `bin`/`obj`/`Compatibility`/`Lexing`/`SyntaxTree`/`Tracing` subdirectory contents you don't immediately care about, but include them later if your starting area is exhausted), then map your random number into that list (`number mod listLength`). Read that file first, side-by-side with sibling rules that share its shape (e.g. if you land on `OneOfRule.cs`, also read `NoneOfRule.cs`, `ScanWhileRule.cs`, `ScanUntilRule.cs` because they share the TokenSet-membership shape). If the area you landed in has already been swept for the obvious categories (check `docs/BugSearchLog/` for that filename), pick a fresh number or follow a thread out into a less-covered area (`Lexer.cs` partials, `TokenSet.cs`, `SyntaxTree/Symbol.cs`, the Compile orchestration in `Rule.cs`, etc.).

The point of randomizing the start is to avoid the natural tendency to keep reviewing the same handful of "interesting-looking" files (OneOf, BetweenInclusive, the recently-touched ones) and to surface bugs in the rules that nobody has thought to stress-test recently. Record the picked number and chosen file in the bug-search-log entry so a future hunter can see what coverage you added.

Read through the source files in `src/` and find one real bug that isn't already covered by an existing backlog item. Be creative with what you are looking for but focus on things users will actually do. If you run out of ideas look for things like: off-by-one errors, edge cases, silent data loss, inconsistent or unexpected behavior, missing edge case handling, incorrect assumptions about input, claims about unicode behavior that aren't backed by a solid citation or are wrong.

The bug must be real and demonstrable with a failing test. "Could theoretically happen" is not good enough. If you can't write a test that fails, it's not a bug.

## How to write the backlog item

Create a new `.md` file in `backlog/` following the same pattern as the other items:

1. Title: short description of the bug as an `- Text` heading
2. A paragraph explaining what's wrong, where in the code it happens (file and line numbers), and what the user-visible consequence is
3. **Verify the Bug (Write Test First)**: a test that goes in the appropriate existing test file (e.g., `utils_tests.mjs` for utils bugs, `format_detector_tests.mjs` for format detector bugs, etc.). Do NOT create a separate `bug_discovery_tests.mjs` file. The test snippet should not include import lines since those already exist at the top of the target file. Show the `npx playwright test` command to run.
4. **Fix**: concrete instructions for what to change in the source
5. **Verify the Fix**: run the same tests, expect them to pass

The filename should follow the `<prefix>-<slug>.md` pattern where the prefix sorts at the top of the backlog (smaller than the current top entry's prefix). `0001-` or `0a00-` is fine for the first ones; pick a prefix slightly smaller than the current top entry for subsequent ones.

## Remove the backlog item once its fix lands

The backlog item you just wrote is the runbook for the fix, not a permanent record. The find-a-bug flow is find, fix, verify, *then delete the backlog item you created*. By the time you finish, the bug is already resolved in the tree (steps 4 and 5 above), so leaving its backlog entry behind just clutters the list with an already-done to-do. Delete the backlog item file as the last step of the fix, in the same commit as the source change.

The lasting record of the hunt is the `docs/BugSearchLog/` entry (next section). That is why the search-log entry is required and the backlog item is not. A backlog item only stays in `backlog/` if it documents a bug you filed but could not fix in the same pass.

The same rule applies to existing items: a backlog item whose bug is already fixed in the tree should be removed.

## Update the bug-hunt docs

Two updates, one in each folder. Both folders use the per-item-per-file layout: each entry is a separate `.md` file in the folder, named `<prefix>-<slug>.md`. The prefix is alphanumeric and the entries sort newest-on-top, so a new entry needs a prefix that sorts before the current top entry's. `1000-` or `0001-` is fine. The VS Code backlog viewer renumbers on next open if you pick something that needs adjusting. The body of each entry follows the bullet format the existing entries use: a top-level `- Title` line, then sub-bullet lines indented under it.

1. **Search log entry (required)** — add a new `.md` file to [docs/BugSearchLog/](../docs/BugSearchLog/) listing the files you reviewed and the categories you checked, plus a one-line note on what (if anything) you fixed. This is the cumulative record that prevents future hunts from repeating your work, so keep it compact. Do not rewrite past entries.
2. **Pattern documentation (if applicable)** — if the bug you found points to a broader category of issues (like "string truncation can split surrogate pairs"), add a new `.md` file to [docs/PotentialBugSources/](../docs/PotentialBugSources/) describing the pattern.

## Important

Do NOT delete *this* backlog item (the find-a-bug item itself) when you're done. It stays in the backlog permanently so we keep finding new bugs. This is the one exception: the per-bug item you create for each hunt does get deleted once its fix lands, as described under "Remove the backlog item once its fix lands" above.
