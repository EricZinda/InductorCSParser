# Mapping Positions Through Unicode Normalization

This doc explains how we map a position in a normalized string back to a position in the caller's original input. Read this if you're working on `NormalizedPositionMap` or want to know why the position translation is shaped the way it is.

If you just want to use normalization in a grammar, start with these:

- [Primer3.md](Primer3.md) walks through what normalization is and how to pick a form.
- [UnicodeModel.md, Step 1: Text Normalization](UnicodeModel.md#step-1-text-normalization) is the conceptual model the parser follows.
- [UnicodeInternalsArchitecture.md, Normalization](UnicodeInternalsArchitecture.md#normalization) covers the per-grammar policy and validation.
- [Primer4.md](Primer4.md) shows security uses of FormKC.
- [UnicodeGotchas.md](UnicodeGotchas.md) lists what normalization can't fix.

## Background

Unicode lets you write the same visible character multiple ways. The letter "é" can be stored as one .NET `char` (U+00E9) or as two chars ("e" plus U+0301, the combining acute). Both render identically. The "ﬁ" ligature can be one char (U+FB01) or two ("f" plus "i"). A Korean syllable can be one precomposed character or several separate component characters.

*Normalization* converts text to one chosen representation. .NET's `string.Normalize(NormalizationForm form)` does this. The four forms split into two pairs:

- `FormC` and `FormD` are the *canonical* forms. They convert between precomposed and decomposed Unicode (like "é" turning into "e" plus combining acute, or back). They don't expand ligatures, don't change ASCII width, and don't change anything visible.
- `FormKC` and `FormKD` are the *compatibility* forms. They do everything the canonical forms do, then go further. They expand ligatures ("ﬁ" becomes "fi"), unify half-width and full-width Latin, flatten superscripts and other typographic variants, and handle other "looks similar but stored differently" cases.

A *grapheme* is what a user thinks of as one character, even when it's stored as multiple chars. "é" is one grapheme whether stored as 1 char or 2. .NET's `StringInfo.GetNextTextElement` walks a string one grapheme at a time.

## The problem

By default, the parser normalizes input before lexing, so the positions the engine works with are offsets into the *normalized* string. The caller wants the corresponding position in the *original* string they handed us, so editor highlighting points at the right place. This comes up two ways: a failure position when parsing fails, and a symbol's source range (`Symbol.SourceRange`) on a successful parse. Both go through `NormalizedPositionMap.TranslateToOriginal`.

Two examples show why this isn't trivial.

The easy case: the "ﬁ" ligature (one char) becomes "fi" (two chars) under FormKC. If the lexer fails at position 1 in the normalized string (between the 'f' and 'i'), there's no separate 'i' in the original. Both came from the one ligature char. The right answer is position 0 in the original, so editor highlighting points at the whole ligature.

The hard case: the two characters U+3131 and U+314F (Korean compatibility jamo, displayed as "ㄱㅏ") compose into one syllable character `가` (U+AC00) under FormKC. Two original chars become one normalized char. If the lexer fails at position 1 in the normalized string (end of `가`), the right answer in the original is position 2 (after both original chars). Simple char counting gets this wrong.

## The key idea

For canonical forms (FormC, FormD), the count of *graphemes* doesn't change after normalization. Individual graphemes might have different *character* counts (precomposed "é" is one char, decomposed is two), but the K-th grapheme in the original always corresponds one-to-one with the K-th grapheme in the normalized form. From the Unicode spec, [UAX #29 §3](https://www.unicode.org/reports/tr29/#Grapheme_Cluster_Boundaries):

> "A key feature of Unicode grapheme clusters (both legacy and extended) is that they remain unchanged across all canonically equivalent forms of the underlying text. Thus the boundaries remain unchanged whether the text is in NFC or NFD."

The §3 quote names two specific normalized forms, but the same guarantee covers the caller's original input too. Per [UAX #15 §1.1](https://www.unicode.org/reports/tr15/#Introduction), *canonical equivalence* is a relation between "sequences of characters which represent the same abstract character." The caller's original and the normalized version represent the same abstract characters (that's what canonical normalization preserves), so they're canonically equivalent. §3 applies to them as a pair.

So for canonical forms, the algorithm walks both strings side by side with `StringInfo.GetNextTextElement`, one grapheme at a time. When the normalized pointer reaches the failure position, the original pointer is at the right spot.

Compatibility forms (FormKC, FormKD) don't get this guarantee. They can produce sequences that recompose into a different grapheme count. In Unicode 16 the only such case is the Korean compatibility-jamo example above. To handle it, the algorithm verifies at each grapheme boundary that the side-by-side walk hasn't drifted.

## The algorithm

Walk the original string grapheme by grapheme. Maintain a parallel pointer `normPos` into the normalized string, plus a *chunk* of the original accumulated from the last verified grapheme boundary up to the current position.

At each new grapheme boundary:

1. Normalize the chunk.
2. Check whether the result matches the normalized string starting at `normPos`.
3. If yes, this grapheme boundary is verified safe. Advance `normPos` by the chunk's normalized length. Start a new chunk at the current grapheme boundary.
4. If no, this grapheme boundary doesn't line up with a boundary in the normalized string. Leave `normPos` alone. The chunk grows by another grapheme on the next iteration.

Pseudocode:

```
TranslatePosition(original, normalized, normalizedPosition, F):
    if normalizedPosition <= 0:                   return 0
    if normalizedPosition >= length(normalized):  return length(original)

    origPos = 0
    normPos = 0
    lastSafeOrigPos = 0

    while origPos < length(original):
        grapheme = next-grapheme of original at origPos
        origPos += length(grapheme)
        chunk = original[lastSafeOrigPos : origPos]
        chunkNormalized = F(chunk)

        if normalized starting at normPos starts with chunkNormalized:
            newNormPos = normPos + length(chunkNormalized)
            if newNormPos > normalizedPosition:   return lastSafeOrigPos   // overshot, snap back
            if newNormPos == normalizedPosition:  return origPos            // exact match
            normPos = newNormPos
            lastSafeOrigPos = origPos
        // else: doesn't line up; chunk grows next iteration

    return length(original)
```

For canonical forms, the check always passes (per the §3 guarantee above). The chunk is always one grapheme. O(N) total work.

For compatibility forms, the check fails for a couple of iterations whenever compatibility decomposition produces one of the rare grapheme-count-changing cases. The chunk absorbs the extra graphemes until the region ends and the check passes again. These multi-grapheme regions are tiny in practice (two graphemes for Korean compatibility jamo), so the extra cost is small.

Worst case is O(N²) if every grapheme were part of one giant multi-grapheme region. Real text doesn't look like that, and the walker runs only on parse failure (off the hot path), so even the pathological cost is acceptable.

### When the failure lands inside a multi-grapheme region

When the failure position falls inside a multi-grapheme region, the algorithm snaps back to the start of that region in the original. Editor highlighting then covers the whole affected piece of text instead of pointing somewhere in the middle of it. This matches the project's snap-to-grapheme-start convention.


## Unicode 16 and the future

[UAX #15 §9.2](https://www.unicode.org/reports/tr15/#Normalization_Contexts) warns about new context-sensitive composites added in Unicode 16 for three scripts: Tulu-Tigalari, Kirat Rai, and Gurung Khema:

> "Starting with Unicode 16.0, there are several new characters ... with normalization behavior not seen in characters encoded in earlier versions ... composite characters that can occur in NFC/NFKC strings, but when those characters occur in a context directly following certain other characters, performing an NFC or NFKC normalization will change those composite characters."

That's the same shape as our Korean case. Characters at one position decompose into pieces that recompose with characters at a nearby position.

We checked the [Unicode 16 character database](https://www.unicode.org/Public/16.0.0/ucd/) for these scripts:

- All the new composites decompose into two characters.
- For Tulu-Tigalari and Gurung Khema, the second decomposed character is a combining mark. UAX #29 keeps a combining mark inside the same grapheme as the preceding character.
- For Kirat Rai, the second decomposed character is a Hangul-style vowel, which UAX #29 also keeps inside the same grapheme.

So none of these add new multi-grapheme cases. As of Unicode 16, Korean compatibility jamo are still the only case where compatibility normalization changes the grapheme count.

Sample code points to verify against when a future Unicode version ships: U+1138E TULU-TIGALARI LETTER AI, U+113C7 TULU-TIGALARI VOWEL SIGN OO, U+16D69 KIRAT RAI VOWEL SIGN O, U+16121 GURUNG KHEMA VOWEL SIGN U.

If a future Unicode release does introduce a real new grapheme-count-changing composite, it just works. The runtime check on each grapheme boundary detects any kind of grapheme-count change, not just the Korean case it was designed for. No code change needed.
