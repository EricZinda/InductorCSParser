# Terminology

A few terms used throughout these docs mean specific things in this library, here's guidance on how they're used and what terms are preferred for anyone writing documentation:

**Leaf rule.** A rule with no child rules. The matching logic consumes input directly (or doesn't consume at all, for zero-width predicates) rather than delegating to other rules. Token, Literal, LiteralIgnoreAsciiCase, OneOf, NoneOf, AnyToken, ScanUntil, Eof, Not, Peek are all leaves. Use "leaf" rather than "primitive" or "terminal" when talking about this category.

**Composite rule.** A rule built out of other rules. AllOf, FirstOf, BetweenInclusive (plus its wrappers OneOrMore, ZeroOrMore, Optional, AtLeast, AtMost, Exactly), and LateBoundRule are the composites. Use "composite" rather than "combinator."

**Syntax tree.** The default output of `rule.Parse(input)`. Each rule's `FlattenType` has already been applied: `FlattenType.Delete` nodes are gone, `FlattenType.Flatten` wrappers have had their children lifted into the parent, and `FlattenType.Preserve` wrappers stay with their own `Id`. `Tree.Find(rule)` works for `FlattenType.Preserve` rules. `FlattenType.Flatten` or `FlattenType.Delete` rules intentionally don't appear, so Find returns null for them. Set `FlattenType.Preserve` on a rule if you need its wrapper to appear in the tree. `Symbol.FlattenInto(...)` (or the no-arg `Flatten()` overload) still exists for trees built by hand outside the parse path, and is idempotent on a tree Parse already returned.

**Debug tree.** What you get back when `ParseOptions.PreserveAllSymbols` is on. Contains every matched token: delimiters, whitespace, individual leaf symbols, and every `FlattenType.Flatten` / `FlattenType.Delete` wrapper the grammar declares. Mirrors the grammar one-to-one. Useful for `PrintTree` output and for `Find`-queries against wrappers that would otherwise be removed. Not the default because most callers want the syntax tree.

**Case invariance.** Use "case invariance" (noun) and "case-invariant" (adjective) when discussing rules or matching that treat upper- and lower-case letters as equivalent. Avoid "case folding" / "case-folded", which are Unicode-spec jargon and read as writer-jargon to a general reader.

**AST.** Not used in this library's vocabulary. The C++ original has `Compiler<T>::ProcessAst` and calls the post-flatten artifact an AST, but the C# port deliberately avoids the term. A true AST in compiler tradition is the user's domain types (something like `Setting(name, value)`) produced by a hand-written compile pass over the syntax tree, not anything the library itself produces. Keeping "syntax tree" as the library's own term means a reader can later talk about "the AST" without overloading the word.

The namespace `InductorParser.SyntaxTree` contains the primitives (`Symbol`, `SymbolId`, `FlattenType`, `SymbolRanges`) that participate in both trees. The namespace name points at the default output shape.

## Writing About FlattenType

When prose refers to a rule's `FlattenType`, use the full enum value ("`FlattenType.Preserve`", "`FlattenType.Flatten`", "`FlattenType.Delete`") rather than the shorthand "Preserve-typed" / "Flatten-typed" / "Delete-typed". The hyphenated form reads as writer jargon and leaves the reader guessing which type is meant. The full name is unambiguous, grep-able, and navigable in an IDE. Examples:

- "a rule with `FlattenType.Preserve`" (not "a Preserve-typed rule")
- "`FlattenType.Delete` children" (not "Delete-typed children")
- "the parent is `FlattenType.Flatten`, so its children bubble up" (not "the parent is Flatten-typed, so ...")

Bare `Preserve`, `Flatten`, `Delete` unqualified are fine only when the surrounding context has already said "FlattenType" in the same sentence or paragraph (e.g., "Its `FlattenType` defaults to `Delete`.").
