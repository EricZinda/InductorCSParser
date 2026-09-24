# Test Architecture

Here's every way to run the tests, all in one place:

```
# The everyday suite. A clean run is the gate for landing a change.
./test.sh

# One supported .NET runtime instead of both.
./test.sh --framework net8.0
./test.sh --framework net10.0

# Everything: the everyday suite, every [Explicit] suite, and the Unity
# IL2CPP pass. Takes a long time, downloads UCD files from unicode.org,
# and needs Unity installed.
./test.sh --all

# Just the official Unicode conformance suites (tens of thousands of
# checks, skipped by default).
./test.sh --filter "TestCategory=UnicodeConformance"

# Just the deep campaigns: the grammar fuzzer plus the exhaustive
# Unicode differential sweeps (several minutes, skipped by default).
./test.sh --filter "TestCategory=DeepCampaign"

# The UCD verification tests. These download data files from unicode.org
# and only matter when bumping the Unicode version.
./test.sh --filter "TestCategory=RequiresNetwork"

# The Unity IL2CPP pass (needs Unity installed, takes a few minutes).
./src/InductorParser.Tests/runil2cpptest.sh

# The performance benchmarks.
dotnet run -c Release --project src/Benchmarks/Benchmarks.csproj -- --filter *Json* --exporters GitHub
```

`./test.sh` runs every test in `InductorParser.sln` (the main test project, the external-API tests, and the `E2ESamples/*` grammars) except the `[Explicit]` suites, which are what the filter commands above opt into. Arguments pass through to `dotnet test`, so `./test.sh --filter "FullyQualifiedName~Atom_fragment"` runs one fixture, and `dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj` runs just the main project. The script works from Git Bash or WSL and keeps the output quiet. How it does both is explained in comments in `test.sh` itself.

The skipped-by-default suites are marked with NUnit's `[Explicit]` attribute, and it has a gotcha. An `[Explicit]` test only runs when the filter picks it out specifically, by method name (`Name~...`) or by category (`TestCategory=...`). Matching its class name doesn't count: `--filter "FullyQualifiedName~GraphemeBreakConformanceTests"` discovers the fixture, skips every test in it, and reports "0 tests run". It looks like a broken filter, but the tests were found and deliberately skipped. That's why the opt-in commands above use `TestCategory`.

`dotnet test` itself has no flag that adds the explicit tests to a normal run, so `./test.sh --all` chains three steps: the everyday suite, a run filtered to the three opt-in categories, and the Unity IL2CPP pass. That's the "run absolutely everything" command, and the cost is what you'd expect. It takes a long time, it needs network access for the RequiresNetwork tests, and it needs Unity installed for the final step. The category list can't silently go stale, either: `ExplicitTestConventionTests` in `Core/` fails the everyday suite if an `[Explicit]` test ever shows up without a category `test.sh --all` knows about.

The Unity pass is covered in detail in "The IL2CPP Pass" at the bottom of this doc. The benchmarks live in their own solution so the main solution never has to restore BenchmarkDotNet and the competitor parsers, and `src/Benchmarks/README.md` explains what the numbers mean.

The rest of this doc answers one question: what does a rule's test file need before its coverage counts as comprehensive? It's a checklist for anyone adding a new rule or auditing an existing one.

## Where Tests Live

`src/InductorParser.Tests/` has seven folders:

- `Rules/`: one file per rule, named `<RuleType>Tests.cs`.
- `Core/`: cross-cutting concerns, named after the concern (`WithErrorTests.cs`, `TracingTests.cs`).
- `E2EExamples/`: full grammars built from the public API. These double as documentation for what a real grammar looks like, so new ones should be realistic use cases, not rule showcases.
- `DocExamples/`: keeps the code samples in the markdown docs compiling and passing.
- `Lexing/`: lexer-level tests, including the conformance suite above.
- `Fuzzing/`: the grammar fuzzer, one of the two `DeepCampaign` suites.
- `Unity/`: the IL2CPP scaffold (last section).

Shared helpers (`TraceTestHelpers.cs`, `NormalizationExamples.cs`, the matrix helpers) sit loose at the project root. Files in the first three folders all use `namespace InductorParser.Tests;`.

## Supported and Tested Platforms

The library is tested in the configurations below. The Unicode column shows
what `UnicodeEnvironment.Implementation = Automatic` selects in each one.

| Platform | Library build | Test coverage | Automatic Unicode implementation |
|---|---|---|---|
| .NET 8 CoreCLR | `net8.0` | All test projects in `InductorParser.sln`, including the opt-in suites when selected | `Runtime`: `StringInfo` and `string.Normalize` |
| .NET 10 CoreCLR | `net8.0` | All test projects in `InductorParser.sln`, including the opt-in suites when selected | `Runtime`: `StringInfo` and `string.Normalize` |
| Unity 6000.3.13f1 Standalone IL2CPP | `netstandard2.1` | The shared `Core`, `Rules`, and `E2EExamples` tests | `Bundled`: the built-in Unicode 16.0 segmenter and normalizer |

.NET 8 is the earliest modern .NET runtime directly tested. The table describes
the default Unicode implementation tested on each platform. Applications can
override it at startup.

The Unity pass directly verifies only the listed editor version and a
Standalone IL2CPP player. WebGL, iOS, Switch, other Unity versions, and other
`netstandard2.1` hosts are compatibility goals, but the current test matrix
doesn't run on them.

Running `./test.sh` tests both supported .NET versions. Pass `--framework
net8.0` or `--framework net10.0` to run just one. To try another installed
runtime without changing the project files, override the test-framework list:

```sh
./test.sh -p:InductorParserTestFrameworks=net9.0
```

This only reaches runtimes newer than the everyday pair. The test SDK refuses
to build a test project for anything older than net8.0, so a runtime below
.NET 8 can't be exercised this way.

.NET 9 was spot-checked this way on 2026-09-23. The everyday suite and the
DeepCampaign sweeps both passed on the 9.0.20 runtime, and
`GlobalizationOracleFixture` admits it, but it stays out of the everyday
matrix because it's a short-term-support release. `./test.sh` doesn't run it
unless you pass the override.

The Unity script uses 6000.3.13f1 by default. `UNITY_VERSION` selects another
Unity Hub installation, while `UNITY_EDITOR` accepts an explicit editor path:

```sh
UNITY_VERSION=6000.3.13f1 ./src/InductorParser.Tests/runil2cpptest.sh
UNITY_EDITOR=/path/to/Unity ./src/InductorParser.Tests/runil2cpptest.sh
```

The script copies the Unity project into `test-results/` (gitignored), opens
that copy, and deletes it when the script exits, so another editor version
can't upgrade or dirty the checked-in project. The copy sits next to the
results rather than in the shell's temp directory because, under WSL, `/tmp`
is on the Linux filesystem and reaches Windows Unity as a network path. These
overrides are exploratory: a version becomes part of the supported matrix only
when it's added to the default configuration.

## What Every Rule's Test File Needs

**Success.** Parse input the rule accepts. Where the rule produces a Symbol, assert its shape (id, text, children).

**Failure position.** Parse input the rule rejects and assert `result.ErrorCharIndex`. Without this, a rule can quietly drift to reporting the wrong position.

**Failure message.** Give the rule a `.WithError("...")` and assert the failure surfaces that exact message. Use `Is.EqualTo(...)`, never `Does.Contain(...)`: substring matches pass when the wrong message happens to share text. For a composite, also assert where the message lands when a child fails deeper than the composite's own start (it lands at the deepest position the subtree reached). Example: `OrRuleTests.Or_named_WithError_beats_same_depth_mechanical_branch_failures`.

**One failure without WithError.** This covers the fallback message. `Does.StartWith("Unexpected '")` is enough.

**Sealed-rule rejection.** After `Compile()` a rule is sealed: `Flatten`, `WithError`, and `As` each throw `InvalidOperationException`. Three small tests, named `Sealed_<RuleName>_rejects_Flatten` and so on. They're per-rule because a subclass that overrides one of those methods can skip the base check, and only a test in that rule's own file would notice. `LateBoundRule` rejects the modifiers always, not just after compile, so its file verifies that instead.

**Trace output.** One success-path and one failure-path trace test asserting the whole trace verbatim with `Assert.That(sink.ToString(), Is.EqualTo(...))`, so a format change fails right next to the code that changed. `NewSink()` and `Lines(...)` come from `TraceTestHelpers.cs`. (`ZeroOrMore` can't fail, so it only has the success side.)

**The two matrices**, for leaf rules. Covered in "Normalization Matrix Tests"
below.

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

## Normalization Matrix Tests

Normalization tests use `NormalizationExamples.RowFormPairs`, which crosses
representative grapheme cases with all four `NormalizationForm` values. A
self-check verifies the expected values against `string.Normalize`. New
normalization tests should reuse this table.

The test project has two normalization matrices:

- **Matching:** Runs every rule whose stored text is normalized (`Token`,
  `Literal`, `LiteralIgnoreAsciiCase`, `OneOf`, `NoneOf`, `ScanWhile`, and
  `ScanUntil`) against every case and normalization form. It tests each rule
  directly and inside `OneOrMore`, `Or`, and `And(..., Eof())` so that both
  matching decisions and the amount of input consumed are checked.
- **Source mapping:** Runs every rule that emits a Symbol after a prefix and
  verifies that its `SourceRange` refers to the caller's original input, even
  when normalization changes its length.

Each emitting rule also verifies that `SourceText` returns the matched original
text under every `FlattenType`. Rule-specific cases, such as zero-width or
multi-line ranges, belong in the rule's test file. Low-level position and
original-text recovery are covered by `Core/SymbolPositionTests.cs` and
`Core/RawSourceTextTests.cs`.

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

`UnicodeEnvironment.Implementation` is one process-wide setting that picks the segmenter and normalizer together. The test architecture has one rule about it: no test sets it, except `UnicodeEnvironmentSettingTests` and `HostGlobalizationCheckTests`, the two fixtures whose whole job is testing the setting (and its neighbor `AcceptHostGlobalization`).

Every other test runs on `Automatic`, and the first segmentation or normalization query freezes that to the build's default: `Runtime` on the net8.0 library build `dotnet test` loads, `Bundled` on the netstandard2.1 build Unity loads. That's the coverage plan. The same test sources run twice, once per platform, so each Compile-and-Parse test exercises the runtime implementations on CoreCLR and the built-in ones under IL2CPP, and no test has to touch the setting to make that happen.

One detail makes the two runs safe. A test that computes its expected answer from an oracle reads the oracle through `NormalizationHelpers` or `GraphemeHelpers`, and those resolve to the same implementation `Parse` resolves to in that process. So on either platform the oracle and the code under test are the same implementation, and they can't disagree.

Both mutating fixtures are `[NonParallelizable]` so they are threadsafe, and their SetUp and TearDown both call `UnicodeEnvironment.ResetForTesting`, which restores unfrozen Automatic and clears the state that could leak answers across the boundary (the per-string cluster-boundary indexes, TokenSet's normalized projections, and the host-globalization verdict). Fixture order therefore doesn't matter: whichever fixture queries next re-freezes to the build's default.

Tests that target one specific implementation never rely on the ambient resolution. The differential, conformance, and data-table tests in `Lexing/` call the internal per-implementation entry points directly (`NormalizeWithBundledImplementation` and its runtime twin, and the same pair for segmentation), so they exercise the built-in code even when the process resolved to Runtime. The one end-to-end `Bundled`-on-CoreCLR test sets the property explicitly, inside the reset-protected fixture.

The resolution itself is asserted at both ends with behavior tripwires, not just enum checks: the setting fixture asserts Automatic resolves to Runtime on net8.0, and the Unity smoke test asserts it resolves to `Bundled` in the player, with the CRLF cluster length and the fi-ligature expansion failing first if the wrong implementation is in use. The freeze semantics defend the arrangement structurally too. Once anything has queried, a stray assignment throws instead of silently flipping the implementation under other fixtures.

## The Unicode Test Environment

The differential tests in `Lexing/` compare the built-in implementation with
.NET's `StringInfo` and `string.Normalize`. Because `StringInfo` gets its data
from the runtime while `string.Normalize` uses the host's globalization
library, those results could otherwise vary by machine.

`GlobalizationOracleFixture` therefore requires .NET 8, 9, or 10, the ICU
72.1 package shipped with the test project, and normal ICU mode rather than
Windows NLS or invariant globalization. It stops the test assembly if any
requirement isn't met. Each passing differential run therefore identifies the
runtime it matched and always uses the same ICU normalization data.

Two suites establish different claims:

- The differential tests establish that the implementation matches .NET in
  that environment. ICU 72.1 contains Unicode 15.0 data, so the
  normalization sweep excludes only the characters added in Unicode 16.0.
  `UnicodeVersionDelta.cs` lists them. Unicode's normalization stability
  policy keeps the results for previously assigned characters unchanged.
- The `[Explicit]` Unicode conformance tests establish compliance with Unicode
  16.0 by running `NormalizationTest-16.0.0.txt` and
  `GraphemeBreakTest-16.0.0.txt`. They include the characters omitted from the
  differential sweep.

The shipped library also detects host configurations for which the Runtime
implementation hasn't been verified. Under NLS or invariant globalization,
the first normalizing Compile or Parse throws unless the caller enables
`UnicodeEnvironment.AcceptHostGlobalization`. `HostGlobalizationCheckTests`
verify the exception and opt-out in-process.
`HostGlobalizationChildProcessTests` start processes in the actual host modes
to verify their detection. `AcceptHostGlobalization` only suppresses this
safety check. It doesn't certify the host's Unicode behavior. An application
that enables it must run its parser integration tests in a separate process
started with the same globalization setting and, for NLS, on the same pinned
Windows version used in production. Invariant globalization should be accepted
only when the application doesn't depend on normalization or deliberately
wants normalization to do nothing. Applications that need tested,
host-independent behavior should select `UnicodeImplementation.Bundled`.

## The IL2CPP Pass

`dotnet test` runs on the .NET 8 and .NET 10 CoreCLR JIT runtimes against the library's net8.0 build. Unity's IL2CPP backend (iOS, WebGL, Switch) loads the netstandard2.1 build and compiles it ahead of time, so code that passes every `dotnet test` can still fail on first load in Unity. `src/InductorParser.Tests/Unity/` is a minimal Unity project that exists to catch exactly that.

```
./src/InductorParser.Tests/runil2cpptest.sh
```

The script syncs the test sources from `{Core,Rules,E2EExamples}/` (plus the Prolog fixture corpus) into a temporary copy of the Unity project under `test-results/` and runs them, about 7,200 tests once the corpus-driven matrices expand, on an IL2CPP Standalone player it builds. `DocExamples/`, `Fuzzing/`, and `Lexing/` don't sync and stay CoreCLR-only (`Lexing/` holds the differential tests that compare the built-in segmenter and normalizer against CoreCLR's `StringInfo` and `string.Normalize`, which only mean something on CoreCLR). Results land in `test-results/` at the repo root. By default it needs Unity 6000.3.13f1 (the version in `ProjectVersion.txt`) with the IL2CPP module installed, and it preflight-checks both before spending minutes on Unity's startup. Set `UNITY_VERSION` or `UNITY_EDITOR` as described in the platform section to try another editor safely.

The same tests behave the same on both platforms because both platforms segment and normalize the same way. On CoreCLR the tests run against the runtime's `StringInfo` and `string.Normalize`. Under IL2CPP they run against the library's built-in segmenter and normalizer. The differential tests validate the built-in implementations against both supported CoreCLR runtimes (normalization is compared everywhere except the code points Unicode 16.0 added), so a grapheme-heavy or normalization-heavy test can't pass on one platform and fail on the other over the compared Unicode data.

These same tests already passed on CoreCLR before this pass runs, so a failure here is never about the grammar logic. It points at the platform: an AOT problem, a string literal IL2CPP rewrote (it replaces unpaired surrogates in literals with U+FFFD, which is why the corpus builds lone surrogates at runtime), or a test-data file the sync doesn't copy.

One wiring detail keeps the synced tests meaningful on Unity. A test that needs a normalization oracle reads it through `NormalizationHelpers`, which resolves to the same implementation Parse uses in that process. On Unity that's the built-in normalizer, never Mono's broken `string.Normalize`.

It's slow, a few minutes even on a warm machine, so it's a pre-merge check for risky changes (reflection, generics, anything AOT-hostile), not the everyday loop. New tests in the three synced folders get picked up automatically. The wiring that makes it work (the `InternalsVisibleTo` grants, the `csc.rsp` language-version bump, the `CopyToUnity` build target) is documented in comments in `InductorParser.csproj` and the Unity folder.
