# Untitled

- `Runes("é")` walks one grapheme, calls `IsNormalized(FormD)` (BCL fast path), one allocation for the NFD form. Microseconds.
