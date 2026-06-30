# Inductor Parser Design Decisions

This document is the design and architecture of the InductorParser C# port. It explains *why* the library is shaped the way it's, what tradeoffs were made, and what we considered but didn't do.

If you want to write grammars, start with the primers below or read [InductorParserReference.md](InductorParserReference.md). This doc is for readers who want to understand or evaluate the design itself, or who plan to extend the library.

Primers:

- [Primer 1: Getting Started](primer1.md): build a grammar that consumes everything up to a stop sequence, parse some input, look at the tree.
- [Primer 2: Walking the Tree](primer2.md): a tiny INI-style config grammar with typed values, a tree walker, and Unicode-aware error positions.
- [Primer 3: Unicode in the Inductor Parser](Primer3.md): how the parser handles Unicode normalization, error positions, ill-formed input, and unexpected characters.
- [Primer 4: Security-Related Concerns](Primer4.md): parser defenses against pathological input (ReDoS, recursion limits) and Unicode-based attacks (Trojan Source, lookalikes, homoglyphs, invisible characters).
- [Tutorial: Peek](tutorial-peek.md): a password-validation regex translated into the parser, using `Peek` for non-consuming lookahead.

Related docs:

- [InductorParserReference.md](InductorParserReference.md): user reference for how to write grammars with the library.
- [Terminology.md](Terminology.md): library-specific meaning of terms used throughout these docs (leaf, composite, syntax tree, debug tree, AST, FlattenType writing conventions).
- [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md): lexer internals (code units, runes, tokens, normalization).
- [UnicodeGotchas.md](UnicodeGotchas.md): caller-side Unicode concerns the lexer can't fix.

## What We're Keeping From C++

The parser semantics are exactly the same as the [C++ version](https://github.com/EricZinda/InductorParser).

It's still a PEG parser. Ordered choice, greedy matching, backtracking, transactional lexer reads. Rules still form a tree. Parsing still walks the tree and tries to match the input. On failure the parser still backtracks and tries the next alternative. On success you still get a `Symbol` tree you can walk and flatten.

Every concept from the original [GettingStarted.md](https://github.com/EricZinda/InductorParser/blob/master/GettingStarted.md) in the C++ parser repository has a direct C# counterpart:

| C++ concept                        | C# counterpart                                  |
|------------------------------------|-------------------------------------------------|
| `AndExpression<Args<...>>`         | `And(...)` factory returning `Rule`             |
| `OrExpression<Args<...>>`          | `Or(...)` factory returning `Rule`              |
| `OneOrMoreExpression<T>`           | `OneOrMore(rule)`                               |
| `ZeroOrMoreExpression<T>`          | `ZeroOrMore(rule)`                              |
| `OptionalExpression<T>`            | `Optional(rule)`                                |
| `AtLeastAndAtMostExpression<T,N,M>`| `BetweenInclusive(n, m, rule)`                  |
| `CharacterSymbol<EqualString>`     | `Token('=')`                                     |
| `CharacterSetSymbol<Chars>`        | `OneOf(TokenSet.Letters)`                     |
| `CharacterSetExceptSymbol<...>`    | `NoneOf(charClass)`                          |
| `LiteralExpression<WordString>`    | `Literal("word")`                               |
| `OptionalWhitespaceSymbol<>`       | `Optional(AnyWhitespace())`                     |
| `WhitespaceSymbol<>`               | `AnyWhitespace()`                               |
| `Integer<>`, `Float<>`             | `Integer()`, `Float()`                          |
| `PeekExpression<T>`                | `Peek(rule)`                                    |
| `NotPeekExpression<T>`             | `Not(rule)`                                     |
| `EofSymbol`                        | `Eof()`                                         |
| `FlattenType::None/Delete/Flatten` | `FlattenType.Preserve/Delete/Flatten`, set via `.Flatten(FlattenType.Preserve)` (implicit when the rule is also `.As(...)`-named) |
| `MySymbolID::SettingName`          | `.As(nameof(SettingName))`, optional. Also flips the rule's flatten policy to `Preserve` so `Tree.Find` can locate it |
| `tree->FlattenInto(vector)`        | `tree.FlattenInto(list)` (same semantics)       |
| `Compiler<T>::ProcessAst`          | plain function, or your own base class (Recipes)|
| `staticErrorMessage`               | `.WithError("...")`                             |
| `SetTraceFilter(...)`              | field on `ParseOptions` passed to `Parse`       |

The mapping is nearly one-to-one at the concept level. What changes is the syntax you use to wire those concepts together.

## Rules Become Instances, Not Types

In the C++ library, a rule is a type. `NameValueRule` is a class, and when you write `NameValueRule::TryParse(...)` you're calling a static method on a type the compiler generated for you from a pile of templates. The rule tree exists at compile time, and the parser exists to walk it at runtime.

In the C# library a rule is an instance. `var nameValueRule = And(...)` builds a `Rule` object by calling factory functions that return `Rule` instances. Where the rule lives is up to you: a local variable in a method, a `static readonly` field on a class, an entry in a dictionary, an instance passed as an argument. The library doesn't require any particular grouping. The tree is built at runtime, compiled once (explicitly or on first parse), and reused for every parse after that.

This is the single biggest shift in authoring style. Most other decisions in the design are consequences of it.

It's built this way for three reasons:

- C# generics don't accept non-type parameters. In C++ you can say `OneOrMoreExpression<CharacterSetSymbol<Chars>, FlattenType::None, MySymbolID::SettingName>` and pass an enum value and a number as template arguments. C# can't express this. The enum and the number have to live somewhere, and the natural place is the constructor of a `Rule` object.
- C# doesn't have variadic generics. In C++ the `Args<...>` wrapper is already a workaround for the same missing feature. However, C# can use `params Rule[]`, and `And(r1, r2, r3)` will work with any number of children.
- Rules-as-instances gives us things C++ rules-as-types can't. We can name rules dynamically for tracing. We can build rules in loops (a generated grammar from a config file, say). We can hold references to rules in collections. We can write tests that construct ad-hoc grammars inline. The C++ version can't do any of this without macro abuse.

The cost is that grammar typos become runtime errors instead of compile errors. If you misspell a rule reference, C++ tells you at compile time (`undefined type NameValueRul`). C# tells you the first time the containing code runs, or worst case the first time you parse. The compile-time story is worse but after compile time is better.

## The Design of Compile

`Compile` is the grammar-finalization pass. Calling `.Compile()` on a rule walks the rule graph using that rule as the root and does four things. The pass is idempotent, returns the same rule for chaining, and is invoked automatically on the first call to `.Parse(...)` if it hasn't already run. Explicit `.Compile()` exists for callers who want grammar-construction errors to surface at program startup rather than on first parse.

The four jobs are bundled together because they share the graph walk and because each of them catches a class of bug that would otherwise explode at parse time:

**Assign symbol ids.** Rules with an explicit id (via `.As(new SymbolId(SymbolRanges.CustomRangeStart + 42))`) get their explicit id first, so explicit ids never shift. Rules named with a string (via `.As("name")` or `.As(nameof(X))`) get an id by hashing the name into the custom range. If the hash lands on a slot that's already in use, the id linear-probes from the hash slot upward until it finds an empty slot. Anonymous rules get ids based on their position in the graph and probe the same way. Because the rule graph is frozen after `Compile` returns, every probe resolution is deterministic and stable for the life of the program.

**Resolve every `LateBoundRule`.** Mutually recursive grammars use a `LateBoundRule` placeholder that gets a target attached via a separate `.Bind(...)` call. If a grammar forgets to bind one, the bug would normally surface as a `NullReferenceException` deep inside a parse. `Compile` fails fast with a message naming the unbound rule. That's a much better failure mode than a runtime null deref far from the original mistake.

**Freeze the rule graph.** After `Compile` returns, every rule in the graph is sealed. Calling `.As(...)`, `.Flatten(...)`, `.WithError(...)`, or any other modification method on a sealed rule throws `InvalidOperationException`. This makes the "effectively immutable" claim enforced rather than implicit, and it closes a bug where user code could accidentally mutate a shared rule after parsing has started. One boolean flag per rule, one check per mutation method, negligible cost.

**Validate against obvious mistakes.** A handful of cheap sanity checks worth running once rather than discovering at parse time: `LateBoundRule` bound to itself or a trivial cycle, rules whose id somehow ended up unset, and rule-specific invariants. Unreachable rules are *not* flagged because a user might legitimately be building standalone rules to use elsewhere. Explicit `SymbolId` slots are reserved so later named and anonymous rules don't steal them, and two reachable rules given the same explicit `SymbolId` are rejected at compile time. Letting duplicates through would make parse-tree lookups by raw `SymbolId` ambiguous and would cause `Rule.NameOf` to depend on graph-walk order.

Bundling them is a design choice. The alternative was to split each into its own pass (a naming pass, a binding pass, a freeze pass, a validation pass), but they all want the same graph walk and there's no observable ordering dependency between them. One walk is cheaper, simpler, and easier to document.

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

It's the size of an int (4 bytes), fits in a single register, and every comparison is a single integer compare. Every `Symbol` node in a parse tree carries one of these, so keeping them small pays off on big trees.

Notice what the struct does *not* carry: a human name. Names live on the grammar side, not on the id itself, so that two different grammars loaded in the same process don't fight over a global namespace. Name lookup happens through the rule that knows the grammar context, via `rule.NameOf(someId)`.

The id namespace is split into three non-overlapping ranges so different kinds of id never collide:

```
0x000000..0x10FFFF   Rune symbols (id equals the Unicode code point)
0x110000..0x1FFFFF   Built-in expression symbols
0x200000..           Custom symbols from user-named rules
```

Rune symbols live at the bottom because single-rune leaf symbols use the code point as their id, and a rune can be anywhere from 0 to 0x10FFFF. Built-in expression ids live just above the Unicode range so they can't collide with a rune. Custom ids live above both. This is a deviation from the C++ numbering (which starts built-ins at 256 and customs at 16000), chosen because both of those ranges fall inside Unicode and would collide with rune ids once the parser started seeing non-ASCII code points. Trace output prints the symbol name rather than the number, so the C++ reference traces still match textually.

## Why Symbol Is a Class, Not a Struct

`Symbol` is a reference type. Every matched rule allocates one on the GC heap. For a 10,000-char parse against a typical grammar, that's roughly 20,000 Symbol allocations per parse. The obvious optimization is to make `Symbol` a struct so those allocations disappear into the stack or inline into their containing arrays. We considered it and didn't do it.

The appeal is real. Each Symbol is small (roughly 32 bytes of data plus a 24-byte class header), most are leaf character nodes whose lifetime tracks their containing tree, and structs are the right tool for "small, immutable, short-lived data." On paper it looks like free performance.

The cost is API churn plus the quiet loss of identity semantics. Identity is the thing worth sitting with for a moment.

A class Symbol has a unique memory address. `Find(someRule)` returns a specific instance. `==` between two Symbol references asks whether they point at the same heap object. That lets code patterns work that don't work on a value type:

- A `HashSet<Symbol>` used to dedupe a tree walk ("have I visited this node?") distinguishes two instances that happen to carry identical field values. With a struct, structural equality wins, and two nodes with the same Id and same children would collapse into one set entry. "I saw this particular node" silently becomes "I saw a node with these field values."
- A `Dictionary<Symbol, Metadata>` attaching information to specific tree positions keys on instance identity today. As a struct, it would key on field contents, so anything structurally equal collides.
- Debugger views of a tree with shared subtrees show one expandable node for each instance. With a struct, every copy looks like a separate entry even when it represents the same position.

None of those patterns appear in this codebase yet. The Symbol API today is read-only tree traversal plus `Find`-by-rule. So the struct change would be fine for the code that exists. The worry is the code that doesn't exist yet. The first time a future user uses one of those patterns expecting reference semantics, they'd get value semantics and a silent bug.

Beyond identity, a handful of smaller costs:

`Symbol?` as a nullable class is a single reference with a null check. As a nullable struct it's `Nullable<Symbol>`, which is the struct plus a `bool HasValue`. Every API that returns `Symbol?` (ParseResult.Tree, Find, and so on) grows. Callers rewrite their null checks. Mostly cosmetic, but it touches a lot of callers.

Copy-on-read. Every property access, every foreach iteration, every method return copies the struct. For a 32-byte type that's cheap per copy but multiplies through recursive tree walks. The JIT removes some copies, not all.

Boxing at interface edges. Generic collections and span-based APIs keep the struct on the stack, but any path that upcasts to `IEnumerable` (non-generic) or passes through `object` boxes the struct back onto the heap. Users who assume "struct means no heap" get surprised.

If Symbol allocation ever shows up as a real bottleneck in a profile, the right answer is probably a parse-scoped object pool rather than a struct conversion. Keep the class, recycle instances between parses, preserve identity semantics, preserve the API. A struct refactor would still be available later, but pooling captures most of the win without the churn.

The deeper question is framing: are parse-tree nodes "data without identity" (struct territory, like `Point` or `DateTime`), or "nodes in a graph with distinct identity" (class territory, like DOM nodes or Roslyn's `SyntaxNode`)? Symbol sits on the boundary. The patterns people naturally want to apply to parse trees (dedup sets, annotation dictionaries, reference comparisons in debuggers and tooling) favor the class framing. So we paid the allocation cost and kept identity.

## Rule Is Extensible

`Rule` is an abstract class, and user code can derive from it to add matching logic the built-in composites don't cover. The contract a subclass has to satisfy:

- Implement the matching method to either consume input and return a `Symbol` subtree (success) or return null and roll back its lexer transaction (failure). Never consume input on failure.
- Use the lexer's transactional API (`BeginTransaction`, `Commit`, `Rollback` or `Dispose`) so backtracking by outer rules works correctly.
- Emit trace output in the same format as built-in rules when `ParseOptions.TraceSink` is set, so grammar-wide traces remain readable.
- Participate in `Compile`: declare yourself named via `.As(...)` if you want an id, declare flatten policy if it matters for tree shape, seal against modification after `Compile` returns.

The full contract including method signatures and the lexer API is covered below in "Tokens and Leaves" and "How a Rule's Match Method Looks".

For grammars that compose existing leaves (which is most grammars) you never need to derive. The built-in composites cover the usual ways rules are combined: run these rules in order, try these alternatives, repeat this rule, or check ahead without consuming input. The built-in leaves cover the character-class cases. User-defined rules matter when you're adding behavior the composites can't express, for example a rule that consumes until a specific UTF-16 offset, a grammar-context-aware matcher that queries external state, or a custom character-boundary detector.

## Why One Lexer, Tokenizing By StringInfo Text Element

The lexer hands the parser one .NET `StringInfo` text element per `Read()`. On modern .NET that's a UAX #29 extended grapheme cluster. On legacy runtimes it's the older Microsoft segmentation, with the gaps documented in [UnicodeGotchas.md](UnicodeGotchas.md#pre-net-5-token-segmentation). Either way, the unit is "what a user perceives as one character." The guitar emoji 🎸 is one character. The family emoji 👨‍👩‍👧‍👦 is one character. The waving hand with a skin-tone modifier 👋🏽 is one character. Grammars that operate on user-typed text want that to be the unit they match.

An earlier design exposed two lexers (one rune-level, one grapheme-level) and let the caller pick via a `ParseOptions` field. We dropped that. Where the two diverged on real input the rune-level behavior was almost always the buggy one: a grammar consuming "one rune" from 👨‍👩‍👧‍👦 matched the first 👨 and left the other six runes (three ZWJs and three people emoji) dangling for subsequent rules to trip over. The grammar author rarely meant that. Picking the grapheme-level unit by default was already the right answer for almost every grammar. Making it the only answer removed a configuration knob whose other position silently produced wrong parses.

The escape hatch for grammars that genuinely need to look inside one token (walk combining marks individually, validate each rune of a grapheme, parse a multi-rune cluster shape) is `WithinToken(innerRule)`. The outer parse reads one token, and the inner rule walks the runes of that one token via a sub-lexer that's bounded to the token's range. `Identifier()` uses this internally to handle Devanagari and Thai conjunct letters. Rune-level access stays available, just for the parts of the grammar that actually need it.

`TokenSet` carries multi-rune entries alongside its rune ranges, so character classes like `TokenSet.Letters | TokenSet.Runes(USFlagGrapheme)` mean what they read: "any single letter rune, or the US flag." `OneOf` and `NoneOf` consult both halves on each token, which is the other half of why a single grapheme-aware lexer covers the cases the dual-lexer design was working around.

## Tokens and Leaves

The rule-vs-lexer interface is narrow: a rule calls `Read()` on the lexer, gets back a `Token`, and compares it against something. Everything in the rule API reduces to that pattern.

### The `Token` Shape

```csharp
public readonly ref struct Token
{
    public string             Source      { get; }   // original input string
    public int                Offset      { get; }   // UTF-16 offset where the token starts
    public int                Length      { get; }   // UTF-16 code-unit length
    public bool               IsEof       { get; }
    public ReadOnlySpan<char> Chars       { get; }   // Source.AsSpan(Offset, Length)
    public ReadOnlyMemory<char> Memory    { get; }   // heap-safe view for Symbols
    public int                RuneValue   { get; }   // one-rune token value, or -1
}
```

`Token` is a `ref struct` so it can carry a `Span<char>` into the original input without allocating. Each token is the section of input that the lexer consumed to produce it. Leaf `Symbol`s store `Memory`, the heap-safe view over that same source text, so parsing still avoids substring copies.

Most tokens are still one rune (ASCII, composed-form Latin, CJK, most punctuation are all one rune = one token), but emoji sequences, regional-indicator flags, skin-tone modified emoji, Devanagari conjuncts, and decomposed-form combinations produce multi-rune tokens when the runtime's `StringInfo` groups them as one text element.

The token stores only the UTF-16 offset and length. The token index isn't carried on every token. `ParseResult` and `Symbol.SourceRange` derive it from the char index only when a caller asks. Rules that need to ask "is this token exactly one rune?" read `Token.RuneValue`, which returns the code point for a one-rune token and `-1` for EOF or multi-rune tokens.

### Comparing Tokens: The Four Leaves

Every built-in rule that looks at token content reduces to one of four operations.

**`Token('=')`, `Token(Rune r)`, `Token(string token)`.** Matches one `StringInfo` text element, specified at rule-construction time. The `string` overload requires exactly one text element and is validated at construction with `StringInfo.GetNextTextElement`. The `char`, `Rune`, and `int` overloads are convenience wrappers that build a one-element string. At match time the rule reads one lexer token and compares its `Chars` span with the expected string. `Token("👋🏽")` matches the waving-hand-with-skin-tone token as a single unit; the lexer already grouped its two runes together so the comparison is a single span equal-check.

**`OneOf(TokenSet set)` and `NoneOf(TokenSet set)`.** These are the character-class tests. A `TokenSet` holds two kinds of members: rune intervals (the common case, what `TokenSet.Letters` and `TokenSet.Range(...)` produce) and explicit multi-rune tokens (added by passing a multi-rune grapheme to `TokenSet.Runes("...")`). Both rules ask the same question of each token, just with the answer flipped:

- `OneOf(set)` succeeds when the lexer's next token is in the set.
- `NoneOf(set)` succeeds when it isn't.

For a single-rune token the test is "is the rune in any of the set's intervals?", which is a binary search over a small sorted list. For a multi-rune token the test is "is this exact grapheme in the set's multi-rune array?", which only fires when the set was given multi-rune content; rune-only sets short-circuit the multi-rune check entirely. The asymmetry that matters in practice: a multi-rune token like 👋🏽 fed to `OneOf(TokenSet.Letters)` doesn't match (Letters is rune-only and 👋🏽 isn't a single rune), but the same token fed to `NoneOf(TokenSet.Runes("\""))` does match (the closing-quote set has no multi-rune entries, so a multi-rune token can't be in it). This is what makes `OneOrMore(NoneOf(stopSet))` sweep up emoji correctly in the pass-through-text recipe, and what makes `OneOf(TokenSet.Letters | TokenSet.Runes(USFlagGrapheme))` accept letters and the US flag without needing a `Or`.

**`Literal(string s)`.** Generalizes `Token` to any non-empty string. It keeps the expected string and uses a lockstep loop: read a lexer token, compare it with the corresponding range of the expected text, and advance by `token.Length`. A literal containing a multi-rune grapheme compares that grapheme as one token, the same way `Token` does. The only difference is that a literal can hold a sequence of multiple tokens, where `Token` requires exactly one. `Literal("👨‍👩‍👧‍👦 and friends")` reads one family-emoji token and then twelve more tokens for the trailing text.

**`AnyToken()`.** Matches any single token regardless of content, as long as the lexer isn't at EOF. This is the "match one token, whatever it is" leaf.

### TokenSet: The Set Primitive

`OneOf` and `NoneOf` take a `TokenSet`, a set of Unicode scalar values with the standard set operations via operators. Keeping the set type separate from the rule types means character-class expressions compose the way set expressions do in ordinary code instead of having to wrap every union inside an `Or(...)`.

```csharp
public readonly struct TokenSet
{
    // Unicode-category built-ins, backed by the runtime's Unicode data
    public static readonly TokenSet Letters;
    public static readonly TokenSet Digits;
    public static readonly TokenSet HexDigits;
    public static readonly TokenSet InlineWhitespace; // intra-line whitespace only
    public static readonly TokenSet LineTerminators;  // LF, VT, FF, CR, NEL, LS, PS
    public static readonly TokenSet Identifier;

    // ASCII-only variants faster than TokenSet.Letters
    public static class Ascii
    {
        public static readonly TokenSet Letters          = Range('A','Z') | Range('a','z');
        public static readonly TokenSet Digits           = Range('0','9');
        public static readonly TokenSet HexDigits        = Digits | Range('a','f') | Range('A','F');
        public static readonly TokenSet InlineWhitespace = Runes(" \t");
        public static readonly TokenSet AnyWhitespace    = InlineWhitespace | Single('\r') | Single('\n') | Graphemes("\r\n");
        public static readonly TokenSet Identifier       = Letters | Digits | Runes("_");
    }

    public static TokenSet Single(char c);
    public static TokenSet Single(Rune r);
    public static TokenSet Range(char low, char high);
    public static TokenSet Range(Rune low, Rune high);
    public static TokenSet Runes(string text);                  // one element per Unicode scalar
    public static TokenSet Graphemes(params string[] clusters);      // one element per grapheme cluster
    public static TokenSet Category(UnicodeCategory c);

    public static TokenSet operator |(TokenSet a, TokenSet b);   // union
    public static TokenSet operator &(TokenSet a, TokenSet b);   // intersection
    public static TokenSet operator -(TokenSet a, TokenSet b);   // difference

    public bool Contains(Rune r);
    public bool Contains(char c);
}
```

The three operator rationales:

**`|` (union)** is the workhorse. `TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_-")` composes identifier characters by piecewise addition. Every grammar uses it.

**`&` (intersection)** narrows one semantic set by another. It shines when one operand is a big Unicode-tracking class like `Letters` and the other is a script or script-block restriction:

```csharp
// Cyrillic letters only: letters AND in the Cyrillic block.
// The composition stays correct as Unicode adds new Cyrillic letters.
TokenSet.Letters & TokenSet.Range(new Rune(0x0400), new Rune(0x04FF))

// Hex-digit-like ASCII letters (a–f, A–F, without the 0–9).
TokenSet.Ascii.Letters & TokenSet.Ascii.HexDigits
```

**`-` (difference)** expresses "this class minus those elements" without hand-enumerating the result:

```csharp
// Letters except vowels. No pre-built class. You build it by subtracting.
TokenSet.Ascii.Letters - TokenSet.Runes("aeiouAEIOU")

// Identifier chars except underscore, for a language where '_' is reserved.
TokenSet.Ascii.Identifier - TokenSet.Runes("_")
```

`a - b` keeps `a`'s multi-rune grapheme members (CRLF, a skin-toned emoji) that `b` doesn't contain, so subtracting a rune from a set leaves its clusters alone. For "everything except these categories," subtract from the full scalar universe:

```csharp
// Any printable non-whitespace character: all runes minus the
// categories you don't want.
TokenSet.Universe - (TokenSet.InlineWhitespace | TokenSet.LineTerminators | TokenSet.Category(UnicodeCategory.Control))
```

`TokenSet.Universe` is the surrogate-free scalar universe, so subtracting from it gives the same "everything except" set that the rule-level `NoneOf(X)` would match, but as a class you can keep composing with `|`, `&`, and `-`.

Intersection and difference are niche compared to union. Most grammars use `|` dozens of times and never touch the others. They earn their spot because they're cheap (sorted-range intersection and difference are single passes), and because when an author does need set difference, hand-enumerating the ranges goes stale the moment Unicode adds a new letter to the base class.

`TokenSet.Letters` and its siblings cover Unicode scalar values by category: `Letters` matches single-rune letters like `é`, `漢`, `Ω`, and `ж`. Grammars that specifically want ASCII-only can use `TokenSet.Ascii.Letters` to say so explicitly. A programming-language keyword parser wants ASCII keywords so a stray `café` doesn't parse as a keyword. A text-processing grammar often wants the full Unicode set, and for scripts whose visible letters are multi-rune graphemes it should combine those sets with `WithinToken(...)` or use `Identifier()`.

`Contains(Rune)` is the predicate every `OneOf` / `NoneOf` match resolves to, exposed as public so user-defined rules can reuse the same predicate without going through the rule wrapper.

Internally a `TokenSet` is a sorted list of rune ranges. Union, intersection, and difference are all linear in the number of ranges, which is small for typical grammars (letters and digits are a handful of ranges each). Construction-time evaluation folds compound expressions into a single range list, so `Letters | Digits | Runes("_")` is one flat structure by the time a `OneOf` rule sees it.

### The Non-Content Leaves

A few more rule types exist but don't touch token content directly:

- `Peek(rule)` and `Not(rule)` run their inner rule without committing the transaction. Whatever the inner rule would do with tokens, `Peek` and `Not` inherit from that behavior. No special handling at the token level.
- `And(...)`, `Or(...)`, `OneOrMore(...)`, `ZeroOrMore(...)`, `Optional(...)` are composites. They never inspect tokens themselves. They just sequence or alternate other rules.
- `Eof()` matches iff the lexer is at the end of input. Doesn't read a token.

Everything else (flatten policies, error messages, named symbols) is metadata on the resulting `Symbol` tree, not comparison logic.

### How a Rule's Match Method Looks

Concretely, the four comparison leaves are all short:

```csharp
// Token(string text): _expected stores exactly one StringInfo text element.
// One lexer Read returns one token whose Chars span covers the whole element.
using var tx = lexer.BeginTransaction();
var actual = lexer.Read();
if (actual.IsEof) return null;
if (actual.Length != _expected.Length) return null;
if (!actual.Chars.SequenceEqual(_expected.AsSpan())) return null;
tx.Commit();
return makeSymbolFrom(actual);

// OneOf(TokenSet set)
using var tx = lexer.BeginTransaction();
var token = lexer.Read();
if (token.IsEof) return null;
// Single-rune tokens probe the rune intervals; multi-rune tokens probe
// the multi-rune array if the set has any. Rune-only sets short-circuit
// the multi-rune check.
if (!set.ContainsToken(token.Chars)) return null;
tx.Commit();
return makeSymbolFrom(token);

// Literal(string s): _expected is any non-empty string
using var tx = lexer.BeginTransaction();
int literalConsumed = 0;
while (literalConsumed < _expected.Length)
{
    var actual = lexer.Read();
    if (actual.IsEof) return null;
    if (literalConsumed + actual.Length > _expected.Length) return null;
    if (!actual.Chars.SequenceEqual(_expected.AsSpan(literalConsumed, actual.Length))) return null;
    literalConsumed += actual.Length;
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

Each is a handful of lines. The common shape (start a transaction, read a token, test, commit on success or fall out returning `null` on failure) is the scaffolding user-defined rules inherit when they derive from `Rule` (see the "Rule Is Extensible" section). The `using` on `tx` rolls the lexer back automatically on any path that doesn't call `Commit()`, including exceptions.

### Matching Multi-Rune Tokens

A multi-rune token like 👨‍👩‍👧‍👦 arrives from the lexer as a single token whose `Chars` span covers the whole sequence (eleven UTF-16 chars, seven runes). The ways a grammar can match it:

- **`Token("👨‍👩‍👧‍👦")`** matches one token by exact content. Construction-time validation rejects arguments that aren't exactly one text element, so `Token("ab")` throws at grammar-build time instead of failing silently at parse time.
- **`Literal("👨‍👩‍👧‍👦 and friends")`** matches a sequence of tokens by exact content. Same lockstep comparison as `Token`. The difference is that `Literal` accepts any length.
- **`AnyToken()`** matches any token including multi-rune ones. Useful when the grammar is streaming text through as opaque content ("an identifier is any non-delimiter character").
- **`OneOf(TokenSet.Runes("👋🏽🇺🇸"))`** matches the listed multi-rune tokens via the set's multi-rune array. Combine with rune ranges to express "letters or these flags": `TokenSet.Letters | TokenSet.Runes(USFlagGrapheme)`.
- **`NoneOf(someSet)`** matches multi-rune tokens that aren't in the set's multi-rune array. For a rune-only set, every multi-rune token passes by definition (no multi-rune entry can be in a rune-only set). This is the mechanism behind the pass-through-text recipe.

What you *can't* do:

- **Express "any grapheme cluster except these" as a TokenSet.** `set - exclusions` (set difference) subtracts members and keeps the clusters of `set` that `exclusions` doesn't contain, but there's no set that means "every cluster except X": the grapheme universe is unbounded (any rune sequence respecting UAX #29 boundaries is a cluster), so "everything but 👋🏽" has no finite explicit representation. For "any token that isn't one of these," use the rule-level `NoneOf(stopSet)` or `ScanUntil(stopSet)`, which test non-membership per token instead of enumerating a set.
- **Test "is this token a letter?" with `OneOf(TokenSet.Letters)`** when the token is multi-rune. `TokenSet.Letters` is built from rune intervals only, so any multi-rune token falls outside it. If you want "any identifier character, including combining marks as part of a letter sequence," use `Identifier()`. For custom shapes, `WithinToken(...)` is the escape hatch: it reads exactly one outer token, then runs your child rule over the runes inside that token. The child must consume the whole token. On success, the outer parse advances by one token and, when preserved, exposes one leaf for the whole token rather than separate leaves for the base letter and marks.

## Greedy Repetition, No Repetition Backtracking

PEG parsers backtrack on alternatives (`Or` tries each branch in order until one succeeds, rolls back between attempts), but they DON'T backtrack inside repetition. `OneOrMore`, `ZeroOrMore`, and `Optional` are greedy by construction: they grab as many matches as they can get and never give any back. This is inherited from the C++ library and it's a defining property of PEG, not a design choice unique to this port.

The practical consequence is the most common trip-up when moving from regex to PEG. Consider:

```csharp
var rule = And(OneOrMore(OneOf(TokenSet.Letters)), Token('a'));
var result = rule.Parse("aaa");
```

A regex engine with greedy backtracking would:

1. `[a-z]+` greedily grabs `"aaa"`.
2. Then try to match the trailing `a` against EOF, fail.
3. Back off the repetition to `"aa"`, try again, succeed on the trailing `a`.

A PEG engine DOESN'T do step 3. Once `OneOrMore` matched `"aaa"`, those matches are committed. The outer `And` then tries `Token('a')` at EOF, fails, and the whole parse fails. Our `BetweenInclusiveRule` (which `OneOrMore`, `ZeroOrMore`, and `Optional` all factory through) preserves this: the loop inside its `TryParse` commits each successful inner match as it goes, and the loop just stops when the inner fails on the next attempt. No rewind.

This looks like a cost, and sometimes it's. Grammars that worked in regex need to be restructured, usually with `Not(...)` lookahead to stop repetition one step short, or by splitting the repeated rule into a less-greedy form. The benefit's unambiguity: given a grammar and an input, PEG returns exactly one parse (or a fail), and the parse is whichever answer the ordered choices and greedy matches produced. Regex engines without this property have decades of scars from ambiguous patterns and catastrophic backtracking (ReDoS).

Two corollaries of "no repetition backtracking" that show up in the implementation:

**Each successful inner match is committed.** Inside `BetweenInclusiveRule.TryParse`, the inner `TryParse` opens its own transaction and commits on success. Once the first inner succeeds, the outer rule's own transaction stays uncommitted only until the final result is decided. Every matched-so-far position is locked in.

**Zero-width inner matches would loop forever.** `OneOrMore(Optional(X))` has an inner that always "succeeds" without consuming input. Without a guard, the greedy loop would match Optional(X) infinitely. `BetweenInclusiveRule` carries an `if (lexer.Position == before) break;` check that stops the loop when a match didn't advance, so all three derived factories inherit the protection. The C++ version has the same guard for the same reason.

So, the full execution model is: ordered-choice backtracking between alternatives, greedy non-backtracking within repetition, and transaction-based rollback ties the two together. The catastrophic-backtracking patterns discussed in the next section aren't about greed failing to back off. They're about ordered choice retrying at overlapping cursor positions when multiple alternatives interact badly.

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

If you genuinely want prefix parsing, set `ParseOptions.AllowTrailingInput = true`. With the flag on, `Parse` succeeds as soon as the root rule matches and leaves whatever the rule didn't consume in the input. That covers the cases where strict end-to-end matching is the wrong contract: matching one record at the front of a longer stream, testing a sub-rule against an input longer than the rule was meant to consume, or recognising a command at the start of a line and handing the rest off to another parser. A failure inside the rule still reports its own position the same way; the flag only relaxes the post-rule "must have reached EOF" check.

The flag is opt-in for the same reason described above: silently accepting trailing input is the kind of "your grammar accepted something it shouldn't have" mistake the default is there to catch, so the library makes you ask for it explicitly.

## Where Errors Get Positioned

When a parse fails, the `ParseResult` carries an `ErrorCharIndex` that tells the caller where the problem was. The question is: where exactly? Every rule has some choice about what to report, and without a shared principle the answer drifts rule by rule.

The library commits to one rule:

> Every leaf rule records its failure at the start of the offending input, the position of the character or token it couldn't match. A composite rule anchors any `.WithError` it carries at the deepest input position its subtree reached, and otherwise reports nothing of its own. Failures are ranked depth-first. See `docs/ErrorArchitecture.md` for the full model.

Concretely this means `input[result.ErrorCharIndex]` gives the actual character that didn't match, not the character after it. If the index equals `input.Length`, that's a genuine end-of-input case: the grammar wanted more and there wasn't any. The index never falls outside `[0, input.Length]`.

Walk through the smallest case to see why this matters. `Token('a').Parse("x")`:

1. GraphemeRule opens a transaction. `transaction.StartPosition` is 0.
2. Reads 'x'. Lexer position advances to 1.
3. 'x' doesn't equal 'a'. GraphemeRule records its failure at `transaction.StartPosition` (0), not at the current lexer position (1).
4. Transaction rolls back, lexer returns to position 0.
5. `result.ErrorCharIndex` is 0. `result.ErrorMessage` is `"Parse failed at offset 0: unexpected 'x'."`.

A naive post-read implementation would record at 1 instead of 0, which equals `input.Length` for this one-char input, which makes `BuildErrorMessage` take the "Unexpected end of input" branch even though the input isn't empty. That's the kind of off-by-one that accumulates over a library's lifetime until every error message is slightly off and nobody remembers why. Picking a principle early and applying it uniformly keeps the error messages accurate.

### Three Cases

**Single-token leaves** (`GraphemeRule` single-rune, `OneOfRule`, `EofRule`) open a transaction, read one token, and fail if the token doesn't match. The pre-read position is exactly `transaction.StartPosition`, which the `Lexer.Transaction` struct exposes for this purpose. No extra locals, no separate state: the transaction already knows.

**Multi-token leaves** (`LiteralRule`) read a sequence of tokens and fail when any one of them mismatches. The position is the start of the *specific* failing token, not the start of the whole attempt. A `Literal("abc")` that matches "ab" and fails on the third token reports offset 2, not offset 0. These rules track a per-iteration `tokenStart` local inside the loop.

**Composite rules** (`AndRule`, `OrRule`, `BetweenInclusiveRule`, `AliasRule`, `ScanUntilRule`) don't report a position shallower than their subtree reached. A composite that carries a `.WithError` anchors that message at the deepest input position any rule in its subtree reached, so the named failure sits level with the deepest child failure and the equal-depth message-claim can let it win. A composite with no `.WithError` adds nothing of its own: its descendants already recorded at the right spots. The lookahead rules `Peek` and `Not` are the exception. They run their inner as a throwaway probe, discard the probe's failures, and record at their own start.

### Deepest Failure Wins

Multiple rules can call `RecordFailure` during one parse. The lexer keeps the deepest position seen and the message attached to it. The resolution rules:

1. **Forced overrides everything.** A failure made with `.WithError("...", forced: true)` beats every non-forced failure at any depth. It's the rare opt-in for an author who wants one summary message regardless of where the parse stalled.
2. **Otherwise, strictly deeper beats shallower.** Among non-forced failures, a rule that records at offset 7 beats one that recorded at offset 3, named and mechanical alike. Depth is the primary key: a `.WithError` doesn't pull the reported position off the parser's furthest progress.
3. **Equal depth, named beats mechanical, first writer wins.** When failures tie on depth, a named failure (one carrying a `.WithError` message) claims the message slot over a bare mechanical failure, and the first writer of a given kind keeps the slot. This is what lets a composite like `OneOrMore(...).WithError("Expected a setting name")` contribute its friendly message at the depth its subtree reached.

Depth ranking first is deliberate: a `.WithError` chooses the words and tips an exact-depth tie, but it never drags the caret to a shallower spot, which would point the user at input the parser was still consuming fine. See `docs/ErrorArchitecture.md` for the worked examples and the rationale.

### A Known Heuristic Limitation

The deepest-failure-wins model works well in practice but has one characteristic quirk: `Optional(...)` rules whose inner gets deeper than the surrounding required path can "capture" the reported position into a branch that was truly optional.

Concrete case: `And(Optional(Literal("abc")), Token('x')).Parse("abdy")`. The Optional's inner reads "ab" and fails on 'd' vs 'c' at offset 2. Optional catches the failure and succeeds with empty children, so the overall grammar proceeds. Then Token('x') tries at offset 0, fails on 'a'. Deepest-failure-wins picks offset 2 (the abandoned Optional attempt), not offset 0 (the actually-required rule's failure). The user sees "unexpected 'd'" pointing at content inside what was supposedly optional.

This isn't a bug. It's a property of the heuristic. Because depth ranks first, a `.WithError` can change the *words* at offset 2 but can't move the caret back to offset 0: that's the same coupling the depth-primary model was designed to remove. An author who genuinely wants the report collapsed to one summary message can mark a `.WithError` `forced: true`, which overrides depth. The full fix for the position itself would require a different error model (something like tracking a separate "required-path failure" position alongside the deepest raw position), and no existing PEG library we've surveyed does that. The smallest core lives with the quirk and documents it.

### LSP Position Semantics

`ParseResult.ErrorLine` and `ErrorColumn` follow the Language Server Protocol's position conventions. LSP is the JSON-RPC protocol that VS Code, Neovim, JetBrains IDEs, and essentially every modern editor use to talk to language tooling. If a grammar author is going to forward a parse error into an editor, they're almost certainly going to do it through LSP, either directly or through a layer that speaks LSP. Matching LSP end-to-end means the integration is `new Diagnostic { Range = new Range(errorLine, errorColumn, ...) }` with no arithmetic in between. Pick a different convention and every caller writes the same `-1` shim forever.

Three specific rules fall out:

**Lines are 0-based.** The first line of the file is line 0, not line 1. This is the part that surprises people reading an error in isolation (editors display 1-based to humans), but the point of these fields is machine-to-machine handoff, not direct human display. If the caller wants 1-based for a user-facing error message they add one at the edge, exactly where the translation belongs.

**Columns count UTF-16 code units, not runes or tokens.** LSP 3.17 made the encoding negotiable via `PositionEncodingKind`, but UTF-16 is still the default every implementation ships with. Counting in UTF-16 means that a token like 👋🏽 (two runes, four UTF-16 chars, one visible character) contributes four to the column count, same as what VS Code's internal buffer sees. The token count lives on its own property (`ErrorTokenIndex`) for callers whose mental model works in tokens.

**`\r\n` is one line break, attributed to the `\n`.** LSP treats the pair atomically: a position can't fall between the `\r` and the `\n`. The lexer tokenizes `\r\n` as a single text element per UAX #29, so an `ErrorCharIndex` from a normal parse never lands inside the pair. The line/column conversion handles the boundary case anyway: a char index that somehow does land on the `\n` half (a caller computing positions by hand, for instance) gets reported on the prior line so the column stays non-negative.

The equivalent C++ library returns a character offset and nothing else, leaving line/column computation to the caller. The C# port bundles them because the caller almost always wants them anyway, and bundling lets us pick the convention once and document it once.

## Tracing Design

The parser emits trace output that shows every rule attempt, its outcome (success/failure), and indentation that mirrors the transaction depth. Enable by setting `ParseOptions.TraceSink` to a `TextWriter`. Leave it null and tracing is off. See [InductorParserReference.md](InductorParserReference.md#tracing) for usage examples.

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

`ParseOptions` exposes three orthogonal budgets plus an optional external cancellation signal: `Timeout`, `RuleCountLimit`, `MaxDepth`, and `ParseCancellation`. They answer different questions and a caller that sets all of them gets whichever trips first.

`Timeout` is what interactive callers want: "don't make the user wait more than a second." `RuleCountLimit` is what tests and security gates want: "this parse shouldn't exceed 10 million rule invocations, and I want the same answer on every machine." A test that only sets `Timeout` will be flaky on slow CI agents and will let pathological input through on fast ones. A production service that only sets `RuleCountLimit` won't protect interactive users from a parser that took the full budget but took it slowly.

`MaxDepth` is a separate concern. It doesn't help with exponential backtracking, it helps with stack overflow on deeply nested but well-formed input. A JSON document nested 10,000 levels deep won't time out and won't exhaust the rule-count limit, but it will blow the call stack before any of those triggers. `MaxDepth` catches it before the stack overflow crashes the whole process.

The default settings are `RuleCountLimit = 10_000_000`, `MaxDepth = 1000`, and `Timeout = TimeSpan.Zero` (disabled). The two hardware-independent limits are on by default because they protect naive callers from catastrophic-backtracking and stack-overflow attacks without being flaky or hardware-dependent. `Timeout` stays opt-in because it's inherently flaky (same input takes different time on different hardware) and would cause unpredictable test failures as a default. `.NET`'s `Regex` shipped for over a decade without any of these defaults and produced a long parade of ReDoS vulnerabilities in real-world applications. A PEG engine is in the same failure class and shouldn't repeat that history.

### Why CancellationTokenSource.CancelAfter Isn't Enough

The obvious .NET answer to "abort after N seconds" is `new CancellationTokenSource(TimeSpan.FromSeconds(N))` plus a `token.ThrowIfCancellationRequested()` check in the parse loop. That works on desktop. It doesn't work on WebGL, and that's the constraint that shapes this design.

`CancellationTokenSource.CancelAfter` schedules the cancellation through `System.Threading.Timer`, which needs a timer thread to fire the callback. WebGL has no timer thread. The callback can only run when control returns to the browser event loop, and a tight synchronous parse loop never yields. You can pass a cancellation token with a ten-second deadline, the parser can run for an hour, and the token will never fire because the scheduler it depends on is suspended. The parser therefore exposes `ParseCancellation`, a tiny manually-canceled signal that callers can bridge to an existing `CancellationToken` when they have one, but it doesn't rely on `CancelAfter` for timeouts.

The parser polls deadlines from inside its own loop, using a clock it reads synchronously. Portable to every platform including WebGL.

### Unwinding a Tripped Budget: Throw Once, Catch at the Boundary

Once a budget trips, the parse has to unwind cleanly from deep inside possibly-nested transactions. The mechanism is a single internal throw:

- The periodic budget check (every *N* rule invocations, not every one) throws `ParseBudgetExceeded` when it trips.
- The exception unwinds through whatever stack of rules is currently active. Each frame has a `using var tx = lexer.BeginTransaction()`, which rolls back on any non-commit exit including an in-flight exception, so the lexer state is restored frame by frame on the way up at no additional cost.
- `Parse()` catches the exception at the top and converts it to a failed `ParseResult` with the budget-exceeded reason.

The throw is cold by construction. It fires once per pathological parse, not per rule invocation, so the IL2CPP exception performance cost is irrelevant. The only IL2CPP constraint that does apply is "no exception filters" (`catch ... when (...)`), which this design doesn't need anyway.

This is a change from an earlier draft that used a sticky abort flag on every `EnterRule` to avoid throwing. The flag-check approach works, but it adds a field to every parse state, a branch to every rule invocation, and a two-step "check flag then null-return" pattern in every rule. The throw-at-the-boundary approach leans on the `using`-based rollback scaffolding that already exists, so the rule-side code stays identical to the normal match-failure path.

### Why the Work Budget Counts Rule Invocations, Not Character Reads

Catastrophic backtracking is characterized by revisiting the same cursor position many times, which shows up as lots of rule invocations at the same cursor. A budget that counts character reads would underweight this: a pathological backtrack reads the same characters repeatedly, and each is still just one read.

Counting rule invocations gives us a metric that responds directly to the thing we're trying to defend against. A 10-million-invocation budget lets well-formed parses through (a 1 MB file runs through low millions of invocations on a typical grammar) and cleanly catches exponential blow-ups, which produce tens of billions of invocations on tiny inputs.

## Future Ideas

### Cut Operator

A grammar-level `Cut()` rule is the PEG community's standard tool for preventing catastrophic backtracking by construction rather than by runtime limit. Once the parser passes a cut, it isn't allowed to backtrack past that point. If a subsequent rule fails, the failure is hard and propagates up instead of triggering a retry of an earlier alternative.

```csharp
// Conceptual sketch of the API if we added it
public static readonly Rule FunctionDecl =
    And(
        Literal("function"),
        Cut(),                              // past here, no backtracking
        Identifier,
        Token('('),
        /* ... */
    );
```

Complementary to the timeout budgets: timeouts catch the cases you didn't anticipate, cuts prevent the cases you did. Worth adding later, not in the first pass.

### Packrat Memoization

The "correct" fix for catastrophic backtracking in the worst case. A packrat parser memoizes every rule result at every cursor position, guaranteeing linear time complexity in the length of the input. Any PEG can be converted to a packrat parser mechanically without changing the grammar.

The cost is memory. The memoization table is `O(input_length × rule_count)`, roughly a gigabyte for a 1 MB file and a 100-rule grammar. WebGL's 2 GB memory ceiling means we can't enable this by default: a caller parsing a large file on mobile WebGL would run the browser out of memory. Packrat would have to be an opt-in parse option (`ParseOptions.Memoize = true`) for callers who know their grammar benefits from it.

Packrat also doesn't help with left-recursive grammars (which standard PEG doesn't support anyway) and adds its own overhead for grammars that don't need it. So it isn't a universal win, just a big hammer for the cases where it applies.

## Things That Got Better

Six places where the C# version is strictly nicer, not just different.

No required class scaffolding. The C++ version makes a grammar a type: every rule is a class, grammar composition is template instantiation. The C# port makes a grammar a set of values, which means you can build one inline as local variables, pass rules around, compose rules across files, and write tests that construct ad-hoc grammars without any class boilerplate.

Unicode-aware defaults. The lexer calls `StringInfo.GetNextTextElement` at the current UTF-16 offset and emits that returned span as one token; on modern .NET this tracks extended grapheme clusters, while older `StringInfo` implementations have the caveats covered in the Unicode docs. `TokenSet` stores Unicode scalar ranges and accepts multi-rune grapheme entries alongside them, and composition normalization runs by default. Grammars start from a much better place for emoji, combining marks, CJK, and non-Latin scripts. The C++ version is ASCII-only in practice.

Runtime defenses against catastrophic backtracking. The C++ version has no protection: a pathological input and a grammar with ambiguous alternatives can combine to spin for minutes. The C# port has three orthogonal budgets plus a `ParseCancellation` signal that can bridge from `CancellationToken`, with protective defaults on the two deterministic ones, and `ParseResult.Outcome` tells the caller which one tripped.

Variadic rules without the `Args` wrapper. `And(r1, r2, r3, r4)` beats `AndExpression<Args<r1, r2, r3, r4>>`.

Composable character classes. `TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_-")` is worth the whole port by itself.

Proper error objects. `ParseResult.ErrorLine` and `ErrorColumn` are computed on demand from the position. In the C++ version you get a message and a character offset and you have to compute line/column yourself. The same conversion is also available on every parse-tree node via `Symbol.SourceRange`, so semantic errors ("duplicate section on line 7", "value out of range at char 42") report positions in the same units the parse error does.

## Things That Got Worse

Two places where we lose something real.

Compile-time errors become runtime errors. If you misspell a rule reference in C++, the compiler catches it. In C# it becomes a `NullReferenceException` the first time you hit that branch of the grammar. Writing a unit test that parses a known-good input against every grammar is the real fix, and that's fine.

Rule graphs can have order-of-initialization traps. `static readonly Rule A = And(B, C);` requires `B` and `C` to exist. If they're in the same file this is fine because C# initializes static fields top-to-bottom in declaration order. If they're in different files and there's a cycle, you can get a default-initialized `Rule` reference (`null`) where you expected a real rule. Mutually recursive grammars (expression grammars, for example) have to use a `LateBoundRule` forward-reference trick:

```csharp
// Expression grammar with self-reference
static readonly LateBoundRule Expression = new LateBoundRule();

static readonly Rule Term =
    Or(
        Integer(),
        And(Token('('), Expression, Token(')'))    // refers to the not-yet-built expression
    );

static readonly Rule Sum =
    And(Term, ZeroOrMore(And(Token('+'), Term)));

static readonly Rule _init = Expression.Bind(Sum);   // wire up the late binding
```

`LateBoundRule` is a rule that forwards to a target set later. It's the C# answer to C++'s ability to reference a class name before it's fully defined. The `_init` field is a static initializer trick to run the `.Bind(...)` call at type init time.

A `LateBoundRule` is fully transparent. It produces no parse-tree node of its own (the Symbol that flows up carries the target's id), and it takes its `FlattenType` from whatever it's bound to rather than having a policy of its own. So a grammar reads the same whether you reference a late-bound rule or splice its target in directly: `Alias(expression)` and `And(x, expression, y)` behave exactly as if `expression`'s target were written in place. The one structural error this can't paper over is a `.Bind(...)` chain that loops through `LateBoundRule`s without ever reaching a concrete rule. That grammar can never match anything, so `Compile` rejects it instead of letting a parse spin.
