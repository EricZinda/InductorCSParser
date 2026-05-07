# Untitled

- `NoneOf("é").Compile(FormD).Parse("é")` returns `Success = true` — the WRONG answer. The decomposed multi-rune token isn't in the set, the membership check returns false, NoneOf inverts that into a false-positive match.

Root cause: a `TokenSet` stores rune intervals (single Unicode scalars) and multi-rune entries (specific cluster strings). When the lexer normalizes input to a different canonical form than how the entry was stored, the membership test sees a token shape the set doesn't have, even though the entry "is" the same character semantically.

## Verify the Bug (Write Test First)

Add to `src/InductorParser.Tests/Core/UnexpectedUnicodeTests.cs`:

```csharp
[Test]
public void OneOf_with_precomposed_entry_matches_decomposed_input_under_FormD()
{
    var rule = OneOf("é");
    rule.Compile(System.Text.NormalizationForm.FormD);

    var precomposed = rule.Parse("é");           // U+00E9
    var decomposed = rule.Parse("é");      // 'e' + combining acute

    Assert.That(precomposed.Success, Is.True, precomposed.ErrorMessage);
    Assert.That(decomposed.Success, Is.True, decomposed.ErrorMessage);
}

[Test]
public void OneOf_with_decomposed_entry_matches_precomposed_input_under_FormC()
{
    var rule = OneOf("é");
    rule.Compile();

    Assert.That(rule.Parse("é").Success, Is.True);
    Assert.That(rule.Parse("é").Success, Is.True);
}

[Test]
public void NoneOf_with_precomposed_entry_rejects_decomposed_input_under_FormD()
{
    var rule = AllOf(NoneOf("é"), Eof());
    rule.Compile(System.Text.NormalizationForm.FormD);

    Assert.That(rule.Parse("é").Success, Is.False);
    Assert.That(rule.Parse("é").Success, Is.False);
    Assert.That(rule.Parse("a").Success, Is.True);
}

[Test]
public void OneOf_with_singleton_decomposing_rune_matches_normalized_form()
{
    // U+2126 OHM SIGN canonically decomposes to U+03A9 GREEK CAPITAL OMEGA.
    var rule = OneOf("Ω");  // U+2126
    rule.Compile();

    Assert.That(rule.Parse("Ω").Success, Is.True);  // U+2126 input
    Assert.That(rule.Parse("Ω").Success, Is.True);  // U+03A9 input
}

[Test]
public void Compile_null_does_not_trigger_NFD_probe_build()
{
    if (InductorParser.TokenSet.RuneNfdProbeIsBuilt)
        Assert.Inconclusive("Probe was already built by an earlier test in this process.");

    var rule = AllOf(OneOf("é"), Eof());
    rule.Compile(null);
    rule.Parse("é");

    Assert.That(InductorParser.TokenSet.RuneNfdProbeIsBuilt, Is.False);
}
```

Run with:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "OneOf_with_precomposed_entry|OneOf_with_decomposed_entry|NoneOf_with_precomposed_entry|OneOf_with_singleton_decomposing|Compile_null_does_not_trigger" --nologo
```

Before the fix the first four tests fail (the rules report parse failures or false-positive successes). The Compile-null test is order-dependent and will be Inconclusive if other tests in the process have already warmed the probe.

## Fix

Compile-time canonical-equivalent augmentation, scoped to OneOf and NoneOf, paid for only by Compile(form) callers.

**1. Add a static lazy NFD probe table to `src/InductorParser/TokenSet.cs`.** Maps every rune whose NFD form differs from itself to that NFD form's string. Built once per process by walking 0..0x10FFFF with `IsNormalized(NormalizationForm.FormD)` as the fast path and `Normalize(...)` only on the unstable few-thousand. Wrap the per-rune normalize calls in a try/catch on `ArgumentException` to skip the 66 Unicode noncharacters (U+FDD0..U+FDEF and U+xFFFE/U+xFFFF on every plane) that .NET's normalization rejects.

**2. Add `TokenSet.WithCanonicalEquivalents()`** that returns a new TokenSet augmented with each entry's canonical-equivalent form: for each single-rune interval entry, lookup in the probe and add the NFD form (single-rune as a new interval, multi-rune as a graphemes-list entry); for each multi-rune entry, compute its NFC and add as a single-rune interval if NFC is one rune. Idempotent — the dedupe step in the merged-graphemes builder folds out double-adds. Returns `this` unchanged when no augmentation applied.

**3. Add `CollectNormalizationOffenders` overrides** on [src/InductorParser/OneOfRule.cs](src/InductorParser/OneOfRule.cs) and [src/InductorParser/NoneOfRule.cs](src/InductorParser/NoneOfRule.cs):

```csharp
internal override void CollectNormalizationOffenders(
    NormalizationForm form,
    List<(Rule rule, string original, string normalized)> offenders,
    List<ArgumentException> failures)
{
    _set = _set.WithCanonicalEquivalents();
}
```

`_set` drops `readonly` so this can replace it. The same field is read by `TryParseRule`, `ComputeRuleStart`, and `LoweringSet`, so the recursive engine, state-machine, and prefilter all see the augmented view consistently.

**4. No residual validation of compatibility-fold cases.** `OneOf("ﬁ").Compile(FormKC)` is a real shape mismatch (ligature folds to two separate Latin-letter tokens, OneOf is single-token), but it surfaces as an ordinary parse failure when the user actually parses input containing 'ﬁ' — not a silent miss like the canonical-equivalence cases. Adding compile-time validation for compat folds also misfires on big category sets like `XidStart` that contain compat-folding code points alongside thousands of harmless ones, because the user's grammar is correct for the inputs they actually parse.

## Verify the Fix

Run the same five tests; they should now pass (with Compile-null possibly Inconclusive depending on test order). Also run the full suite to confirm no regression on the 1841 existing tests.

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --nologo
dotnet test ExperimentalSrc/InductorParser.Tests/InductorParser.StateMachine.Tests.csproj --nologo
```

Expected: 1845 + 1 inconclusive (or 1846 if the inconclusive resolves) in src; 94 in StateMachine.

## Cost

The NFD probe takes 1-3 seconds the first time any Compile(form) call hits a OneOf or NoneOf rule. After that it's cached; subsequent calls are O(set_size) hashmap lookups. Compile(null) callers never trigger the probe and pay nothing. If the 1-3 seconds first-touch is unacceptable for some application, a follow-up optimization is to ship a hardcoded table and skip the runtime walk; that's worth filing separately if measurements show the probe is on the critical path.
