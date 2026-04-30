# Test Architecture

Run the test suite via `./test.sh` at the repo root. By default it runs the recursive engine; pass `statemachine` to run under the state-machine engine, or `both` to run both sequentially. Extra arguments pass through to `dotnet test`, so `./test.sh statemachine --filter "FullyQualifiedName~Atom_fragment"` targets one fixture under the SM.

This doc describes what makes a rule's test file "comprehensive" in this codebase. It's aimed at contributors adding a new rule or auditing coverage of an existing one. Use it as a checklist.

Tests live in `src/InductorParser.Tests/`, organized into three subfolders:

- `Rules/`: one file per rule (`GraphemeRuleTests.cs`, `OneOfRuleTests.cs`, `AllOfRuleTests.cs`, etc.), each named after the rule type with a `Tests` suffix.
- `Core/`: cross-cutting concerns that don't belong to any one rule (`WithErrorTests.cs`, `LexerSwitchTests.cs`, `IdAssignmentTests.cs`, `RuneSetTests.cs`). Files are named after the concern.
- `E2EExamples/`: end-to-end grammar tests that exercise full grammars built from the public API (e.g. `SettingExampleTests.cs`).

Test files in all three folders share the same `namespace InductorParser.Tests;`, so the folder layout is a discoverability convention, not a namespace boundary.

Related docs:

- [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md): the error-position principle and deepest-failure-wins semantics the tests lock in.
- [InductorParserReference.md](InductorParserReference.md): the public API tests exercise.

## Universal Requirements

Every rule's test file, regardless of rule type, should cover these four categories. If any is missing, the coverage isn't comprehensive.

**1. Success.** At least one test that parses input the rule accepts and verifies `result.Success` is true. Where the rule produces a Symbol the caller can inspect, assert the Symbol's shape (id, text via `ToString()`, children as applicable).

**2. Failure position.** At least one test that parses input the rule rejects and asserts `result.ErrorCharIndex`. Position matters because it locks in the error-position principle (the failing read's pre-read position) against regression. Without this assertion, a rule can silently drift back to post-read semantics without the test suite noticing.

**3. Failure message propagation.** At least one test where the rule has a `.WithError("...")` set and the parse failure surfaces that exact message via `result.ErrorMessage`. This verifies the equal-depth message-claim path in `RecordFailure` that lets rule authors attach user-friendly messages. Assert with `Is.EqualTo(...)`, not `Does.Contain(...)`. Contain-based assertions pass accidentally when the wrong message happens to share a substring.

**4. At least one test without WithError.** To verify the positional-fallback path in `BuildErrorMessage`. Without this, the fallback code could break silently. One `Does.StartWith("Unexpected end of input")` or `Does.StartWith("Parse failed at offset")` test per rule file is enough.

**5. Sealed-rule rejection.** Three tests, one each verifying that `Flatten(...)`, `WithError(...)`, and `As(...)` throw `InvalidOperationException` when called on the rule after `Compile()` has run. The pattern:

```csharp
[Test]
public void Sealed_<RuleName>_rejects_Flatten()
{
    var rule = <construct the rule>;
    rule.Compile();
    Assert.Throws<InvalidOperationException>(() => rule.Flatten(FlattenType.Preserve));
}
// plus the same shape for WithError and As
```

The base `Rule.ThrowIfSealed` enforces the seal, but subclasses that ever override `Flatten` / `WithError` / `As` (or factory paths that produce wrapper rules) can silently skip the check. Per-rule tests catch that drift in the rule's own file rather than letting one shared test in `Core/CompileTests.cs` cover everything. `LateBoundRule` is the exception: it rejects these modifiers *always*, not just post-compile, so its test file verifies the always-rejecting form instead.

### Shared-Body Composites and Their Factory Derivatives

Some rules share a single underlying class behind several public factories. Today this means `BetweenInclusiveRule`, which is the body behind `OneOrMore`, `ZeroOrMore`, `Optional`, `AtLeast`, `AtMost`, and `Exactly`. Each named factory just constructs a `BetweenInclusiveRule` with different `(atLeast, atMost)` bounds and a different trace name and returns it unchanged. None of the derivatives override anything.

When a rule is shaped this way, the universal tests above all live in the shared body's fixture (`BetweenInclusiveRuleTests.cs`), and each derivative's fixture shrinks to a small set of tests that prove the factory wires the right bounds and trace name into the base. The pattern per derivative:

- A trace test that parses input exercising the factory's specific bounds, asserting on the verbatim trace output. The named trace label (`OneOrMore`, `Exactly[3]`, etc.) proves the factory was used. The trace's `count= N` line and any probe-past-the-bound `Lexer.Read` lines prove the bounds were wired correctly.
- One behavior test for the bound that's hardest to read off the trace alone (typically the lower-bound failure case for the rules that have one: `OneOrMore`, `AtLeast`, `Exactly`).

Each derivative's fixture opens with a comment pointing at the shared body's fixture so the reader knows where to find the full coverage. Sealed-rule rejection, WithError surfacing, deepest-failure-wins, and the scanner-skip optimization all live in `BetweenInclusiveRuleTests` and are not duplicated per derivative.

## Per-Rule-Type Requirements

### Single-Token Primitive Rules

Rules that call `lexer.Read()` exactly once. Today: `OneOfRule`, `EofRule` (which doesn't actually read but checks `lexer.IsEof`). The single-rune case of `GraphemeRule` behaves the same way.

Required tests:

- **EOF on empty input.** Pass `""` or the rule under a constructed context where EOF arrives immediately. Assert `ErrorCharIndex == 0`. Confirms the rule records at `transaction.StartPosition` (pre-read) rather than post-read, and that the position sits at offset 0 where `input[offset]` would crash but `offset == input.Length` is the legitimate EOF case.
- **Mismatch at start.** Pass input whose first token the rule rejects. Assert `ErrorCharIndex == 0`.
- **Mismatch after successful matches.** Construct a composite (e.g., `AllOf(otherRule, thisRule)`) where an earlier rule consumes some input before this rule fails. Assert the failure is at the consumed-to-position, not at 0. The first two tests both expect position 0, so a buggy rule that hard-codes 0 (or uses the outer parse's start instead of its own pre-read position) would still pass them. This test is what proves the rule is actually tracking where its failing read started.

Example (from `OneOfRuleTests.cs`):

```csharp
[Test]
public void OneOf_mismatch_after_successful_matches_points_at_first_bad_char()
{
    var rule = AllOf(OneOrMore(OneOf(RuneSet.Letters)),
                   Grapheme(';').WithError("expected ';'"));
    var result = rule.Parse("abc1");
    Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
    Assert.That(result.ErrorMessage, Is.EqualTo("expected ';'"));
}
```

### Multi-Token Primitive Rules

Rules that read multiple tokens in a lockstep loop. Today: `GraphemeRule` for multi-rune graphemes, `LiteralRule`, `LiteralIgnoreAsciiCaseRule`.

Required tests beyond single-token coverage:

- **Mismatch on the first token.** Assert position is at the very start (offset 0 for a top-level rule).
- **Mismatch on a later token.** Construct input that matches the first N-1 tokens successfully then diverges. Assert position equals the start of the Nth token (where `tokenStart` was captured in the Nth iteration), not the start of the whole match (offset 0) and not post-read (offset of the token after the failure).
- **Both lexer modes where applicable.** For rules whose behavior changes between `GraphemeLexer` and `RuneLexer`, include at least one test under each via `new ParseOptions { InputUnit = InputUnit.Rune }`.

Example (from `GraphemeRuleTests.cs`):

```csharp
[Test]
public void Token_multi_rune_mismatch_on_second_token_reports_at_second_token_start()
{
    var rule = Grapheme("\uD83D\uDC4B\uD83C\uDFFD").WithError("expected wave");
    var result = rule.Parse("\uD83D\uDC4Bxy",
        new ParseOptions { InputUnit = InputUnit.Rune });
    Assert.That(result.ErrorCharIndex, Is.EqualTo(2));
    Assert.That(result.ErrorMessage, Is.EqualTo("expected wave"));
}
```

### Composite Rules

Rules that wrap other rules and combine their results: `AllOfRule`, `FirstOfRule`, `BetweenInclusiveRule` (the body shared by `OneOrMore`/`ZeroOrMore`/`Optional`).

Required tests beyond universal coverage:

- **First-child failure.** Pass input the first child rejects. Assert the failure position comes from the first child's pre-read offset (usually 0 for a top-level test). Use different `WithError` messages on each child and assert the *correct* child's message appears, not just "something failed."
- **Later-child failure.** Pass input the first child (or first several) accept, then the next child rejects. Assert the position is the later child's pre-read offset. Again with per-child `WithError` to verify which child's message surfaces.
- **Children that consume different amounts before failing.** Specifically for FirstOf, construct alternatives where different branches advance different distances before failing. Assert the deepest-advancing branch's message wins (deepest-failure-wins) and position.
- **Edge cases specific to the composite.** Cover the shapes a count rule can land in: zero matches with a 0-lower-bound (`Optional` / `ZeroOrMore` / `AtMost` shape), one-below-lower-bound failure (`OneOrMore` / `AtLeast` / `Exactly` shape), the always-succeeds case where `WithError` cannot surface, and the known-PEG-quirk case where a 0-lower-bound rule's inner failure depth beats a required outer rule's shallower depth. These tests live on `BetweenInclusiveRuleTests`, which is the shared body for all six count rules. See "Shared-Body Composites and Their Factory Derivatives" above.

Example (from `AllOfRuleTests.cs`):

```csharp
[Test]
public void And_later_child_failure_reports_at_deeper_position()
{
    var rule = AllOf(Grapheme('a').WithError("need an 'a'"),
                   Grapheme('b').WithError("need a 'b'"));
    var result = rule.Parse("ax");
    Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    Assert.That(result.ErrorMessage, Is.EqualTo("need a 'b'"));
}
```

### Rules with Construction-Time Validation

Any rule (or factory) that validates its arguments and throws at build time. Today: `Grapheme(char/Rune/int/string)` rejects surrogates, out-of-range values, multi-grapheme strings. `RuneSet.Single`/`Range`/`Runes` reject invalid scalar values.

Required tests:

- **Each validation path, one test.** `Assert.Throws<ArgumentOutOfRangeException>(() => Rule.Factory(badValue))`. Cover each documented failure mode (surrogate, out-of-range-low, out-of-range-high, multi-element where single required, empty where non-empty required).
- **Boundary values.** Test the exact boundary: `0xD800` (first surrogate), `0xDFFF` (last surrogate), `0x10FFFF` (last valid), `0x110000` (first out-of-range), empty string, single-element string.
- **Valid inputs adjacent to boundaries.** Show that the rule doesn't over-reject: `0xD7FF` (last before surrogate block) and `0xE000` (first after surrogate block) should build successfully.

### Lookahead and Forwarding Rules

Rules that don't consume input or simply delegate: future `Not`, `Peek`, current `LateBoundRule`.

Required tests:

- **Zero-consumption.** Assert that parsing past the lookahead rule lands at the same position as before it ran. For `Peek(x).Parse(input)` followed by some tracking of where the lexer is, position shouldn't have advanced.
- **Forwarding correctness.** For `LateBoundRule`, tests should cover both bound and unbound states, and the expected error when `.Bind(...)` was skipped.
- **No modifier acceptance where forbidden.** `LateBoundRule` specifically rejects `.As`, `.Flatten`, `.WithError` because it's transparent at parse time and those modifiers would silently do nothing. If another rule has similar no-ops, its tests should verify that too.

### Cross-Cutting Concerns (Not Per-Rule)

Some tests don't belong to any one rule's file. These live in `Core/`:

- **Lexer-mode switching** → `Core/LexerSwitchTests.cs`. Tests that exercise `ParseOptions.InputUnit` switching.
- **WithError deepest-failure across multiple rules**: `Core/WithErrorTests.cs`. Tests that build grammars spanning several rules and assert the right message wins across them.
- **Id assignment (Compile)**: `Core/IdAssignmentTests.cs`. Tests that verify the three-pass id assignment (pinned, named-hash, anonymous) behaves correctly.
- **RuneSet behavior**: `Core/RuneSetTests.cs`. Tests for the `RuneSet` data type itself (not its consumers like `OneOfRule`).
- **Tracing (cross-cutting concerns only)**: `Core/TracingTests.cs`. Covers behaviors that aren't any one rule's property: null TraceSink is a no-op, ParseOptions defaults (null sink, Diagnostic level), the trace-label fallback chain (Name > ErrorMessage > rule class name), `TraceLevel.Normal` suppresses output, `Lexer.Read` and `Lexer.RecordFailure` emit their own diagnostic lines, transaction depth returns to zero after a parse (regression guard, since running the same parse twice must produce identical trace output), and two side-effect proof tests (`Off_path_does_not_evaluate_interpolated_arguments`, `On_path_evaluates_interpolated_arguments_exactly_once`, plus `Rule_TraceSuccess_off_path_does_not_evaluate_interpolated_arguments`) that verify the C# interpolated-string-handler rewrite. They're the critical tests for "tracing is cheap when disabled and doesn't evaluate interpolated arguments."

Rules emit their traces via two base-class helpers, `TraceSuccess(lexer, $"...")` and `TraceFailure(lexer, $"...")`, defined on `Rule`. The rule's class name (`"AllOf"`, `"Grapheme"`, etc.) is derived automatically from `GetType().Name` with the `"Rule"` suffix stripped and cached in the base constructor, so new rules get correct trace names without touching trace plumbing. Both helpers have explicit-level overloads (`TraceSuccess(lexer, level, $"...")`) for the rare case a rule wants to emit at something other than Diagnostic.

**Per-rule trace tests live in each rule's own test file.** Every rule in `Rules/` must include at least one success-path trace test and at least one failure-path trace test (if the rule has a failure path; `ZeroOrMoreRule` has none). The tests lock in the full trace output verbatim via `Assert.That(sink.ToString(), Is.EqualTo(...))`. This way, changing a rule's trace format produces a test failure in the rule's own file, right next to the code being edited, rather than in a central file the author might not have open. Shared helpers (`NewSink()`, `Lines(params string[])`) live in `TraceTestHelpers.cs` at the test project root and are pulled in via `using static InductorParser.Tests.TraceTestHelpers;`.

Note on C++ trace mapping. The original InductorParser (C++) emits traces using template-unrolled class names like `CharacterSymbol::Parse`, `CharacterSetSymbol::Parse`, `1to2147483647Expression::Parse`, and so on. The C# port uses the rule's C# name instead (`Grapheme`, `OneOf`, `OneOrMore`). Captured C++ traces used for reference material need a one-time mental mapping: C++ `CharacterSymbol` → C# `Grapheme`, C++ `CharacterSetSymbol` → C# `OneOf`, C++ `EofSymbol` → C# `Eof`, C++ `AndExpression` → C# `AllOf`, C++ `OrExpression` → C# `FirstOf`, C++ `AtLeastAndAtMostExpression<T, 1, INT_MAX>` (`1to2147483647Expression`) → C# `OneOrMore`, C++ `<T, 0, INT_MAX>` → C# `ZeroOrMore`, C++ `<T, 0, 1>` → C# `Optional`. The general `BetweenInclusive(inner, n, m)` traces as `BetweenInclusive[n..m]`.

When you write a test that primarily exercises one of these concerns, put it in the corresponding file, not in a rule-specific file. When a test exercises a rule but happens to touch a cross-cutting concern, put it in the rule's file and keep the cross-cutting concern under test as a secondary focus.

End-to-end grammars built from the public API live in `E2EExamples/`. Examples there serve a dual purpose: they catch regressions in cross-rule interactions, and they double as documentation for what a real grammar in this library looks like. New examples should reflect realistic use cases, not just exercise rules already covered in `Rules/`.

## File Organization

One test fixture per rule, in `Rules/`. File naming follows the rule's type name plus `Tests`: `Rules/GraphemeRuleTests.cs` → `GraphemeRule.cs`. Cross-cutting files in `Core/` are named after the concern (`WithErrorTests.cs`, `LexerSwitchTests.cs`).

Tests inside a fixture are ordered loosely by category: success paths first, failure-position tests next, WithError-message tests after that, then edge cases and construction-time validation. This isn't enforced by tooling, it's a readability convention.

Each test method's name should describe the scenario, not the expected outcome. `Token_mismatch_on_single_char_input_points_at_offender` beats `Token_should_fail_correctly`. Reading the fixture's method list tells you what cases are covered without opening any body.

## Anti-Patterns

**Substring-matching on error messages.** `Does.Contain("'x'")` passes when the right message happens to include `'x'` but also when a completely unrelated failure happens to have `'x'` in it. Prefer `Is.EqualTo(...)` with a concrete WithError-attached message. Only fall back to substring when the message genuinely has variable content (like the positional fallback message with its dynamic offset).

**Failure tests without `ErrorCharIndex` assertions.** Every parse-failure test should lock in the position. Without it, the error-position principle can regress silently.

**Tests that only assert `result.Success`.** `Assert.That(result.Success, Is.False)` is a sanity check but a weak one. Asserting position and message in the same test catches order-of-magnitude-more regressions for the same test-maintenance cost.

**Multiple unrelated assertions in one test.** If a single test verifies success-path behavior AND failure-path behavior AND construction validation, it's doing the work of three tests and will produce confusing failures. Prefer small focused tests.

**Testing implementation details instead of observable behavior.** Don't assert on private fields or internal state. If the test passes when you change the behavior in a way the user can observe, the test is wrong. If the test fails when you refactor internals without changing observable behavior, the test is brittle.

## Running the Suite

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj
```

The test project targets net8.0 and consumes the net8.0 build of the library. A clean suite run on net8.0 is the gate for landing a change.

### Routing the Whole Suite Through the State Machine

Every `Rule.Parse(...)` call in the test suite normally goes through the recursive evaluator. Setting `INDUCTOR_DEFAULT_ENGINE=statemachine` before `dotnet test` flips a process-wide default so the same fixtures run against the state-machine evaluator instead, without rewriting individual tests.

```
INDUCTOR_DEFAULT_ENGINE=statemachine dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj
```

The mechanics. `EngineSelectionFixture` (a `[SetUpFixture]` at the test-project root) reads the env var once before any fixture runs and writes `true` into `ParseOptions.DefaultUseStateMachine` when the value is `statemachine` (case-insensitive). From there the dispatcher in `Rule.Parse` resolves to `StateMachineParser.Parse` instead of `Rule.ParseRecursive`. Cross-engine compare fixtures (`StateMachineE2ECompareTests`, `StateMachineParserTests`, `StateMachineNormalizationCompareTests`, `StateMachineBudgetCompareTests`, `StateMachineGrammarCompareTests`) call `rule.ParseRecursive(...)` directly for the recursive baseline, so the comparison stays apples-to-apples even when the global default is flipped on. A `TestContext.WriteLine` at the start of the run says which engine the suite picked up.

The selector lives on `ParseOptions` as the internal `UseStateMachine` (per-call, nullable bool) and `DefaultUseStateMachine` (process-wide static). Both are internal on purpose: this is test plumbing, not a documented user feature. Outside callers who explicitly want the state machine should keep calling `StateMachineParser.Parse` directly.

## IL2CPP Test Pass

`dotnet test` runs against CoreCLR and only exercises the net8.0 build of the library. The netstandard2.1 build (the one Unity's IL2CPP scripting backend actually loads on iOS, WebGL, and Switch) is compile-checked on every build but never executed by the net8.0 test pass. CoreCLR is a JIT runtime and IL2CPP is AOT-only, so a library that passes every `dotnet test` can still fail on first load in a Unity IL2CPP player.

The `src/InductorParser.Tests/Unity/` folder is a minimal Unity scaffold whose entire purpose is to catch that class of regression. It holds an Editor script that flips the Standalone scripting backend to IL2CPP, a run script that drives Unity in batch mode, and a PlayMode asmdef under `Assets/Tests/PlayMode/`. It lives under `InductorParser.Tests/` because it's test infrastructure for the .NET library, not a separate Unity game.

The .NET test sources in `src/InductorParser.Tests/{Core,Rules,E2EExamples}/` are the single source of truth. `syncteststounity.sh` copies them into `Unity/Assets/Tests/PlayMode/Synced/`, and `runil2cpptest.sh` invokes that script before starting Unity, so the IL2CPP pass exercises the same ~90 tests that `dotnet test` does, not a hand-picked subset. The synced tree is `.gitignore`d and cleaned each run so a deletion in the .NET project can't linger in the Unity project. `syncteststounity.sh` is also safe to run on its own, handy after a fresh clone if you want to open the Unity project in the Editor and drive the Test Runner window directly (the Editor runs PlayMode tests on Mono, which is a faster iteration loop than the full batch-mode IL2CPP run but won't catch IL2CPP-only regressions).

Two wiring details that make this work. First, the library's csproj declares `InternalsVisibleTo` for both `InductorParser.Tests` (the .NET assembly) and `InductorParser.PlayModeTests` (the Unity asmdef's assembly). The tests subclass `Rule` and override its `internal TryParseRule`, so both assemblies need it. Second, `Unity/Assets/csc.rsp` sets `-langversion:latest` so the Unity Roslyn pass accepts the C# 10 interpolated-string-handler calls used by `TraceSuccess`/`TraceFailure` in the trace tests.

To run it:

```
./src/InductorParser.Tests/runil2cpptest.sh
```

The script builds the netstandard2.1 DLL (which the `CopyToUnity` target in `src/InductorParser/InductorParser.csproj` drops into `src/InductorParser.Tests/Unity/Assets/Plugins/`), syncs the test sources into the Unity PlayMode folder, then invokes Unity 6000.3.13f1 in batch mode with `-executeMethod InductorParser.Editor.IL2CPPTestRunner.Run`. That method sets `PlayerSettings.SetScriptingBackend(Standalone, IL2CPP)` and `SetApiCompatibilityLevel(Standalone, .NET_Standard)`, then uses `TestRunnerApi` to build a Standalone Player with IL2CPP and run the Play Mode tests on it. Results land at `test-results/il2cpp-playmode-results.xml` (at the repo root); the Unity log lands at `test-results/il2cpp-log.txt`.

Requirements: Unity 6000.3.13f1 installed via Unity Hub (the version is pinned in `src/InductorParser.Tests/Unity/ProjectSettings/ProjectVersion.txt`), the IL2CPP build support module for the host platform, and Unity not currently open on the scaffold. The script preflight-checks all three and fails fast with a pointer at the fix (for example, "IL2CPP compiler not installed... Install via Unity Hub: Installs -> 6000.3.13f1 -> Add modules -> check Windows Build Support (IL2CPP)") so you don't sit through a multi-minute Unity startup only to hit "Currently selected scripting backend (IL2CPP) is not installed" at the end.

A word on speed. This is slow. Really slow. A cold run from an empty `Unity/Library/` spends about a minute just on Unity's domain reload and package resolution before it even compiles any of our code, then another chunk on top of that to build the IL2CPP Standalone player and execute the tests on it. Running ~90 tests instead of a handful doesn't really move the needle because the cost is Unity's startup plus the player build, not per-test execution. Figure a few minutes end-to-end on a warm machine, longer on the first run after cloning the repo or after `Library/` is deleted. It's the main reason the IL2CPP test is a pre-merge check and not a primary loop: you run it before merging a risky change, not on every save. Keep the fast `dotnet test` loop for day-to-day work.

Adding a new test. Put it in `src/InductorParser.Tests/{Core,Rules,E2EExamples}/` as usual. It will be picked up by both `dotnet test` and `runil2cpptest.sh` automatically. If the test touches something IL2CPP is known to mangle (reflection, generic virtual methods, runtime codegen), the IL2CPP pass is where you'll find out.
