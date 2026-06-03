# Inductor Parser Reference

This document is a reference document and is more technical and detailed than the primers (see below). It goes into detail about every aspect of the parser and discusses how to write grammars with the library. It shows what the API looks like, gives working examples end to end, and points at the other docs when you want depth on a specific topic.

In this library a *rule* is a C# object. You build rules by calling factory functions like `And(...)`, `Or(...)`, `Token('=')`, you compose them into a grammar, and you call `.Parse(input)` on the root rule to get a tree back.

The library implements a [Parsing Expression Grammar (PEG)](https://en.wikipedia.org/wiki/Parsing_expression_grammar) parser. In PEG terms, `And` is sequence (match a, then b, then c), `Or` is ordered choice (try each alternative in order, the first match wins, so grammars are unambiguous by construction), `OneOrMore` and `ZeroOrMore` are greedy repetition, and `Peek` and `Not` are the lookahead predicates. Matching is recursive-descent with backtracking on failure, but greedy repetition never gives input back once it has matched, so grammars are written with that in mind.

Primers:

- [Primer 1: Getting Started](primer1.md): build a grammar that consumes everything up to a stop sequence, parse some input, look at the tree.
- [Primer 2: Walking the Tree](primer2.md): a tiny INI-style config grammar with typed values, a tree walker, and Unicode-aware error positions.
- [Primer 3: Unicode in the Inductor Parser](Primer3.md): how the parser handles Unicode normalization, error positions, ill-formed input, and unexpected characters.
- [Primer 4: Security-Related Concerns](Primer4.md): parser defenses against pathological input (ReDoS, recursion limits) and Unicode-based attacks (Trojan Source, lookalikes, homoglyphs, invisible characters).
- [Tutorial: Peek](tutorial-peek.md): a password-validation regex translated into the parser, using `Peek` for non-consuming lookahead.
- [Recipes](Recipes.md): small patterns that come up often when writing grammars. Each recipe shows the natural-but-wrong translation and walks through what actually works.

Related docs:

- [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md): design and architecture of the library. Why it's shaped the way it's, what tradeoffs were made.
- [Terminology.md](Terminology.md): library-specific meaning of terms used throughout these docs (leaf, composite, syntax tree, debug tree, AST, FlattenType writing conventions).
- [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md): lexer internals (code units, runes, graphemes, normalization).
- [UnicodeGotchas.md](UnicodeGotchas.md): caller-side Unicode concerns the lexer can't fix (case-insensitive matching, BOMs, homoglyphs, etc.).

## Hello World Example

This is the same example as [GettingStarted.md](https://github.com/EricZinda/InductorParser/blob/master/GettingStarted.md) from the C++ parser: parse `setting = 5;` into a name and a value.

A rule is an instance. You build one by calling factory functions and you call `.Parse(...)` on it. 

```csharp
using static InductorParser.Rules;

var settingName = Identifier();

var settingValue = Or(
    Float().Flatten(FlattenType.Flatten),
    Integer().Flatten(FlattenType.Flatten),
    Identifier()
).Preserve();

var document = And(
    settingName,
    Optional(AnyWhitespace()),
    Token('='),
    Optional(AnyWhitespace()),
    settingValue,
    Optional(AnyWhitespace()),
    Token(';')
).Preserve();

var result = document.Parse("setting = 5;");
if (result.Success)
{
    var name  = result.Tree.Find(settingName).ToString();
    var value = result.Tree.Find(settingValue).ToString();
    Console.WriteLine($"{name} = {value}");   // setting = 5
}
else
{
    Console.WriteLine(result.ErrorMessage);
}
```

The `.Preserve()` on the root keeps the whole document under a single wrapper Symbol, which is what `result.Tree` returns. Without it, `And`'s default `FlattenType.Flatten` lifts every child up to the top level and `result.Tree` is null because there's more than one top-level Symbol; in that case use `result.Symbols` to walk the bubbled-up children directly.

Four variables hold rules, one call to `.Parse(...)` returns a tree, and `result.Tree.Find(someRule)` locates the node that rule produced. Renaming any of the local variables via an IDE refactor updates every reference including the lookups, because `Find` matches on the rule reference itself, not on any separate name or id.

Compare that side by side with the C++ version from `GettingStarted.md` and you can see they line up rule by rule. Every C++ template instantiation becomes a C# factory call, and the trailing template parameters (flatten policy, symbol id, error message) become fluent method calls on the returned `Rule`. The `MySymbolID` class and the stack of `.As(MySymbolIds.X)` calls from the C++ tutorial are gone: lookups use the rule reference you already have in scope.

The `using static InductorParser.Rules;` at the top is what lets us write `And(...)` and `Or(...)` and `Token('=')` without a class qualifier. It is the C# moral equivalent of `using namespace FXPlat;` in the C++ version. Grammars that want a cleaner look use this import. Grammars that want to be explicit can write `Rules.And(...)`.

Two things happen automatically in this example but are worth knowing about for when you want more control. First, the rule graph is finalized (validated, frozen, ids stamped on whatever named rules exist) on the first call to `.Parse(...)`. You can force this earlier by calling `.Compile()` on the root rule explicitly, which is useful when you want grammar-construction errors to surface at program startup rather than on first use. Second, nothing in this example has a user-supplied name: the rules are anonymous. Parsing works fine, `Find(someRule)` works fine (it matches on rule identity), but trace output and error messages will fall back to class-derived labels like `And` or `OneOrMore`, which tell you the rule's shape but not what it represents in your grammar. Adding explicit `.As(nameof(...))` calls for better names is covered in the next section for grammars that want them.

## Naming Rules

Most rules don't need a name. `Find(someRule)` matches on the rule object itself, so as long as you have a reference to the rule you want to locate, you can find its nodes in the tree.

One caveat: `Find(rule)` only hits rules with `FlattenType.Preserve`. Rules with the default `FlattenType.Flatten` (every `And`, `Or`, `OneOrMore`, `ZeroOrMore`, `Optional`, `BetweenInclusive`) or `FlattenType.Delete` (every `Token`, `Literal`, `Eof`, `Not`, `Peek`) have their wrapper removed from `ParseResult.Tree`, so Find can't locate them. The fix is one of two things: name the rule with `.As(...)` (which automatically flips an unset policy to `Preserve` for exactly this reason), or set `FlattenType.Preserve` directly on an unnamed rule with the `.Preserve()` shortcut. For debugging, `ParseOptions.PreserveAllSymbols` turns the lift-up off globally so the tree matches the grammar one-to-one.

Sometimes names do matter though: trace output, error messages, serialization. Trace output prints rule names to show which rule was tried at each position. Error messages quote the "deepest rule" that failed. Without an explicit name, these fall back to a class-derived label like `And`, `OneOrMore`, or `BetweenInclusive[1..3]`, which tells you the rule's shape but not what it represents in your grammar.

Here are different ways you can name rules:

**`.As(nameof(X))` on a rule held in a field.** This is the standard form for grammars organized as a class. The C# compiler checks the `nameof` against the field name, so a rename via IDE refactor updates the string automatically:

```csharp
public static readonly Rule SettingName =
    Identifier().As(nameof(SettingName));
```

This works for fields because a field name is in scope inside its own initializer. For local variables it isn't (`var x = ....As(nameof(x))` is a compile error: "Cannot use local variable before it is declared"), so for locals either use a string literal or split the assignment:

```csharp
var settingName = Identifier();
settingName = settingName.As(nameof(settingName));
```

The rule's id is derived deterministically from the string, and the name carries through into trace output. 

**`.As("some label")` on inline rules that don't live in a variable.** If you want to label a chunk of rule tree that isn't pulled out into its own variable, pass a string literal:

```csharp
And(
    Identifier().As("operatorName"),
    Optional(AnyWhitespace()),
    Token(':'),
    /* ... */
)
```

This is just the first form with a literal string instead of a `nameof`. The tradeoff is that a string literal doesn't update when you rename anything nearby, but there's usually nothing *to* rename for an inline rule.

**`.As(new SymbolId(SymbolRanges.CustomRangeStart + 42))` for explicit numeric ids.** If a grammar needs stable numeric ids across versions for serialization or cross-version debugging, pass a `SymbolId` directly instead of a string. The number stays fixed no matter how you refactor the code. The name lives on the rule, not on the id, so chain a separate `.As("Thing")` call to attach a debug name (the two `.As` overloads write different fields, so they compose).

Naming a rule with `.As(...)` also flips its `FlattenType` to `Preserve` if the policy is still the rule's class default. Identification implies findability: a named rule is one the caller wants to locate later with `Tree.Find` or `result.Find`, and that only works when the rule's Symbol reaches the parse tree. So `Token('!').As("breaking")` quietly upgrades from the default `FlattenType.Delete` to `Preserve`, and `ZeroOrMore(letter).As("word")` upgrades from the default `FlattenType.Flatten` to `Preserve`, without the caller having to chain an explicit `.Preserve()`. If `.Flatten(...)` (or `.Delete()` / `.Flatten()`) was already called with a non-Preserve value, `.As` throws instead of overriding the caller's explicit choice. The reverse direction throws too: setting a non-Preserve policy on a rule that's already been named would silently break `Tree.Find` for that rule, so it fails loudly at grammar-build time. `.Preserve()` (or `.Flatten(FlattenType.Preserve)`) is always safe to chain with `.As` in either order.

### What `Compile` Actually Does

Calling `.Compile()` on a rule walks the rule graph using that rule as the root and does five things. The pass is idempotent, returns the same rule for chaining, and is invoked automatically on the first call to `.Parse(...)` if it hasn't already run. Explicit `.Compile()` exists for callers who want grammar-construction errors to surface at program startup rather than on first parse.

`Compile` has an overload that takes a Unicode normalization form: `Compile(NormalizationForm? normalizeInput)`. The default is `NormalizationForm.FormC`. Pass `null` to opt out of normalization. The form is part of the grammar's identity and is committed at first compile: a subsequent `Compile` with a different form throws `InvalidOperationException`. The chosen form is readable on the compiled rule via the public `NormalizationForm` property.

**Assign symbol ids.** Rules with an explicit id (via `.As(new SymbolId(SymbolRanges.CustomRangeStart + 42))`) get their explicit id first, so explicit ids never shift. Rules named with a string (via `.As("name")` or `.As(nameof(X))`) get an id by hashing the name into the custom range. If the hash lands on a slot that is already in use, the id linear-probes from the hash slot upward until it finds an empty slot. Anonymous rules get ids based on their position in the graph and probe the same way. Because the rule graph is frozen after `Compile` returns, every probe resolution is deterministic and stable for the life of the program.

**Resolve every `LateBoundRule`.** Mutually recursive grammars use a `LateBoundRule` placeholder that gets a target attached via a separate `.Bind(...)` call. If a grammar forgets to bind one, the bug would normally surface as a `NullReferenceException` deep inside a parse. `Compile` fails fast with a message naming the unbound rule:

```
Rule 'Expression' is a LateBoundRule that was never bound. Call
Expression.Bind(...) before running the parser.
```

That is a much better failure mode than a runtime exception.

**Freeze the rule graph.** After `Compile` returns, every rule in the graph is sealed. Calling `.As(...)`, `.Flatten(...)`, `.WithError(...)`, or any other modification method on a sealed rule throws `InvalidOperationException`. This makes the "effectively immutable" claim enforced rather than implicit, and it closes a bug where user code could accidentally mutate a shared rule after parsing has started. One boolean flag per rule, one check per mutation method, negligible cost.

**Validate against obvious mistakes.** A handful of cheap sanity checks worth running once rather than discovering at parse time: `LateBoundRule` bound to itself or a trivial cycle, rules whose id somehow ended up unset, and rule-specific construction invariants. Unreachable rules are *not* flagged because a user might legitimately be building standalone rules to use elsewhere. Explicit `SymbolId` slots are reserved so anonymous and named rules don't steal them, and two reachable rules given the same explicit `SymbolId` are rejected at compile time with an error that names both rules. Allowing duplicates would break parse-tree lookups by raw `SymbolId` and let `Rule.NameOf` return whichever rule the graph walk visited second.

**Validate every literal against the chosen normalization form.** When `Compile` is given a non-null form, every reachable `Token`, `Literal`, and `LiteralIgnoreAsciiCase` rule has its expected text checked against its own normalization in that form. A literal that isn't already in the chosen form would silently never match (the lexer normalizes input before tokenizing, so a rule looking for an unnormalized sequence sees nothing the lexer produces). Compile collects every offending rule and throws one `InvalidOperationException` listing each name, the original literal, and the suggested normalized form. The check is skipped when the form is `null` (the author opted out of normalization).

### SymbolId

```csharp
public readonly struct SymbolId : IEquatable<SymbolId>
{
    public int Value { get; }                       // the only field, 4 bytes

    public bool Equals(SymbolId other) => Value == other.Value;
    public override int GetHashCode()  => Value;
}
```

`SymbolId` is intentionally a single-field struct. It is the size of an int (4 bytes), fits in a single register, and every comparison is a single integer compare. Every `Symbol` node in a parse tree carries one of these, so keeping them small pays off on big trees.

Notice what the struct does *not* carry: a human name. Names live on the grammar side, not on the id itself, so that two different grammars loaded in the same process don't fight over a global namespace. Name lookup happens through the rule that knows the grammar context:

```csharp
public abstract class Rule
{
    public SymbolId Id   { get; }
    public string?  Name { get; }           // this rule's own name if set by .As(...)

    public string? NameOf(SymbolId id);     // any id in this grammar
}
```

`rule.NameOf(someId)` consults two sources in order and returns the first match. For rune-range ids (0..0x10FFFF) it renders the code point directly as a single-rune string (`"A"` or `"漢"`). Otherwise it looks the id up in a per-grammar index built at compile time, which maps every reachable rule's id to the user's `.As(...)` name (if set) or the rule's class-derived name like `"And"`, `"OneOrMore"`, or `"BetweenInclusive[1..3]"`. Returns null if the id isn't in the grammar.

Built-in symbol ids live in a static class and use a numbering space chosen so the three kinds of symbol id never collide:

```
0x000000..0x10FFFF   Rune symbols (id equals the rune)
0x110000..0x1FFFFF   Built-in expression symbols
0x200000..           Custom symbols from user-named rules
```

## Characters and TokenSet

The parser operates on Unicode text, not raw bytes. By default the lexer reads one .NET `StringInfo` text element per step. On modern .NET that means extended grapheme clusters, so `👨‍👩‍👧‍👦` is one token rather than seven scalar values. The full lexer story, including legacy-runtime caveats and how to opt into rune-level lexing instead, lives in [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md). For grammar-authoring purposes, you can ignore the distinction until you hit emoji or combining-mark input, at which point the Unicode doc has the answer.

`TokenSet` is a composable value type for character sets. The full API surface (built-ins, factory methods, and the `|`, `&`, `~` operators) lives in [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md). The grammar-authoring shorthand is that you build a class out of built-ins and factory calls and combine them with `|` for union, `&` for intersection, and `~` for complement.

Grammar code reads like:

```csharp
OneOf(TokenSet.Ascii.Letters)                                   // ASCII letters, explicit
OneOf(TokenSet.Letters)                                         // Unicode letters (café, 名前, Ωmega)
OneOf(TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_-"))   // combined
NoneOf(TokenSet.Single('"'))                                  // anything except a quote
OneOf(TokenSet.Single(new Rune(0x1F3B8)))                       // guitar emoji (above U+FFFF)
OneOf(TokenSet.Range(new Rune(0x0370), new Rune(0x03FF)))       // Greek and Coptic block
```

The default built-ins cover Unicode scalar values by category. `TokenSet.Letters` includes single-rune letters like `é`, `漢`, `Ω`, and `ж` according to the runtime's Unicode category tables. Grammars that specifically want ASCII-only use `TokenSet.Ascii.Letters` to say so explicitly.

`Token(...)` takes a `char` for any character that fits in a C# char literal (code points — the integers Unicode assigns to characters — in the range U+0000..U+FFFF) and a `Rune` for characters above U+FFFF:

```csharp
Token('=')                       // ASCII
Token('♭')                       // U+266D, fits in a char literal
Token('漢')                      // U+6F22, fits in a char literal
Token(new Rune(0x1F3B8))         // U+1F3B8 guitar emoji, above U+FFFF
Token(0x1F3B8)                   // same via int overload
```

### How Rules React to the Lexer

The parser's token is a `StringInfo` text element: one user-perceived character. See [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md) for the mechanics. What that means for the leaves that compare against tokens:

- `Token('=')` matches the `[=]` token. Single-rune tokens compare to a single rune by identity, so ASCII and other characters that fit in a C# char literal work as you would expect.
- `Token("👋🏽")` matches the multi-rune waving-hand-with-skin-tone token as one unit. Construction-time validation rejects arguments that aren't exactly one text element.
- `TokenSet.Letters` matches single-rune letter tokens. For composed-form text (the default after normalization), almost all Latin-style letters are single-rune tokens, so this works as expected. Multi-rune letter tokens (Devanagari conjuncts, decomposed-form sequences with no precomposed equivalent) don't match `TokenSet.Letters` because the token contains more than one rune. Use `Identifier()` or `WithinToken(...)` when you want to validate the runes inside a token.
- `TokenSet.Letters | TokenSet.Graphemes("🇺🇸")` extends a rune set with explicit multi-rune tokens. (`Graphemes`, not `Runes`: the flag is a regional-indicator pair, one grapheme cluster of two runes, and `Runes` throws on a multi-rune cluster.) `OneOf` and `NoneOf` consult both halves on each token, so the US flag matches as one token alongside the rune-only letter ranges.
- `Literal("café")` matches four tokens, one per character in the literal. Composition normalization runs first so `café` typed as `e + U+0301` reaches the lexer as one token per visible character.
- Emoji sequences (👋🏽, 🇺🇸, 👨‍👩‍👧‍👦) match as single tokens on runtimes whose `StringInfo` recognizes those extended grapheme clusters, which is almost always what you want.

This is right for almost every grammar that handles user-supplied text, because "one character" in the user's mental model is usually one user-perceived character. An emoji programming language works naturally on runtimes with modern `StringInfo` segmentation. Identifiers that include combining marks work naturally. Keywords like `function` parse the same way they always did (all ASCII, all single-rune text elements).

When a grammar genuinely needs to look inside one token (walk combining marks individually, validate each rune of a token), wrap the inner rule in `WithinToken(innerRule)`. The outer parse reads one full token; the inner rule walks its runes one at a time.

## Rule Construction Is Fluent

Every rule factory (the `Token`, `Literal`, `And`, `Or`, etc. functions used above) is a static method on the `Rules` class in [src/InductorParser/Rules.cs](../src/InductorParser/Rules.cs), which is what `using static InductorParser.Rules;` brings into scope. Each one returns a `Rule` object. Modifier methods mutate one property in place and return the same rule for chaining:

```csharp
public abstract class Rule
{
    public SymbolId Id   { get; }
    public string?  Name { get; }

    public Rule As(string name);                    // attaches a debug name (id derives from it)
    public Rule As(SymbolId id);                    // stamps an explicit id (for stable numbering)
    public Rule Flatten(FlattenType type);          // sets the flatten policy
    public Rule WithError(string errorMessage);     // sets the static error message

    public Rule Compile();                                       // FormC default
    public Rule Compile(NormalizationForm? normalizeInput);      // explicit form, or null to disable
    public NormalizationForm? NormalizationForm { get; }         // form the grammar was compiled against

    public ParseResult Parse(string input);
    public ParseResult Parse(string input, ParseOptions options);

    public string? NameOf(SymbolId id);
}
```

Chaining is how you get the equivalent of the C++ trailing template args:

```csharp
var settingName = Identifier()
    .As(nameof(settingName))
    .WithError("Expected a setting name");
```

`.As` flips the rule's `FlattenType` to `Preserve` automatically when the rule's policy is still its class default, so chaining `.Flatten(FlattenType.Preserve)` after `.As` is redundant. The reverse pair throws: setting a non-Preserve policy on a rule that's already been named (or naming a rule whose policy was already set to non-Preserve) raises `InvalidOperationException`, since `Tree.Find` would silently return null otherwise.

`.Compile()` walks the rule graph, stamps ids, resolves `LateBoundRule`s, freezes the graph, and returns the same rule for chaining. `.Parse(...)` auto-compiles on first call, so you don't need to call `.Compile()` yourself unless you want grammar-construction errors to surface at startup rather than at first parse. `.Compile()` doesn't assign names: a rule's user-supplied name comes from an explicit `.As(...)` call, and unnamed rules already carry a class-derived trace label like `And` or `OneOrMore` from their constructor that trace output and error messages fall back to.

Rules are mutable up until `Compile` runs and then sealed. `.As(...)`, `.Flatten(...)`, `.WithError(...)` mutate the rule in place and return the same rule for chaining, so `var rule = Identifier(); rule.Flatten(FlattenType.Preserve);` and `var rule = Identifier().Flatten(FlattenType.Preserve);` produce the same end state on the same object. The practical consequence: if you keep a reference to a rule and reuse it in multiple places, calling `.Flatten(...)` on one of those references changes the policy at every other use site too. To get two flatten policies for the same shape, build two separate rule instances. `.As(string)`, `.As(SymbolId)`, and `.WithError(...)` are each set-once against themselves: calling the same overload a second time on the same rule instance throws, since a silent overwrite on a shared rule is almost always a bug (the same numeric core chained under `.As("major")` / `.As("minor")` / `.As("patch")` actually mutates one rule three times, last call wins). The two `.As` overloads write different fields and still compose on a single instance, so `.As(explicitId).As("name")` is fine. Use a factory function that returns a fresh rule each call when you want the same shape under different names, ids, or error messages. After `Compile` returns the rule graph is sealed: calling `.As(...)`, `.Flatten(...)`, or any other mutation method on a sealed rule throws `InvalidOperationException`.

Default values for `Flatten`, error messages, and so on mostly match the C++ defaults from the original source. `InlineWhitespace()` and `AnyWhitespace()` default to `FlattenType.Delete`. `Token('=')` defaults to `FlattenType.Delete`. `And(...)` defaults to `FlattenType.Flatten`. `Integer()` and `Float()` are compositions whose outer rule also defaults to `FlattenType.Flatten`. Either chain `.As(name)` (which upgrades the default to `Preserve`) or call `.Preserve()` directly when you want to find them as parent Symbols. `Parse` applies these types to the tree before returning: `Delete` nodes are dropped, `Preserve` parent Symbols survive, and `Flatten` nodes pass their content up to the parent. For a composite that means lifting its children into the parent's children list and dropping the composite's own Symbol. For a leaf that means keeping the leaf as-is (a leaf has no separate children to lift past it, so it is its own content). `ParseOptions.PreserveAllSymbols` turns the whole pass off and gives you back a grammar-shaped debug tree with every Symbol in place.

### Counted Repetition Over a Rule That Can Match Empty

The repetition rules (`OneOrMore`, `ZeroOrMore`, `Optional`, `AtLeast`, `AtMost`, `Exactly`, `BetweenInclusive`) run their inner in a greedy loop. When the inner can succeed without consuming input (`Optional`, `ZeroOrMore`, `Peek`, `Not`, and similar zero-width rules), one rule covers what the loop does: **only one empty success is counted.**

Each successful inner match counts. The first iteration that succeeds without advancing the lexer is the last: it counts once and the loop breaks. The break is what makes the loop terminate (a nullable inner would otherwise match empty forever at the same position), and the count is what makes `OneOrMore(Optional(X))` succeed on input where no `X` appears:

```csharp
OneOrMore(Optional(OneOf("a"))).Parse("");    // success, matched ""
ZeroOrMore(Optional(OneOf("a"))).Parse("");   // success, matched ""
```

The consequence is that a nullable inner inflates the count by exactly one over the real consuming matches. `AtLeast(N, Optional(a))` therefore requires `N - 1` actual a's, not N:

```csharp
AtLeast(2, Optional(OneOf("a"))).Parse("a");   // success (1 real + 1 empty = 2)
AtLeast(3, Optional(OneOf("a"))).Parse("a");   // fail   (count tops out at 2)
AtLeast(3, Optional(OneOf("a"))).Parse("aa");  // success (2 real + 1 empty = 3)
```

This isn't unique to InductorParser. Every greedy PEG has to settle this one way or another, because counted repetition over a rule that can match empty doesn't terminate under the natural recursive semantics. Ford's PEG paper ([Ford 2004](https://bford.info/pub/lang/peg/), §3.3 "*-loop condition") identifies it as one of the two structural non-termination cases (the other is left recursion). Some implementations refuse to build the grammar at all (Lua's LPeg throws "loop body may accept empty string"). InductorParser instead runs it via the "only one empty success" rule, which is the cheapest terminating choice that still lets a normal `OneOrMore` of a sometimes-empty inner succeed.

If you find yourself writing `OneOrMore(Optional(X))` or `AtLeast(N, Optional(X))`, the `Optional` is almost always a mistake. Drop it: `OneOrMore(X)` and `AtLeast(N, X)` say what you mean and don't depend on the +1 from the empty terminal.

### User-Defined Rules

`Rule` is an abstract class and users can derive from it to add matching logic the built-in composites don't cover. The contract a subclass has to satisfy:

- Implement the matching method to either consume input and return a `Symbol` subtree (success) or return null and roll back its lexer transaction (failure). Never consume input on failure.
- Use the lexer's transactional API (`BeginTransaction`, `Commit`, `Rollback` or `Dispose`) so backtracking by outer rules works correctly.
- Emit trace output in the same format as built-in rules when `ParseOptions.TraceSink` is set, so grammar-wide traces remain readable.
- Participate in `Compile`: declare yourself named via `.As(...)` if you want an id, declare flatten policy if it matters for tree shape, seal against modification after `Compile` returns.

The full contract including method signatures and the lexer API is documented in [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md) under "Tokens and Leaves" and "How a Rule's Match Method Looks". For grammars that compose existing leaves (which is most grammars) you never need to derive. The built-in composites cover the usual ways rules are combined: run these rules in order, try these alternatives, repeat this rule, or check ahead without consuming input. The built-in leaves cover the character-class cases. User-defined rules matter when you are adding behavior the composites can't express, for example a rule that consumes until a specific UTF-16 offset, a grammar-context-aware matcher that queries external state, or a custom character-boundary detector.

## The Parse Result

The C++ version returns `shared_ptr<Symbol>` for success and `nullptr` for failure, and you ask the `Lexer` separately for the error message. The C# port combines these into a single return value:

```csharp
public readonly struct ParseResult
{
    public bool          Success       { get; }   // shorthand for Outcome == Success
    public Symbol?       Tree          { get; }   // null on failure
    public ParseOutcome  Outcome       { get; }   // why the parse ended
    public string        ErrorMessage  { get; }   // empty on success

    // Position of the error. Line/column follow LSP conventions end-to-end:
    // 0-based line, 0-based column in UTF-16 code units, \r\n as one
    // atomic break. See InductorParserDesignDecisions.md "LSP Position Semantics" for
    // why 0-based and why UTF-16. Add 1 at the edge if you want 1-based
    // for a human-facing error message.
    public int  ErrorCharIndex         { get; }   // UTF-16 char index; use for input[...]
    public int  ErrorLine              { get; }   // 0-based line number (LSP)
    public int  ErrorColumn            { get; }   // 0-based column in UTF-16 chars (LSP)

    // For callers that count in tokens (user-perceived characters). Derived lazily.
    public int  ErrorTokenIndex        { get; }

    // The error position bundled into a SourcePosition. Null on success.
    // Use this when you want all four units in one shot (one walk of the
    // input instead of several lazy ones).
    public SourcePosition? ErrorPosition { get; }
}

public enum ParseOutcome
{
    Success,
    GrammarMismatch,       // rules didn't match the input
    Timeout,               // ParseOptions.Timeout elapsed
    RuleCountLimitExceeded,  // ParseOptions.RuleCountLimit exceeded
    DepthLimitExceeded,    // ParseOptions.MaxDepth exceeded
    Canceled               // ParseOptions.Cancellation was canceled
}
```

Putting the error position into the result directly removes an entire class of C++ pitfall where you forgot to ask the lexer for the error before it went out of scope. `ErrorLine` and `ErrorColumn` are computed lazily from `ErrorCharIndex` and the original input string. The char-based trio (`ErrorCharIndex`, `ErrorLine`, `ErrorColumn`) uses the same conventions the Language Server Protocol uses, so a caller forwarding a parse error into an editor through LSP does no arithmetic in between. See [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md) for the full rationale. `ErrorTokenIndex` is there for callers that count in user-perceived characters (a `^^^` underline a human will look at). It is computed lazily from the char index and costs nothing unless used.

`Symbol.SourceRange` uses the same machinery for any node in the parse tree, not just the error point. Each `SourcePosition` (the type returned by `Start` and `End`) carries the same `CharIndex`, `TokenIndex`, `Line`, and `Column` fields, so a tool reporting "duplicate section on line 7" or "value out of range at char 42" reads from the symbol with the same semantics LSP and `string.Substring` already use.

The `Outcome` field distinguishes "the grammar didn't match" from "we ran out of budget." A grammar mismatch means the input is invalid and you should show the user where. A timeout or rule-count-limit exhaustion means the input might be valid but we couldn't decide in the budget we were given, and the caller might want to reject it as suspicious, retry with a looser budget, or show a different error to the user. See the "Catastrophic Backtracking and Timeouts" section below for the mechanics.

## The Symbol Tree

`Symbol` stays close to the C++ version. One ID, a flatten type, and a list of children.

```csharp
public class Symbol
{
    public SymbolId Id { get; }
    public FlattenType FlattenType { get; }
    public IReadOnlyList<Symbol> Children { get; }

    public override string ToString();             // recovers the parsed text
    public void FlattenInto(List<Symbol> result);  // same semantics as C++
    public IReadOnlyList<Symbol> Flatten();        // convenience: returns a fresh list

    public Symbol? Find(Rule rule);                // first match (recursive)
    public Symbol? Find(SymbolId id);              // same, by raw id
    public IEnumerable<Symbol> FindAll(Rule rule);
    public IEnumerable<Symbol> FindAll(SymbolId id);
    public IEnumerable<Symbol> Walk();             // pre-order traversal

    // Span this Symbol covers in the original input. Null when the
    // Symbol has no surviving leaves (an empty composite, or one whose
    // leaves were Delete-flattened away). Same five units as
    // ParseResult's error position (char, rune, grapheme, line, column).
    public SourceRange? SourceRange { get; }
}
```

`Find` and `FindAll` are a small addition. Walking a tree with raw indexing (`tree.children()[0].children()[3]` style, which is how the C++ tutorial does it) is fragile the moment you add an optional element. Search by rule reference is more robust and reads better in compiler code, and it plays nicely with IDE refactors: renaming the rule field updates every `Find(...)` call automatically.

`ToString()` is the same contract as the C++ version: concatenate all descendant character symbols in order. This is how you recover the original input text for any subtree.

### LINQ on the Symbol Tree

Every traversal on `Symbol` is a direct LINQ target because each one is typed as `IReadOnlyList<Symbol>` or `IEnumerable<Symbol>`. The four entry points cover the four things you usually want to do with a Symbol tree:

```csharp
// Direct children (no recursion)
symbol.Children.Where(c => c.Id == someRule.Id)

// Entire subtree, pre-order walk
symbol.Walk().Where(s => s.Id == integerRule.Id)

// All descendants matching a specific rule
symbol.FindAll(settingName).Select(s => s.ToString())

// Flattened tree as a list
symbol.Flatten()
```

Picking between these comes up most often for "a list of named items" grammars (domain labels, JSON members, function parameters, file-path components, ...). A rule shaped like `And(Label, ZeroOrMore(And(Separator, Label)), Eof())` lifts every `Label` up to the wrapping `And` because the inner `And` defaults to `Flatten` and `Separator` / `Eof` default to `Delete`, so `tree.Children` is already the list of Labels. That works as long as you know the FlattenType layout, but it ties the consumer to it: a later grammar change that preserves a new sibling under the wrapper will silently mix the sibling into the list. `symbol.Children.Where(c => c.Is(label))` is the defensive form and reads no worse. When the items can sit anywhere in the subtree instead of only as direct children, use `symbol.FindAll(label)` and the walk recurses for you.

`Symbol` itself deliberately does *not* implement `IEnumerable<Symbol>`. It would be a two-line change to forward to `Children.GetEnumerator()`, and the tradeoff isn't worth it. Iterating a tree node silently means picking one of children, descendants-pre-order, descendants-post-order, siblings, and tokens, and the four other choices then become second-class. `System.Xml.Linq` and Roslyn both refuse to implement `IEnumerable` on their node types for exactly this reason. The parser port takes the same stance.

## A Walkthrough With a Compiler Function

Parsing turns text into a tree. Most callers want to go one step further and turn the tree into their own domain types. The C++ version ships a `Compiler<T>` base class for this. In C# it's usually simpler to write a plain function that takes a rule and an input string and returns your target type:

```csharp
using static InductorParser.Rules;

var settingName  = Identifier().As("settingName");

var settingValue = Or(
    Float().Flatten(FlattenType.Flatten),
    Integer().Flatten(FlattenType.Flatten),
    Identifier()
).As("settingValue");

var document = And(
    Optional(AnyWhitespace()),
    settingName,
    Optional(AnyWhitespace()),
    Token('='),
    Optional(AnyWhitespace()),
    settingValue,
    Optional(AnyWhitespace()),
    Token(';'),
    Optional(AnyWhitespace()),
    Eof()
).As("document").Compile();

static (Setting? result, string? error) CompileSetting(Rule root, Rule name, Rule value, string input)
{
    var parsed = root.Parse(input);
    if (!parsed.Success)
        return (null, $"Line {parsed.ErrorLine}: {parsed.ErrorMessage}");

    var nameText  = parsed.Tree!.Find(name)!.ToString();
    var valueText = parsed.Tree!.Find(value)!.ToString();
    return (new Setting(nameText, valueText), null);
}

public sealed record Setting(string Name, string Value);
```

Usage:

```csharp
var (setting, error) = CompileSetting(document, settingName, settingValue, "difficulty = hard;");

if (setting is not null)
    Console.WriteLine($"{setting.Name} = {setting.Value}");
else
    Console.WriteLine($"Parse failed: {error}");
```

If you have several compilers that share the same scaffolding, or you want a consistent `TryCompile(out TResult, out string error)` contract on a public API, it's worth writing a small reusable base class once and inheriting from it. The library doesn't ship one because the right shape depends on use (return nullable vs out-parameter vs throw, whether to forward `ParseOptions`, and so on). For one-off compilers, the plain function shown above is simpler.

## A Bigger Example: Nested Rules

To show how this scales, here is a mini settings file grammar where a document can have multiple settings and settings can have multiple values:

```csharp
using static InductorParser.Rules;

var key = Identifier(extraStartRunes: TokenSet.Runes("_")).As("key");

// Private helper, not named because it never appears in the final tree
// (its children are flattened directly under `values`). The
// identifier-shaped alternative is built fresh here rather than reusing
// `key` because `.Flatten(...)` mutates the rule it's called on, and
// reusing `key` would also flatten its tree position inside `pair`.
var valueAtom = Or(
    Float().Flatten(FlattenType.Flatten),
    Integer().Flatten(FlattenType.Flatten),
    Identifier(extraStartRunes: TokenSet.Runes("_"))
        .Flatten(FlattenType.Flatten)
);

var values = And(
    valueAtom,
    ZeroOrMore(
        And(
            Optional(AnyWhitespace()),
            Token(','),
            Optional(AnyWhitespace()),
            valueAtom
        )
    )
).As("values");

var pair = And(
    key,
    Optional(AnyWhitespace()),
    Token('='),
    Optional(AnyWhitespace()),
    values,
    Optional(AnyWhitespace()),
    Token(';')
).As("pair");

var document = And(
    Optional(AnyWhitespace()),
    ZeroOrMore(
        And(pair, Optional(AnyWhitespace()))
    ),
    Eof()
).As("document").Compile();
```

The names here are string literals because these are local variables. For grammars organized as a class with rule fields, swap each `.As("key")` for `.As(nameof(Key))` so an IDE rename keeps the names in sync. `.As(...)` on each rule keeps the whole document under one wrapper so `result.Tree.FindAll(pair)` works against it (the names auto-flip the default `FlattenType.Flatten` on the `And` rules to `Preserve`).

Parses input like:

```
colors = red, green, blue;
difficulty = hard;
retries = 3;
```

into a flattened tree shaped like:

```
- [pair]
    - [key] colors
    - [values]
        - red
        - green
        - blue
- [pair]
    - [key] difficulty
    - [values]
        - hard
- [pair]
    - [key] retries
    - [values]
        - 3
```

## Tracing

The C++ version's tracing story (`SetTraceFilter(SystemTraceType::Parsing, TraceDetail::Diagnostic)` before calling `TryParse`) relies on a global trace switch. That doesn't belong in a library that might be loaded into the Unity editor alongside other systems. The C# port takes the sink (along with everything else you can tune per-parse) through a `ParseOptions` argument:

```csharp
var options = new ParseOptions
{
    TraceSink  = Console.Out,
    TraceLevel = TraceLevel.Diagnostic
};

var result = grammar.Parse(input, options);
```

`TraceSink` is `TextWriter?`. Set it to `Console.Out` for the C++ behavior, set it to a file writer to capture a trace, set it to a custom writer to filter or tag lines. Leave it null and tracing is off, with trace-message construction short-circuited by a cheap null check that IL2CPP devirtualizes.

The trace format matches the C++ version exactly, including the indentation-by-transaction-depth trick. We do this on purpose: the C++ test corpus has traced output captured in comments and docs, and matching the format lets us reuse those examples as reference material.

## Thread Safety

The rule for sharing a grammar across threads is short: compile it on one thread, then parse it from as many threads as you like.

A grammar is built and compiled once, on a single thread. After `Compile` returns the whole rule graph is sealed and immutable: ids, `FirstConsumedTokens`, projected literal text, and the normalization form are all fixed, and every modification method throws. Parsing never writes back to the grammar. Each `Parse` call builds its own lexer, its own `Symbol` tree, and its own `ParseResult`, all of which point into the grammar and the input but never mutate the grammar. So once a grammar is compiled, any number of threads can call `Parse` on it at the same time with no locking. That immutability is the whole reason the grammar gets sealed after compile, and "build once, parse many times" is the model the library is designed around.

What is *not* thread-safe is `Compile` itself. Compilation walks the graph and mutates each rule across several passes, and those passes assume nothing else is touching the graph at the same time. Two threads compiling one grammar at once will corrupt it. The catch is that `Parse` auto-compiles on its first call, so if you share an *uncompiled* grammar and the first parses land on several threads at once, they race on that hidden compile. The usual symptom is a confusing `InvalidOperationException` out of compile ("...has already been compiled and can't be reused in another grammar"), and occasionally a wrong parse result. This is by design: compilation is a build step, not a per-parse operation, so the library doesn't pay to make it thread-safe. Doing the compile yourself, once, keeps it off the parse hot path.

So compile before you share. Two ways:

```csharp
// Option A: compile explicitly at startup (single thread), then share.
static readonly Rule Grammar = BuildGrammar().Compile();

// Option B: one warm-up parse on a single thread before going wide.
static readonly Rule Grammar = BuildGrammar();
...
Grammar.Parse("");   // forces the one-time compile here, single-threaded
// now safe to Parse from many threads
```

Option A is the clean one, and it doubles as a startup check that the grammar is well-formed: compile errors (an unbound `LateBoundRule`, a literal that isn't in the chosen normalization form, two rules sharing an explicit id) surface at program start instead of on the first parse.

A few smaller points for the concurrent case:

- The parser keeps a per-input cache of grapheme-cluster boundaries, keyed on the input *string instance*. If two threads parse the same string instance (an interned literal, a cached config string), they share that one cache. It's internally synchronized, so that case is safe too.
- `ParseCancellation` is built for cross-thread use. Hold the instance and call `Cancel()` from any thread, and the running parse picks it up on its next periodic budget check (see the next section).
- `ParseOptions` is a per-call bag of settings. Building a fresh one per parse is simplest. Sharing one across concurrent parses is fine as long as you treat it as read-only once parsing has started, since the parser only reads from it.
- If you set a `TraceSink`, remember a `TextWriter` generally isn't thread-safe. Don't point concurrent traced parses at one unsynchronized writer, or the trace lines will interleave and corrupt each other. Tracing is a debug aid, so this rarely comes up in production.

## Catastrophic Backtracking and Timeouts

PEG parsers can backtrack pathologically on certain grammar/input combinations. The library's defense is a set of budgets on `ParseOptions` that abort the parse if any trips. Two of them default to protective values so naive callers are safe without thinking about it. The third is opt-in.

```csharp
public sealed class ParseOptions
{
    /// Rule-count limit: maximum rule invocations before the parse aborts.
    /// A pure count, not a wall-clock measurement, so the same input and
    /// grammar trip at exactly the same point on every run regardless of
    /// machine speed. Default catches catastrophic backtracking without
    /// clipping legitimate multi-MB parses. Raise it for genuinely huge
    /// inputs. Lower it for tighter control. Set to 0 to disable (not
    /// recommended for untrusted input).
    public long RuleCountLimit { get; set; } = 10_000_000;

    /// Recursion depth limit. Protects against stack overflow on
    /// pathologically nested input like ((((((...)))))). Default is
    /// ~10x deeper than any legitimate grammar produces; real data
    /// almost never nests past ~100 levels. Set to 0 to disable.
    public int MaxDepth { get; set; } = 1000;

    /// Wall-clock limit. Polled from inside the parse loop with Stopwatch.
    /// Portable to every platform including WebGL. TimeSpan.Zero disables
    /// it, which is the default. Interactive callers set this for user-
    /// experience reasons. The rule-count limit above handles the security
    /// case with a count that doesn't vary across machines.
    public TimeSpan Timeout { get; set; } = TimeSpan.Zero;

    /// Optional external cancellation signal. Use ParseCancellation
    /// directly, or bridge from an existing CancellationToken by registering
    /// a callback that calls ParseCancellation.Cancel().
    public ParseCancellation? Cancellation { get; set; }

    /// Tracing sink. Null means tracing off.
    public TextWriter? TraceSink { get; set; }
    public TraceLevel TraceLevel { get; set; }

    /// Debug knob: when true, `Parse` skips the flatten pass that normally
    /// applies each rule's `FlattenType` before returning. `Delete` nodes
    /// stay in the tree, `Flatten` wrappers stay in the tree, and the tree
    /// shape matches the grammar one-to-one. Off by default because most
    /// callers want the flattened syntax tree; flip it on for `PrintTree`
    /// output and for `Find`-queries against wrappers that would otherwise
    /// be lifted away.
    public bool PreserveAllSymbols { get; set; } = false;

    /// When true, `Parse` succeeds as soon as the root rule matches and
    /// leaves whatever it didn't consume in the input. The default
    /// (false) requires every token of the input to be consumed by the
    /// grammar. Turn this on for prefix parsing: matching one record at
    /// the front of a longer stream, testing a sub-rule against an
    /// input the rule was never meant to fully consume, or peeling a
    /// command off the start and handing the rest to another parser. A
    /// rule failing inside the grammar still reports its own position
    /// the same way; the flag only relaxes the post-rule "must have
    /// reached EOF" check. See InductorParserDesignDecisions.md "Parse Requires
    /// Consuming All Input" for the rationale.
    public bool AllowTrailingInput { get; set; } = false;
}
```

**What to actually do as a caller:**

- For most code, the defaults are fine. The rule-count limit protects against pathological input, the depth limit protects against stack overflow, and you don't need to think about either.
- For interactive contexts (editor plugins, real-time feedback), add a `Timeout` so the user never waits too long: `new ParseOptions { Timeout = TimeSpan.FromMilliseconds(200) }`.
- For parsing genuinely huge input (multi-hundred-MB JSON or similar), raise `RuleCountLimit` explicitly. Lower it tighter if you know your grammar should be fast: a small config file shouldn't need a million rule invocations.
- `MaxDepth = 1000` is enough for every real grammar. Only touch it if you have some exotic deeply-nested data format.

When a budget trips, the parse returns a `ParseResult` with `Outcome` set to `Timeout`, `RuleCountLimitExceeded`, `DepthLimitExceeded`, or `Canceled` (not `GrammarMismatch`). Callers who need to distinguish "input was invalid" from "we ran out of budget" switch on `Outcome`.

For the design rationale behind these choices (why three budgets and not one, why `Timeout` is opt-in but the others default on, why cancellation uses `ParseCancellation` instead of relying on `CancellationTokenSource.CancelAfter`, and future ideas like the cut operator and packrat memoization), see [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md).
