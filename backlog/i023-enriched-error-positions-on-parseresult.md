- Enriched error positions on ParseResult
    - Current state: ParseResult only carries `	` (UTF-16 char offset into the input). Editors and IDE integrations expect more: line/column numbers for display, rune offsets for logical positions, grapheme offsets for caret positioning in rendered text.
    - Design from docs/ProgrammingAGrammar.md:
        ```csharp
        public int  ErrorCharOffset        { get; }   // UTF-16 char offset; use for input[...]
        public int  ErrorLine              { get; }   // 1-based line number for display
        public int  ErrorColumn            { get; }   // 1-based column in UTF-16 chars (matches LSP)
        public int  ErrorOffsetInRunes     { get; }   // for callers that measure in runes
        public int  ErrorOffsetInGraphemes { get; }   // for callers that measure in graphemes
        ```
    - All derived lazily from ErrorCharOffset + the original input string. None of them cost anything unless the caller reads them. Computing line/column is a single linear scan of the input up to the offset; rune/grapheme offsets are similar.
    - ParseResult would need to keep a reference to the input string for the derivations. Worth deciding whether to store the input or have a separate "resolve" helper that takes the input.
    - Priority: Cheap, user-facing, unlocks decent error messages in editor integrations.
    - Done when: ParseResult exposes all five properties, each computed correctly, tests cover the common cases (line and column at offset 0, at a newline, on the last line, at EOF).
