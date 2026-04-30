- Update docs to match FlattenType.Preserve rename

The enum value `FlattenType.None` was renamed to `FlattenType.Preserve`
and the `TryParseRule` parameter `successfulChildSymbols` was renamed to
`outputSymbols`. The source code and tests are updated, but these
documents still reference the old names and need a pass:
	- docs/InductorParserReference.md
	- docs/InductorParserDesignDecisions.md
	- src/Benchmarks/README.md

	- backlog/9lll-fixes.md (the existing "Should FlattenType.None be
 	 Preserve" line can be removed once the rest of the pass is done)

Also worth checking: any place that says "None-typed" in prose should
become "Preserve-typed", and the enum-value-meaning commentary in docs
should match the new comments in FlattenType.cs.
