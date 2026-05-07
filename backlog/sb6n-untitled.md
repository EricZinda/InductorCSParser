# Untitled

- Global NFD probe table for faster Compile under non-null normalization

Compile under any non-null `NormalizationForm` walks every entry in every `OneOf` / `NoneOf` set to compute canonical equivalents, plus calls `Normalize(form)` on every literal in `Token` / `Literal` / `LiteralIgnoreAsciiCase`. For grammars that pull in large category sets the per-Compile cost adds up: `OneOf(TokenSet.Letters)` walks ~130K entries, `OneOf(TokenSet.Universe)` walks ~1M. Estimated 50-200ms for Letters-sized sets and 500ms-2s for Universe-sized ones, paid on every Compile.

The equivalent of the per-rune `IsNormalized(form)` / `Normalize(form)` calls can be amortized into a single one-time hashmap. A `Lazy<Dictionary<int, string>>` populated on the first non-null Compile maps every NFD-non-stable rune to its NFD form, computed by walking the BCL one time across all valid runes. Subsequent Compiles do an O(1) hashmap lookup per entry instead of an O(BCL probe) string-allocation per entry. The first Compile pays the build cost (estimated 1-3 seconds across all valid runes); every Compile after that is fast.

Bounded one-time cost vs. unbounded per-Compile cost. Most users that compile multiple grammars or iterate on a grammar will pay back the build cost in the second or third Compile.

## Verify the bug (Write Test First)

Add a Stopwatch-wrapped Compile test in `src/InductorParser.Tests/Core/NormalizationTests.cs`:

```csharp
[Test]
public void Compile_with_letters_set_finishes_within_one_second()
{
    var rule = OneOf(TokenSet.Letters);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    rule.Compile();
    watch.Stop();
    Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1000),
        $"Compile took {watch.ElapsedMilliseconds}ms; should be sub-second after the global probe table is in place");
}
```

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~Compile_with_letters_set_finishes"
```

Expected: passes today (Letters is sub-second per estimate). Same test for `TokenSet.Universe` is the more interesting one, but is slower than this sub-second bound and is the actual stress case the probe table fixes.

## Fix

Add a private static `Lazy<Dictionary<int, string>>` to `TokenSet.cs` (or a new internal helper class) keyed by rune. The lazy initializer walks every valid rune (skipping surrogates U+D800..U+DFFF and noncharacters), calls `string.IsNormalized(text, NormalizationForm.FormD)` and `text.Normalize(NormalizationForm.FormD)` on each, and stores the mapping for non-stable entries. `WithCanonicalEquivalents()` and the literal-rule conversion path then look up runes in this table instead of calling the BCL per-rune.

The probe table is keyed by NFD only. NFC of a multi-rune cluster goes through `string.Normalize(NormalizationForm.FormC)` on the whole cluster as today; the cluster is small so this is fine. NFKC / NFKD are also small enough at the point of use to skip caching.

## Verify the Fix

Re-run the Stopwatch test. Add an analogous test for `TokenSet.Universe`:

```csharp
[Test]
public void Compile_with_universe_set_finishes_within_two_seconds_first_call()
{
    var rule = OneOf(TokenSet.Universe);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    rule.Compile();
    watch.Stop();
    Assert.That(watch.ElapsedMilliseconds, Is.LessThan(2000),
        $"first-Compile took {watch.ElapsedMilliseconds}ms; includes one-time probe build");
}
```

Run again and confirm a second Compile of the same grammar in the same process is fast (sub-100ms):

```csharp
[Test]
public void Compile_second_call_after_warm_probe_table_is_fast()
{
    OneOf(TokenSet.Letters).Compile();  // warm the cache
    var rule = OneOf(TokenSet.Universe);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    rule.Compile();
    watch.Stop();
    Assert.That(watch.ElapsedMilliseconds, Is.LessThan(100),
        $"warm Compile took {watch.ElapsedMilliseconds}ms; should be O(set-size) lookups only");
}
```
