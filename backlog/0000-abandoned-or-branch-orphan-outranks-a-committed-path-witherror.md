# Depth-primary ranking lets an abandoned Or branch's orphan failure outrank a committed-path WithError

The test `WithinToken_WithError_surfaces_over_deeper_orphan_from_abandoned_Or_alternative` in [WithinTokenRuleTests.cs:494](src/InductorParser.Tests/Rules/WithinTokenRuleTests.cs#L494) fails on the current tree. It expects `ErrorCharIndex == 1` with `ErrorMessage == "expected b in WithinToken"`. The parse instead reports `ErrorCharIndex == 3` with `"expected z at end"`.

The grammar is `And(Or(And(AnyToken, AnyToken, AnyToken, Token('z').WithError("expected z at end")), Token('a').Delete()), WithinToken(Token('b')).WithError("expected b in WithinToken"))` parsed against `"axyw"`. The `Or`'s first alternative reads three tokens and fails at offset 3, recording the named failure `"expected z at end"` at offset 3. The `Or` then commits to alternative 2 (`Token('a').Delete()`), consuming offset 0. The parse continues and `WithinToken(Token('b')).WithError("expected b in WithinToken")` runs at offset 1, fails because the next token is `'x'`, and records its own named WithError at offset 1.

By then the three-slot tracker's named slot already holds `"expected z at end"` at offset 3 from the abandoned alternative. `Lexer.RecordFailure`'s named-slot rule ([Lexer.cs:538](src/InductorParser/Lexing/Lexer.cs#L538)) only overwrites the slot on a strictly-deeper position, so the offset-1 WithError can't claim it. `DeepestFailure` / `DeepestFailureMessage` ([Lexer.cs:173](src/InductorParser/Lexing/Lexer.cs#L173)) then resolve depth-primary (`NamedPosition >= MechanicalPosition`) and surface the offset-3 orphan.

User-visible consequence: a WithError the user attached to a rule the parser genuinely reached on its committed path is shadowed by a stale message a sibling `Or` alternative left behind on a path the parser abandoned, and the reported position (offset 3) sits inside input the `Or` already committed past via alternative 2. The user is told `"expected z at end"` at a position the parse never actually got stuck on.

This is a regression. The test's own commit (`78420fc`, "Test: WithinToken WithError wins over deeper orphan failure") records that it passed against the three-tier error-resolution rework (`db08046`). It was regressed by `8a78e72` ("Feature: depth-primary error ranking with composite anchoring", closes backlog `0a06`), which landed the depth-primary resolution model. The depth-primary model and this regression test are in direct conflict.

## Verify the Bug

The regression test already exists, no new test is needed:

```
dotnet test --filter "FullyQualifiedName~WithinTokenRuleTests.WithinToken_WithError_surfaces_over_deeper_orphan_from_abandoned_Or_alternative"
```

Pre-fix it fails with `Expected: 1 / But was: 3`.

## Fix

This is a design conflict and needs a decision, not a mechanical patch:

- Option A: the depth-primary model is intended to supersede the test. A failure from an abandoned `Or` alternative is treated as a legitimate deepest near-miss, and `78420fc`'s test should be updated or removed to match. The cost is that a user's WithError no longer reliably surfaces at the rule the parser hit, which WithError and `docs/ErrorArchitecture.md` otherwise promise.

- Option B: the depth-primary model shouldn't let a failure from an `Or` alternative the parser ABANDONED outrank a failure recorded afterward on the alternative it COMMITTED to. When `OrRule` commits to a branch ([OrRule.cs:70](src/InductorParser/OrRule.cs#L70)), records left by the rejected branches are stale relative to anything the committed path records later. The fix would scope or discount those abandoned-branch records so a later committed-path failure can still claim the named slot. Compare against the `db08046` behavior the test passed under (where WithinToken's WithError reached the forced slot) to see which records the depth-primary model now treats as live when it shouldn't.

The related pattern is already noted under `docs/PotentialBugSources/0000b-shallower-witherror-vs-deeper-orphan-deepest.md`.

## Verify the Fix

Re-run the filter above. Whichever option is chosen, the full `InductorParser.Tests` suite must end green on both engines (`./test.sh both`).
