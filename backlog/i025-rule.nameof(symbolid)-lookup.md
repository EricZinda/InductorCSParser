- rule.NameOf(SymbolId) lookup
    - Current state: Rule has a `Name` property for the rule itself, but there's no way to look up "what was the name of the rule with this SymbolId?" given just the id. Once tracing and richer error messages land (i021, i023), they'll want to render ids as names, and they need a reverse lookup.
    - Design from docs/ProgrammingAGrammar.md:
        ```csharp
        public string? NameOf(SymbolId id);
        ```
        Consults three sources in order and returns the first match:
        1. User names from this grammar (scoped per-Compile, built by walking the graph once and indexing name-by-id).
        2. Built-in names (library-global, never change, e.g. "AndExpression", "OneOrMoreExpression"). Map lives in a static table.
        3. Character fallback: if the id is in the character range (0..0x10FFFF), render as the single-char string.
    - Index: built once during Compile (or lazily on first NameOf call) as a `Dictionary<SymbolId, string>` on the root rule. Small; one entry per named rule in the graph.
    - Priority: Trivial to build, but defer until consumers exist (tracing, error messages). Useless without them.
    - Done when: NameOf returns correct names for user-named rules, built-in category names for unnamed built-ins, and the character for character ids.
