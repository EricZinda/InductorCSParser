# Grammar-level NFC normalization collapses byte-distinct TOML keys, diverging from Tomlyn

`InductorParser` runs per-grapheme NFC normalization on input as part of the lexer (see `NormalizedPositionMap.TranslateViaPerGraphemeNormalize` and the matching backlog items `pgnm-` and `0000-systematic-normalization-...`). For most grammars that's invisible. For a parser that's supposed to be byte-faithful to the source — TOML is one — it changes user-visible behavior.

Concrete case from `E2ESamples/Toml/Tests/UnicodeTests.cs`:

```
"à" = 1
"à" = 2
```

The first key is precomposed `a-grave` (1 code point). The second is `a` plus combining grave (2 code points). They look identical but their bytes differ. Tomlyn (the BSD-2 reference at `Original/`) keeps these as two separate keys. The InductorParser TOML rewrite normalizes both to NFC at scan time, so the consumer sees the same key twice and reports `Duplicate key 'à'`. The two pinning tests `NfdInput_IsNormalizedToNfc_BeforeConsumerSeesIt` and `NfdInput_DivergesFromTomlyn_OnSameDocument` lock in the current behavior.

The TOML 1.0 spec says nothing about normalization for keys, so neither answer is "wrong" against the spec. But the divergence from Tomlyn is real, and any existing TOML document that uses a key in two normalization forms will round-trip through Tomlyn and fail through us with a duplicate-key error. That's a footgun for anyone porting between the two.

## Why this matters beyond TOML

Any grammar where a Symbol's `ToString()` is supposed to round-trip the source bytes will hit this. JSON's spec is silent on key normalization too. A regex's character literals shouldn't get normalized. A `unique-id-string` in a binary header definitely shouldn't. The general shape: the lexer's normalization helps for "find me anything that looks like this identifier" use cases and hurts for "give me back what was on disk" use cases.

## What would make this easy

Two options worth exploring:

1. **Per-grammar opt-out for normalization.** A `Compile()` overload that takes `NormalizationForm.None` or similar, telling the scanner not to fold input. The position-translation machinery is already there; with normalization turned off it's a no-op map. Cheapest change. The user picks their poison per grammar.

2. **Preserve the original-bytes view alongside the normalized one.** Symbols already have a position-translated view back to the original input. Add a `RawText` accessor on Symbol that returns the unnormalized substring of the original input that this Symbol covers. (Backlog item `srct-symbol-needs-raw-source-text-accessor-...` is in this neighborhood; this case is one of the motivating examples for it.) The TOML rewrite would call `bodyNode.RawText` instead of `bodyNode.ToString()` for string-body decoding and for key extraction.

Option 2 is the more general fix. Option 1 is the cheaper one and works today for grammars where every Symbol of interest needs raw text.

## Done when

- One of the two options exists in `src/InductorParser/`.
- The TOML rewrite at `E2ESamples/Toml/Rewrite/` switches over and the `NfdInput_DivergesFromTomlyn_OnSameDocument` test flips: now both parsers agree.
- A regression test covers the JSON case in `src/InductorParser.Tests/E2EExamples/JSON/` so the same fix doesn't quietly regress when someone changes the scanner.
