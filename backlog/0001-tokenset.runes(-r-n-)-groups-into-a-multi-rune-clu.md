# TokenSet.Runes("\r\n") groups into a multi-rune cluster, breaks `~Runes("\r\n")` and the rebar BenchmarkRegistry static init

The post-pull "Feature: form-aware Compile auto-converts grammar literals" commit (`645deda`) reworked `TokenSet.Runes(string)` so it walks the input by grapheme cluster instead of by rune. Under UAX #29 the `\r\n` pair is one cluster (CRLF), so `TokenSet.Runes("\r\n")` now stores `"\r\n"` as a multi-rune entry rather than the two separate single-rune intervals it used to produce. That breaks the `~` complement: `TokenSet.operator ~` throws `InvalidOperationException` on any set with multi-rune entries, by design (the universe of grapheme clusters is unbounded).

User-visible consequence: `private static readonly TokenSet NotNewline = ~TokenSet.Runes("\r\n");` in `ExperimentalSrc/BenchMarks/Rebar/BenchmarkPlan.cs` line 331 throws at static-cctor time. Every rebar benchmark that touches `NotNewline` (04-ruff-noqa/real, 04-ruff-noqa/tweaked, 08-words, 10-bounded-repeat/*, 11-unstructured-to-json/extract, and probably others that use line-oriented matching) fails with `TypeInitializationException` the first time `BenchmarkRegistry` is reached. The runner returns `0` on the FAIL path and reports the inner exception. Only the cases whose grammar functions don't reference `NotNewline` (01-literal/*, 02-literal-alternate/*) still work.

Likely the same shape exists in any caller-written grammar that wrote `~TokenSet.Runes("\r\n")` to mean "everything except CR or LF as runes." The post-merge `Runes` no longer does that. The intent has to be restated as `~(TokenSet.Single('\r') | TokenSet.Single('\n'))` (rune-level union, then complement) to get the old semantics back.

## Verify the regression

Reproduces in the rebar runner directly:

```
dotnet build -c Release ExperimentalSrc/BenchMarks/Rebar/RebarRunner.csproj -m:1 -p:BuildInParallel=false
dotnet run -c Release --project ExperimentalSrc/BenchMarks/Rebar/RebarRunner.csproj --no-build -- --bench-shortcut
```

Expected output before any fix: 04-ruff-noqa/real, 08-words/all-english, and 10-bounded-repeat/letters-en (and any case that uses BenchmarkRegistry's `NotNewline` field) print `SKIP,InvalidOperationException: Cannot complement a TokenSet that contains multi-rune graphemes...`. The 01-literal and 02-literal-alternate cases still produce real timing numbers.

Add a unit test in `src/InductorParser.Tests/Core/TokenSetTests.cs` (or wherever the existing complement coverage lives) that pins the regression:

```csharp
[Test]
public void Runes_with_crlf_treats_it_as_multi_rune_cluster_breaking_complement()
{
    // Pinning test for the post-merge Runes change. The old
    // intent of `~Runes("\r\n")` was "any rune except CR or LF",
    // a rune-level set. Post-merge Runes walks the string by
    // grapheme, so "\r\n" becomes the multi-rune CRLF cluster
    // and complement throws. This test pins the surprising
    // semantics so future readers see why grammar code that
    // used to work now needs the rune-level workaround.
    var crlfSet = TokenSet.Runes("\r\n");
    Assert.That(crlfSet.HasMultiRuneGraphemes, Is.True);
    Assert.Throws<InvalidOperationException>(() => { var _ = ~crlfSet; });

    // Workaround: build the rune-only union explicitly.
    var runes = TokenSet.Single('\r') | TokenSet.Single('\n');
    var inverse = ~runes; // doesn't throw
    Assert.That(inverse.Contains('\r'), Is.False);
    Assert.That(inverse.Contains('\n'), Is.False);
    Assert.That(inverse.Contains('a'), Is.True);
}
```

## Fix

Two-part fix.

1. Restore the rebar runner. In `ExperimentalSrc/BenchMarks/Rebar/BenchmarkPlan.cs` line 331, replace the broken initializer with the rune-level workaround. Already applied locally but worth landing on master:

```csharp
private static readonly TokenSet NotNewline = ~(TokenSet.Single('\r') | TokenSet.Single('\n'));
```

2. Decide on `Runes` semantics. Two options:
   - **Document the breakage.** Add a doc comment on `TokenSet.Runes(string)` explaining that `"\r\n"` becomes the CRLF cluster, so `~Runes("\r\n")` no longer means "rune-level NotNewline." Push grammar authors to use explicit rune unions when they want rune-level semantics.
   - **Add a sibling factory.** Introduce `TokenSet.RuneChars(string)` (or rename: `Runes` is already taken in the API surface, so add an explicit `RunesAsCharSet` or similar) that ALWAYS treats every char position as an independent rune, ignoring grapheme grouping. Pre-merge `Runes` behavior under a new name. Existing `Runes` keeps the new grapheme-aware behavior. Then update the documented `~Runes` workaround in caller code to use the new factory.

Option 1 is less work but bites every existing grammar that used the pre-merge contract. Option 2 fixes both the BenchmarkPlan regression and the user-grammar surface area in one go.

## Verify the fix

After applying the rune-level workaround in BenchmarkPlan.cs:

```
dotnet run -c Release --project ExperimentalSrc/BenchMarks/Rebar/RebarRunner.csproj --no-build -- --bench-shortcut
```

Expected: every line outputs real timing samples; no SKIP rows. The 04-ruff-noqa, 08-words, and 10-bounded-repeat cases produce numbers in line with the README's published baseline (within run-to-run noise).

## Important

This is a quiet break: the static cctor fails on first reference, but the failure looks like a test/runtime issue, not a grammar bug, so it can hide behind "the rebar runner is broken on my machine" until someone digs in. Worth fixing soon so the rebar baseline doesn't drift further from reality. Other grammars in user code that wrote `~Runes("\r\n")` for line-oriented matching have the same latent break.
