# Broaden RuneSet (renamed TokenSet) to include grapheme clusters

## Context

Today RuneSet is a set of single Unicode scalar values, with closed set algebra (union, intersection, complement) over the rune universe. Multi-rune graphemes (skin-toned emoji, ZWJ sequences, regional indicator pairs, base+combining-mark clusters, etc.) aren't members of any RuneSet, and `Runes("...")` actively rejects them with one CRLF-shaped exception.

Grammars that want to match a class of grapheme clusters have no clean tool. The only way is `FirstOf(Grapheme(a), Grapheme(b), ...)`, which doesn't compose with set algebra and can't be intersected with `RuneSet.Letters` or used as a `NoneOf` exclusion. The goal is to broaden the type so a single set can describe "this rune class plus these specific multi-rune graphemes" and still behave like a set. As part of the same change, rename `RuneSet` to `TokenSet` and `RuleStartRequirements.FirstConsumedRunes` to `FirstConsumedTokens`, since "rune" is no longer accurate for what the set holds and what a rule's lookahead is filtering against.

## The set-orientation challenge

The universe of grapheme clusters is unbounded (any rune sequence that respects UAX #29 boundaries is a grapheme). Complement against an unbounded universe can't be represented by a finite explicit set. The other invariants (canonical form, fast Contains, closed union and intersection, equality, hashing) carry over fine, but complement needs a defined behavior.

Decision: complement is defined only when the set has no multi-rune graphemes. `~set` returns the rune-complement of the rune part. If the set contains any multi-rune graphemes, `~` throws InvalidOperationException with a message that explains why and points at the workaround (build the rune-only mask separately and union the multi-rune entries back in). Silently dropping multi-rune entries would be a quiet bug. The idiom `set & ~exclusions` keeps working in the typical case where `exclusions` is rune-only, which is what every current call looks like.

## Recommended design

Rename the type to `TokenSet`. Extend its data with a sorted, deduplicated array of multi-rune grapheme strings:

    private readonly Interval[] _ranges;
    private readonly string[] _multiRuneGraphemes;   // sorted ordinal, deduped

Single-rune graphemes still go in `_ranges`. Only graphemes that occupy two or more runes go in `_multiRuneGraphemes`. That keeps the rune fast path (binary search of intervals) untouched for the common case and avoids any behavior change for callers that don't add multi-rune content.

Membership:

    Contains(int rune)        → checks _ranges only
    Contains(Rune r)          → checks _ranges only
    Contains(string grapheme) → if grapheme is one rune (1-2 chars decoding to a single Rune),
                                 check _ranges; else binary search _multiRuneGraphemes

Set operations:

    a | b   union          → ranges-union, plus sorted merge of grapheme arrays
    a & b   intersection   → ranges-intersection, plus sorted merge-intersect of grapheme arrays
    ~a      complement     → if a._multiRuneGraphemes.Length > 0, throw InvalidOperationException;
                              otherwise return rune-complement of ranges

Canonical form: ranges are already sorted non-overlapping non-adjacent. Multi-rune graphemes get sorted ordinal and deduped at construction. Two TokenSets compare equal iff both arrays compare equal element-wise. Hash mixes both.

Factory:

    TokenSet.Runes(string source)
        Walks StringInfo text elements in source and adds each one to the set. A
        single-rune text element becomes an interval add. A multi-rune text element
        becomes a string add. The current rejection of multi-rune text elements is
        removed, and the CRLF special case folds away as a degenerate version of the
        general path. Callers that want to combine multiple sources just union them:
        `Runes("a") | Runes("👋🏽")`.

The other factories (Single, Range, Category, Letters, Digits, Whitespace, XidStart, XidContinue, the Ascii variants, SingleRuneLineTerminators) all continue to produce rune-only sets and are unaffected.

ToString: multi-rune graphemes go in the same bracketed list as everything else, with no special quoting. Each one renders as the user-perceived character it is, e.g. `[a-z,👋🏽,🇺🇸]`. The cap on long range lists carries over, counting multi-rune graphemes as entries on equal footing with rune ranges.

## Rule integration

Rules that consume a TokenSet today read one rune at a time. After the change, three of them need a multi-rune-token path. Concretely:

[OneOfRule.cs](../src/InductorParser/OneOfRule.cs)
    Currently fails the match when `token.RuneValue == -1` (multi-rune token under
    GraphemeLexer). New behavior: when RuneValue is -1 and the set has any multi-rune
    graphemes, binary search `_multiRuneGraphemes` against the token's `Chars` span
    (compare as ordinal). If the set has no multi-rune graphemes, keep the fast fail.

[NoneOfRule.cs](../src/InductorParser/NoneOfRule.cs)
    Currently passes multi-rune tokens through unconditionally. Same shape: when
    RuneValue is -1 and the set has any multi-rune graphemes, check the span against
    the array and fail the rule on a hit; otherwise pass through.

[ScanWhileRule.cs](../src/InductorParser/ScanWhileRule.cs) and [ScanUntilRule.cs](../src/InductorParser/ScanUntilRule.cs)
    Both call `lexer.AdvanceWhileSingleRuneIn(_set)` style helpers. When the set is
    rune-only, keep that path. When the set has multi-rune graphemes, the lexer needs
    a slower path that pulls one token at a time and tests the full grapheme. Add a
    second method on the lexer (`AdvanceWhileIn(TokenSet)` that handles both) and
    let ScanWhile/ScanUntil pick based on `_set.HasMultiRuneGraphemes`.

[RuleStartRequirements.cs](../src/InductorParser/RuleStartRequirements.cs)
    Rename `FirstConsumedRunes` to `FirstConsumedTokens`. The lookahead test in the
    start-requirements check needs the same dual path (rune fast path plus the
    multi-rune fallback that walks the multi-rune array via the token's Chars span).

Search for other consumers via grep for `RuneSet` outside `RuneSet.cs` (now `TokenSet.cs`) to make sure no other path silently breaks.

## Tradeoffs

Complement throws on sets with multi-rune content. Worth a clear doc-comment paragraph and the InvalidOperationException message itself should explain why and point at the workaround.

Memory cost. An empty multi-rune array (or a shared `Array.Empty<string>()` sentinel) costs nothing meaningful. Sets with multi-rune entries cost one string reference per entry plus the chars.

Equality and hashing now depend on string content. Use ordinal comparison and `string.GetHashCode(StringComparison.Ordinal)` to keep things deterministic.

## Performance

Rune-only callers (everything in the codebase today) pay nothing. The new field starts empty, every multi-rune code path is gated behind `_multiRuneGraphemes.Length > 0` and short-circuits before any string work, and the data layout for `_ranges` is unchanged. Same Contains, same `|` `&` `~`, same Equals, same hash, same machine code in the hot loop.

Mixed sets, single-rune token (typical `OneOf` / `NoneOf` case when the input is plain text). The rule's `if (token.RuneValue >= 0)` branch goes first, hits the rune fast path, and the multi-rune array is never consulted. One additional cheap branch on construction-known data.

Mixed sets, multi-rune token (the new capability, fires when the input is an emoji or ZWJ cluster). Binary search over `_multiRuneGraphemes` with `MemoryExtensions.SequenceCompareTo(ReadOnlySpan<char>, ReadOnlySpan<char>)` over `string.AsSpan()`, allocation-free. Cost is `O(log g × k)` where `g` is the multi-rune entry count and `k` is the longest grapheme length in the set. For a hundred-emoji set that's around seven span comparisons of a handful of chars each. Not free, but well below a single token-allocation cost.

ScanWhile and ScanUntil dispatch once per call based on `_set.HasMultiRuneGraphemes`, not per character. The rune-only fast path stays inline-char-loop fast. The mixed path pulls a full Token per iteration (per-grapheme overhead instead of per-char inline), which is the unavoidable cost of grapheme-aware scanning, but only fires when the grammar actually contains multi-rune entries.

Equality and hashing become slightly heavier when sets carry multi-rune content (the string array gets compared / hashed alongside the intervals), but TokenSet equality isn't a hot path. RuleStartRequirements composition runs at grammar-build time, not at parse time.

## Files to modify

src/InductorParser/RuneSet.cs (rename to TokenSet.cs)
    Add the multi-rune field, update the private constructor, rewrite the doc comment,
    extend Runes(string) to accept multi-rune text elements, extend Contains to take
    string, extend |, &, Equals, GetHashCode, ToString. Make ~ throw when multi-rune
    entries are present.

src/InductorParser/RuneSet.Xid.cs (rename to TokenSet.Xid.cs)
    Type rename only.

[src/InductorParser/OneOfRule.cs](../src/InductorParser/OneOfRule.cs)
    Add the multi-rune token path described above.

[src/InductorParser/NoneOfRule.cs](../src/InductorParser/NoneOfRule.cs)
    Same.

[src/InductorParser/ScanWhileRule.cs](../src/InductorParser/ScanWhileRule.cs) and [src/InductorParser/ScanUntilRule.cs](../src/InductorParser/ScanUntilRule.cs)
    Pick the rune fast path or the grapheme-aware path based on whether the set has
    any multi-rune graphemes.

[src/InductorParser/Lexing/Lexer.cs](../src/InductorParser/Lexing/Lexer.cs) (or wherever AdvanceWhileSingleRuneIn lives)
    Add the grapheme-aware variant. Keep the existing rune-fast variant.

[src/InductorParser/RuleStartRequirements.cs](../src/InductorParser/RuleStartRequirements.cs)
    Rename `FirstConsumedRunes` to `FirstConsumedTokens`. Update the lookahead-fail
    path to handle multi-rune tokens against `FirstConsumedTokens`.

All call sites of `RuneSet` and `FirstConsumedRunes`
    Mechanical rename. Tests, grammars, docs.

## Verification

Unit tests:
- `TokenSet.Runes("a👋🏽é")` produces the expected mix of intervals and multi-rune entries. Use `LatinEAcuteGrapheme` from UnicodeExamples.cs to pin down whether StringInfo merges "é" into one text element on the test runtime.
- Set algebra: `(rune-only) | (mixed)`, `(mixed) & (mixed)` produce the expected canonical content.
- Complement: `~(rune-only)` works as it does today; `~(mixed)` throws InvalidOperationException with the documented message.
- Equality: the same logical set built two different ways compares equal and has equal hash.
- All existing rune-only TokenSet tests pass unchanged after the rename.

Integration tests via OneOf and NoneOf:
- `OneOf(TokenSet.Runes(USFlagGrapheme + WomanShruggingGrapheme))` parses the corresponding inputs successfully under GraphemeLexer.
- `NoneOf(TokenSet.Runes(USFlagGrapheme))` rejects the flag and accepts neighboring graphemes.
- A mixed set: `TokenSet.Letters | TokenSet.Runes(USFlagGrapheme)` matches both letters and the flag in OneOf.
- Under RuneLexer, multi-rune entries are unreachable (each token is one rune), so they never match. Pin this with a regression test.

ScanWhile: a set with multi-rune entries scans across a string of mixed letters and emoji and stops at the first non-member. The rune-only fast path is exercised by the existing tests.

End-to-end: pick one of the existing grapheme tests and rewrite a `FirstOf(Grapheme(...), ...)` chain as a single `OneOf(TokenSet.Runes(...))` to confirm the new path produces the same parse result.
