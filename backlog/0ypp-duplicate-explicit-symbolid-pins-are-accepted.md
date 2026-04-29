- Duplicate explicit SymbolId pins are accepted

Docs currently describe the behavior as intentional, but this should be treated as a bug. If two reachable rules are both pinned with the same explicit `SymbolId`, `Compile()` keeps both IDs. That makes any raw-`SymbolId` parse-tree lookup ambiguous, and `Rule.NameOf(SymbolId)` currently lets the later graph walk entry overwrite the earlier one in its reverse-name map.

Current documentation/comments that reference the behavior:

`docs/InductorParserReference.md:131` says explicit pins are trusted and two user-pinned rules with the same id are not rejected.

`docs/InductorParserDesignDecisions.md:83` says the same thing in the compile/validation design section.

`src/InductorParser/Rule.cs:308` has a source comment saying that if two rules pin the same `SymbolId`, both keep it.

Code path to fix:

`src/InductorParser/Rule.cs:328` calls `CollectPinnedIds(...)` during `Compile()`.

`src/InductorParser/Rule.cs:670` defines `CollectPinnedIds(...)`.

`src/InductorParser/Rule.cs:673` calls `usedIds.Add(r.Id.Value)` and ignores the return value. It should detect `false` and throw a clear compile-time exception for duplicate explicit pins.

`src/InductorParser/Rule.cs:406` shows one fallout path: `BuildNameIndex()` assigns `map[r.Id] = ...`, so duplicate IDs collapse to whichever rule is visited later.

Acceptance notes:

Add a test in `src/InductorParser.Tests/Core/IdAssignmentTests.cs` that two reachable `.As(new SymbolId(...))` rules with the same id fail at compile time.

Keep existing behavior where named and anonymous rules probe away from already claimed explicit pins.

After the fix, update the two docs above and the `Rule.cs` comment to say duplicate explicit pins are rejected.

Original scratch notes about `Token(string)` under `RuneLexer` were checked separately: a single expected grapheme can consume multiple rune tokens under `RuneLexer`, so that behavior is accurate and does not belong to this bug.
