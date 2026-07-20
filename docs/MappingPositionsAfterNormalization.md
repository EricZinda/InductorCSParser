# Mapping Positions Through Unicode Normalization

This doc explains how we map a position in a normalized string back to a position in the caller's original input. Read this if you're working on `NormalizedPositionMap` or want to know why the position translation is shaped the way it is.

If you just want to use normalization in a grammar, start with these:

- [Primer3.md](Primer3.md) walks through what normalization is and how to pick a form.
- [UnicodeInternalsArchitecture.md, Normalization](UnicodeInternalsArchitecture.md#normalization) is the conceptual model the parser follows: the per-grammar form and how Compile converts rule literals to it.
- [Primer4.md](Primer4.md) shows security uses of FormKC.
- [UnicodeGotchas.md](UnicodeGotchas.md) lists what normalization can't fix.

## Background

Unicode lets you write the same visible character multiple ways. The letter "é" can be stored as one .NET `char` (U+00E9) or as two chars ("e" plus U+0301, the combining acute). Both render identically. The "ﬁ" ligature can be one char (U+FB01) or two ("f" plus "i"). A Korean syllable can be one precomposed character or several separate component characters.

*Normalization* converts text to one chosen representation. .NET's `string.Normalize(NormalizationForm form)` does this. The four forms split into two pairs:

- `FormC` and `FormD` are the *canonical* forms. They convert between precomposed and decomposed Unicode (like "é" turning into "e" plus combining acute, or back). They don't expand ligatures, don't change ASCII width, and don't change anything visible.
- `FormKC` and `FormKD` are the *compatibility* forms. They do everything the canonical forms do, then go further. They expand ligatures ("ﬁ" becomes "fi"), unify half-width and full-width Latin, flatten superscripts and other typographic variants, and handle other "looks similar but stored differently" cases.

A *grapheme* is what a user thinks of as one character, even when it's stored as multiple chars. "é" is one grapheme whether stored as 1 char or 2. The parser's bundled UAX #29 segmenter (`GraphemeSegmentation`) walks a string one grapheme at a time.

## The problem

By default, the parser normalizes input before lexing, so the positions the engine works with are offsets into the *normalized* string. The caller wants the corresponding position in the *original* string they handed us, so editor highlighting points at the right place. This comes up two ways: a failure position when parsing fails, and a symbol's source range (`Symbol.SourceRange`) on a successful parse. Both go through `NormalizedPositionMap.TranslateToOriginal`.

Two examples show why this isn't trivial.

The easy case: the "ﬁ" ligature (one char) becomes "fi" (two chars) under FormKC. If the lexer fails at position 1 in the normalized string (between the 'f' and 'i'), there's no separate 'i' in the original. Both came from the one ligature char. The right answer is position 0 in the original, so editor highlighting points at the whole ligature.

The hard case: the two characters U+3131 and U+314F (Korean compatibility jamo, displayed as "ㄱㅏ") compose into one syllable character `가` (U+AC00) under FormKC. Two original chars become one normalized char. If the lexer fails at position 1 in the normalized string (end of `가`), the right answer in the original is position 2 (after both original chars). Simple char counting gets this wrong.

## The key idea

The algorithm depends on four facts. First, the normalized string is ground truth: it's exactly what the whole original converts to in one go. Second, graphemes are the right step size: the parser reads input as whole graphemes, so every position this walker is asked to translate is a grapheme boundary, and every answer anyone can use is one too. Normalization also rearranges runes so freely inside a grapheme that a boundary in the middle of one usually doesn't survive conversion (the algorithm section below shows what goes wrong with rune-sized steps). Third, a match can be trusted: when a chunk's conversion reproduces the next piece of the normalized string, that boundary is real, because Unicode has no way to change a character's conversion based on its neighbors without also changing the neighbor's bytes (the "Why Unicode guarantees this will work forever" section below explains why). Fourth, a chunk whose conversion doesn't match can always grow by one more grapheme and try again, and growth always ends, at worst when the chunk reaches the end of the string. So the walk converts the original one chunk at a time, verifies each chunk's conversion against the normalized string, maps positions exactly at every boundary that verifies, and grows the chunk through any stretch that doesn't. It never needs to know *why* a stretch didn't line up.

What keeps the walk fast is that a chunk is almost always one grapheme, because normalizing a grapheme's representation almost always gives the same result whether it sits between other graphemes or on its own. For the canonical forms (FormC, FormD) that's guaranteed by [UAX #29 §3](https://www.unicode.org/reports/tr29/#Grapheme_Cluster_Boundaries): "A key feature of Unicode grapheme clusters (both legacy and extended) is that they remain unchanged across all canonically equivalent forms of the underlying text." (The quote names NFC and NFD, but per [UAX #15 §1.1](https://www.unicode.org/reports/tr15/#Introduction) the caller's original is canonically equivalent to its normalized version, so the guarantee covers that pair too.) One grapheme in, one matching grapheme out, every boundary check passes.

The compatibility forms (FormKC, FormKD) don't get the guarantee, but the chunk still rarely grows. Grapheme counts change all the time ("ﬁ" converts to the two graphemes "fi", and Thai "กำ" splits in two when SARA AM decomposes), and that's harmless: each original grapheme still converts, on its own, to a matching piece of the normalized string. A chunk grows only when the conversions of *adjacent* original graphemes merge, the way ㄱ alone converts to U+1100 while ㄱㅏ together convert to the single syllable 가. In Unicode 16 the only characters that do that are Korean jamo, the compatibility jamo the example above uses (U+3131, U+314F) and their halfwidth siblings (U+FFA1, U+FFC2). That list is a fact about speed, not correctness: if a future Unicode version adds another merging composite, the chunk grows there too and the answers stay right.

## Why Unicode guarantees this will work forever

This section backs up the third fact from the key idea, that a match can be trusted. The check at the heart of the walk compares plain text: convert the chunk, then ask whether the normalized string continues with exactly that text. Comparing plain text invites a worry. Couldn't two different pieces of the original convert to text that happens to look the same, so the walk pairs up the wrong pieces?

There's exactly one shape that worry can take, and it's easiest to see with made-up characters. Imagine Unicode added two letters, X and Y, with these conversion rules: X converts to "b", and Y converts to "c" on its own but to "z" when it directly follows a "b", with the "b" left untouched. Feed the walker the original "XY", whose whole-string conversion is "bz". The first chunk is X, it converts to "b", the normalized string starts with "b", and the boundary verifies. The bytes matched but the pairing is wrong, because that "z" was partly caused by X. The next chunk is Y, which converts to "c" (inside the chunk there's no "b" in front of it), and "z" doesn't start with "c". Growing the chunk can't help, because the character Y needed to interact with sits behind a boundary that already verified, and chunks only grow forward. The walk runs out of string and returns its fallback answer (the pseudocode below maps everything after the drift to the end of the original).

Notice what the dangerous ingredient is. It isn't context sensitivity by itself. It's context sensitivity that changes the *second* character's conversion while leaving the first one's conversion byte-for-byte identical. If the first character's bytes change too, the walker's check fails right there, the chunk grows until both characters are inside it, and the conversion matches again. That's the Korean case from above, and the walker handles it by design.

Unicode can't produce the dangerous ingredient. The first reason is that the normalization algorithm is frozen. The Unicode Consortium's [character encoding stability policy](https://www.unicode.org/policies/stability_policy.html) promises that text normalized under one Unicode version stays normalized under every future version (for strings whose characters that version defines), because identifier systems and network protocols normalize text once and store it forever. New Unicode versions therefore can't change how normalization works. They can only add new characters as new data (a decomposition mapping, a combining class, maybe a composition pair) plugged into the same frozen machinery. [UAX #15 §3, Versioning and Stability](https://www.unicode.org/reports/tr15/#Versioning) spells out those rules.

The second reason is the shape of the machinery itself. The only operation in normalization where one character's result depends on another character is canonical composition, and composition always replaces the *pair* with a single combined character (the Canonical Composition Algorithm, [The Unicode Standard §3.11](https://www.unicode.org/versions/latest/core-spec/chapter-3/)). There's no rule form that changes a character because of its neighbor while leaving the neighbor's bytes alone, which is exactly what X and Y would need. Every context effect Unicode can express also changes the first character's bytes, and a changed first character is precisely the thing the walker's check notices. The Unicode 16 context-sensitive composites described in [UAX #15 §9.2](https://www.unicode.org/reports/tr15/#Contexts_Care) went through this same door, ordinary pairwise compositions involving newly encoded pieces, and the "Unicode 16 and the future" section below checks them against the walker one by one.

A stability policy is a promise from a standards body, not a law of physics. But if the Consortium ever broke this one, the walker would be the least of anyone's problems, because everything that normalizes text once and stores it forever (domain names, identifiers, security checks) would quietly stop being normalized. The world breaks before this algorithm does. Just in case, the code doesn't trust the promise anyway. It's defensive three times over: it verifies every boundary instead of assuming the guarantee, it grows the chunk when a check fails, and it falls back to mapping the position to the end of the original when nothing ever matches again.

## The algorithm

Walk the original string grapheme by grapheme. Maintain a parallel pointer `normPos` into the normalized string, plus a *chunk* of the original accumulated from the last verified grapheme boundary up to the current position.

The walk steps by grapheme rather than by rune for two reasons. Inside a grapheme, normalization reorders and composes freely, so a mid-cluster rune boundary usually doesn't survive into the normalized string: a chunk ending there just fails the check until it has swallowed the whole cluster anyway. And when a mid-cluster check *would* pass (both strings store the cluster decomposed, so the "e" of "é" matches one rune before its combining mark), the map would hand back a position inside a grapheme, which nothing downstream can use. The lexer's tokens are graphemes, so every position this walker receives is a cluster boundary, and the answer has to be a cluster start in the original (the snap-to-grapheme-start convention below). Grapheme steps make both properties automatic.

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

Here it is running on the two examples from "The problem" section. Each diagram stacks the original stream over the normalized stream, with the chunks the walk verified drawn as blocks. A boundary between blocks is a spot where a chunk's conversion matched the normalized string, so positions on either side of it map exactly.

First the ligature. The original is "xﬁy", FormKC expands the ligature to "fi", and the parse failed at position 2 in the normalized string:

```
original:    [ x ][ ﬁ  ][ y ]
index:         0    1     2
               |    |     |        every chunk is one grapheme and every
               |    |     |        conversion matches, so all the
               v    v     v        boundaries line up
normalized:  [ x ][ fi ][ y ]
index:         0   1  2   3
                      ^
                      failure at position 2
```

Position 2 falls *inside* the [fi] block. Both of those chars came from the one ligature char, so no original position corresponds to the spot between them. The walk snaps back to the block's start in the original, position 1, and highlighting covers the whole ligature.

Now the Korean merge, the case the chunk logic exists for. The original is "xㄱㅏy" (four chars), FormKC composes the two jamo into the single syllable char 가, and the parse failed at position 2 in the normalized string (the 'y'):

```
original:    [ x ][ ㄱ ㅏ ][ y ]
index:         0    1  2     3
               |      |      |     ㄱ alone converts to ᄀ (U+1100), which
               |      |      |     doesn't match the 가 below, so the chunk
               v      v      v     grows until the pair converts to 가
normalized:  [ x ][  가  ][ y ]
index:         0     1      2
                            ^
                            failure at position 2
```

The middle block is the drift the per-boundary check exists to catch. "ㄱ" on its own converts to the lead-consonant form U+1100, which isn't what sits at position 1 of the normalized string, because the 가 there was produced from *two* original graphemes together. So the chunk absorbs "ㅏ", the pair converts to exactly the 가 at position 1, and the walk lines back up. The failure at position 2 sits on the boundary after that block, and boundaries map exactly: the matching original boundary is position 3, after both jamo chars.

Both diagrams are locked in by tests in `InductorParser.Tests/DocExamples/MappingPositionsAfterNormalizationExamples.cs`, so if the walker's behavior changes, these diagrams fail a test instead of quietly going stale.

For canonical forms, the check always passes (per the §3 guarantee above). The chunk is always one grapheme. O(N) total work.

For compatibility forms, the check fails for a couple of iterations whenever the conversions of adjacent original graphemes merge (the rare Korean-jamo case). The chunk absorbs the extra graphemes until the region ends and the check passes again. These multi-grapheme regions are tiny in practice (two graphemes for Korean jamo), so the extra cost is small. A lone grapheme that expands, like "ﬁ" becoming "fi", never triggers this. Its conversion matches the normalized string on the first try, so the check passes and the walk moves on without absorbing anything.

Worst case is O(N²) if every grapheme were part of one giant multi-grapheme region. Real text doesn't look like that, and the walker runs only on parse failure (off the hot path), so even the pathological cost is acceptable.

### When the failure lands inside a multi-grapheme region

When the failure position falls inside a multi-grapheme region, the algorithm snaps back to the start of that region in the original. Editor highlighting then covers the whole affected piece of text instead of pointing somewhere in the middle of it. This matches the project's snap-to-grapheme-start convention.


## Unicode 16 and the future

[UAX #15 §9.2](https://www.unicode.org/reports/tr15/#Contexts_Care) warns about new context-sensitive composites added in Unicode 16 for three scripts: Tulu-Tigalari, Kirat Rai, and Gurung Khema:

> "Starting with Unicode 16.0, there are several new characters ... with normalization behavior not seen in characters encoded in earlier versions ... composite characters that can occur in NFC/NFKC strings, but when those characters occur in a context directly following certain other characters, performing an NFC or NFKC normalization will change those composite characters."

That's the same shape as our Korean case. Characters at one position decompose into pieces that recompose with characters at a nearby position.

We checked the [Unicode 16 character database](https://www.unicode.org/Public/16.0.0/ucd/) for these scripts:

- All the new composites decompose into two characters.
- For Tulu-Tigalari and Gurung Khema, the second decomposed character is a combining mark. UAX #29 keeps a combining mark inside the same grapheme as the preceding character.
- For Kirat Rai, the second decomposed character is a Hangul-style vowel, which UAX #29 also keeps inside the same grapheme.

So none of these add new multi-grapheme cases. As of Unicode 16, Korean jamo (compatibility and halfwidth forms) are still the only characters where compatibility normalization merges adjacent original graphemes into one. That's a much narrower claim than "the grapheme count never changes otherwise". Count changes are common under FormKC and FormKD: "ﬁ" becomes the two-grapheme "fi", Thai "กำ" splits into two graphemes, and the Arabic ligature U+FDFA expands into an 18-grapheme phrase. Each of those converts one original grapheme to a matching piece of the normalized string, so the per-boundary check passes and the walker handles them with no absorption. The regression tests in `InductorParser.Tests/DocExamples/MappingPositionsAfterNormalizationExamples.cs` keep these counterexamples on record.

Sample code points to verify against when a future Unicode version ships: U+1138E TULU-TIGALARI LETTER AI, U+113C7 TULU-TIGALARI VOWEL SIGN OO, U+16D69 KIRAT RAI VOWEL SIGN O, U+16121 GURUNG KHEMA VOWEL SIGN U.

If a future Unicode release does introduce a real new boundary-merging composite, it just works. The runtime check on each grapheme boundary detects any drift between the side-by-side walk and the normalized string, not just the Korean case it was designed for. No code change needed.
