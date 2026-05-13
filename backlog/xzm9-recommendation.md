# Recommendation

Start with A. It captures most of the value (the user always sees what the rule wanted, not just what it didn't get), keeps the swap-out surface at one property, and is backwards-compatible (default template can stay the current sentence with `, expected {expected}` appended). If specific rules later need richer formatting that doesn't fit the single-sentence shape, B can be layered on top: introduce a per-rule template that, if set, wins over the generic one.

The work to land A:

1. Add an internal virtual `string? DescribeExpected()` on Rule, override in Literal, Grapheme, OneOf, NoneOf, Eof, AnyToken, ScanUntil, ScanWhile, BetweenInclusive.
2. Plumb it through `RecordFailure` (probably as a third slot next to `errorMessage`, since the deepest-failure tracking already picks between competing failure descriptors).
3. Add `{expected}` to `FormatTemplate`'s placeholder list in Rule.cs:934-968.
4. Update `PositionalErrorTemplate` default to mention `{expected}`.
5. Update tests that pin on the exact default text. The existing template tests at `ErrorMessageTemplateTests.cs` only assert against custom templates so they'd be unaffected, but other tests likely check default-text snapshots.
