# Error reports should be stable under neutral grammar rewrites

- Where this came up: the 2026-07-10 discussion of where bugs could still hide after 151 recorded hunts.
- The gap: the deepest-failure machinery (depth-primary ranking, forced .WithError overrides, probes that discard lookahead failures, composite failures floored at a rule's start) has produced one-off bugs before: probe leaks, budget aborts losing their position, WithinToken dropping a forced inner flag. Wrong error output doesn't fail existing tests because most tests assert the success path, so this class survives ordinary hunts.
- The property: rewriting a grammar in a way that can't change what it matches shouldn't change what a failing parse reports. Rewrites to check:
    - Wrap any subrule in an unnamed Alias(...) with no .WithError.
    - Wrap the whole grammar in And(x).
    - Replace Exactly(2, x) with And(x, x).
    - Reorder Or alternatives whose first tokens can't overlap.
- For each generated grammar and non-matching input, ErrorCharIndex and ErrorMessage must be identical before and after the rewrite. Divergences are real findings even when both answers look plausible, because callers build editor squiggles and diagnostics from these values. This composes with the differential-fuzzer harness (see backlog 0a01), pointed at failing inputs instead of trees.
- Done when: the rewrite oracles run over generated grammars in the fuzzer harness and any divergence is minimized to a failing test and filed.
