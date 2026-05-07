# Untitled

- `OneOf("é").Compile().Parse("é")` (default FormC, decomposed in source) similarly fails: the user typed `"é"` so the set has a multi-rune entry "é"; the lexer normalizes input to U+00E9 (single-rune); the multi-rune array doesn't have that single rune.
