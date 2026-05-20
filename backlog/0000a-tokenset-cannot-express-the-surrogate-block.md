# TokenSet can't express the surrogate block, so a grammar can't match or exclude a bare surrogate code unit

Found while rewriting serde_json's JSON string parser (`E2ESamples/JsonStringEscapes/`). The grammar needed to reject a bare unpaired surrogate code unit sitting literally in the string content, which is serde_json's `InvalidUnicodeCodePoint` error. The obvious way is a `TokenSet` over the surrogate block `0xD800..0xDFFF`, used in the body's `NoneOf` and in a `Not(OneOf(...))` check:

```csharp
var loneSurrogate = OneOf(TokenSet.Range(0xD800, 0xDFFF));
```

That throws at construction:

```
System.ArgumentOutOfRangeException: Must be a valid Unicode scalar value
(0..0x10FFFF, excluding surrogates 0xD800..0xDFFF). (Parameter 'low')
```

`TokenSet.Range` and `TokenSet.Single` call `ValidateScalarValue` on each endpoint, and a surrogate isn't a scalar value, so any range or singleton whose endpoint lands in `0xD800..0xDFFF` is rejected.

You also can't build the block by complement. The `~` operator routes through `EmitIntervalSkippingSurrogates`, which splits every emitted interval around `0xD800..0xDFFF`, so a complemented set never contains a surrogate (`TokenSet.Universe` is documented as "0..0x10FFFF minus the surrogate block"). And you can't intersect two straddling ranges down to the block either, because `Range(a, b) & Range(c, d)` needs an endpoint at `0xD800` or `0xDFFF` to land there, and those endpoints are rejected.

So the surrogate block is unreachable from the public API. The only `TokenSet` that can *contain* a surrogate code unit is a `Range` with two non-surrogate endpoints that straddles the block (`Range(0, 0x10FFFF)`, `Range(0xD7FF, 0xE000)`), where the surrogates ride along as interior slots, and such a set always also contains non-surrogate code points you didn't want.

This contradicts `TokenSet.ContainsToken`, which has a deliberate lone-surrogate branch: a one-char surrogate token is matched against the rune intervals by its UTF-16 code unit (see the comment there, and BugSearchLog `0000-2026-05-15-scanwhile-rune-fast-path-vs-lone-surrogate`). The matching side supports surrogate code units on purpose. The set-building side gives a grammar author no way to produce a set whose intervals are the surrogate block.

## Where it came up

`E2ESamples/JsonStringEscapes/`. Five of serde_json's six string errors became grammar rules. The sixth, `InvalidUnicodeCodePoint`, couldn't, so `Rewrite/JsonStringParser.cs` does it as a post-parse `FindUnpairedSurrogate` scan. That works, but a grammar can't say "a surrogate code unit isn't a legal body character" the way it says it for control characters one line above with `TokenSet.Range(0x00, 0x1F)`.

## Fix

Four changes, together. The mental model: a `TokenSet` is a set of values in `[0, 0x10FFFF]`, surrogates included as ordinary code points. They only enter through explicit named APIs, and `|`, `&`, `~` are honest set algebra over that universe with no special-case handling.

```csharp
// All surrogate code units U+D800..U+DFFF as a TokenSet. Matchable only by
// grammars compiled with Compile(null), where the lexer surfaces a lone
// surrogate as a one-char token.
public static readonly TokenSet Surrogates;

// Build a TokenSet whose endpoints are both surrogate code units. Rejects
// non-surrogate endpoints so the spelling stays unambiguous.
public static TokenSet SurrogateRange(int low, int high);
```

1. Add the two APIs above. Under `Compile(null)`, they build sets that the lexer's lone-surrogate tokens match.

2. `Range(low, high)` (the existing scalar factory) splits its emitted intervals around the surrogate block. Today `Range(0, 0x10FFFF)` silently contains the surrogate block as interior slots, and a `ContainsToken` against a lone-surrogate token under `Compile(null)` quietly matches, so there's no way to spell "every scalar value, no surrogates" at all. After the change `Range(0, 0x10FFFF)` means exactly that, and a grammar that genuinely wants surrogates too writes `Range(0, 0x10FFFF) | Surrogates`. `Single` and `Runes` already reject surrogate-bearing arguments at construction, so they need no change.

3. `~` stops stripping. Today it routes through `EmitIntervalSkippingSurrogates`, which drops the surrogate block from every result. That preserved the "TokenSet is scalars only" invariant when surrogates couldn't get in any other way, but once `Surrogates` exists the stripping breaks involution: take any `A` that contains a surrogate, and `~~A` no longer contains it, so `~~A ≠ A`. After the change `~` is honest set complement over `[0, 0x10FFFF]`. `~~A == A`, `~Empty == Universe`, and `Surrogates == ~Range(0, 0x10FFFF)` falls out of the algebra. The named constant is still worth keeping for readability.

4. `|` and `&` need no change. They were already honest set union and intersection, and they operate over the bigger universe naturally.

The rule that falls out: surrogate code units enter a `TokenSet` only through `Surrogates` or `SurrogateRange`. After that, `|`, `&`, and `~` are set algebra. They preserve, intersect, or complement whatever is in their operands, with no silent surrogate handling on either side.

This is a breaking change in two places under `Compile(null)`:

- Existing `Range(big, big)` that relied on the silent surrogate inclusion. BugSearchLog `0000-2026-05-15-scanwhile-rune-fast-path-vs-lone-surrogate` calls out `ScanWhile(TokenSet.Range(0, 0x10FFFF))` as a generic "any rune" idiom. It now needs `| Surrogates` to keep matching lone surrogates.
- Existing `~set` (and `NoneOf(set)`) that relied on the silent surrogate stripping. After the change `~Letters` includes lone surrogate code units, because Letters doesn't cover them and the honest complement does. Code that doesn't want surrogates in the complement adds `& ~Surrogates`.

Default `Compile(FormC)` is unaffected. The lexer pre-rejects lone surrogates from input there, so none of this is observable.

For `E2ESamples/JsonStringEscapes/` specifically, `Rewrite/JsonStringParser.cs` drops the post-parse `FindUnpairedSurrogate` scan and the grammar's `JsonString` rule grows one more `Not(...)` next to the existing control-character check:

```csharp
Not(OneOf(TokenSet.Surrogates)).WithError(LoneSurrogateCodeUnitMessage),
```

The sixth error then moves from a post-parse check into the grammar, like the other five.
