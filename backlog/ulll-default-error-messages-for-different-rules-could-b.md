# Default Error messages for different Rules could be specific to the rule

right now they're very general

## What's there now

The default messages live in one place: `ParseOptions.cs` lines 108 to 181. Six templated strings (positional, EOF, plus four budget aborts). The two failure-path ones are:

    PositionalErrorTemplate  "Parse failed at offset {charIndex}: unexpected '{character}'."
    EndOfInputErrorTemplate  "Unexpected end of input."

Every rule that fails calls `lexer.RecordFailure(pos, ErrorMessage)` (Lexer.cs:438) passing its own `_errorMessage` (the `.WithError(...)` text, or null). On failure, `Rule.BuildErrorMessage` (Rule.cs:934-968) picks: user's WithError if set, otherwise the EOF template if the failure is at end of input, otherwise the positional template.

So the actual default a user sees on a normal failure is always one of two sentences. The rule that failed doesn't get a say.

## What "specific to the rule" could look like

Each rule already knows what it was looking for:

    LiteralRule            _expected      "expected 'maj'"
    GraphemeRule           _expected      "expected 'a'"
    LiteralIgnoreAsciiCase _expected      "expected 'select' (case-insensitive)"
    OneOfRule              _setRendered   "expected one of [A-Z,a-z]"
    NoneOfRule             _setRendered   "expected anything except [...]"
    EofRule                (none)         "expected end of input"
    AnyTokenRule           (none)         "expected any character"
    ScanUntilRule          terminator     "expected to find ';' before end of input"
    BetweenInclusive       min,max        "expected at least 2 of ..."

`AndRule` and `OrRule` don't usually win the deepest-failure slot (some child of them does), so their own defaults matter less.

## Two designs, with different costs at the swap-out location

A. One extra placeholder. Add `{expected}` to the existing `PositionalErrorTemplate`. Each rule contributes its own "expected ..." snippet when that placeholder is in the template. Default becomes:

    "Parse failed at offset {charIndex}: unexpected '{character}', expected {expected}."

Swap-out cost: zero new properties. The user still overrides one template. Trade-off: the "expected {expected}" phrasing has to be a single sentence shape that fits every rule. "expected 'maj'" and "expected end of input" need to read OK next to "unexpected 'x'".

B. Per-rule templates. Add roughly 6 to 8 new properties to `ParseOptions`: `LiteralFailureTemplate`, `OneOfFailureTemplate`, `NoneOfFailureTemplate`, `EofFailureTemplate`, `ScanUntilFailureTemplate`, etc. Each rule picks its own template. Each template has its own placeholders (`{expected}`, `{set}`, `{terminator}`).

Swap-out cost: 6 to 8 new properties to override if you want full control. Trade-off: more API surface, but each default is shaped for the rule. A grammar author with one bespoke voice ("we expected X, but got Y") would have to mirror that voice across all 6 to 8 templates to be consistent.

## Recommendation

Start with A. It captures most of the value (the user always sees what the rule wanted, not just what it didn't get), keeps the swap-out surface at one property, and is backwards-compatible (default template can stay the current sentence with `, expected {expected}` appended). If specific rules later need richer formatting that doesn't fit the single-sentence shape, B can be layered on top: introduce a per-rule template that, if set, wins over the generic one.

The work to land A:

1. Add an internal virtual `string? DescribeExpected()` on Rule, override in Literal, Grapheme, OneOf, NoneOf, Eof, AnyToken, ScanUntil, ScanWhile, BetweenInclusive.
2. Plumb it through `RecordFailure` (probably as a third slot next to `errorMessage`, since the deepest-failure tracking already picks between competing failure descriptors).
3. Add `{expected}` to `FormatTemplate`'s placeholder list in Rule.cs:934-968.
4. Update `PositionalErrorTemplate` default to mention `{expected}`.
5. Update tests that pin on the exact default text. The existing template tests at `ErrorMessageTemplateTests.cs` only assert against custom templates so they'd be unaffected, but other tests likely check default-text snapshots.
