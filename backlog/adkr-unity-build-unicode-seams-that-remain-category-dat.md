# Unity build Unicode seams that remain: category data and whitespace

- Where this came up: parked from the aabm item (IL2CPP `string.Normalize` divergence, closed 2026-07-21 by the built-in UAX #15 normalizer). Fixing normalization closed the normalize-then-segment seam on the netstandard2.1 build: segmentation and normalization now both come from built-in Unicode 15.0 implementations there, selectable everywhere through the single `UnicodeEnvironment.Implementation` setting (one setting so the two can never diverge). Two seams stayed open.

- **`UnicodeCategory` data.** `TokenSet.Category(...)` and the General_Category half of the XID sets read the BCL's tables, documented in `TokenSet.Xid.cs` to track whatever Unicode version the runtime ships. On the Unity build that means Mono-era category data next to built-in 16.0 segmentation and normalization (15.0 when this item was written, upgraded 2026-07-24). The hand-typed Xid spec constants are transcribed from the Unicode 17.0 UCD on every build, a third version in the mix. Nothing measures today how far Mono's category tables actually lag or which sets a Unity grammar would see differently.

- **`char.IsWhiteSpace`.** `TokenSet.InlineWhitespace` is built from it at static init, so whitespace membership tracks the runtime's White_Space property. Real precedent: U+180E MONGOLIAN VOWEL SEPARATOR left White_Space in Unicode 6.3, and the corpus comment on `MongolianVowelSeparatorText` already documents the per-runtime shift.

- If either seam gets closed the way segmentation and normalization were, the shape is known: a generated table at the same pinned Unicode version, dispatch through the existing `UnicodeEnvironment.Implementation` setting (extending what Bundled covers, not adding a second setting), differential tests against the CoreCLR oracle, and an `[Explicit]` UCD re-derivation test. Category data is the bigger table (every code point has a General_Category), whitespace is tiny.

- **Completed perf follow-up.** On 2026-07-23 the built-in normalizer gained generated NFC_QC/NFD_QC/NFKC_QC/NFKD_QC tables. Already-normalized input now returns from the scan without rebuilding or allocating.

- Done when: either a measurement shows the category/whitespace seams don't matter for real grammars on Unity's runtimes (write the result down where the seams are documented), or the seams get closed with built-in data behind the established setting pattern.
