# Terminology

A few terms used throughout these docs mean specific things in this library, here's guidance on how they're used and what terms are preferred for anyone writing documentation:

**Leaf rule.** A rule that emits a single leaf `Symbol` with the matched text. What makes a rule a leaf is the shape of its output, not its count of child rules: WithinToken and the escape / rule-stopper forms of ScanUntil hold inner rules they run along the way, but each still emits one leaf over everything it consumed. Token, Literal, LiteralIgnoreAsciiCase, OneOf, NoneOf, AnyToken, ScanWhile, ScanUntil, WithinToken are the leaves. Use "leaf" rather than "primitive" or "terminal" when talking about this category.

**Composite rule.** A rule that emits a composite `Symbol` whose children come from the rules it's built out of. And, Or, BetweenInclusive (plus its special cases OneOrMore, ZeroOrMore, Optional, AtLeast, AtMost, Exactly), and Identifier are the composites. Alias and LateBoundRule wrap a single target rule and take the shape of whatever they wrap. Use "composite" rather than "combinator."

**Zero-width rule.** A rule that consumes nothing and emits no `Symbol` at all: Not, Peek, and Eof. Not and Peek hold an inner rule they run as a lookahead probe and then roll back. Eof holds nothing, it just checks for end of input. Use "zero-width rule" rather than "predicate" or "assertion", which are PEG and regex jargon.

**Token, rune, grapheme cluster.** The parser's "token" is one grapheme cluster: a user-perceived character, split off by the Unicode Standard Annex #29 rules and handed back one per lexer `Read()`. It isn't a token in the traditional compiler sense (a keyword, an identifier), so a keyword like `select` is a sequence of tokens here, not one token. A token is built from one or more runes (Unicode code points, `System.Text.Rune`), and a rune is stored as one or two chars (UTF-16 code units). [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md) walks through the full representation stack.

**Syntax tree.** The default output of `rule.Parse(input)`. Each rule's `FlattenType` has already been applied: `FlattenType.Delete` nodes are gone, `FlattenType.Flatten` Symbols have had their children lifted into the parent, and `FlattenType.Preserve` Symbols stay with their own `Id`. `ParseResult.Find(rule)` (or `Symbol.Find(rule)` from any node) works for `FlattenType.Preserve` rules. `FlattenType.Flatten` or `FlattenType.Delete` rules intentionally don't appear, so Find returns null for them. Set `FlattenType.Preserve` on a rule if you need its Symbol to appear in the tree. `Symbol.Flatten()` (or `FlattenInto(...)` to append into a list you already have) applies the same flattening after the fact. Its main use is turning a debug tree into this shape, it also works on trees built by hand with the public `Symbol` constructors, and it does nothing to a tree Parse already flattened.

**Debug tree.** What you get back when `ParseOptions.PreserveAllSymbols` is on. Contains every matched token (delimiters, whitespace, individual leaf symbols) and a `Symbol` for every `FlattenType.Flatten` / `FlattenType.Delete` rule the grammar declares. Mirrors the grammar one-to-one. Useful for `PrintTree` output and for `Find`-queries against rules whose Symbols the default path removes. Not the default because most callers want the syntax tree. Calling `Flatten()` on the root `Symbol` turns a debug tree into exactly the tree the default parse would have returned.

**Case-insensitive.** Use "case-insensitive" (adjective) and "case insensitivity" (noun) when discussing rules or matching that treat upper- and lower-case letters as equivalent. That's the term the source and the rest of these docs already use. Avoid "case folding" / "case-folded", which are Unicode-spec jargon and read as writer-jargon to a general reader, and avoid "case-invariant", which nothing else in these docs uses. <!-- style-lint-ok: this entry names the banned terms in order to ban them -->

**Normalization converts.** When a normalization form (FormC, FormD, FormKC, FormKD) turns a character into its canonical or compatibility equivalent, say the form "converts" it: "FormKC converts math-bold 𝐀 to plain A", "FormD converts é to e plus combining acute". Avoid "folds" / "folding" for this, the same kind of Unicode-spec jargon called out in the case entry above. "Converts" is the plain word.

**AST.** Not used in this library's vocabulary. The C++ original has `Compiler<T>::ProcessAst` and calls the post-flatten artifact an AST, but the C# port deliberately avoids the term. A true AST in compiler tradition is the user's domain types (something like `Setting(name, value)`) produced by a hand-written compile pass over the syntax tree, not anything the library itself produces. Keeping "syntax tree" as the library's own term means a reader can later talk about "the AST" without overloading the word.

The namespace `InductorParser.SyntaxTree` contains the primitives (`Symbol`, `SymbolId`, `FlattenType`, `SymbolRanges`) that participate in both trees. The namespace name points at the default output shape.

## Writing About FlattenType

When prose refers to a rule's `FlattenType`, use the full enum value ("`FlattenType.Preserve`", "`FlattenType.Flatten`", "`FlattenType.Delete`") rather than the shorthand "Preserve-typed" / "Flatten-typed" / "Delete-typed". The hyphenated form reads as writer jargon and leaves the reader guessing which type is meant. The full name is unambiguous, grep-able, and navigable in an IDE. Examples:

- "a rule with `FlattenType.Preserve`" (not "a Preserve-typed rule")
- "`FlattenType.Delete` children" (not "Delete-typed children")
- "the parent is `FlattenType.Flatten`, so its children bubble up" (not "the parent is Flatten-typed, so ...")

Bare `Preserve`, `Flatten`, `Delete` unqualified are fine only when the surrounding context has already said "FlattenType" in the same sentence or paragraph (e.g., "Its `FlattenType` defaults to `Delete`.").
