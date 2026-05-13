# Two designs, with different costs at the swap-out location

A. One extra placeholder. Add `{expected}` to the existing `PositionalErrorTemplate`. Each rule contributes its own "expected ..." snippet when that placeholder is in the template. Default becomes:

    "Parse failed at offset {charIndex}: unexpected '{character}', expected {expected}."

Swap-out cost: zero new properties. The user still overrides one template. Trade-off: the "expected {expected}" phrasing has to be a single sentence shape that fits every rule. "expected 'maj'" and "expected end of input" need to read OK next to "unexpected 'x'".

B. Per-rule templates. Add roughly 6 to 8 new properties to `ParseOptions`: `LiteralFailureTemplate`, `OneOfFailureTemplate`, `NoneOfFailureTemplate`, `EofFailureTemplate`, `ScanUntilFailureTemplate`, etc. Each rule picks its own template. Each template has its own placeholders (`{expected}`, `{set}`, `{terminator}`).

Swap-out cost: 6 to 8 new properties to override if you want full control. Trade-off: more API surface, but each default is shaped for the rule. A grammar author with one bespoke voice ("we expected X, but got Y") would have to mirror that voice across all 6 to 8 templates to be consistent.
