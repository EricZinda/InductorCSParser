# Localization: English leaks a non-English app can't replace

The parse-failure path is already well localized. Every GrammarMismatch and budget abort renders through one of six overridable `ParseOptions` templates (`PositionalErrorTemplate`, `EndOfInputErrorTemplate`, and the four `*AbortTemplate`s), placeholders are language-neutral, and built-in rule factories don't bake in English `.WithError` text. So a non-English app can already replace the everyday "unexpected 'x'" / "end of input" / "timeout exceeded" messages.

But four spots still hand English to the end user with no good override. Ranked by how reachable they are. Finding 2 (ill-formed input) is now fixed. Findings 1, 3, and 4 remain.

## 1. Rule names fall back to English class labels (the main one)

`Symbol.DisplayName` (Symbol.cs:197), `Rule.NameOf(SymbolId)` (Rule.cs:894), and `ParseResult.DisplayNameOf` resolve any rule the author didn't `.As(...)`-name to its class-derived trace label: "And", "Or", "OneOrMore", "BetweenInclusive[1..3]", "Eof", and the rest. A character-leaf renders as the matched rune (language-neutral), but every composite or structural rule renders as an English keyword.

This matters because `NameOf`'s own doc comment recommends it "for error-message rendering that wants to quote a rule's name." A non-English app that takes that advice and builds its own diagnostics by walking the tree gets English the moment it hits a node it didn't explicitly name. The only override today is per-rule: `.As("localized name")` on every rule that might surface. There's no neutral fallback and no way to set a different default.

Options to consider:
- A hook on `ParseOptions` (or the grammar) that maps a `SymbolId` / rule kind to a caller-supplied label, consulted before the class-name fallback.
- Or make the class-name fallback opt-in and have `DisplayName` / `NameOf` return null for unnamed structural rules, so the app decides what to render instead of getting English silently.
- At minimum, document loudly that anything you surface to users has to be `.As(...)`-named, and that `DisplayName` is otherwise an English debug label.

## 2. Ill-formed UTF-16 input threw an unlocalizable BCL ArgumentException (DONE)

Fixed. `Parse` now catches the `string.Normalize` failure and returns a `ParseResult` whose `Outcome` is the new `ParseOutcome.MalformedInput`, positioned at the offending character, with a message rendered from the new `ParseOptions.MalformedInputTemplate`. So ill-formed UTF-16 (a lone surrogate, a reversed pair, or U+FFFE) now flows through the same localizable, structured-position model as every other failure instead of escaping as a BCL `ArgumentException`. `Compile(null)` still skips normalization and surfaces the code units as tokens.

Original write-up: under the default FormC, `Parse` called `input.Normalize(FormC)` before lexing, and on ill-formed input .NET threw `System.ArgumentException` with the English message "Invalid Unicode code point found at index N." That was the one parse-time failure mode that escaped the template system: an exception, not a `ParseResult`, with a BCL message the app couldn't localize. Catching it also lost the structured error position every other failure gives you.

## 3. "Parse aborted." has no template

`BuildBudgetMessage`'s `default:` branch returns the hardcoded "Parse aborted." (Rule.cs:1193). It's the only abort message with no `ParseOptions` template behind it. Effectively unreachable right now (all five `ParseOutcome` values are handled above it), so this is purely defensive, but if the enum ever grows a new abort kind, the new path emits English with no override. Either add a catch-all template or keep the hardcoded string and note it's unreachable by construction.

## 4. TokenSet.ToString() emits "more"

`TokenSet.ToString()` ends an over-long render with `,...+5 more` (TokenSet.cs:866). The word "more" is hardcoded. This is mostly a debug / trace rendering and isn't on the default end-user error path, so it's the lowest priority of the four, but it's a public method and an app that surfaces a character-set constraint to users would show English. If we ever make the truncation suffix configurable, this goes away.

See docs/BugSearchLog/2026-06-26-localization-english-leaks-non-english-apps-cant-replace.md for the full sweep, including the verified non-findings (the `NormalizedPositionMap` Normalize call is not a second throw site, and the per-rule trace strings are debug-only).
