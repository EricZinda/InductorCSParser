- Find a new bug (add new backlog items before this)

This is a real backlog item, if you reach it, do it!

This item should always be the last item in the backlog. Add new backlog items before it, not after.

Before you start, read [docs/BugSearchLog.backlog](../docs/BugSearchLog.backlog) for the search log of which files and categories prior hunts have already swept, and [docs/PotentialBugSources.backlog](../docs/PotentialBugSources.backlog) for the recurring patterns those hunts surfaced. Don't re-tread the same files for the same patterns unless you have a specific reason (e.g. the code has changed since, or you spot something the prior sweep missed).

Read through the source files in `src/` and find one real bug that isn't already covered by an existing backlog item. Be creative with what you are looking for but focus on things users will actually do. If you run out of ideas look for things like: off-by-one errors, edge cases, silent data loss, inconsistent or unexpected behavior, missing edge case handling, incorrect assumptions about input, claims about unicode behavior that aren't backed by a solid citation or are wrong.

The bug must be real and demonstrable with a failing test. "Could theoretically happen" is not good enough. If you can't write a test that fails, it's not a bug.

## How to write the backlog item

Create a new `.md` file in `backlog/` following the same pattern as the other items:

1. Title: short description of the bug as an `- Text` heading
2. A paragraph explaining what's wrong, where in the code it happens (file and line numbers), and what the user-visible consequence is
3. **Verify the Bug (Write Test First)**: a test that goes in the appropriate existing test file (e.g., `utils_tests.mjs` for utils bugs, `format_detector_tests.mjs` for format detector bugs, etc.). Do NOT create a separate `bug_discovery_tests.mjs` file. The test snippet should not include import lines since those already exist at the top of the target file. Show the `npx playwright test` command to run.
4. **Fix**: concrete instructions for what to change in the source
5. **Verify the Fix**: run the same tests, expect them to pass

The filename should follow the `NNN-slug.md` pattern where NNN is the next available alphanumeric.

## Update the bug-hunt docs

Two updates, one for each doc:

1. **Search log entry (required)** — append a terse dated entry to [docs/BugSearchLog.backlog](../docs/BugSearchLog.backlog) listing the files you reviewed and the categories you checked, plus a one-line note on what (if anything) you fixed. This is the cumulative record that prevents future hunts from repeating your work, so keep it compact. Do not rewrite past entries.
2. **Pattern documentation (if applicable)** — if the bug you found points to a broader category of issues (like "string truncation can split surrogate pairs"), add a new item to [docs/PotentialBugSources.backlog](../docs/PotentialBugSources.backlog) describing the pattern.

## Important

Do NOT delete this backlog item when you're done. It stays in the backlog permanently so we keep finding new bugs.
