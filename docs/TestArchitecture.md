# Test Architecture

This doc describes what makes a rule's test file "comprehensive" in this codebase. It's aimed at contributors adding a new rule or auditing coverage of an existing one. Use it as a checklist.

Tests live in `src/InductorParser.Tests/`, organized into three subfolders:

- `Rules/` — one file per rule (`CharRuleTests.cs`, `RuneInRuleTests.cs`, `AndRuleTests.cs`, etc.), each named after the rule type with a `Tests` suffix.
- `Core/` — cross-cutting concerns that don't belong to any one rule (`WithErrorTests.cs`, `LexerSwitchTests.cs`, `IdAssignmentTests.cs`, `RuneSetTests.cs`). Files are named after the concern.
- `E2EExamples/` — end-to-end grammar tests that exercise full grammars built from the public API (e.g. `SettingExampleTests.cs`).

Test files in all three folders share the same `namespace InductorParser.Tests;`, so the folder layout is a discoverability convention, not a namespace boundary.

Related docs:

- [ProgrammingModel.md](ProgrammingModel.md) — the error-position principle and deepest-failure-wins semantics the tests pin down.
- [ProgrammingAGrammar.md](ProgrammingAGrammar.md) — the public API tests exercise.

## Universal Requirements

Every rule's test file, regardless of rule type, should cover these four categories. If any is missing, the coverage isn't comprehensive.

**1. Success.** At least one test that parses input the rule accepts and verifies `result.Success` is true. Where the rule produces a Symbol the caller can inspect, assert the Symbol's shape (id, text via `ToString()`, children as applicable).

**2. Failure position.** At least one test that parses input the rule rejects and asserts `result.ErrorCharIndex`. Position matters because it pins the error-position principle — the failing read's pre-read position — against regression. Without this assertion, a rule can silently drift back to post-read semantics without the test suite noticing.

**3. Failure message propagation.** At least one test where the rule has a `.WithError("...")` set and the parse failure surfaces that exact message via `result.ErrorMessage`. This pins the equal-depth message-claim path in `RecordFailure` that lets rule authors attach user-friendly messages. Assert with `Is.EqualTo(...)`, not `Does.Contain(...)` — contain-based assertions pass accidentally when the wrong message happens to share a substring.

**4. At least one test without WithError.** To pin the positional-fallback path in `BuildErrorMessage`. Without this, the fallback code could break silently. One `Does.StartWith("Unexpected end of input")` or `Does.StartWith("Parse failed at offset")` test per rule file is enough.

## Per-Rule-Type Requirements

### Single-Token Primitive Rules

Rules that call `lexer.Read()` exactly once. Today: `RuneInRule`, `EofRule` (which doesn't actually read but checks `lexer.IsEof`). The single-rune case of `CharRule` behaves the same way.

Required tests:

- **EOF on empty input.** Pass `""` or the rule under a constructed context where EOF arrives immediately. Assert `ErrorCharIndex == 0`. Confirms the rule records at `transaction.StartPosition` (pre-read) rather than post-read, and that the position sits at offset 0 where `input[offset]` would crash but `offset == input.Length` is the legitimate EOF case.
- **Mismatch at start.** Pass input whose first token the rule rejects. Assert `ErrorCharIndex == 0`.
- **Mismatch after successful matches.** Construct a composite (e.g., `And(otherRule, thisRule)`) where an earlier rule consumes some input before this rule fails. Assert the failure is at the consumed-to-position, not at 0. The first two tests both expect position 0, so a buggy rule that hard-codes 0 (or uses the outer parse's start instead of its own pre-read position) would still pass them. This test is what proves the rule is actually tracking where its failing read started.

Example (from `RuneInRuleTests.cs`):

```csharp
[Test]
public void RuneIn_mismatch_after_successful_matches_points_at_first_bad_char()
{
    var rule = And(OneOrMore(RuneIn(RuneSet.Letters)),
                   Char(';').WithError("expected ';'"));
    var result = rule.Parse("abc1");
    Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
    Assert.That(result.ErrorMessage, Is.EqualTo("expected ';'"));
}
```

### Multi-Token Primitive Rules

Rules that read multiple tokens in a lockstep loop. Today: `CharRule` for multi-rune graphemes. Future: `Literal`.

Required tests beyond single-token coverage:

- **Mismatch on the first token.** Assert position is at the very start (offset 0 for a top-level rule).
- **Mismatch on a later token.** Construct input that matches the first N-1 tokens successfully then diverges. Assert position equals the start of the Nth token (where `tokenStart` was captured in the Nth iteration), not the start of the whole match (offset 0) and not post-read (offset of the token after the failure).
- **Both lexer modes where applicable.** For rules whose behavior changes between `GraphemeLexer` and `RuneLexer`, include at least one test under each via `new ParseOptions { InputUnit = InputUnit.Rune }`.

Example (from `CharRuleTests.cs`):

```csharp
[Test]
public void Char_multi_rune_mismatch_on_second_token_reports_at_second_token_start()
{
    var rule = Char("\uD83D\uDC4B\uD83C\uDFFD").WithError("expected wave");
    var result = rule.Parse("\uD83D\uDC4Bxy",
        new ParseOptions { InputUnit = InputUnit.Rune });
    Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    Assert.That(result.ErrorMessage, Is.EqualTo("expected wave"));
}
```

### Composite Rules

Rules that wrap other rules and combine their results: `AndRule`, `OrRule`, `OneOrMoreRule`, `ZeroOrMoreRule`, `OptionalRule`.

Required tests beyond universal coverage:

- **First-child failure.** Pass input the first child rejects. Assert the failure position comes from the first child's pre-read offset (usually 0 for a top-level test). Use different `WithError` messages on each child and assert the *correct* child's message appears, not just "something failed."
- **Later-child failure.** Pass input the first child (or first several) accept, then the next child rejects. Assert the position is the later child's pre-read offset. Again with per-child `WithError` to pin which child's message surfaces.
- **Children that consume different amounts before failing.** Specifically for Or, construct alternatives where different branches advance different distances before failing. Assert the deepest-advancing branch's message wins (deepest-failure-wins) and position.
- **Edge cases specific to the combinator.** `OneOrMore` needs a "no matches" test. `Optional` needs a "inner fails, Optional succeeds with empty" test plus the known-PEG-quirk test where an Optional's inner depth beats the required rule's shallower depth. `ZeroOrMore` has no failure path at all — it always succeeds — so it needs zero-match and N-match success tests but no error-position tests.

Example (from `AndRuleTests.cs`):

```csharp
[Test]
public void And_later_child_failure_reports_at_deeper_position()
{
    var rule = And(Char('a').WithError("need an 'a'"),
                   Char('b').WithError("need a 'b'"));
    var result = rule.Parse("ax");
    Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    Assert.That(result.ErrorMessage, Is.EqualTo("need a 'b'"));
}
```

### Rules with Construction-Time Validation

Any rule (or factory) that validates its arguments and throws at build time. Today: `Char(char/Rune/int/string)` rejects surrogates, out-of-range values, multi-grapheme strings. `RuneSet.Single`/`Range`/`Runes` reject invalid scalar values.

Required tests:

- **Each validation path, one test.** `Assert.Throws<ArgumentOutOfRangeException>(() => Rule.Factory(badValue))`. Cover each documented failure mode (surrogate, out-of-range-low, out-of-range-high, multi-element where single required, empty where non-empty required).
- **Boundary values.** Test the exact boundary: `0xD800` (first surrogate), `0xDFFF` (last surrogate), `0x10FFFF` (last valid), `0x110000` (first out-of-range), empty string, single-element string.
- **Valid inputs adjacent to boundaries.** Show that the rule doesn't over-reject: `0xD7FF` (last before surrogate block) and `0xE000` (first after surrogate block) should build successfully.

### Lookahead and Forwarding Rules

Rules that don't consume input or simply delegate: future `Not`, `Peek`, current `LateBoundRule`.

Required tests:

- **Zero-consumption.** Assert that parsing past the lookahead rule lands at the same position as before it ran. For `Peek(x).Parse(input)` followed by some tracking of where the lexer is, position should not have advanced.
- **Forwarding correctness.** For `LateBoundRule`, tests should cover both bound and unbound states, and the expected error when `.Bind(...)` was skipped.
- **No modifier acceptance where forbidden.** `LateBoundRule` specifically rejects `.As`, `.Flatten`, `.WithError` because it's transparent at parse time and those modifiers would silently do nothing. If another rule has similar no-ops, its tests should pin that too.

### Cross-Cutting Concerns (Not Per-Rule)

Some tests don't belong to any one rule's file. These live in `Core/`:

- **Lexer-mode switching** → `Core/LexerSwitchTests.cs`. Tests that exercise `ParseOptions.InputUnit` switching.
- **WithError deepest-failure across multiple rules** → `Core/WithErrorTests.cs`. Tests that build grammars spanning several rules and assert the right message wins across them.
- **Id assignment (Compile)** → `Core/IdAssignmentTests.cs`. Tests that verify the three-pass id assignment (pinned, named-hash, anonymous) behaves correctly.
- **RuneSet behavior** → `Core/RuneSetTests.cs`. Tests for the `RuneSet` data type itself (not its consumers like `RuneInRule`).

When you write a test that primarily exercises one of these concerns, put it in the corresponding file, not in a rule-specific file. When a test exercises a rule but happens to touch a cross-cutting concern, put it in the rule's file and keep the cross-cutting concern under test as a secondary focus.

End-to-end grammars built from the public API live in `E2EExamples/`. Examples there serve a dual purpose: they catch regressions in cross-rule interactions, and they double as documentation for what a real grammar in this library looks like. New examples should reflect realistic use cases, not just exercise rules already covered in `Rules/`.

## File Organization

One test fixture per rule, in `Rules/`. File naming follows the rule's type name plus `Tests`: `Rules/CharRuleTests.cs` → `CharRule.cs`. Cross-cutting files in `Core/` are named after the concern (`WithErrorTests.cs`, `LexerSwitchTests.cs`).

Tests inside a fixture are ordered loosely by category: success paths first, failure-position tests next, WithError-message tests after that, then edge cases and construction-time validation. This isn't enforced by tooling — it's a readability convention.

Each test method's name should describe the scenario, not the expected outcome. `Char_mismatch_on_single_char_input_points_at_offender` beats `Char_should_fail_correctly`. Reading the fixture's method list tells you what cases are covered without opening any body.

## Anti-Patterns

**Substring-matching on error messages.** `Does.Contain("'x'")` passes when the right message happens to include `'x'` but also when a completely unrelated failure happens to have `'x'` in it. Prefer `Is.EqualTo(...)` with a concrete WithError-attached message. Only fall back to substring when the message genuinely has variable content (like the positional fallback message with its dynamic offset).

**Failure tests without `ErrorCharIndex` assertions.** Every parse-failure test should pin the position. Without it, the error-position principle can regress silently.

**Tests that only assert `result.Success`.** `Assert.That(result.Success, Is.False)` is a sanity check but a weak one. Asserting position and message in the same test catches order-of-magnitude-more regressions for the same test-maintenance cost.

**Multiple unrelated assertions in one test.** If a single test verifies success-path behavior AND failure-path behavior AND construction validation, it's doing the work of three tests and will produce confusing failures. Prefer small focused tests.

**Testing implementation details instead of observable behavior.** Don't assert on private fields or internal state. If the test passes when you change the behavior in a way the user can observe, the test is wrong. If the test fails when you refactor internals without changing observable behavior, the test is brittle.

## Running the Suite

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj
```

The test project targets net8.0 and consumes the net8.0 build of the library. The netstandard2.1 build compiles but isn't exercised by `dotnet test` (that requires a Unity or NativeAOT pipeline, tracked in backlog i002). Assume that a clean suite run on net8.0 is the gate for landing a change.
