# Test Architecture

Run the test suite with `./test.sh` at the repo root. It runs everything in `InductorParser.sln` (the main test project, the external-API tests, and the `E2ESamples/*` grammars), and a clean run is the gate for landing a change. Arguments pass through to `dotnet test`, so `./test.sh --filter "FullyQualifiedName~Atom_fragment"` runs one fixture, and `dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj` runs just the main project. The script works from Git Bash or WSL and keeps the output quiet. How it does both is explained in comments in `test.sh` itself.

The ~10,000-case UAX #29 conformance suite is the one part the default run skips. Opt in by category:

```
./test.sh --filter "TestCategory=UnicodeConformance"
```

NUnit's `[Explicit]` only clears when tests are picked out by Name or Category, so a class-level `FullyQualifiedName` filter discovers "0 tests run".

The rest of this doc answers one question: what does a rule's test file need before its coverage counts as comprehensive? It's a checklist for anyone adding a new rule or auditing an existing one.

## Where Tests Live

`src/InductorParser.Tests/` has six folders:

- `Rules/`: one file per rule, named `<RuleType>Tests.cs`.
- `Core/`: cross-cutting concerns, named after the concern (`WithErrorTests.cs`, `TracingTests.cs`).
- `E2EExamples/`: full grammars built from the public API. These double as documentation for what a real grammar looks like, so new ones should be realistic use cases, not rule showcases.
- `DocExamples/`: keeps the code samples in the markdown docs compiling and passing.
- `Lexing/`: lexer-level tests, including the conformance suite above.
- `Unity/`: the IL2CPP scaffold (last section).

Shared helpers (`TraceTestHelpers.cs`, `NormalizationExamples.cs`, the matrix helpers) sit loose at the project root. Files in the first three folders all use `namespace InductorParser.Tests;`.

Every failure test below leans on the error model in [ErrorArchitecture.md](ErrorArchitecture.md): when a parse fails, the deepest failure wins, and the reported position is where the failing read started, not where it gave up.

## What Every Rule's Test File Needs

**Success.** Parse input the rule accepts. Where the rule produces a Symbol, assert its shape (id, text, children).

**Failure position.** Parse input the rule rejects and assert `result.ErrorCharIndex`. Without this, a rule can quietly drift to reporting the wrong position.

**Failure message.** Give the rule a `.WithError("...")` and assert the failure surfaces that exact message. Use `Is.EqualTo(...)`, never `Does.Contain(...)`: substring matches pass when the wrong message happens to share text. For a composite, also assert where the message lands when a child fails deeper than the composite's own start (it lands at the deepest position the subtree reached). Example: `OrRuleTests.Or_named_WithError_beats_same_depth_mechanical_branch_failures`.

**One failure without WithError.** This covers the fallback message. `Does.StartWith("Unexpected '")` is enough.

**Sealed-rule rejection.** After `Compile()` a rule is sealed: `Flatten`, `WithError`, and `As` each throw `InvalidOperationException`. Three small tests, named `Sealed_<RuleName>_rejects_Flatten` and so on. They're per-rule because a subclass that overrides one of those methods can skip the base check, and only a test in that rule's own file would notice. `LateBoundRule` rejects the modifiers always, not just after compile, so its file verifies that instead.

**Trace output.** One success-path and one failure-path trace test asserting the whole trace verbatim with `Assert.That(sink.ToString(), Is.EqualTo(...))`, so a format change fails right next to the code that changed. `NewSink()` and `Lines(...)` come from `TraceTestHelpers.cs`. (`ZeroOrMore` can't fail, so it only has the success side.)

**The two matrices**, for leaf rules. Covered in "Matrix Tests" below.

## Extra Tests by Rule Shape

**Single-token leaf rules** (`OneOf`, `NoneOf`, `AnyToken`, `Eof`). Three failure positions: EOF on empty input (position 0), mismatch on the first token (position 0), and mismatch after earlier rules consumed input, which must report the position where this rule's own read started. The third is the one that catches real bugs: the first two both expect 0, so a rule that hard-codes 0 passes them.

```csharp
[Test]
public void OneOf_mismatch_after_successful_matches_points_at_first_bad_char()
{
    var rule = And(OneOrMore(OneOf(TokenSet.Letters)),
                   Token(';').WithError("expected ';'"));

    var result = rule.Parse("abc1");

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
    Assert.That(result.ErrorMessage, Is.EqualTo("expected ';' at line 1, column 4."));
}
```

**Multi-token leaf rules** (`Token` with a multi-rune grapheme, `Literal`, `LiteralIgnoreAsciiCase`). Add mismatch on the first token (position 0) and mismatch partway through, which must report the start of the token that didn't match, not 0 and not past it.

**Composites** (`And`, `Or`, the count rules).

- First-child failure and later-child failure, each child with its own `.WithError`, asserting which child's message surfaced.
- For `Or`, branches that consume different amounts before failing. The deepest failure wins, and a rejected branch's failures are kept, so its `.WithError` competes like any other. See `OrRuleTests.Or_rejected_branch_WithError_is_kept_as_a_near_miss`.
- An inner rule's `.WithError` surfaces through a composite that has none of its own.
- Where the composite's own `.WithError` lands: the deepest position its subtree reached, except `Not` and `Peek` anchor at their own start and `ScanWhile` at where the scan stopped.
- `Peek` and `Not` run their inner as a throwaway probe, so an inner `.WithError` should *not* surface through them. Test the discard. Example: `PeekRuleTests.Peek_discards_inner_WithError_because_lookahead_failures_are_dropped`.

```csharp
[Test]
public void And_later_child_failure_reports_at_deeper_position()
{
    var rule = And(Token('a').WithError("need an 'a'"),
                   Token('b').WithError("need a 'b'"));

    var result = rule.Parse("ax");

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    Assert.That(result.ErrorMessage, Is.EqualTo("need a 'b' at line 1, column 2."));
}
```

**Count rules** (`OneOrMore`, `ZeroOrMore`, `Optional`, `AtLeast`, `AtMost`, `Exactly`). All six are factory methods over one class, so the real coverage (bound edge cases, WithError, sealing) lives in `BetweenInclusiveRuleTests.cs`. Each factory's own file just proves the wiring: one verbatim trace test showing the factory's label and bounds, and one behavior test for its trickiest bound (usually the lower-bound failure). Each of those files opens with a comment pointing at the shared fixture.

**Construction-time validation** (`Token`, `TokenSet.Single`/`Range`/`Runes`). One test per rejection path, asserting the exact exception type: scalar paths throw `ArgumentOutOfRangeException`, string paths throw `ArgumentException`, and `Assert.Throws<T>` matches exactly, so the difference shows. Test the boundaries themselves (`0xD7FF`, `0xD800`, `0xDFFF`, `0xE000`, `0x10FFFF`, `0x110000`, the empty string) from both sides, so over-rejection and under-rejection both show up.

**Lookahead and forwarding rules** (`Not`, `Peek`, `LateBoundRule`, `Alias`). A zero-consumption test: position doesn't move when the rule runs. For `LateBoundRule`, cover bound and unbound states, the "never bound" error, and its always-rejected modifiers.

## Matrix Tests

Normalization can rewrite both the grammar's stored text and the input at Compile time, and the tests that catch normalization bugs are parameterized matrices, not hand-picked examples. Both matrices draw from `NormalizationExamples.RowFormPairs` at the test project root: a table of grapheme behaviors crossed with the four `NormalizationForm` values, with a self-check fixture asserting every column matches what `string.Normalize` really produces. New normalization tests should pull cases from this table too.

The matching matrix runs every leaf rule that rewrites stored text under a form (`Token`, `Literal`, `LiteralIgnoreAsciiCase`, `OneOf`, `NoneOf`, `ScanWhile`, `ScanUntil`) against every row, under four shapes: bare, `OneOrMore(leaf)`, `Or(leaf, fallback)`, and `And(leaf, Eof())`. The composite shapes matter because an evaluator optimization that peeks precomputed first-token data is exactly the code that goes wrong when that data goes stale under normalization, and `And` + `Eof` catches a leaf that consumed the wrong span.

The SourceRange matrix runs every leaf-emitting rule as the target of `And(Literal(prefix), target)` under every form and asserts the target Symbol's `SourceRange` points into the caller's original input. Whenever a form changes the input's length, a bug here silently reports positions in the internal normalized copy instead.

Each rule also has one `SourceText_on_<Rule>_returns_matched_text_under_every_FlattenType` test: FlattenType controls what shows up in the tree, never what text a Symbol reports. Rule-shape-specific SourceRange cases (zero-width matches, multi-line spans, Delete-flattened children) go in the rule's file. The machinery-level position tests (`Core/SymbolPositionTests.cs`, `Core/RawSourceTextTests.cs`) cover line/column derivation and original-text recovery.

## Cross-Cutting Tests

Tests about the framework rather than one rule live in `Core/`:

- `WithErrorTests.cs`: the resolution rules themselves. Deepest wins, named beats mechanical on a depth tie, forced beats everything non-forced, and `WithError` is set-once.
- `IdAssignmentTests.cs`: the three-pass id assignment (explicit, named-hash, anonymous).
- `TokenSetTests.cs`: the `TokenSet` type itself, apart from the rules that consume it.
- `TracingTests.cs`: trace behavior that belongs to no one rule. A null sink is a no-op, an `.As()` name prefixes the rule class name in the label, and the proof tests that a disabled trace never evaluates its interpolated arguments (the property that makes tracing free when off).

If a test mostly exercises one rule, it goes in that rule's file even when it touches one of these concerns.

## Conventions and Anti-Patterns

Name tests for the scenario, not the verdict. `Token_mismatch_on_single_char_input_points_at_offender` beats `Token_should_fail_correctly`, and the fixture's method list then reads as a coverage summary. Within a fixture, order tests loosely: success, failure positions, messages, edge cases, construction validation.

The recurring mistakes to avoid:

- `Does.Contain` on error messages. It passes when an unrelated message shares the substring.
- A failure test with no `ErrorCharIndex` assert. The position can regress silently.
- Tests that only assert `result.Success`. Position and message in the same test catch far more for the same cost.
- One test asserting several unrelated behaviors. Split it.
- Asserting internals. If a refactor with identical observable behavior breaks the test, the test is wrong.

## The Unicode Implementation Setting

`UnicodeEnvironment.Implementation` selects the segmenter and normalizer for the whole process, and the test architecture around it has one rule: only `UnicodeEnvironmentSettingTests` ever sets it. Everything else runs on `Automatic`, which the first segmentation or normalization query freezes to the build's default, `Runtime` on the net8.0 build `dotnet test` runs and `Bundled` on the netstandard2.1 build Unity loads. That two-run design is the functional coverage story: the same synced test sources run twice, once per resolution, so every Compile-and-Parse test exercises the runtime implementations on CoreCLR and the built-in ones under IL2CPP without any test touching the setting. A test that derives expected data from an oracle reads it through `NormalizationHelpers` or `GraphemeHelpers`, which resolve to the same frozen choice Parse resolves to in that process, so the oracle and the behavior under test can't disagree on either platform.

The one mutating fixture is `[NonParallelizable]` so nothing runs mid-mutation, and its SetUp and TearDown both call `UnicodeEnvironment.ResetForTesting`, which restores unfrozen Automatic and clears the caches that could leak answers across the boundary (the per-string cluster-boundary indexes and TokenSet's normalized projections). Fixture order therefore doesn't matter: whichever fixture queries next re-freezes to the build's default.

Tests that target one specific implementation never rely on the ambient resolution. The differential, conformance, and data-table tests in `Lexing/` call the internal per-implementation entry points directly (`NormalizeWithBundledImplementation` and its runtime twin, and the same pair for segmentation), so they exercise the built-in code even when the process resolved to Runtime. The one end-to-end `Bundled`-on-CoreCLR test sets the property explicitly, inside the reset-protected fixture.

The resolution itself is asserted at both ends with behavior tripwires, not just enum checks: the setting fixture asserts Automatic resolves to Runtime on net8.0, and the Unity smoke test asserts it resolves to `Bundled` in the player, with the CRLF cluster length and the fi-ligature expansion failing first if the wrong implementation is in use. The freeze semantics defend the arrangement structurally too. Once anything has queried, a stray assignment throws instead of silently flipping the implementation under other fixtures.

## The Globalization Oracle

The differential tests in `Lexing/` compare the built-in segmenter and normalizer against the runtime's `StringInfo` and `string.Normalize`, and those two runtime implementations get their Unicode data from different places. `string.Normalize` hands the work to ICU (International Components for Unicode, the open-source library most operating systems supply for Unicode algorithms), and which ICU a host has moves with OS updates (or disappears entirely under Windows NLS or invariant globalization). `StringInfo`'s Unicode data is compiled into the runtime itself, so its version stamp is the runtime version.

The suite enforces one known configuration instead of reporting whatever it found. The test project ships its own ICU as the normalization oracle (the `Microsoft.ICU.ICU4C.Runtime` 72.1 reference in `InductorParser.Tests.csproj`, built from the same Unicode 15.0 data as the built-in tables), and `GlobalizationOracleFixture`, a `[SetUpFixture]` that runs before any test in the assembly, fails the whole run with an actionable message if the loaded ICU isn't exactly that one, if NLS or invariant globalization is active, or if the runtime major isn't 8 (the runtime whose `StringInfo` the built-in segmenter is verified against). A green run therefore always means "verified against .NET 8 with ICU 72.1", and a host whose globalization drifted can't quietly redefine what the differential tests prove. Windows NLS stays rejected rather than tested. If NLS ever becomes a supported deployment, it would get its own named test run instead of being silently selected by the host.

The two oracle claims stay distinct. Compatibility ("matches .NET") is what the differential tests prove, against that one enforced environment. Standards conformance is what the `[Explicit]` UnicodeConformance suites prove, by running the official `NormalizationTest-15.0.0.txt` and `GraphemeBreakTest-15.1.0.txt` corpora against the built-in implementations.

## The IL2CPP Pass

`dotnet test` runs the net8.0 build on CoreCLR, a JIT runtime. Unity's IL2CPP backend (iOS, WebGL, Switch) loads the netstandard2.1 build and compiles it ahead of time, so code that passes every `dotnet test` can still fail on first load in a player. `src/InductorParser.Tests/Unity/` is a minimal Unity project that exists to catch exactly that.

```
./src/InductorParser.Tests/runil2cpptest.sh
```

The script syncs the test sources from `{Core,Rules,E2EExamples}/` (plus the Prolog fixture corpus) into the Unity project and runs them, about 7,200 tests once the corpus-driven matrices expand, on an IL2CPP Standalone player it builds. `DocExamples/` and `Lexing/` don't sync and stay CoreCLR-only (`Lexing/` holds the differential tests that compare the built-in segmenter and normalizer against CoreCLR's `StringInfo` and `string.Normalize`, which only mean something on CoreCLR). Results land in `test-results/` at the repo root. It needs Unity 6000.3.13f1 (the version in `ProjectVersion.txt`) with the IL2CPP module installed, and it preflight-checks both before spending minutes on Unity's startup.

The netstandard2.1 build Unity loads includes the library's UAX #29 segmenter and UAX #15 normalizer, which match the `StringInfo` segmentation and `string.Normalize` the net8.0 build uses on CoreCLR (the differential tests hold each pair equal), so the grapheme-heavy and normalization-heavy tests behave the same under IL2CPP as under `dotnet test`. The pass is expected green. Any failure is a new finding: usually an AOT problem, a string literal IL2CPP rewrote (it replaces unpaired surrogates in literals with U+FFFD, which is why the corpus builds lone surrogates at runtime), or a test-data file the sync doesn't copy. The synced tests that need a normalization oracle read it from `NormalizationHelpers`, which resolves to the same process-wide implementation Parse uses, so they stay meaningful on Unity instead of asserting against Mono's broken `string.Normalize`.

It's slow, a few minutes even on a warm machine, so it's a pre-merge check for risky changes (reflection, generics, anything AOT-hostile), not the everyday loop. New tests in the three synced folders get picked up automatically. The wiring that makes it work (the `InternalsVisibleTo` grants, the `csc.rsp` language-version bump, the `CopyToUnity` build target) is documented in comments in `InductorParser.csproj` and the Unity folder.
