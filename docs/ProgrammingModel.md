# Programming Model

This document is the design and architecture of the InductorParser C# port. It explains *why* the library is shaped the way it is, what tradeoffs were made, and what we considered but did not do.

If you want to write grammars, read [ProgrammingAGrammar.md](ProgrammingAGrammar.md) first. This doc is for readers who want to understand or evaluate the design itself, or who plan to extend the library.

Related docs:

- [ProgrammingAGrammar.md](ProgrammingAGrammar.md): user reference for how to write grammars with the library.
- [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md): lexer internals (code units, runes, graphemes, the two lexers, normalization).
- [UnicodeGotchas.md](UnicodeGotchas.md): caller-side Unicode concerns the lexer cannot fix.
- [Recipes.md](Recipes.md): common grammar patterns.

## Terminology

A few terms used throughout these docs mean specific things in this library:

**Leaf rule.** A rule with no child rules. The matching logic consumes input directly (or doesn't consume at all, for zero-width predicates) rather than delegating to other rules. Token, Literal, LiteralIgnoreAsciiCase, OneOf, NoneOf, AnyToken, StringBody, Eof, Not, Peek are all leaves. Use "leaf" rather than "primitive" or "terminal" when talking about this category.

**Composite rule.** A rule built out of other rules. AllOf, FirstOf, BetweenInclusive (plus its wrappers OneOrMore, ZeroOrMore, Optional, AtLeast, AtMost, Exactly), and LateBoundRule are the composites. Use "composite" rather than "combinator."

**Syntax tree.** The default output of `rule.Parse(input)`. Each rule's `FlattenType` has already been applied: `FlattenType.Delete` nodes are gone, `FlattenType.Flatten` wrappers have had their children lifted into the parent, and `FlattenType.Preserve` wrappers stay with their own `Id`. `Tree.Find(rule)` works for `FlattenType.Preserve` rules. `FlattenType.Flatten` or `FlattenType.Delete` rules intentionally do not appear, so Find returns null for them. Set `FlattenType.Preserve` on a rule if you need its wrapper to appear in the tree. `Symbol.FlattenInto(...)` (or the no-arg `Flatten()` overload) still exists for trees built by hand outside the parse path, and is idempotent on a tree Parse already returned.

**Debug tree.** What you get back when `ParseOptions.PreserveAllSymbols` is on. Contains every matched token: delimiters, whitespace, individual leaf symbols, and every `FlattenType.Flatten` / `FlattenType.Delete` wrapper the grammar declares. Mirrors the grammar one-to-one. Useful for `PrintTree` output and for `Find`-queries against wrappers that would otherwise be removed. Not the default because most callers want the syntax tree.

**AST.** Not used in this library's vocabulary. The C++ original has `Compiler<T>::ProcessAst` and calls the post-flatten artifact an AST, but the C# port deliberately avoids the term. A true AST in compiler tradition is the user's domain types (something like `Setting(name, value)`) produced by a hand-written compile pass over the syntax tree, not anything the library itself produces. Keeping "syntax tree" as the library's own term means a reader can later talk about "the AST" without overloading the word.

The namespace `InductorParser.SyntaxTree` contains the primitives (`Symbol`, `SymbolId`, `FlattenType`, `SymbolRanges`) that participate in both trees. The namespace name points at the default output shape.

### Writing About FlattenType

When prose refers to a rule's `FlattenType`, use the full enum value ("`FlattenType.Preserve`", "`FlattenType.Flatten`", "`FlattenType.Delete`") rather than the shorthand "Preserve-typed" / "Flatten-typed" / "Delete-typed". The hyphenated form reads as writer jargon and leaves the reader guessing which type is meant. The full name is unambiguous, grep-able, and navigable in an IDE. Examples:

- "a rule with `FlattenType.Preserve`" (not "a Preserve-typed rule")
- "`FlattenType.Delete` children" (not "Delete-typed children")
- "the parent is `FlattenType.Flatten`, so its children bubble up" (not "the parent is Flatten-typed, so ...")

Bare `Preserve`, `Flatten`, `Delete` unqualified are fine only when the surrounding context has already said "FlattenType" in the same sentence or paragraph (e.g., "Its `FlattenType` defaults to `Delete`.").

## What We Are Keeping From C++

The parser semantics are exactly the same as the C++ version.

It is still a PEG parser. Ordered choice, greedy matching, backtracking, transactional lexer reads. Rules still form a tree. Parsing still walks the tree and tries to match the input. On failure the parser still backtracks and tries the next alternative. On success you still get a `Symbol` tree you can walk and flatten.

Every concept from the original `GettingStarted.md` has a direct C# counterpart:

| C++ concept                        | C# counterpart                                  |
|------------------------------------|-------------------------------------------------|
| `AndExpression<Args<...>>`         | `AllOf(...)` factory returning `Rule`             |
| `OrExpression<Args<...>>`          | `FirstOf(...)` factory returning `Rule`              |
| `OneOrMoreExpression<T>`           | `OneOrMore(rule)`                               |
| `ZeroOrMoreExpression<T>`          | `ZeroOrMore(rule)`                              |
| `OptionalExpression<T>`            | `Optional(rule)`                                |
| `AtLeastAndAtMostExpression<T,N,M>`| `BetweenInclusive(n, m, rule)`                  |
| `CharacterSymbol<EqualString>`     | `Token('=')`                                     |
| `CharacterSetSymbol<Chars>`        | `OneOf(RuneSet.Letters)`                     |
| `CharacterSetExceptSymbol<...>`    | `NoneOf(charClass)`                          |
| `LiteralExpression<WordString>`    | `Literal("word")`                               |
| `OptionalWhitespaceSymbol<>`       | `OptionalWhitespace()`                          |
| `WhitespaceSymbol<>`               | `Whitespace()`                                  |
| `Integer<>`, `Float<>`             | `Integer()`, `Float()`                          |
| `PeekExpression<T>`                | `Peek(rule)`                                    |
| `NotPeekExpression<T>`             | `Not(rule)`                                     |
| `EofSymbol`                        | `Eof()`                                         |
| `FlattenType::None/Delete/Flatten` | same enum, set via `.Flatten(FlattenType.None)` |
| `MySymbolID::SettingName`          | `.As(nameof(SettingName))`, optional            |
| `tree->FlattenInto(vector)`        | `tree.FlattenInto(list)` (same semantics)       |
| `Compiler<T>::ProcessAst`          | plain function, or your own base class (Recipes)|
| `staticErrorMessage`               | `.WithError("...")`                             |
| `SetTraceFilter(...)`              | field on `ParseOptions` passed to `Parse`       |

The mapping is nearly one-to-one at the concept level. What changes is the syntax you use to wire those concepts together.

## Rules Become Instances, Not Types

In the C++ library, a rule is a type. `NameValueRule` is a class, and when you write `NameValueRule::TryParse(...)` you are calling a static method on a type the compiler generated for you from a pile of templates. The rule tree exists at compile time, and the parser exists to walk it at runtime.

In the C# library a rule is an instance. `var nameValueRule = AllOf(...)` builds a `Rule` object by calling factory functions that return `Rule` instances. Where the rule lives is up to you: a local variable in a method, a `static readonly` field on a class, an entry in a dictionary, an instance passed as an argument. The library does not require any particular grouping. The tree is built at runtime, compiled once (explicitly or on first parse), and reused for every parse after that.

This is the single biggest shift in authoring style. Most other decisions in the design are consequences of it.

It is built this way for three reasons:

- C# generics do not accept non-type parameters. In C++ you can say `OneOrMoreExpression<CharacterSetSymbol<Chars>, FlattenType::None, MySymbolID::SettingName>` and pass an enum value and a number as template arguments. C# cannot express this. The enum and the number have to live somewhere, and the natural place is the constructor of a `Rule` object.
- C# does not have variadic generics. In C++ the `Args<...>` wrapper is already a workaround for the same missing feature. However, C# can use `params Rule[]`, and `AllOf(r1, r2, r3)` will work with any number of children.
- Rules-as-instances gives us things C++ rules-as-types cannot. We can name rules dynamically for tracing. We can build rules in loops (a generated grammar from a config file, say). We can hold references to rules in collections. We can write tests that construct ad-hoc grammars inline. The C++ version cannot do any of this without macro abuse.

The cost is that grammar typos become runtime errors instead of compile errors. If you misspell a rule reference, C++ tells you at compile time (`undefined type NameValueRul`). C# tells you the first time the containing code runs, or worst case the first time you parse. The compile-time story is worse but after compile time is better.

## The Design of Compile

`Compile` is the grammar-finalization pass. Calling `.Compile()` on a rule walks the rule graph using that rule as the root and does four things. The pass is idempotent, returns the same rule for chaining, and is invoked automatically on the first call to `.Parse(...)` if it has not already run. Explicit `.Compile()` exists for callers who want grammar-construction errors to surface at program startup rather than on first parse.

The four jobs are bundled together because they share the graph walk and because each of them catches a class of bug that would otherwise explode at parse time:

**Assign symbol ids.** Rules with an explicit pin (via `.As(SymbolId.Custom(42, ...))`) get their pinned id first, so pinned ids never shift. Rules named with a string (via `.As("name")` or `.As(nameof(X))`) get an id by hashing the name into the custom range. If the hash lands on a slot that is already in use, the id linear-probes from the hash slot upward until it finds an empty slot. Anonymous rules get ids based on their position in the graph and probe the same way. Because the rule graph is frozen after `Compile` returns, every probe resolution is deterministic and stable for the life of the program.

**Resolve every `LateBoundRule`.** Mutually recursive grammars use a `LateBoundRule` placeholder that gets a target attached via a separate `.Bind(...)` call. If a grammar forgets to bind one, the bug would normally surface as a `NullReferenceException` deep inside a parse. `Compile` fails fast with a message naming the unbound rule. That is a much better failure mode than a runtime null deref far from the original mistake.

**Freeze the rule graph.** After `Compile` returns, every rule in the graph is sealed. Calling `.As(...)`, `.Flatten(...)`, `.WithError(...)`, or any other modification method on a sealed rule throws `InvalidOperationException`. This makes the "effectively immutable" claim enforced rather than implicit, and it closes a bug where user code could accidentally mutate a shared rule after parsing has started. One boolean flag per rule, one check per mutation method, negligible cost.

**Validate against obvious mistakes.** A handful of cheap sanity checks worth running once rather than discovering at parse time: two rules pinned to the same explicit `SymbolId.Custom(...)` number, `LateBoundRule` bound to itself or a trivial cycle, rules whose id somehow ended up unset. Unreachable rules are *not* flagged because a user might legitimately be building standalone rules to use elsewhere.

Bundling them is a design choice. The alternative was to split each into its own pass (a naming pass, a binding pass, a freeze pass, a validation pass), but they all want the same graph walk and there is no observable ordering dependency between them. One walk is cheaper, simpler, and easier to document.

## The Design of SymbolId

`SymbolId` is intentionally a single-field struct:

```csharp
public readonly struct SymbolId : IEquatable<SymbolId>
{
    public int Value { get; }

    public bool Equals(SymbolId other) => Value == other.Value;
    public override int GetHashCode()  => Value;
}
```

It is the size of an int (4 bytes), fits in a single register, and every comparison is a single integer compare. Every `Symbol` node in a parse tree carries one of these, so keeping them small pays off on big trees.

Notice what the struct does *not* carry: a human name. Names live on the grammar side, not on the id itself, so that two different grammars loaded in the same process do not fight over a global namespace. Name lookup happens through the rule that knows the grammar context, via `rule.NameOf(someId)`.

The id namespace is split into three non-overlapping ranges so different kinds of id never collide:

```
0x000000..0x10FFFF   Rune symbols (id equals the Unicode code point)
0x110000..0x1FFFFF   Built-in expression symbols
0x200000..           Custom symbols from user-named rules
```

Rune symbols live at the bottom because `LexerSymbol` uses the code point as its id, and a rune can be anywhere from 0 to 0x10FFFF. Built-in expression ids live just above the Unicode range so they cannot collide with a rune. Custom ids live above both. This is a deviation from the C++ numbering (which starts built-ins at 256 and customs at 16000), chosen because both of those ranges fall inside Unicode and would collide with rune ids once the parser started seeing non-ASCII code points. Trace output prints the symbol name rather than the number, so the C++ reference traces still match textually.

## Why Symbol Is a Class, Not a Struct

`Symbol` is a reference type. Every matched rule allocates one on the GC heap. For a 10,000-char parse against a typical grammar, that's roughly 20,000 Symbol allocations per parse. The obvious optimization is to make `Symbol` a struct so those allocations disappear into the stack or inline into their containing arrays. We considered it and didn't do it.

The appeal is real. Each Symbol is small (roughly 32 bytes of data plus a 24-byte class header), most are leaf character nodes whose lifetime tracks their containing tree, and structs are the right tool for "small, immutable, short-lived data." On paper it looks like free performance.

The cost is API churn plus the quiet loss of identity semantics. Identity is the thing worth sitting with for a moment.

A class Symbol has a unique memory address. `Find(someRule)` returns a specific instance. `==` between two Symbol references asks whether they point at the same heap object. That lets code patterns work that don't work on a value type:

- A `HashSet<Symbol>` used to dedupe a tree walk ("have I visited this node?") distinguishes two instances that happen to carry identical field values. With a struct, structural equality wins, and two nodes with the same Id and same children would collapse into one set entry. "I saw this particular node" silently becomes "I saw a node with these field values."
- A `Dictionary<Symbol, Metadata>` attaching information to specific tree positions keys on instance identity today. As a struct, it would key on field contents, so anything structurally equal collides.
- Debugger views of a tree with shared subtrees show one expandable node for each instance. With a struct, every copy looks like a separate entry even when it represents the same position.

None of those patterns appear in this codebase yet. The Symbol API today is read-only tree traversal plus `Find`-by-rule. So the struct change would be fine for the code that exists. The worry is the code that doesn't exist yet. The first time a future user reaches for one of those patterns expecting reference semantics, they'd get value semantics and a silent bug.

Beyond identity, a handful of smaller costs:

`Symbol?` as a nullable class is a single reference with a null check. As a nullable struct it's `Nullable<Symbol>`, which is the struct plus a `bool HasValue`. Every API that returns `Symbol?` (ParseResult.Tree, Find, and so on) grows. Callers rewrite their null checks. Mostly cosmetic, but it touches a lot of callers.

Copy-on-read. Every property access, every foreach iteration, every method return copies the struct. For a 32-byte type that's cheap per copy but multiplies through recursive tree walks. The JIT removes some copies, not all.

Boxing at interface edges. Generic collections and span-based APIs keep the struct on the stack, but any path that upcasts to `IEnumerable` (non-generic) or passes through `object` boxes the struct back onto the heap. Users who assume "struct means no heap" get surprised.

If Symbol allocation ever shows up as a real bottleneck in a profile, the right answer is probably a parse-scoped object pool rather than a struct conversion. Keep the class, recycle instances between parses, preserve identity semantics, preserve the API. A struct refactor would still be available later, but pooling captures most of the win without the churn.

The deeper question is framing: are parse-tree nodes "data without identity" (struct territory, like `Point` or `DateTime`), or "nodes in a graph with distinct identity" (class territory, like DOM nodes or Roslyn's `SyntaxNode`)? Symbol sits on the boundary. The patterns people naturally want to apply to parse trees (dedup sets, annotation dictionaries, reference comparisons in debuggers and tooling) favor the class framing. So we paid the allocation cost and kept identity.

## Rule Is Extensible

`Rule` is an abstract class, and user code can derive from it to add matching logic the built-in composites do not cover. The contract a subclass has to satisfy:

- Implement the matching method to either consume input and return a `Symbol` subtree (success) or return null and roll back its lexer transaction (failure). Never consume input on failure.
- Use the lexer's transactional API (`Begin`, `Commit`, `Rollback`) so backtracking by outer rules works correctly.
- Emit trace output in the same format as built-in rules when `ParseOptions.TraceSink` is set, so grammar-wide traces remain readable.
- Participate in `Compile`: declare yourself named via `.As(...)` if you want an id, declare flatten policy if it matters for tree shape, seal against modification after `Compile` returns.

The full contract including method signatures and the lexer API will be documented alongside the implementation.

For grammars that compose existing leaves (which is most grammars) you never need to derive. The built-in composites cover the PEG operators and the built-in leaves cover the character-class cases. User-defined rules matter when you are adding behavior the composites cannot express, for example a rule that consumes until a specific byte-level offset, a grammar-context-aware matcher that queries external state, or a custom character-boundary detector.

## Why Two Lexers

The parser ships two lexers: `GraphemeLexer` (default) and `RuneLexer`. Both produce one "token" per `Read()` call. They differ in what counts as a token. `GraphemeLexer` walks by Unicode grapheme cluster (UAX #29), `RuneLexer` walks by Unicode code point.

`GraphemeLexer` is the default because "one character" in the user's mental model is one grapheme (the guitar emoji 🎸 is one character, the family emoji 👨‍👩‍👧‍👦 is one character), and grammars that operate on user-typed text want that to be the unit they match. `RuneLexer` exists because some grammars specifically need rune-level access: parsing Unicode-category boundaries, walking combining-mark sequences individually, or implementing a Unicode library on top of the parser.

The implementation details (how graphemes are detected, how position tracking works across the two, where the two produce different streams) live in [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md). The design rationale worth keeping here is: swapping the lexer is a `ParseOptions` field, not a grammar change, and grammars written against the `Rule` API work against either lexer. The rules whose behavior can observably differ between lexers are the ones that compare against a token directly (`Token`, `OneOf`, `NoneOf`, `Literal`, `Peek`, `Not`). Composite rules inherit any difference from a leaf inside them.

Where the two diverge on real input, the `RuneLexer` behavior is usually the buggy one: it was matching part of a grapheme as if it were a standalone character. `GraphemeLexer` fixes this by treating the whole sequence as one token. The reframing is "`GraphemeLexer` revealed that my grammar was silently wrong on multi-rune input," not "`GraphemeLexer` broke my grammar."

## Tokens and Leaves

The rule-vs-lexer interface is narrow: a rule calls `Read()` on the lexer, gets back a `Token`, and compares it against something. Everything in the rule API reduces to that pattern.

### The `Token` Shape

```csharp
public readonly ref struct Token
{
    public ReadOnlySpan<char> Chars       { get; }   // section of the input string
    public int                CharOffset  { get; }   // UTF-16 offset where the token starts
    public int                RuneOffset  { get; }
    public int                GraphemeIndex { get; }
}
```

`Token` is a `ref struct` so it can carry a `Span<char>` into the original input without allocating. Each token is the section of input that the lexer consumed to produce it. Everything else is derived.

Under `RuneLexer` every token is one rune wide by construction (the lexer emits one rune per read). Under `GraphemeLexer` most tokens are still one rune (ASCII, composed-form Latin, CJK, most punctuation are all one grapheme = one rune), but emoji sequences, regional-indicator flags, skin-tone modified emoji, Devanagari conjuncts, and decomposed-form combinations produce multi-rune tokens whose `Chars` span covers the whole grapheme.

The three position offsets let any caller (editors, IDE integrations, error messages) pick the unit they speak in without the parser having to compute the other two lazily later. All three are constant-time reads on the token.

Rules that need to ask "is this token exactly one rune?" walk `Chars` with `EnumerateRunes()`. The enumerator is a `SpanRuneEnumerator` (a ref struct that allocates nothing), so the cost is two `MoveNext` calls in the common single-rune case, which is small enough that caching the answer as a bool on the token is not worth the extra field or the derived-state hazard.

### Comparing Tokens: The Four Leaves

Every built-in rule that looks at token content reduces to one of four operations.

**`Token('=')`, `Token(Rune r)`, `Token(string grapheme)`.** Matches one grapheme, specified at rule-construction time. The `string` overload requires exactly one grapheme and is validated at construction by walking the argument with `StringInfo.GetTextElementEnumerator` and asserting a single element. The `char` and `Rune` overloads are convenience wrappers that build a one-grapheme string. At match time the rule pre-tokenizes its expected grapheme the same way the lexer will tokenize input and walks the expected sequence against `lexer.Read()` in lockstep, comparing `Chars` spans with `SequenceEqual`. Under `GraphemeLexer` that is a single-token compare. Under `RuneLexer` it is a one-to-N token compare (`Token("👋🏽")` expects two rune tokens, waving hand plus medium skin tone, so it reads two tokens and compares each).

**`OneOf(RuneSet cc)` and `NoneOf(RuneSet cc)`.** These are the rune-set tests. Both are defined in terms of the predicate "the token is exactly one rune *r*, and `cc.Contains(r)`." `OneOf` matches when the predicate is true. `NoneOf` matches when it is false. The asymmetry that falls out of this is important: a multi-rune token never matches `OneOf` (the predicate is false because the token is not one rune) but it *does* match `NoneOf` (the predicate is false, so the negation is true). This is what makes `OneOrMore(NoneOf(formattingChars))` sweep up emoji correctly in the pass-through-text recipe.

The two semantics in prose:

- `OneOf(class)` is existential: "is this token one of the runes in the class?" A multi-rune token is not any single rune, so no.
- `NoneOf(class)` is universal: "does this token avoid all runes in the class?" A multi-rune token avoids every single-rune value, so yes.

**`Literal(string s)`.** Tokenizes `s` the same way the lexer will tokenize input (grapheme-walk via `StringInfo.GetTextElementEnumerator` under `GraphemeLexer`, rune-walk via `string.EnumerateRunes()` under `RuneLexer`), caches the tokenized sequence at rule construction time, and matches by walking both sequences in lockstep comparing `Chars` spans with `SequenceEqual`. This is the only one of these types that can consume more than one token in a single match. The other three each look at exactly one token.

Because `Literal` tokenizes the same way the lexer does, a literal like `Literal("👨‍👩‍👧‍👦")` becomes one expected token under `GraphemeLexer` (the whole family-emoji grapheme) and seven expected tokens under `RuneLexer` (four people emoji plus three ZWJs). Either way, the literal matches input that contains the same sequence of characters.

**`AnyToken()`.** Matches any single token regardless of content, as long as the lexer is not at EOF. Under `RuneLexer` it matches any rune. Under `GraphemeLexer` it matches any grapheme, including multi-rune ones. This is the "match one token, whatever it is" leaf.

### RuneSet: The Set Primitive

`OneOf` and `NoneOf` take a `RuneSet`, a set of Unicode code points with the standard set operations lifted onto operators. Keeping the set type separate from the rule types means character-class expressions compose the way set expressions do in ordinary code instead of having to wrap every union inside an `FirstOf(...)`.

```csharp
public readonly struct RuneSet
{
    // Full-Unicode built-ins (correct for every script)
    public static readonly RuneSet Letters;
    public static readonly RuneSet Digits;
    public static readonly RuneSet HexDigits;
    public static readonly RuneSet Whitespace;
    public static readonly RuneSet Identifier;

    // ASCII-only variants faster that RuneSet.Letters
    public static class Ascii
    {
        public static readonly RuneSet Letters    = Range('A','Z') | Range('a','z');
        public static readonly RuneSet Digits     = Range('0','9');
        public static readonly RuneSet HexDigits  = Digits | Range('a','f') | Range('A','F');
        public static readonly RuneSet Whitespace = Runes(" \t\r\n");
        public static readonly RuneSet Identifier = Letters | Digits | Runes("_");
    }

    public static RuneSet Single(char c);
    public static RuneSet Single(Rune r);
    public static RuneSet Range(char low, char high);
    public static RuneSet Range(Rune low, Rune high);
    public static RuneSet Runes(string characters);
    public static RuneSet Category(UnicodeCategory c);

    public static RuneSet operator |(RuneSet a, RuneSet b);   // union
    public static RuneSet operator &(RuneSet a, RuneSet b);   // intersection
    public static RuneSet operator ~(RuneSet a);                // complement

    public bool Contains(Rune r);
    public bool Contains(char c);
}
```

The three operator rationales:

**`|` (union)** is the workhorse. `RuneSet.Letters | RuneSet.Digits | RuneSet.Runes("_-")` composes identifier characters by piecewise addition. Every grammar uses it.

**`&` (intersection)** narrows one semantic set by another. It shines when one operand is a big Unicode-tracking class like `Letters` and the other is a script or script-block restriction:

```csharp
// Cyrillic letters only: letters AND in the Cyrillic block.
// The composition stays correct as Unicode adds new Cyrillic letters.
RuneSet.Letters & RuneSet.Range(new Rune(0x0400), new Rune(0x04FF))

// Hex-digit-like ASCII letters (a–f, A–F, without the 0–9).
RuneSet.Ascii.Letters & RuneSet.Ascii.HexDigits
```

**`~` (complement)** is mostly useful combined with `&` as set difference (`A & ~B`). It is the only way to express "this class minus those elements" without hand-enumerating the result:

```csharp
// Letters except vowels. No pre-built class. You build it by subtracting.
RuneSet.Ascii.Letters & ~RuneSet.Runes("aeiouAEIOU")

// Identifier chars except underscore, for a language where '_' is reserved.
RuneSet.Ascii.Identifier & ~RuneSet.Runes("_")

// Any printable non-whitespace character. Start from "all runes",
// subtract categories you don't want.
~(RuneSet.Whitespace | RuneSet.Category(UnicodeCategory.Control))
```

`OneOf(~X)` and `NoneOf(X)` match the same single-rune tokens, so at the outermost level the complement operator is redundant with `NoneOf`. The reason complement exists on the class is that `NoneOf` is a rule and cannot be fed back into another set expression. `~X` is a class and can be intersected, unioned, or handed to another `OneOf` / `NoneOf`.

Intersection and complement are niche compared to union. Most grammars use `|` dozens of times and never touch the other two. They earn their spot because they are cheap (sorted-range intersection and complement are single passes), and because when an author does need set difference, hand-enumerating the ranges goes stale the moment Unicode adds a new letter to the base class.

`RuneSet.Letters` and its siblings cover the full Unicode character set: `Letters` matches `é`, `漢`, `Ω`, `ж`, and every other letter in every script Unicode knows about. Grammars that specifically want ASCII-only reach for `RuneSet.Ascii.Letters` to say so explicitly. The split is deliberate because the two are different defaults. A programming-language keyword parser wants ASCII identifiers so a stray `café` does not parse as a variable name. A text-processing grammar wants the full Unicode set so combining-mark scripts work at all.

`Contains(Rune)` is the predicate every `OneOf` / `NoneOf` match resolves to, exposed as public so user-defined rules can reuse the same predicate without going through the rule wrapper.

Internally a `RuneSet` is a sorted list of rune ranges. Union, intersection, and complement are all linear in the number of ranges, which is small for typical grammars (letters and digits are a handful of ranges each). Construction-time evaluation folds compound expressions into a single range list, so `Letters | Digits | Runes("_")` is one flat structure by the time a `OneOf` rule sees it.

### The Non-Content Leaves

A few more rule types exist but do not touch token content directly:

- `Peek(rule)` and `Not(rule)` run their inner rule without committing the transaction. Whatever the inner rule would do with tokens, `Peek` and `Not` inherit from that behavior. No special handling at the token level.
- `AllOf(...)`, `FirstOf(...)`, `OneOrMore(...)`, `ZeroOrMore(...)`, `Optional(...)` are composites. They never inspect tokens themselves. They just sequence or alternate other rules.
- `Eof()` matches iff the lexer is at the end of input. Does not read a token.

Everything else (flatten policies, error messages, named symbols) is metadata on the resulting `Symbol` tree, not comparison logic.

### How a Rule's Match Method Looks

Concretely, the four comparison leaves are all short:

```csharp
// Token(string grapheme): _tokenizedExpected is precomputed, length 1 under
// GraphemeLexer and 1..N runes under RuneLexer
using var tx = lexer.BeginTransaction();
foreach (var expected in _tokenizedExpected)
{
    var actual = lexer.Read();
    if (!actual.Chars.SequenceEqual(expected)) return null;
}
tx.Commit();
return makeSymbolFrom(...);

// OneOf(RuneSet cc)
using var tx = lexer.BeginTransaction();
var token = lexer.Read();
var it = token.Chars.EnumerateRunes();
if (!it.MoveNext()) return null;
var rune = it.Current;
if (it.MoveNext()) return null;              // multi-rune token, fails set membership
if (!cc.Contains(rune)) return null;
tx.Commit();
return makeSymbolFrom(token);

// Literal(string s): _tokenizedLiteral is precomputed
using var tx = lexer.BeginTransaction();
foreach (var expected in _tokenizedLiteral)
{
    var actual = lexer.Read();
    if (!actual.Chars.SequenceEqual(expected)) return null;
}
tx.Commit();
return makeSymbolFrom(...);

// AnyToken()
using var tx = lexer.BeginTransaction();
var token = lexer.Read();
if (token.IsEof) return null;
tx.Commit();
return makeSymbolFrom(token);
```

Each is a handful of lines. The common shape (start a transaction, read a token, test, commit on success or fall out returning `null` on failure) is the scaffolding user-defined rules inherit when they derive from `Rule` (see the "Rule Is Extensible" section). The `using` on `tx` rolls the lexer back automatically on any path that does not call `Commit()`, including exceptions.

### Matching Multi-Rune Graphemes

Under `GraphemeLexer`, a multi-rune grapheme like 👨‍👩‍👧‍👦 arrives as a single token whose `Chars` span covers the whole sequence (eleven UTF-16 chars, seven runes). The ways a grammar can match it:

- **`Token("👨‍👩‍👧‍👦")`** matches one grapheme by exact content. Construction-time validation rejects arguments that are not exactly one grapheme, so `Token("ab")` throws at grammar-build time instead of failing silently at parse time.
- **`Literal("👨‍👩‍👧‍👦 and friends")`** matches a sequence of graphemes by exact content. Same pre-tokenize-then-lockstep logic as `Token`. The difference is that `Literal` accepts any length.
- **`AnyToken()`** matches any token including multi-rune ones. Useful when the grammar is streaming text through as opaque content ("an identifier is any non-delimiter character").
- **`NoneOf(someClass)`** matches multi-rune tokens because they are not in any single-rune class. This is the mechanism behind the pass-through-text recipe.

What you *cannot* do:

- **Define a `RuneSet` that includes specific multi-rune sequences.** A `RuneSet` is a set of code points, not a set of sequences. If you want to match "any of these specific multi-rune sequences," express it as `FirstOf(Token(a), Token(b), Token(c))`, not as a character class.
- **Test "is this grapheme a letter?" with `OneOf(RuneSet.Letters)`** when the grapheme is multi-rune. The class is defined over single runes, so any multi-rune grapheme is outside it. If you want "any identifier character, including combining marks as part of a letter sequence," either switch to `RuneLexer` and consume each rune individually, or include Mark categories in a broader character class and accept that the grammar will capture combining marks as separate tokens under `RuneLexer`.

The split that remains is between rune-set tests (`OneOf`, `NoneOf`) and content-match leaves (`Token`, `Literal`). The set tests are defined over single runes by construction (a `RuneSet` is a set of code points), and the content-match leaves compare raw `Chars` spans, so they handle multi-rune graphemes naturally. A glance at a rule tells you which half of the API it lives in.

## Greedy Repetition, No Repetition Backtracking

PEG parsers backtrack on alternatives (`FirstOf` tries each branch in order until one succeeds, rolls back between attempts), but they do NOT backtrack inside repetition. `OneOrMore`, `ZeroOrMore`, and `Optional` are greedy by construction: they grab as many matches as they can get and never give any back. This is inherited from the C++ library and it's a defining property of PEG, not a design choice unique to this port.

The practical consequence is the most common trip-up when moving from regex to PEG. Consider:

```csharp
var rule = AllOf(OneOrMore(OneOf(RuneSet.Letters)), Token('a'));
var result = rule.Parse("aaa");
```

A regex engine with greedy backtracking would:

1. `[a-z]+` greedily grabs `"aaa"`.
2. Then try to match the trailing `a` against EOF, fail.
3. Back off the repetition to `"aa"`, try again, succeed on the trailing `a`.

A PEG engine does NOT do step 3. Once `OneOrMore` matched `"aaa"`, those matches are committed. The outer `AllOf` then tries `Token('a')` at EOF, fails, and the whole parse fails. Our `BetweenInclusiveRule` (which `OneOrMore`, `ZeroOrMore`, and `Optional` all factory through) preserves this: the loop inside its `TryParse` commits each successful inner match as it goes, and the loop just stops when the inner fails on the next attempt. No rewind.

This looks like a cost, and sometimes it is. Grammars that worked in regex need to be restructured, usually with `Not(...)` lookahead to stop repetition one step short, or by splitting the repeated rule into a less-greedy form. The benefit is unambiguity: given a grammar and an input, PEG returns exactly one parse (or a fail), and the parse is whichever answer the ordered choices and greedy matches produced. Regex engines without this property have decades of scars from ambiguous patterns and catastrophic backtracking (ReDoS).

Two corollaries of "no repetition backtracking" that show up in the implementation:

**Each successful inner match is committed.** Inside `BetweenInclusiveRule.TryParse`, the inner `TryParse` opens its own transaction and commits on success. Once the first inner succeeds, the outer rule's own transaction stays uncommitted only until the final result is decided. Every matched-so-far position is locked in.

**Zero-width inner matches would loop forever.** `OneOrMore(Optional(X))` has an inner that always "succeeds" without consuming input. Without a guard, the greedy loop would match Optional(X) infinitely. `BetweenInclusiveRule` carries an `if (lexer.Position == before) break;` check that stops the loop when a match didn't advance, so all three derived factories inherit the protection. The C++ version has the same guard for the same reason.

So, the full execution model is: ordered-choice backtracking between alternatives, greedy non-backtracking within repetition, and transaction-based rollback ties the two together. The catastrophic-backtracking patterns discussed in the next section are not about greed failing to back off. They're about ordered choice retrying at overlapping cursor positions when multiple alternatives interact badly.

## Parse Requires Consuming All Input

The top-level `Rule.Parse(input)` returns success only when the grammar both matches AND consumes the entire input. If the grammar matches and leaves a tail of unmatched characters, `Parse` returns failure pointing at the first leftover character. There's no partial-success mode.

Concretely:

```csharp
var rule = OneOrMore(Token('a'));
var result = rule.Parse("aabb");
// result.Success == false
// result.ErrorCharIndex == 2
// result.ErrorMessage starts with "Parse failed at offset 2"
```

`OneOrMore(Token('a'))` greedily matches "aa" and stops because the next char isn't 'a'. The rule's own `TryParse` returned a tree happily. But the top-level `Parse` then checks `lexer.IsEof`, finds we're at offset 2 with "bb" still ahead, and turns the success into a failure.

Why this default. Most grammars represent "what a valid input looks like end-to-end" (a settings file, an expression, a query). If a user types `setting = 5` without a trailing `;`, they want to hear "missing ;", not "I happily parsed `setting = 5` and ignored what came after." Silently dropping trailing input would mask the entire class of "your grammar accepted something it shouldn't have" bugs that grammar authors care most about catching.

The flip side is that grammars built piecewise can't be unit-tested in isolation by calling `Parse` on a prefix. If you have a `settingName` sub-rule and want to test it against `"setting"`, that works because `"setting"` is fully consumed. But testing it against `"setting = 5"` needs the whole grammar, not just `settingName.Parse(...)`. This shows up in the test suite: rules used in composition are tested standalone with inputs sized to match the rule, not inputs sized to match a real document.

If you genuinely want prefix parsing in some future grammar, the workaround today is to wrap the grammar in something that swallows trailing content explicitly, `AllOf(yourGrammar, ZeroOrMore(AnyToken))` once the `AnyToken` leaf lands (backlog i028). The library could grow a `ParseOptions.AllowTrailingInput` flag if a real use case shows up. For now the default catches more bugs than it causes.

## Where Errors Get Positioned

When a parse fails, the `ParseResult` carries an `ErrorCharIndex` that tells the caller where the problem was. The question is: where exactly? Every rule has some choice about what to report, and without a shared principle the answer drifts rule by rule.

The library commits to one rule:

> Every rule records its failure at the start of the offending input, the position of the character or token it couldn't match. Composite rules don't introduce new positions. They propagate their children's recorded positions via deepest-failure-wins.

Concretely this means `input[result.ErrorCharIndex]` gives the actual character that didn't match, not the character after it. If the index equals `input.Length`, that's a genuine end-of-input case: the grammar wanted more and there wasn't any. The index never falls outside `[0, input.Length]`.

Walk through the smallest case to see why this matters. `Token('a').Parse("x")`:

1. TokenRule opens a transaction. `transaction.StartPosition` is 0.
2. Reads 'x'. Lexer position advances to 1.
3. 'x' doesn't equal 'a'. TokenRule records its failure at `transaction.StartPosition` (0), not at the current lexer position (1).
4. Transaction rolls back, lexer returns to position 0.
5. `result.ErrorCharIndex` is 0. `result.ErrorMessage` is `"Parse failed at offset 0: unexpected 'x'."`.

A naive post-read implementation would record at 1 instead of 0, which equals `input.Length` for this one-char input, which makes `BuildErrorMessage` take the "Unexpected end of input" branch even though the input isn't empty. That's the kind of off-by-one that accumulates over a library's lifetime until every error message is slightly off and nobody remembers why. Picking a principle early and applying it uniformly keeps the error messages accurate.

### Three Cases

**Single-token leaves** (`TokenRule` single-rune, `OneOfRule`, `EofRule`) open a transaction, read one token, and fail if the token doesn't match. The pre-read position is exactly `transaction.StartPosition`, which the `Lexer.Transaction` struct exposes for this purpose. No extra locals, no separate state: the transaction already knows.

**Multi-token leaves** (`TokenRule`'s lockstep loop for multi-rune graphemes under `RuneLexer`, `LiteralRule`) read a sequence of tokens and fail when any one of them mismatches. The position is the start of the *specific* failing token, not the start of the whole attempt. A `Literal("abc")` that matches "ab" and fails on the third token reports offset 2, not offset 0. These rules track a per-iteration `tokenStart` local inside the loop.

**Composite rules** (`AllOfRule`, `FirstOfRule`, `BetweenInclusiveRule`) don't introduce new positions of their own. They call `RecordFailure(lexer.Position, ...)` (the current lexer position after a child's transaction has rolled back), which equals where the child started trying. The child has already recorded at its own pre-read position (which is the same or deeper, depending on whether the child committed any sub-tokens before failing), so the composite's record either ties or is shallower, and deepest-failure-wins routes to the child's more-specific location. The composite still gets a chance to attach its `WithError` message via the equal-depth message-claim rule below.

### Deepest Failure Wins

Multiple rules can call `RecordFailure` during one parse. The lexer keeps track of the deepest position seen and the error message attached to that position. Two resolution rules:

1. **Strictly deeper beats shallower.** A rule that records at offset 7 beats one that recorded at offset 3. The deeper record's message replaces whatever was there.
2. **Equal depth, first-non-null-message wins.** When two rules record at the same position, the one with a non-null error message claims the message slot, but only if no one has claimed it yet. This is what lets a composite like `OneOrMore(...).WithError("Expected a setting name")` contribute its friendly message even though its inner leaf got there first with a null message.

The equal-depth restriction matters: without it, a shallow rule's `WithError` could steal the message slot from an unrelated deeper failure, which would produce misleading error output. Equal-depth-only keeps the two signals (position and message) aligned.

### A Known Heuristic Limitation

The deepest-failure-wins model works well in practice but has one characteristic quirk: `Optional(...)` rules whose inner gets deeper than the surrounding required path can "capture" the error message into a branch that was truly optional.

Concrete case: `AllOf(Optional(Literal("abc")), Token('x')).Parse("abdy")`. The Optional's inner reads "ab" and fails on 'd' vs 'c' at offset 2. Optional catches the failure and succeeds with empty children, so the overall grammar proceeds. Then Token('x') tries at offset 0, fails on 'a'. Deepest-failure-wins picks offset 2 (the abandoned Optional attempt), not offset 0 (the actually-required rule's failure). The user sees "unexpected 'd'" pointing at content inside what was supposedly optional.

This isn't a bug. It's a property of the heuristic. Grammars that care about this can put `.WithError(...)` on the outer required rule, and the equal-depth message-claim rule will make that message appear even when the deepest position came from the optional branch. The full fix would require a different error model (something like tracking a separate "required-path failure" position alongside the deepest raw position), and no existing PEG library we've surveyed does that. The smallest core lives with the quirk and documents it.

### LSP Position Semantics

`ParseResult.ErrorLine` and `ErrorColumn` follow the Language Server Protocol's position conventions. LSP is the JSON-RPC protocol that VS Code, Neovim, JetBrains IDEs, and essentially every modern editor use to talk to language tooling. If a grammar author is going to forward a parse error into an editor, they are almost certainly going to do it through LSP, either directly or through a layer that speaks LSP. Matching LSP end-to-end means the integration is `new Diagnostic { Range = new Range(errorLine, errorColumn, ...) }` with no arithmetic in between. Pick a different convention and every caller writes the same `-1` shim forever.

Three specific rules fall out:

**Lines are 0-based.** The first line of the file is line 0, not line 1. This is the part that surprises people reading an error in isolation (editors display 1-based to humans), but the point of these fields is machine-to-machine handoff, not direct human display. If the caller wants 1-based for a user-facing error message they add one at the edge, exactly where the translation belongs.

**Columns count UTF-16 code units, not runes or graphemes.** LSP 3.17 made the encoding negotiable via `PositionEncodingKind`, but UTF-16 is still the default every implementation ships with. Counting in UTF-16 means that a grapheme like 👋🏽 (two runes, four UTF-16 chars, one visible character) contributes four to the column count, same as what VS Code's internal buffer sees. The rune and grapheme counts live on their own properties (`ErrorRuneIndex`, `ErrorGraphemeIndex`) for callers whose mental model works in those units.

**`\r\n` is one line break, attributed to the `\n`.** LSP treats the pair atomically: a position cannot fall between the `\r` and the `\n`. An `ErrorCharIndex` that somehow does land on the `\n` half (possible under `RuneLexer`, where the two are separate tokens) is reported on the prior line so the column stays non-negative. Grammars using the default `GraphemeLexer` never hit this case because the lexer tokenizes `\r\n` as a single grapheme cluster per UAX #29.

The equivalent C++ library returns a character offset and nothing else, leaving line/column computation to the caller. The C# port bundles them because the caller almost always wants them anyway, and bundling lets us pick the convention once and document it once.

## Tracing Design

The parser emits trace output that shows every rule attempt, its outcome (success/failure), and indentation that mirrors the transaction depth. Enable by setting `ParseOptions.TraceSink` to a `TextWriter`. Leave it null and tracing is off. See [ProgrammingAGrammar.md](ProgrammingAGrammar.md#tracing) for usage examples.

### Why a Custom Tracer, Not System.Diagnostics.Trace or ILogger

We don't use `System.Diagnostics.Trace`, `TraceSource`, `EventSource`, or `Microsoft.Extensions.Logging.ILogger`. Four reasons in order of how much they drove the choice:

**Zero cost when off, without a source generator.** The default paths in standard tracing APIs aren't zero-cost on a disabled call. `Trace.WriteLine($"...")` evaluates the interpolated string before checking for listeners. `TraceSource.TraceInformation(template, arg)` and `ILogger.LogInformation(template, args)` defer formatting but still allocate a `params object[]` and box each value-type argument. The one standard path that's genuinely near-zero-cost is `LoggerMessage.Define` plus the `[LoggerMessage]` source generator, which generates typed, `IsEnabled`-guarded code. Our `TraceInterpolatedStringHandler` matches that result by piggybacking on C# 10's interpolated-string-handler compiler rewrite, same zero-cost outcome, no generator setup, no logging dependency.

**Pre-declaring every distinct message doesn't fit.** `LoggerMessage.Define` and `[LoggerMessage]` require each distinct trace shape to be pre-declared as a delegate or a partial method. Our rules have 3-5 distinct message shapes each, so the library would need 30-50 separate declarations instead of the 3-4 helper methods it has now. The interpolated-string-handler approach lets any call look like `TraceSuccess(lexer, $"found {count}")` with no pre-declaration.

**Output shape is specific to parsing.** Trace output uses indentation based on `Lexer.TransactionDepth`, so nested rule attempts read visually. `SUCC` / `FAIL` prefixes, deepest-failure markers, and rule labels are all parse-specific conventions. Standard logging APIs have categories and levels but no notion of "indent by the currently-open transaction depth", using them would mean either baking the indent into the message string (breaking structured logging) or writing a custom `ILoggerProvider`. At that point there's no real borrowing left.

**Synchronous ordering.** Parsing is CPU-bound synchronous work. Trace lines have to come out in execution order, on the calling thread, at the depth that was live when the line was written. Several `ILogger` implementations route through async queues or batched sinks where execution order and call-stack context are lost. A `TextWriter` writes immediately on the parsing thread, which is what the indentation scheme needs.

The cost of the custom approach is about 50 lines of code in `TraceInterpolatedStringHandler.cs` plus the `Trace` / `WriteTraceLine` helpers on `Lexer`. That's less than the `LoggerMessage.Define` declarations alone would have been, and there's no external dependency.

## Catastrophic Backtracking Design

PEG parsers backtrack. Ordered choice with greedy matching makes most grammars linear in practice, but certain grammar shapes interact with certain inputs to produce exponential work. The classic shape is `OneOrMore(OneOrMore(A))` where `A` can match in multiple ways at the same cursor position: the parser ends up trying every partition of the matching prefix. You can write this accidentally. The original C++ parser has no defense against it, and a grammar that runs fine on your test corpus can hit a pathological input in production and spin for seconds or minutes.

The C# port builds in runtime defenses from the start.

### Why Three Budgets, Not One

`ParseOptions` exposes three orthogonal budgets plus the cancellation token: `Timeout`, `RuleCountLimit`, `MaxDepth`. They answer different questions and a caller that sets all three gets whichever trips first.

`Timeout` is what interactive callers want: "don't make the user wait more than a second." `RuleCountLimit` is what tests and security gates want: "this parse should not exceed 10 million rule invocations, and I want the same answer on every machine." A test that only sets `Timeout` will be flaky on slow CI agents and will let pathological input through on fast ones. A production service that only sets `RuleCountLimit` will not protect interactive users from a parser that took the full budget but took it slowly.

`MaxDepth` is a separate concern. It does not help with exponential backtracking, it helps with stack overflow on deeply nested but well-formed input. A JSON document nested 10,000 levels deep will not time out and will not exhaust the rule-count limit, but it will blow the call stack before any of those triggers. `MaxDepth` catches it before the stack overflow crashes the whole process.

The default settings are `RuleCountLimit = 10_000_000`, `MaxDepth = 1000`, `Timeout = null`. The two hardware-independent limits are on by default because they protect naive callers from catastrophic-backtracking and stack-overflow attacks without being flaky or hardware-dependent. `Timeout` stays opt-in because it is inherently flaky (same input takes different time on different hardware) and would cause unpredictable test failures as a default. `.NET`'s `Regex` shipped for over a decade without any of these defaults and produced a long parade of ReDoS vulnerabilities in real-world applications. A PEG engine is in the same failure class and should not repeat that history.

### Why CancellationTokenSource.CancelAfter Is Not Enough

The obvious .NET answer to "abort after N seconds" is `new CancellationTokenSource(TimeSpan.FromSeconds(N))` plus a `token.ThrowIfCancellationRequested()` check in the parse loop. That works on desktop. It does not work on WebGL, and that is the constraint that shapes this design.

`CancellationTokenSource.CancelAfter` schedules the cancellation through `System.Threading.Timer`, which needs a timer thread to fire the callback. WebGL has no timer thread. The callback can only run when control returns to the browser event loop, and a tight synchronous parse loop never yields. You can pass a cancellation token with a ten-second deadline, the parser can run for an hour, and the token will never fire because the scheduler it depends on is suspended. `CancellationToken` is useful for desktop and test callers, and the parser supports it, but it cannot be the *only* defense against runaway parses.

The parser polls deadlines from inside its own loop, using a clock it reads synchronously. Portable to every platform including WebGL.

### Unwinding a Tripped Budget: Throw Once, Catch at the Boundary

Once a budget trips, the parse has to unwind cleanly from deep inside possibly-nested transactions. The mechanism is a single internal throw:

- The periodic budget check (every *N* rule invocations, not every one) throws `ParseBudgetExceeded` when it trips.
- The exception unwinds through whatever stack of rules is currently active. Each frame has a `using var tx = lexer.BeginTransaction()`, which rolls back on any non-commit exit including an in-flight exception, so the lexer state is restored frame by frame on the way up at no additional cost.
- `Parse()` catches the exception at the top and converts it to a failed `ParseResult` with the budget-exceeded reason.

The throw is cold by construction. It fires once per pathological parse, not per rule invocation, so the IL2CPP exception performance cost is irrelevant. The only IL2CPP constraint that does apply is "no exception filters" (`catch ... when (...)`), which this design does not need anyway.

This is a change from an earlier draft that used a sticky abort flag on every `EnterRule` to avoid throwing. The flag-check approach works, but it adds a field to every parse state, a branch to every rule invocation, and a two-step "check flag then null-return" pattern in every rule. The throw-at-the-boundary approach leans on the `using`-based rollback scaffolding that already exists, so the rule-side code stays identical to the normal match-failure path.

### Why the Work Budget Counts Rule Invocations, Not Character Reads

Catastrophic backtracking is characterized by revisiting the same cursor position many times, which shows up as lots of rule invocations at the same cursor. A budget that counts character reads would underweight this: a pathological backtrack reads the same characters repeatedly, and each is still just one read.

Counting rule invocations gives us a metric that responds directly to the thing we are trying to defend against. A 10-million-invocation budget lets well-formed parses through (a 1 MB file runs through low millions of invocations on a typical grammar) and cleanly catches exponential blow-ups, which produce tens of billions of invocations on tiny inputs.

## Future Ideas

### Cut Operator

A grammar-level `Cut()` rule is the PEG community's standard tool for preventing catastrophic backtracking by construction rather than by runtime limit. Once the parser passes a cut, it is not allowed to backtrack past that point. If a subsequent rule fails, the failure is hard and propagates up instead of triggering a retry of an earlier alternative.

```csharp
// Conceptual sketch of the API if we added it
public static readonly Rule FunctionDecl =
    AllOf(
        Literal("function"),
        Cut(),                              // past here, no backtracking
        Identifier,
        Token('('),
        /* ... */
    );
```

Complementary to the timeout budgets: timeouts catch the cases you did not anticipate, cuts prevent the cases you did. Worth adding later, not in the first pass.

### Packrat Memoization

The "correct" fix for catastrophic backtracking in the worst case. A packrat parser memoizes every rule result at every cursor position, guaranteeing linear time complexity in the length of the input. Any PEG can be converted to a packrat parser mechanically without changing the grammar.

The cost is memory. The memoization table is `O(input_length × rule_count)`, roughly a gigabyte for a 1 MB file and a 100-rule grammar. WebGL's 2 GB memory ceiling means we cannot enable this by default: a caller parsing a large file on mobile WebGL would run the browser out of memory. Packrat would have to be an opt-in parse option (`ParseOptions.Memoize = true`) for callers who know their grammar benefits from it.

Packrat also does not help with left-recursive grammars (which standard PEG does not support anyway) and adds its own overhead for grammars that do not need it. So it is not a universal win, just a big hammer for the cases where it applies.

## Things That Got Better

Six places where the C# version is strictly nicer, not just different.

No required class scaffolding. The C++ version makes a grammar a type: every rule is a class, grammar composition is template instantiation. The C# port makes a grammar a set of values, which means you can build one inline as local variables, pass rules around, compose rules across files, and write tests that construct ad-hoc grammars without any class boilerplate.

Unicode correctness by default. The GraphemeLexer reads one grapheme per step, the RuneSet stores full-Unicode ranges, and composition normalization runs by default. Grammars handle emoji, combining marks, CJK, and non-Latin scripts correctly without the author having to think about encoding. The C++ version is ASCII-only in practice.

Runtime defenses against catastrophic backtracking. The C++ version has no protection: a pathological input and a grammar with ambiguous alternatives can combine to spin for minutes. The C# port has three orthogonal budgets plus cancellation-token support, with protective defaults on the two deterministic ones, and `ParseResult.Outcome` tells the caller which one tripped.

Variadic rules without the `Args` wrapper. `AllOf(r1, r2, r3, r4)` beats `AndExpression<Args<r1, r2, r3, r4>>`.

Composable character classes. `RuneSet.Letters | RuneSet.Digits | RuneSet.Runes("_-")` is worth the whole port by itself.

Proper error objects. `ParseResult.ErrorLine` and `ErrorColumn` are computed on demand from the position. In the C++ version you get a message and a character offset and you have to compute line/column yourself.

## Things That Got Worse

Two places where we lose something real.

Compile-time errors become runtime errors. If you misspell a rule reference in C++, the compiler catches it. In C# it becomes a `NullReferenceException` the first time you hit that branch of the grammar. Writing a unit test that parses a known-good input against every grammar is the real fix, and that is fine.

Rule graphs can have order-of-initialization traps. `static readonly Rule A = AllOf(B, C);` requires `B` and `C` to exist. If they are in the same file this is fine because C# initializes static fields top-to-bottom in declaration order. If they are in different files and there is a cycle, you can get a default-initialized `Rule` reference (`null`) where you expected a real rule. Mutually recursive grammars (expression grammars, for example) have to use a `LateBoundRule` forward-reference trick:

```csharp
// Expression grammar with self-reference
static readonly LateBoundRule Expression = new LateBoundRule();

static readonly Rule Term =
    FirstOf(
        Integer(),
        AllOf(Token('('), Expression, Token(')'))    // refers to the not-yet-built expression
    );

static readonly Rule Sum =
    AllOf(Term, ZeroOrMore(AllOf(Token('+'), Term)));

static readonly Rule _init = Expression.Bind(Sum);   // wire up the late binding
```

`LateBoundRule` is a rule that forwards to a target set later. It is the C# answer to C++'s ability to reference a class name before it is fully defined. The `_init` field is a static initializer trick to run the `.Bind(...)` call at type init time.

## Open Questions

Three things this document does not decide yet, because they need the first real grammar to shake out.

**Whether `Rule.Parse(...)` should have an async variant.** The design above is sync, which matches the architecture doc. If a consumer wants async file loading they can load the file first and then call the sync parser. Nothing inside a parse is async-worthy (parsing is CPU-bound), so this question is really about convenience for callers whose compose-with-file-loading wrapper wants to be async end to end.

**Whether `Compile` should warn about unnamed rules that look like they should be named.** The current rule is "anonymous rules are fine, named rules are opt-in," which is easy to reason about but makes it possible to end up with a grammar whose trace output is full of `rule#47` labels because the author forgot the `.As(...)` calls. A cheap heuristic warning might catch this, but it also might be noise.

**Whether to ship a `Regex` helper built on top of the parser.** The grapheme-level PEG engine can implement regex-style find-and-replace cleanly (the core idiom is a `ZeroOrMore(FirstOf(pattern, AnyToken()))` scanner plus a tree walk that emits replacements). A small helper class (`new Regex(findRule).Replace(input, replacer)`, `Regex.IsMatch`, `Regex.Matches`) would wrap this with a friendlier API and let grammars reuse the parser's Unicode correctness, catastrophic-backtracking protection, and timeout budgets. Three missing leaves would be needed (`AnyToken()`, position-aware anchors like `StartOfLine` / `EndOfLine` / `WordBoundary`, and a lazy-quantifier helper). The first version should almost certainly be rule-based only (no classic `/pattern/flags` string parsing), since users who want compact regex syntax can still use `System.Text.RegularExpressions`.
