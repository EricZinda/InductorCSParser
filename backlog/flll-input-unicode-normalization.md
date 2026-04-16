- Input Unicode normalization
    - Current state: ParseOptions has no NormalizeInput field. Caller input goes into the lexer as-is. A grammar that matches `Literal("café")` will succeed on composed input ("café" with U+00E9) and silently fail on decomposed input ("cafe" + combining acute U+0301), even though both render the same to the user. Surprising and hard to debug.
    - Design from docs/ProgrammingModel.md:
        ```csharp
        public NormalizationForm? NormalizeInput { get; set; } = NormalizationForm.FormC;
        ```
        Default is composed form (NFC). Set to null to skip normalization. Applied once when the lexer wraps the input string, before any rule runs.
    - Implementation: in Rule.Parse, call `input.Normalize(options.NormalizeInput.Value)` before constructing the lexer. Normalize returns a new string if the input wasn't already in the target form; same string reference back if it was (so no allocation in the common case).
    - Callers who care about offsets into the original string need to know the normalized input can differ in length. ErrorCharIndex would be into the normalized input, not the original. Worth documenting; might matter for editor integrations that want to map errors back to source positions.
		- Issue: Can we properly adjust these values so they are correct if normalized is different?  Put a backlog item on the list to do this
    - Priority: Needed the moment a grammar handles arbitrary user text. Most ASCII-only grammars don't care.
    - Done when: Default NFC normalization is applied, a test shows composed-form and decomposed-form `café` both parse successfully under `Literal("café")`, the null-means-skip path works.
