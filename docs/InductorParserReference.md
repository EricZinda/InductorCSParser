# Inductor Parser Reference

This document is a reference document and is more technical and detailed than the primers (see below). It goes into detail about every aspect of the parser and discusses how to write grammars with the library. It shows what the API looks like, gives working examples end to end, and points at the other docs when you want depth on a specific topic.

In this library a *rule* is a C# object. You build rules by calling factory functions like `And(...)`, `Or(...)`, `Token('=')`, you compose them into a grammar, and you call `.Parse(input)` on the root rule to get a tree back.

The library implements a [Parsing Expression Grammar (PEG)](https://en.wikipedia.org/wiki/Parsing_expression_grammar) parser. In PEG terms, `And` is sequence (match a, then b, then c), `Or` is ordered choice (try each alternative in order, the first match wins, so grammars can't be ambiguous), `OneOrMore` and `ZeroOrMore` are greedy repetition, and `Peek` and `Not` are the lookahead predicates. Matching is recursive-descent with backtracking on failure, but greedy repetition never gives input back once it has matched.

Primers:

- [Primer: Building a Grammar](primer1.md): build a grammar that consumes everything up to a stop sequence, parse some input, look at the tree.
- [Primer: Parsing and Processing](primer2.md): a tiny INI-style config grammar with typed values, a tree walker, semantic validation, and Unicode-aware source positions.
- [Primer: Parsing Errors](primerFailure.md): what the parser reports when input doesn't match: failure positions, custom `.WithError` messages, and reshaping or localizing the default text.
- [Primer: Unicode in the Inductor Parser](Primer3.md): how the parser handles Unicode normalization, error positions, ill-formed input, and unexpected characters.
- [Primer: Security-Related Concerns](Primer4.md): parser defenses against pathological input (ReDoS, recursion limits) and Unicode-based attacks (Trojan Source, lookalikes, homoglyphs, invisible characters).
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

Three variables hold rules, one call to `.Parse(...)` returns a tree, and `result.Tree.Find(someRule)` locates the node that rule produced. Renaming any of the local variables via an IDE refactor updates every reference including the lookups, because `Find` takes the rule object itself, so there's no separate name string to keep in sync.

Compare that side by side with the C++ version from `GettingStarted.md` and you can see they line up rule by rule. Every C++ template instantiation becomes a C# factory call, and the trailing template parameters (flatten policy, symbol id, error message) become fluent method calls on the returned `Rule`. The `MySymbolID` class and the symbol-id template parameters from the C++ tutorial are gone: lookups use the rule reference you already have in scope.

The `using static InductorParser.Rules;` at the top is what lets us write `And(...)` and `Or(...)` and `Token('=')` without a class qualifier. It is the C# moral equivalent of `using namespace FXPlat;` in the C++ version. Grammars that want a cleaner look use this import. Grammars that want to be explicit can write `Rules.And(...)`.

Two things happen automatically in this example but are worth knowing about for when you want more control. First, the rule graph is finalized (validated, frozen, ids stamped on every reachable rule) on the first call to `.Parse(...)`. You can force this earlier by calling `.Compile()` on the root rule explicitly, which is useful when you want grammar-construction errors to surface at program startup rather than on first use. Second, nothing in this example has a user-supplied name: the rules are anonymous. Parsing works fine, `Find(someRule)` works fine (it matches on the id the rule carries), but trace output and tree printing will fall back to class-derived labels like `And` or `OneOrMore`, which tell you the rule's shape but not what it represents in your grammar. Adding explicit `.As(nameof(...))` calls for better names is covered in the next section for grammars that want them.

## Naming Rules

Most rules don't need a name. `Find(someRule)` takes the rule object you already hold, so as long as you have a reference to the rule you want to locate, you can find its nodes in the tree. (What `Find` compares under the covers is the `SymbolId` stamped on the rule, not the object reference. For named rules and compiled composites the id is unique to the rule, so it behaves like identity. Anonymous single-rune leaves are the exception: `Token('a')` carries the rune's code point as its id, so two anonymous `Token('a')` rules look like the same rule to `Find`, and an anonymous `OneOf(...)` labels each leaf with whichever rune matched rather than with the rule's own id. Naming a leaf with `.As(...)` gives it a unique id and removes both wrinkles.)

One caveat: `Find(rule)` only hits rules with `FlattenType.Preserve`. Rules with the default `FlattenType.Flatten` (every `And`, `Or`, `OneOrMore`, `ZeroOrMore`, `Optional`, `BetweenInclusive`) or `FlattenType.Delete` (every `Token`, `Literal`, `Eof`, `Not`, `Peek`) have their Symbol removed from `ParseResult.Tree`, so Find can't locate them. The fix is one of two things: name the rule with `.As(...)` (which automatically flips an unset policy to `Preserve` for exactly this reason), or set `FlattenType.Preserve` directly on an unnamed rule with the `.Preserve()` shortcut. For debugging, `ParseOptions.PreserveAllSymbols` turns flattening off globally so the tree matches the grammar one-to-one.

Sometimes names do matter though: trace output, tree printing, serialization. Trace output prints rule names to show which rule was tried at each position. `Symbol.DisplayName` labels each node when you print a parse tree. Without an explicit name, these fall back to a class-derived label like `And`, `OneOrMore`, or `BetweenInclusive[1..3]`, which tells you the rule's shape but not what it represents in your grammar. Error messages are a separate mechanism entirely: a failed parse reports the `.WithError("...")` text of the deepest rule that failed, or the generic "Unexpected 'x' at line L, column C." default when there isn't one. Rule names never appear in error messages, so naming a rule doesn't change what a failed parse reports. See [Primer: Parsing Errors](primerFailure.md) for how error reporting works.

Here are different ways you can name rules:

**`.As(nameof(X))` on a rule held in a field.** This is the standard form for grammars organized as a class. The C# compiler checks the `nameof` against the field name, so a rename using an IDE refactor will update the string automatically:

```csharp
public static readonly Rule SettingName =
    Identifier().As(nameof(SettingName));
```

This works for fields, and it works for locals declared with an explicit type too (`Rule settingName = Identifier().As(nameof(settingName));` compiles fine). The `var` form is the one that fails (`var x = ....As(nameof(x))` is a compile error: "Cannot use local variable 'x' before it is declared") because the compiler can't infer the type of `x` from an initializer that mentions `x`. For a `var` local, either write the type out, use a string literal, or split the assignment: <!-- style-lint-ok -->

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

Naming a rule with `.As(...)` also flips its `FlattenType` to `Preserve` if the policy is still the rule's class default. That's because a named rule is one the caller wants to locate later with `Tree.Find` or `result.Find`, and that only works when the rule's Symbol reaches the parse tree. So `Token('!').As("breaking")` quietly upgrades from the default `FlattenType.Delete` to `Preserve`, and `ZeroOrMore(letter).As("word")` upgrades from the default `FlattenType.Flatten` to `Preserve`, without the caller having to chain an explicit `.Preserve()`. If `.Flatten(...)` (or `.Delete()` / `.Flatten()`) was already called with a non-Preserve value, `.As` throws instead of overriding the caller's explicit choice. The reverse direction throws too: setting a non-Preserve policy on a rule that's already been named would silently break `Tree.Find` for that rule, so it throws at grammar-build time. `.Preserve()` (or `.Flatten(FlattenType.Preserve)`) is always safe to chain with `.As` in either order.

### What `Compile` Actually Does

Calling `.Compile()` on a rule walks the rule graph using that rule as the root and does five things. The pass is idempotent, returns the same rule for chaining, and is invoked automatically on the first call to `.Parse(...)` if it hasn't already run. Explicit `.Compile()` exists for callers who want grammar-construction errors to surface at program startup rather than on first parse.

`Compile` has an overload that takes a Unicode normalization form: `Compile(NormalizationForm? normalizeInput)`. The default is `NormalizationForm.FormC`. Pass `null` to opt out of normalization. The form is part of the grammar's identity and is committed at first compile: a subsequent `Compile` with a different form throws `InvalidOperationException`. The chosen form is readable on the compiled rule via the public `NormalizationForm` property.

**1. Assign symbol ids.** Rules with an explicit id (via `.As(new SymbolId(SymbolRanges.CustomRangeStart + 42))`) get their explicit id first, so explicit ids never shift. Rules named with a string (via `.As("name")` or `.As(nameof(X))`) get an id by hashing the name into the custom range. If the hash lands on a slot that is already in use, the id linear-probes from the hash slot upward until it finds an empty slot. Anonymous rules get ids based on their position in the graph and probe the same way. Every input to this pass is deterministic: the name hash is FNV-1a (a fixed byte-level algorithm, not .NET's `string.GetHashCode`, which is randomized per process) and the walk visits children in declaration order. So a given grammar produces the same ids on every run of the program, not just within one run. The catch is that "a given grammar" means the whole graph. Adding, removing, or renaming any rule can shift hashed ids (a new hash collision moves where the probe lands) and anonymous ids (they're positional). Explicit ids are the exception, which is why grammars that serialize parse trees across versions use them.

**2. Resolve every `LateBoundRule`.** Mutually recursive grammars use a `LateBoundRule` placeholder that gets a target attached via a separate `.Bind(...)` call. If a grammar forgets to bind one, the bug would normally surface as a `NullReferenceException` deep inside a parse. `Compile` throws with a message naming the unbound rule:

```
Rule 'Expression' is a LateBoundRule that was never bound. Call
.Bind(targetRule) before calling Parse or Compile.
```

**3. Freeze the rule graph.** After `Compile` returns, every rule in the graph is sealed. Calling `.As(...)`, `.Flatten(...)`, `.WithError(...)`, or any other modification method on a sealed rule throws `InvalidOperationException`. 

**4. Validate against obvious mistakes.** A handful of cheap sanity checks worth running once rather than discovering at parse time: `LateBoundRule` bound to itself or a trivial cycle, and rule-specific construction invariants (each rule class gets a validation hook that `Compile` calls once per rule). Unreachable rules are *not* flagged because a user might legitimately be building standalone rules to use elsewhere. Explicit `SymbolId` slots are reserved so anonymous and named rules don't steal them, and two reachable rules given the same explicit `SymbolId` are rejected at compile time with an error. 

**5. Convert every literal to the chosen normalization form.** When `Compile` is given a non-null form, every reachable rule's expected text is converted to that form in place: `Literal` and `Token` rewrite their stored text, and the `TokenSet`-bearing rules (`OneOf`, `NoneOf`, `ScanWhile`, `ScanUntil`) project their set entries the same way. You can type a literal in whatever form is convenient and it will still match, because the lexer normalizes input to the same form before tokenizing. `Compile` throws only when text can't be represented in the chosen form: an unpaired surrogate that `string.Normalize` rejects, or a one-grapheme slot (a `Token`, a set entry) whose conversion produces more than one grapheme (the ligature `ﬁ` becomes the two-grapheme `fi` under `FormKC`, and a `Token` matches exactly one grapheme). Those failures are collected across the whole grammar and thrown as a single `InvalidOperationException` listing each rule, its original text, and how to fix it. The pass is skipped when the form is `null` (the author opted out of normalization). `LiteralIgnoreAsciiCase` needs no conversion at all: its constructor only accepts ASCII, and ASCII is unchanged by every normalization form.

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

Notice what the struct does *not* carry: a human name. Names live on the grammar side, not on the id itself. Name lookup happens through the rule that knows the grammar context:

```csharp
public abstract class Rule
{
    public SymbolId Id   { get; }
    public string?  Name { get; }           // this rule's own name if set by .As(...)

    public string? NameOf(SymbolId id);     // any id in this grammar
}
```

`rule.NameOf(someId)` consults two sources in order and returns the first match. For rune-range ids (0..0x10FFFF) it renders the code point directly as a single-rune string (`"A"` or `"漢"`). Otherwise it looks the id up in a per-grammar index built lazily on the first `NameOf` call (grammars that never ask never pay for building it), which maps every reachable rule's id to the user's `.As(...)` name (if set) or the rule's class-derived name like `"And"`, `"OneOrMore"`, or `"BetweenInclusive[1..3]"`. Returns null if the id isn't in the grammar.

The id numbering space is split into three ranges so the kinds of symbol id never collide:

```
0x000000..0x10FFFF   Rune symbols (id equals the rune)
0x110000..0x1FFFFF   Reserved for built-in expression symbols (currently unused)
0x200000..           Custom symbols: explicit ids, name-hash ids, and anonymous rule ids
```

## Characters and TokenSet

The parser operates on Unicode text, not raw bytes. By default the lexer reads one .NET `StringInfo` text element per step. On modern .NET that means extended grapheme clusters, so `👨‍👩‍👧‍👦` is one token rather than seven scalar values. The full lexer story, including legacy-runtime caveats and how to opt into rune-level lexing instead, lives in [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md). For grammar-authoring purposes, you can ignore the distinction until you hit emoji or combining-mark input, at which point the Unicode doc has the answer.

`TokenSet` is a composable value type for character sets. The full API surface (built-ins, factory methods, and the `|`, `&`, `-` operators) lives in [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md). The grammar-authoring shorthand is that you build a class out of built-ins and factory calls and combine them with `|` for union, `&` for intersection, and `-` for difference ("a minus b").

Grammar code reads like:

```csharp
OneOf(TokenSet.Ascii.Letters)                                      // ASCII letters, explicit
OneOf(TokenSet.Letters)                                            // Unicode letters (café, 名前, Ωmega)
OneOf(TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_-"))   // combined
NoneOf(TokenSet.Single('"'))                                       // anything except a quote
OneOf(TokenSet.Single(new Rune(0x1F3B8)))                          // guitar emoji (above U+FFFF)
OneOf(TokenSet.Range(new Rune(0x0370), new Rune(0x03FF)))          // Greek and Coptic block
```

The default built-ins cover Unicode scalar values by category. `TokenSet.Letters` includes single-rune letters like `é`, `漢`, `Ω`, and `ж` according to the runtime's Unicode category tables. Grammars that specifically want ASCII-only use `TokenSet.Ascii.Letters` to say so explicitly.

A set can hold two kinds of member: rune ranges (everything shown so far) and explicit multi-rune graphemes. The split exists because no built-in range can list every possible multi-rune letter token (Devanagari conjuncts, decomposed-form sequences with no precomposed equivalent): those are open-ended combinations of runes, not an enumerable range. When your input has specific multi-rune tokens you care about, union them in with `TokenSet.Graphemes(...)`:

```csharp
TokenSet.Letters | TokenSet.Graphemes("🇺🇸")   // letter ranges plus one explicit multi-rune token
```

(`Graphemes`, not `Runes`: the flag is a regional-indicator pair, one grapheme cluster of two runes, and `Runes` throws on a multi-rune cluster.) And when the rule you want is really about the runes *inside* a token ("starts with a letter, the rest are combining marks") rather than a list of whole tokens, skip the set and use `Identifier()` or `WithinToken(...)`.

`Token(...)` takes a `char` for any character that fits in a C# char literal (code points, the integers Unicode assigns to characters, in the range U+0000..U+FFFF), a `Rune` or plain `int` for characters above U+FFFF, and a `string` for a single user-perceived character that Unicode builds from several runes (an emoji with a skin tone, an accented letter typed as base + accent). Whatever the overload, the rule matches exactly one token. The string overload throws at construction if you hand it more than one text element (matching a sequence of tokens is `Literal(...)`'s job):

```csharp
Token('=')                       // ASCII
Token('♭')                       // U+266D, fits in a char literal
Token('漢')                      // U+6F22, fits in a char literal
Token(new Rune(0x1F3B8))         // U+1F3B8 guitar emoji, above U+FFFF
Token(0x1F3B8)                   // same via int overload
Token("👋🏽")                      // multi-rune grapheme, still one token
```

### How Rules React to the Lexer

The parser's token is a `StringInfo` text element: one user-perceived character. See [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md) for the mechanics. What that means for the leaves that compare against tokens:

- `Token('=')` matches the `[=]` token. Single-rune tokens compare to a single rune by identity, so ASCII and other characters that fit in a C# char literal work as you would expect.
- `Token("👋🏽")` matches the multi-rune waving-hand-with-skin-tone token as one unit. Construction-time validation rejects arguments that aren't exactly one text element.
- `OneOf(TokenSet.Letters)` tests the whole token for set membership, not the runes inside it. A rune-range member can only ever match a single-rune token, so a multi-rune token whose runes are all letters (a Devanagari conjunct) isn't in `Letters`. It matches only if the set names it as a `Graphemes(...)` member. 
- `Literal("café")` matches four tokens, one per visible character. That count comes from grapheme clustering, not normalization: `é` typed as `e + U+0301` is one cluster, so one token, normalized or not. What the default normalization adds is that the two spellings agree: Compile converts the literal and Parse converts the input to the same form, so a precomposed `é` in the grammar matches a decomposed one in the input. Under `Compile(null)` the comparison is exact code units, so the literal only matches input typed the same way.
- Emoji sequences (👋🏽, 🇺🇸, 👨‍👩‍👧‍👦) match as single tokens on .NET 5 and later, where `StringInfo` implements the current Unicode segmentation rules. Older runtimes (.NET Framework, old Unity Mono) split a handful of cluster shapes into several tokens. [UnicodeGotchas.md](UnicodeGotchas.md#pre-net-5-token-segmentation) lists which ones and what to do about it.

This is right for almost every grammar that handles user-supplied text, because "one character" in the user's mental model is usually one user-perceived character. An emoji programming language works naturally on .NET 5 and later. Identifiers that include combining marks work naturally. Keywords like `function` parse the same way they always did (all ASCII, all single-rune text elements).

When a grammar genuinely needs to look inside one token (walk combining marks individually, validate each rune of a token), wrap the inner rule in `WithinToken(innerRule)`. The outer parse reads one full token; the inner rule walks its runes one at a time.

### Surrogates

UTF-16, the encoding behind every .NET string, stores characters above U+FFFF as a pair of reserved code units called surrogates: a high surrogate (U+D800..U+DBFF) followed by a low one (U+DC00..U+DFFF). A well-formed pair is nothing special here, it's just how 🎸 is stored, and the lexer reads it as one token. The problem case is a *lone* surrogate, half a pair without its partner. A .NET string can hold one (strings are code-unit arrays, not validated Unicode), but it doesn't represent any character, and text-processing code that assumes well-formed input tends to crash or silently corrupt when one shows up. The parser is built so a lone surrogate can never crash a parse or sneak into a match that didn't ask for it. What happens depends on the normalization mode:

- Under a normalizing `Compile` (the default `FormC`, or any other form), input containing a lone surrogate never reaches the rules at all. `string.Normalize` rejects it, `Parse` catches that, and you get back a failed `ParseResult` with `ParseOutcome.MalformedInput` positioned at the offending code unit. The message comes from `ParseOptions.MalformedInputTemplate`, so a localized app reports it like any other failure. No exception escapes `Parse`.

- Under `Compile(null)`, ill-formed input is allowed through and the lexer tokenizes a lone surrogate as ordinary content, one token like everything else. Rules can't misread it. Its `Token.RuneValue` is -1, which no real character can equal, so `Token('x')` and `Literal("abc")` fail on it correctly. `TokenSet` membership has a dedicated lone-surrogate path, so `OneOf(TokenSet.Letters)` also fails (no built-in set contains surrogate code units) rather than crashing on text that doesn't decode. Rules that accept anything (`AnyToken`, `NoneOf`, a `ScanUntil` body) consume it as opaque text and move on.

Matching one on purpose is opt-in. The built-in sets and the scalar factories can never accidentally contain a surrogate: `Single` and `Runes` reject surrogate arguments, and `Range` splits an interval that straddles the surrogate block, so even `TokenSet.Range(0, 0x10FFFF)` contains no surrogate code units. The two named ways in are `TokenSet.Surrogates` (the whole U+D800..U+DFFF block) and `TokenSet.SurrogateRange(low, high)` (a sub-block). A validator that hunts for encoding damage under `Compile(null)` reads like:

```csharp
var loneSurrogate = OneOf(TokenSet.Surrogates);   // matches only ill-formed code units
```

The set operators carry surrogate members along like any other member, so `TokenSet.Surrogates - TokenSet.SurrogateRange(0xD800, 0xDBFF)` is the low (trailing) half of the block. One wrinkle to know about: in the default grapheme lexing mode, a lone surrogate followed by a combining mark fuses with it into one multi-rune token (the same joining rule normal graphemes follow), and that fused token is no longer a bare surrogate, so a surrogate set won't match it. This wrinkle only exists under `Compile(null)`. A normalizing `Compile` rejects a lone surrogate as `MalformedInput` whether it's bare or fused, since `string.Normalize` refuses the input either way. For the full ill-formed-input story, including the other kinds of "unexpected" Unicode a grammar can meet, see [Primer3.md](Primer3.md).

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
    public Rule WithError(string errorMessage,
                          bool forced = false);     // sets the static error message (forced: true beats deeper failures)

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

Rules are mutable up until `Compile` runs and then sealed. `.As(...)`, `.Flatten(...)`, `.WithError(...)` mutate the rule in place and return the same rule for chaining, so `var rule = Identifier(); rule.Flatten(FlattenType.Preserve);` and `var rule = Identifier().Flatten(FlattenType.Preserve);` produce the same end state on the same object. The practical consequence: if you keep a reference to a rule and reuse it in multiple places, calling `.Flatten(...)` on one of those references changes the policy at every other use site too. 

To reuse one shape under two different settings, wrap it in an alias. An alias is a lightweight rule with its own identity, so it takes any modifier a normal rule does: `digitSequence.AliasedAs("year")` and `digitSequence.AliasedAs("month")` reuse one rule under two names, and the bare `Alias(digitSequence)` form can carry its own flatten policy, id, or error message instead. A factory function that returns a fresh rule each call does the same job with no alias in the middle, which reads better when the reuses outnumber the shared definition.

The modifier methods catch accidental sharing bugs. `.As(string)`, `.As(SymbolId)`, and `.WithError(...)` are each set-once: calling the same one twice on the same instance throws instead of silently overwriting. (Chaining `.As("major")` / `.As("minor")` / `.As("patch")` onto one shared rule would otherwise leave it named "patch" everywhere.) One of each is fine, `.As(explicitId).As("name")` composes. And after `Compile` returns the whole graph is sealed, so every mutation method throws `InvalidOperationException`.

Default values for `Flatten`, error messages, and so on mostly match the C++ defaults from the original source. `Parse` applies these types to the tree before returning: `Delete` nodes are dropped, `Preserve` parent Symbols survive, and `Flatten` nodes pass their content up to the parent. For a composite that means lifting its children into the parent's children list and dropping the composite's own Symbol. For a leaf that means keeping the leaf as-is (a leaf has no separate children to lift past it, so it is its own content). `ParseOptions.PreserveAllSymbols` turns the whole pass off and gives you back a grammar-shaped debug tree with every Symbol in place.

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

If you find yourself writing `OneOrMore(Optional(X))` or `AtLeast(N, Optional(X))`, the `Optional` is almost always a mistake. Dropping it isn't a no-op though, so pick the replacement by what you meant. The behavior-preserving rewrites are `ZeroOrMore(X)` (like `OneOrMore(Optional(X))`, it succeeds even when no X appears) and `AtLeast(N - 1, X)` (the counted empty success was donating the +1). If you actually meant "at least one X" or "at least N X's", write `OneOrMore(X)` or `AtLeast(N, X)` and accept the behavior change: inputs with too few X's now fail, which is usually the bug fix you were after.

### User-Defined Rules

`Rule` is an abstract class and users can derive from it to add matching logic the built-in composites don't cover. What a subclass has to do:

- Override `TryParseRule` to do the matching: consume input and return the rule's `Symbol` on success (or the shared `Symbol.Discarded` when the rule's `FlattenType` means its own node doesn't survive), or return null on failure.
- Don't manage transactions. The base `Rule.TryParse` opens one around every call and commits it only on a non-null return, so a failed match rolls the lexer back automatically, including any input the subclass consumed before failing and any partial Symbols it wrote. For lookahead inside the method, use `lexer.BeginProbe()`.
- Emit trace output in the same format as built-in rules when `ParseOptions.TraceSink` is set, so grammar-wide traces remain readable.
- `Compile` needs nothing special from a custom rule: like every reachable rule, it gets an id and gets sealed automatically. The one policy decision the subclass makes is its default `FlattenType`, passed to the base constructor (that's how `Token` defaults to `Delete` and `And` to `Flatten`). 

The full details, including method signatures and the lexer API, are documented in [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md) under "Tokens and Leaves" and "How a Rule's Match Method Looks". For grammars that compose existing leaves (which is most grammars) you never need to derive. The built-in composites cover the usual ways rules are combined: run these rules in order, try these alternatives, repeat this rule, or check ahead without consuming input. The built-in leaves cover the character-class cases. User-defined rules matter when you are adding behavior the composites can't express, for example a rule that consumes until a specific UTF-16 offset, a grammar-context-aware matcher that queries external state, or a custom character-boundary detector.

## The Parse Result

The C++ version returns `shared_ptr<Symbol>` for success and `nullptr` for failure, and you ask the `Lexer` separately for the error message. The C# port combines these into a single return value:

```csharp
public readonly struct ParseResult
{
    public bool          Success       { get; }   // true only for a real successful parse
    public Symbol?       Tree          { get; }   // the single top-level Symbol, null when there isn't exactly one
    public IReadOnlyList<Symbol> Symbols { get; } // all top-level Symbols (what Tree reads from)
    public ParseOutcome  Outcome       { get; }   // why the parse ended
    public string        ErrorMessage  { get; }   // empty on success

    // Search every top-level Symbol and its subtree. Same semantics as
    // Symbol.Find / Symbol.FindAll, but these work even when Tree is null.
    // SymbolId overloads of both exist too.
    public Symbol? Find(Rule rule);
    public IEnumerable<Symbol> FindAll(Rule rule);
    public string? DisplayNameOf(SymbolId id);     // display label via the grammar, like Symbol.DisplayName

    // Position of the error. Line/column follow LSP conventions end-to-end:
    // 0-based line, 0-based column in UTF-16 code units, \r\n as one
    // atomic break. See InductorParserDesignDecisions.md "LSP Position Semantics" for
    // why 0-based and why UTF-16. Add 1 at the edge if you want 1-based
    // for a human-facing error message.
    public int  ErrorCharIndex         { get; }   // UTF-16 char index; use for input[...]
    public int  ErrorLine              { get; }   // 0-based line number (LSP)
    public int  ErrorCharColumn        { get; }   // 0-based column in UTF-16 chars (LSP)

    // For callers that count in tokens (user-perceived characters). Derived
    // lazily. ErrorTokenColumn is the unit the default error message prints
    // (the {tokenColumnNumber} placeholder is ErrorTokenColumn + 1).
    public int  ErrorTokenIndex        { get; }
    public int  ErrorTokenColumn       { get; }

    // The error position bundled into a SourcePosition. Null on success.
    // Use this when you want all four units in one shot (one walk of the
    // input instead of several lazy ones).
    public SourcePosition? ErrorPosition { get; }
}

public enum ParseOutcome
{
    Success,
    GrammarMismatch,       // rules didn't match the input
    MalformedInput,        // input isn't well-formed UTF-16, the grammar never ran
    Timeout,               // ParseOptions.Timeout elapsed
    RuleCountLimitExceeded,  // ParseOptions.RuleCountLimit exceeded
    DepthLimitExceeded,    // ParseOptions.MaxDepth exceeded
    Canceled               // ParseOptions.Cancellation was canceled
}
```

The char-based trio (`ErrorCharIndex`, `ErrorLine`, `ErrorCharColumn`) uses the same units and zero-based indexing the Language Server Protocol uses, so a caller forwarding a parse error into an editor through LSP does no arithmetic in between. One nuance on `ErrorLine`: the parser counts line breaks by the full UAX #18 set (LF, CRLF, lone CR, plus VT, FF, NEL, LS, PS), a superset of the LF, CRLF, and lone CR an LSP client recognizes, so the line matches an editor on ordinary source and diverges only on input containing the rarer terminators. See [InductorParserDesignDecisions.md](InductorParserDesignDecisions.md) for the full rationale. `ErrorTokenIndex` is there for callers that count in user-perceived characters (a `^^^` underline a human will look at). It is computed lazily from the char index and costs nothing unless used.

`Symbol.SourceRange` uses the same machinery for any node in the parse tree, not just the error point. Each `SourcePosition` (the type returned by `Start` and `End`) carries the same `CharIndex`, `TokenIndex`, `Line`, `CharColumn`, and `TokenColumn` fields (plus 1-based `LineNumber` / `CharColumnNumber` / `TokenColumnNumber` conveniences for human-facing messages), so a tool reporting "duplicate section on line 7" or "value out of range at char 42" reads from the symbol with the same semantics LSP and `string.Substring` already use.

The `Outcome` field distinguishes "the grammar didn't match" from "we ran out of budget." A grammar mismatch means the input is invalid and you should show the user where. A timeout or rule-count-limit exhaustion means the input might be valid but we couldn't decide in the budget we were given, and the caller might want to reject it as suspicious, retry with a looser budget, or show a different error to the user. See the "Catastrophic Backtracking and Timeouts" section below for the mechanics.

## The Symbol Tree

`Symbol` stays close to the C++ version. One ID, a flatten type, and a list of children.

```csharp
public class Symbol
{
    public SymbolId Id { get; }
    public FlattenType FlattenType { get; }
    public IReadOnlyList<Symbol> Children { get; }

    public override string ToString();             // text of the leaves that survived flattening
    public string SourceText { get; }              // verbatim input span this Symbol covers
    // Apply the FlattenType pass by hand: Delete nodes dropped, Flatten
    // nodes' children lifted, Preserve nodes kept. For trees parsed with
    // ParseOptions.PreserveAllSymbols; an already-flattened tree passes
    // through unchanged.
    public void FlattenInto(List<Symbol> result);
    public IReadOnlyList<Symbol> Flatten();        // same, returning a fresh list

    public Symbol? Find(Rule rule);                // first match (recursive)
    public Symbol? Find(SymbolId id);              // same, by raw id
    public IEnumerable<Symbol> FindAll(Rule rule);
    public IEnumerable<Symbol> FindAll(SymbolId id);
    public IEnumerable<Symbol> Walk();             // pre-order traversal

    // Span this Symbol covers in the original input. Composites record
    // the span they consumed even when their leaves were Delete-flattened
    // away, and a zero-width match gets a zero-length range at its anchor,
    // so this is null only for a Symbol no parse produced (hand-built,
    // no source behind it). Same units as ParseResult's error position
    // (char index, token index, line, char column, token column).
    public SourceRange? SourceRange { get; }
}
```

`Find` and `FindAll` are depth-first searches over the subtree: `Find` returns the first Symbol the rule produced (or null if there isn't one), `FindAll` yields every one. They're the alternative to walking the tree with raw child indexing (`tree.Children[0].Children[3]` style), which breaks the moment the grammar adds an optional element. Searching by rule reference survives grammar changes, and it plays nicely with IDE refactors: renaming the rule field updates every `Find(...)` call automatically.

`ToString()` concatenates the leaves that survived flattening, in order. Text that `Delete`'d rules matched is gone (a subtree that matched `"XXX"` through Delete leaves returns `""`), so it is not a verbatim copy of the input. When you want the exact original text for a subtree, read `SourceText`, which spans the input the Symbol consumed no matter what flattening did to the children.

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

The one non-obvious choice is between the first and the third: `Children` looks one level down, `FindAll` searches the whole subtree, so pick by where the items can sit. And whichever you use, filter (`c.Is(label)`) rather than assuming a node's `Children` holds only your items, since a later grammar change can add new siblings to the list.

`Symbol` itself deliberately does *not* implement `IEnumerable<Symbol>`. It would be a two-line change to forward to `Children.GetEnumerator()`, and the tradeoff isn't worth it. Iterating a tree node silently means picking one of children, descendants-pre-order, descendants-post-order, siblings, and tokens, and the four other choices then become second-class. `System.Xml.Linq` and Roslyn both refuse to implement `IEnumerable` on their node types for exactly this reason. 

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
        return (null, parsed.ErrorMessage);

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
// `key`: this spot needs a flattened, unnamed identifier, and `key` is
// named (so Preserve). Calling .Flatten(FlattenType.Flatten) on it
// would throw rather than silently override the .As choice.
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

into a flattened tree shaped like this (each letter under `[values]` is its own leaf Symbol, shown on one line to keep the diagram readable):

```
- [document]
    - [pair]
        - [key] colors
        - [values] r e d g r e e n b l u e
    - [pair]
        - [key] difficulty
        - [values] h a r d
    - [pair]
        - [key] retries
        - [values] 3
```

One surprise in that shape: the leaves under `[values]` are single characters, not words. `Identifier()` flattens to one leaf per character and the commas are `Delete`'d, so `values.ToString()` on the first pair returns `"redgreenblue"`, the three values run together with nothing marking where one ends and the next begins. If a consumer needs each value separately, name `valueAtom` with `.As("value")`: the name flips it to `Preserve`, each value survives as its own node, and `FindAll` returns them one at a time.

## Tracing

The C++ version's tracing story (`SetTraceFilter(SystemTraceType::Parsing, TraceDetail::Diagnostic)` before calling `TryParse`) relies on a global trace switch. That doesn't belong in a library that might be loaded into an application alongside other systems. The C# port takes the sink (along with everything else you can tune per-parse) through a `ParseOptions` argument:

```csharp
var options = new ParseOptions
{
    TraceSink  = Console.Out,
    TraceLevel = TraceLevel.Diagnostic
};

var result = grammar.Parse(input, options);
```

`TraceSink` is `TextWriter?`. Set it to `Console.Out` for the C++ behavior, set it to a file writer to capture a trace, set it to a custom writer to filter or tag lines. Leave it null and tracing is off. The off path costs almost nothing: a cheap null check skips the trace call, and the interpolated-string handler behind the message argument skips the formatting too, so a `$"..."` full of state is never built when no sink is listening.

The output indents by transaction depth (three spaces per level), the same trick the C++ version uses, but the line format is the C# port's own: outcome marker first, then the rule label, then a message.

```
      SUCC | OneOf: found 'f', wanted one of '[A-Z,a-z]'
```

Captured C++ traces look different (`name(Succ) - CharacterSymbol::Parse found 'x'` style, with the outcome tucked after the name), so old C++ trace transcripts are background reading, not expected output for the C# port.

## Thread Safety

The rule for sharing a grammar across threads is short: compile it on one thread, then parse it from as many threads as you like.

A grammar is built and compiled once, on a single thread. After `Compile` returns the whole rule graph is sealed and immutable: ids, the normalized literal text, and the normalization form are all fixed, and every modification method throws. Parsing never writes back to the grammar. Each `Parse` call builds its own lexer, its own `Symbol` tree, and its own `ParseResult`, all of which point into the grammar and the input but never mutate the grammar. So once a grammar is compiled, any number of threads can call `Parse` on it at the same time with no locking. That immutability is the whole reason the grammar gets sealed after compile, and "build once, parse many times" is the model the library is designed around.

What is *not* thread-safe is `Compile` itself. Compilation walks the graph and mutates each rule across several passes, and those passes assume nothing else is touching the graph at the same time. Two threads compiling one grammar at once will corrupt it. The catch is that `Parse` auto-compiles on its first call, so if you share an *uncompiled* grammar and the first parses land on several threads at once, they race on that hidden compile. Doing the compile yourself, once, keeps it safe.

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
    /// Work-unit limit. Each rule invocation counts one unit, and each
    /// iteration of a bulk-scan inner loop (ScanWhile, ScanUntil, and the
    /// other scanning primitives) counts one too, so scan-heavy parses
    /// tick it faster than rule invocations alone would.
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
    public TraceLevel TraceLevel { get; set; } = TraceLevel.Diagnostic;

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
