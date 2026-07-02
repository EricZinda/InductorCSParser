# Localization: English leaks a non-English app can't replace

A localization audit (see docs/BugSearchLog/2026-06-26-localization-english-leaks-non-english-apps-cant-replace.md) found four spots where an app built in another language surfaces hardcoded English to its end users with no good override. The parse-failure path itself is already well localized: every GrammarMismatch and budget abort renders through one of six overridable ParseOptions templates, placeholders are language-neutral, and built-in rule factories don't bake in English .WithError text.

One of the four is already fixed: ill-formed UTF-16 input now returns a `ParseOutcome.MalformedInput` result with a localizable `ParseOptions.MalformedInputTemplate` message, instead of throwing an unlocalizable BCL `ArgumentException`. The three below still hand English to the end user, ranked by how reachable they are.

## 1. Rule names fall back to English class labels (the main one)

`Symbol.DisplayName` (Symbol.cs), `Rule.NameOf(SymbolId)` (Rule.cs), and `ParseResult.DisplayNameOf` resolve any rule the author didn't `.As(...)`-name to its class-derived trace label: "And", "Or", "OneOrMore", "BetweenInclusive[1..3]", "Eof", and the rest. A character-leaf renders as the matched rune (language-neutral), but every composite or structural rule renders as an English keyword.

This matters because `NameOf`'s own doc comment recommends it "for error-message rendering that wants to quote a rule's name." A non-English app that takes that advice and builds its own diagnostics by walking the tree gets English the moment it hits a node it didn't explicitly name. The only override today is per-rule: `.As("localized name")` on every rule that might surface. There's no neutral fallback and no way to set a different default.

Options to consider:
- A hook on `ParseOptions` (or the grammar) that maps a `SymbolId` or rule kind to a caller-supplied label, consulted before the class-name fallback.
- Or make the class-name fallback opt-in and have `DisplayName` / `NameOf` return null for unnamed structural rules, so the app decides what to render instead of getting English silently.
- At minimum, document prominently that anything you surface to users has to be `.As(...)`-named, and that `DisplayName` is otherwise an English debug label.

## 2. "Parse aborted." has no template

`BuildBudgetMessage`'s `default:` branch returns the hardcoded "Parse aborted." (Rule.cs). It's the only abort message with no `ParseOptions` template behind it. Effectively unreachable right now (every abort outcome is handled above it), so this is purely defensive, but if the enum ever grows a new abort kind, the new path emits English with no override. Either add a catch-all template or keep the hardcoded string and note that it can't run while every abort outcome has its own branch above it.

## 3. TokenSet.ToString() emits "more"

`TokenSet.ToString()` ends an over-long render with `,...+5 more` (TokenSet.cs). The word "more" is hardcoded. This is mostly a debug / trace rendering and isn't on the default end-user error path, so it's the lowest priority of the three, but it's a public method and an app that surfaces a character-set constraint to users would show English. If we ever make the truncation suffix configurable, this goes away.

See docs/BugSearchLog/2026-06-26-localization-english-leaks-non-english-apps-cant-replace.md for the full sweep, including the verified non-findings (the NormalizedPositionMap Normalize call is not a second throw site, and the per-rule trace strings are debug-only).
