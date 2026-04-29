# Programming A Grammar

This document is user reference: how to write grammars with the library. It shows what the API looks like, gives working examples end to end, and points at the other docs when you want depth on a specific topic.

In this library a *rule* is a C# object. You build rules by calling factory functions like `AllOf(...)`, `FirstOf(...)`, `Token('=')`, you compose them into a grammar, and you call `.Parse(input)` on the root rule to get a tree back.

Primers (worked examples):

- [Primer 1: Getting Started](primer1.md): build a grammar that consumes everything up to a stop sequence, parse some input, look at the tree.
- [Primer 2: Walking the Tree](primer2.md): a tiny INI-style config grammar with typed values, a tree walker, and Unicode-aware error positions.
- [Tutorial: Peek](tutorial-peek.md): a password-validation regex translated into the parser, using `Peek` for non-consuming lookahead.

Related docs:

- [ProgrammingModel.md](ProgrammingModel.md): design and architecture of the library. Why it is shaped the way it is, what tradeoffs were made.
- [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md): lexer internals (code units, runes, graphemes, normalization, the two lexers).
- [UnicodeGotchas.md](UnicodeGotchas.md): caller-side Unicode concerns the lexer cannot fix (case-insensitive matching, BOMs, homoglyphs, etc.).
- [Recipes.md](Recipes.md): common grammar patterns (pass-through text, class-based grammar organization, a reusable compiler base class).

## Hello World Example

This is the same example as `GettingStarted.md`: parse `setting = 5;` into a name and a value.

A rule is an instance. You build one by calling factory functions and you call `.Parse(...)` on it. No class, no inheritance, no initialization ceremony.

```csharp
using static InductorParser.Rules;

var settingName = Identifier();

var settingValue = FirstOf(
    Float().Flatten(FlattenType.Flatten),
    Integer().Flatten(FlattenType.Flatten),
    Identifier()
);

var document = AllOf(
    settingName,
    OptionalWhitespace(),
    Token('='),
    OptionalWhitespace(),
    settingValue,
    OptionalWhitespace(),
    Token(';')
);

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

Four variables hold rules, one call to `.Parse(...)` returns a tree, and `result.Tree.Find(someRule)` locates the node that rule produced. Renaming any of the local variables via an IDE refactor updates every reference including the lookups, because `Find` matches on the rule reference itself, not on any separate name or id.

Compare that side by side with the C++ version from `GettingStarted.md` and you can see they line up rule by rule. Every C++ template instantiation becomes a C# factory call, and the trailing template parameters (flatten policy, symbol id, error message) become fluent method calls on the returned `Rule`. The `MySymbolID` class and the stack of `.As(MySymbolIds.X)` calls from the C++ tutorial are gone: lookups use the rule reference you already have in scope.

The `using static InductorParser.Rules;` at the top is what lets us write `AllOf(...)` and `FirstOf(...)` and `Token('=')` without a class qualifier. It is the C# moral equivalent of `using namespace FXPlat;` in the C++ version. Grammars that want a cleaner look use this import. Grammars that want to be explicit can write `Rules.AllOf(...)`.

Two things happen automatically in this example but are worth knowing about for when you want more control. First, the rule graph is finalized (validated, frozen, ids stamped on whatever named rules exist) on the first call to `.Parse(...)`. You can force this earlier by calling `.Compile()` on the root rule explicitly, which is useful when you want grammar-construction errors to surface at program startup rather than on first use. Second, nothing in this example has a symbol name: the rules are anonymous. Parsing works fine, `Find(someRule)` works fine (it matches on rule identity), but trace output and error messages will use generated placeholder names instead of human-readable ones. Adding explicit `.As(nameof(...))` calls for better names is covered in the next section for grammars that want them.

## Naming Rules

Most rules do not need a name. `Find(someRule)` matches on the rule object itself, so as long as you have a reference to the rule you want to locate, you can find its nodes in the tree. The hello-world example never calls `.As(...)` and works fine.

One caveat: `Find(rule)` only hits rules with `FlattenType.None`. Rules with the default `FlattenType.Flatten` (every `AllOf`, `FirstOf`, `OneOrMore`, `ZeroOrMore`, `Optional`, `BetweenInclusive`) have their children lifted up into the parent and their own wrapper removed from `ParseResult.Tree`, so Find cannot locate them. If you want to `Find(someRule)` and have it hit, set `FlattenType.None` on the rule to preserve its wrapper. For debugging, `ParseOptions.PreserveAllSymbols` turns the lift-up off globally so the tree matches the grammar one-to-one.

Sometimes names do matter though: trace output, error messages, serialization. Trace output prints rule names to show which rule was tried at each position. Error messages quote the "deepest rule" that failed. Without names, these fall back to generated labels like `<anonymous>` or `rule#47`, which are technically correct but unpleasant to read.

Here are different ways you can name rules:

**`.As(nameof(X))` on a rule you have assigned to a variable or field.** This is the standard form. The C# compiler checks the `nameof` against the symbol in scope, so a rename via IDE refactor updates the string automatically:

```csharp
var settingName = Identifier().As(nameof(settingName));
```

The rule's id is derived deterministically from the string, and the name carries through into trace output. 

**`.As("some label")` on inline rules that do not live in a variable.** If you want to label a chunk of rule tree that is not pulled out into its own variable, pass a string literal:

```csharp
AllOf(
    Identifier().As("operatorName"),
    OptionalWhitespace(),
    Token(':'),
    /* ... */
)
```

This is just the first form with a literal string instead of a `nameof`. The tradeoff is that a string literal does not update when you rename anything nearby, but there is usually nothing *to* rename for an inline rule.

**`.As(SymbolId.Custom(42, "Thing"))` for pinned numeric ids.** If a grammar needs stable numeric ids across versions for serialization or cross-version debugging, pass a `SymbolId` directly instead of a string. The name still carries for debug output. The number stays fixed no matter how you refactor the code.


### What `Compile` Actually Does

Calling `.Compile()` on a rule walks the rule graph using that rule as the root and does four things. The pass is idempotent, returns the same rule for chaining, and is invoked automatically on the first call to `.Parse(...)` if it has not already run. Explicit `.Compile()` exists for callers who want grammar-construction errors to surface at program startup rather than on first parse.

**Assign symbol ids.** Rules with an explicit pin (via `.As(SymbolId.Custom(42, ...))`) get their pinned id first, so pinned ids never shift. Rules named with a string (via `.As("name")` or `.As(nameof(X))`) get an id by hashing the name into the custom range. If the hash lands on a slot that is already in use, the id linear-probes from the hash slot upward until it finds an empty slot. Anonymous rules get ids based on their position in the graph and probe the same way. Because the rule graph is frozen after `Compile` returns, every probe resolution is deterministic and stable for the life of the program.

**Resolve every `LateBoundRule`.** Mutually recursive grammars use a `LateBoundRule` placeholder that gets a target attached via a separate `.Bind(...)` call (see the "Things That Got Worse" section for the pattern). If a grammar forgets to bind one, the bug would normally surface as a `NullReferenceException` deep inside a parse. `Compile` fails fast with a message naming the unbound rule:

```
Rule 'Expression' is a LateBoundRule that was never bound. Call
Expression.Bind(...) before running the parser.
```

That is a much better failure mode than a runtime exception.

**Freeze the rule graph.** After `Compile` returns, every rule in the graph is sealed. Calling `.As(...)`, `.Flatten(...)`, `.WithError(...)`, or any other modification method on a sealed rule throws `InvalidOperationException`. This makes the "effectively immutable" claim enforced rather than implicit, and it closes a bug where user code could accidentally mutate a shared rule after parsing has started. One boolean flag per rule, one check per mutation method, negligible cost.

**Validate against obvious mistakes.** A handful of cheap sanity checks worth running once rather than discovering at parse time: two rules pinned to the same explicit `SymbolId.Custom(...)` number (that is a real bug, unlike hash collisions which just get probed), `LateBoundRule` bound to itself or a trivial cycle, and rules whose id somehow ended up unset. Unreachable rules are *not* flagged because a user might legitimately be building standalone rules to use elsewhere.

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

Notice what the struct does *not* carry: a human name. Names live on the grammar side, not on the id itself, so that two different grammars loaded in the same process do not fight over a global namespace. Name lookup happens through the rule that knows the grammar context:

```csharp
public abstract class Rule
{
    public SymbolId Id   { get; }
    public string?  Name { get; }           // this rule's own name if set by .As(...)

    public string? NameOf(SymbolId id);     // any id in this grammar
}
```

`rule.NameOf(someId)` consults two sources in order and returns the first match. For rune-range ids (0..0x10FFFF) it renders the code point directly as a single-rune string (`"A"` or `"漢"`). Otherwise it looks the id up in a per-grammar index built at compile time, which maps every reachable rule's id to the user's `.As(...)` name (if set) or the rule's class-derived name like `"AllOf"`, `"OneOrMore"`, or `"BetweenInclusive[1..3]"`. Returns null if the id isn't in the grammar.

Built-in symbol ids live in a static class and use a numbering space chosen so the three kinds of symbol id never collide:

```
0x000000..0x10FFFF   Rune symbols (id equals the rune)
0x110000..0x1FFFFF   Built-in expression symbols
0x200000..           Custom symbols from user-named rules
```

## Characters and RuneSet

The parser operates on Unicode characters, not raw bytes. By default the lexer reads one grapheme cluster per step (so `👨‍👩‍👧‍👦` is one token, not seven), which is what you want for grammars that handle user-typed text. The full lexer story, including how to opt into rune-level lexing instead, lives in [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md). For grammar-authoring purposes, you can ignore the distinction until you hit emoji or combining-mark input, at which point the Unicode doc has the answer.

`RuneSet` is a composable value type for character sets. The full API surface (built-ins, factory methods, and the `|`, `&`, `~` operators) lives in [ProgrammingModel.md](ProgrammingModel.md). The grammar-authoring shorthand is that you build a class out of built-ins and factory calls and combine them with `|` for union, `&` for intersection, and `~` for complement.

Grammar code reads like:

```csharp
OneOf(RuneSet.Ascii.Letters)                                   // ASCII letters, explicit
OneOf(RuneSet.Letters)                                         // Unicode letters (café, 名前, Ωmega)
OneOf(RuneSet.Letters | RuneSet.Digits | RuneSet.Runes("_-"))   // combined
NoneOf(RuneSet.Single('"'))                                  // anything except a quote
OneOf(RuneSet.Single(new Rune(0x1F3B8)))                       // guitar emoji (above U+FFFF)
OneOf(RuneSet.Range(new Rune(0x0370), new Rune(0x03FF)))       // Greek and Coptic block
```

The default built-ins cover the full Unicode character set. `RuneSet.Letters` includes `é`, `漢`, `Ω`, `ж`, and every other letter in every script Unicode knows about. Grammars that specifically want ASCII-only use `RuneSet.Ascii.Letters` to say so explicitly.

`Token(...)` takes a `char` for any character that fits in a C# char literal (code points U+0000..U+FFFF) and a `Rune` for characters above U+FFFF:

```csharp
Token('=')                       // ASCII
Token('♭')                       // U+266D, fits in a char literal
Token('漢')                      // U+6F22, fits in a char literal
Token(new Rune(0x1F3B8))         // U+1F3B8 guitar emoji, above U+FFFF
Token(0x1F3B8)                   // same via int overload
```

### How Rules React to the Lexer

The parser's token is a grapheme cluster by default (`GraphemeLexer`). Setting `ParseOptions.InputUnit = InputUnit.Rune` switches to rune-level lexing (`RuneLexer`). See [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md) for the mechanics. The two modes change how specific rules behave:

**Under `GraphemeLexer` (default):**

- `Token('=')` matches the `[=]` grapheme. Single-rune graphemes compare to a single rune by identity, so ASCII and other characters that fit in a C# char literal work as you would expect.
- `RuneSet.Letters` matches single-rune letter graphemes. For composed-form text (the default after normalization), almost all letters are single-rune graphemes, so this works as expected. Multi-rune letter graphemes (Devanagari conjuncts, decomposed-form sequences with no precomposed equivalent) do not match `RuneSet.Letters` because the grapheme contains more than one rune. Use a more permissive rule if you want those, or include Mark categories in your character class.
- `Literal("café")` matches four graphemes, one per character in the literal.
- Emoji sequences (👋🏽, 🇺🇸, 👨‍👩‍👧‍👦) match as single graphemes, which is almost always what you want.

The default is right for almost every grammar that handles user-supplied text, because "one character" in the user's mental model is one grapheme. An emoji programming language works naturally. Identifiers that include combining marks work naturally. Keywords like `function` parse the same way they always did (all ASCII, all single-rune graphemes).

**Under `RuneLexer` (opt-in):**

- `Token('=')` same as `GraphemeLexer`: matches `[=]`.
- `RuneSet.Letters` matches single-rune letters, and in this mode a combining mark is a separate token. A rule that consumed a letter and then encountered a combining mark would stop at the combining mark (it is not a letter).
- `Literal("café")` matches four runes if `café` uses the precomposed `é` (U+00E9), five runes if the `é` is stored as `e` + combining acute.
- Emoji sequences come through as separate runes, so `👋🏽` is two units and `👨‍👩‍👧‍👦` is seven.

Reach for `RuneLexer` when the grammar specifically needs rune-level access: parsing Unicode-category boundaries, walking combining-mark sequences individually, or matching specific rune values regardless of what grapheme they are part of. Most grammars do not need this.

## Rule Construction Is Fluent

Every rule factory returns an effectively-immutable `Rule` object. Modifier methods return a new rule with one property changed:

```csharp
public abstract class Rule
{
    public SymbolId Id   { get; }
    public string?  Name { get; }

    public Rule As(string name);                    // attaches a debug name (id derives from it)
    public Rule As(SymbolId id);                    // stamps an explicit id (for stable numbering)
    public Rule Flatten(FlattenType type);          // sets the flatten policy
    public Rule WithError(string errorMessage);     // sets the static error message

    public Rule Compile();
    public ParseResult Parse(string input);
    public ParseResult Parse(string input, ParseOptions options);

    public string? NameOf(SymbolId id);
}
```

Chaining is how you get the equivalent of the C++ trailing template args:

```csharp
var settingName = Identifier()
    .As(nameof(settingName))
    .Flatten(FlattenType.None)
    .WithError("Expected a setting name");
```

`.Compile()` walks the rule graph, stamps ids, resolves `LateBoundRule`s, freezes the graph, and returns the same rule for chaining. `.Parse(...)` auto-compiles on first call, so you do not need to call `.Compile()` yourself unless you want grammar-construction errors to surface at startup rather than at first parse. `.Compile()` does not auto-name anything on its own: names come from explicit `.As(nameof(X))` calls.

Rules are immutable to the user. `OneOrMore(x).Flatten(FlattenType.None)` does not mutate the underlying `OneOrMore` rule, it returns a new wrapped rule with the flatten policy set. After `Compile` returns, the rule graph is sealed: calling `.As(...)`, `.Flatten(...)`, or any other mutation method on a sealed rule throws `InvalidOperationException`.

Default values for `Flatten`, error messages, and so on match the C++ defaults from the original source. `OptionalWhitespace()` defaults to `FlattenType.Delete`. `Token('=')` defaults to `FlattenType.Delete`. `AllOf(...)` defaults to `FlattenType.Flatten`. `Integer()` defaults to `FlattenType.None`. `Parse` applies these types to the tree before returning: `Delete` nodes are dropped, `Flatten` wrappers have their children lifted into the parent, and `None` wrappers survive. `ParseOptions.PreserveAllSymbols` turns the whole pass off and gives you back a grammar-shaped debug tree with every wrapper in place.

### User-Defined Rules

`Rule` is an abstract class and users can derive from it to add matching logic the built-in composites do not cover. The contract a subclass has to satisfy:

- Implement the matching method to either consume input and return a `Symbol` subtree (success) or return null and roll back its lexer transaction (failure). Never consume input on failure.
- Use the lexer's transactional API (`Begin`, `Commit`, `Rollback`) so backtracking by outer rules works correctly.
- Emit trace output in the same format as built-in rules when `ParseOptions.TraceSink` is set, so grammar-wide traces remain readable.
- Participate in `Compile`: declare yourself named via `.As(...)` if you want an id, declare flatten policy if it matters for tree shape, seal against modification after `Compile` returns.

The full contract including method signatures and the lexer API will be documented alongside the implementation. For grammars that compose existing leaves (which is most grammars) you never need to derive. The built-in composites cover the PEG operators and the built-in leaves cover the character-class cases. User-defined rules matter when you are adding behavior the composites cannot express, for example a rule that consumes until a specific byte-level offset, a grammar-context-aware matcher that queries external state, or a custom character-boundary detector.

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
    // atomic break. See ProgrammingModel.md "LSP Position Semantics" for
    // why 0-based and why UTF-16. Add 1 at the edge if you want 1-based
    // for a human-facing error message.
    public int  ErrorCharIndex         { get; }   // UTF-16 char index; use for input[...]
    public int  ErrorLine              { get; }   // 0-based line number (LSP)
    public int  ErrorColumn            { get; }   // 0-based column in UTF-16 chars (LSP)

    // For callers that measure in other units. Derived lazily.
    public int  ErrorRuneIndex         { get; }
    public int  ErrorGraphemeIndex     { get; }

    // The error position bundled into a SourcePosition. Null on success.
    // Use this when you want all five units in one shot (one walk of the
    // input instead of several lazy ones).
    public SourcePosition? ErrorPosition { get; }
}

public enum ParseOutcome
{
    Success,
    GrammarMismatch,       // rules did not match the input
    Timeout,               // ParseOptions.Timeout elapsed
    RuleCountLimitExceeded,  // ParseOptions.RuleCountLimit exceeded
    DepthLimitExceeded,    // ParseOptions.MaxDepth exceeded
    Canceled               // ParseOptions.CancellationToken fired
}
```

Putting the error position into the result directly removes an entire class of C++ pitfall where you forgot to ask the lexer for the error before it went out of scope. `ErrorLine` and `ErrorColumn` are computed lazily from `ErrorCharIndex` and the original input string. The char-based trio (`ErrorCharIndex`, `ErrorLine`, `ErrorColumn`) uses the same conventions the Language Server Protocol uses, so a caller forwarding a parse error into an editor through LSP does no arithmetic in between. See [ProgrammingModel.md](ProgrammingModel.md) for the full rationale. The two extra index properties (`ErrorRuneIndex`, `ErrorGraphemeIndex`) are there for callers that measure in other units. They are computed lazily from the char index and cost nothing unless used.

`Symbol.SourceRange` uses the same machinery for any node in the parse tree, not just the error point. Each `SourcePosition` (the type returned by `Start` and `End`) carries the same five fields, so a tool reporting "duplicate section on line 7" or "value out of range at char 42" reads from the symbol with the same semantics LSP and `string.Substring` already use.

The `Outcome` field distinguishes "the grammar did not match" from "we ran out of budget." A grammar mismatch means the input is invalid and you should show the user where. A timeout or rule-count-limit exhaustion means the input might be valid but we could not decide in the budget we were given, and the caller might want to reject it as suspicious, retry with a looser budget, or show a different error to the user. See the "Catastrophic Backtracking and Timeouts" section below for the mechanics.

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
    public IReadOnlyList<Symbol> FlattenInto();    // convenience overload

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
symbol.Walk().Where(s => s.Id == BuiltinSymbols.Integer)

// All descendants matching a specific rule
symbol.FindAll(settingName).Select(s => s.ToString())

// Flattened tree as a list
symbol.FlattenInto().OfType<Symbol>()
```

`Symbol` itself deliberately does *not* implement `IEnumerable<Symbol>`. It would be a two-line change to forward to `Children.GetEnumerator()`, and the tradeoff is not worth it. Iterating a tree node silently means picking one of children, descendants-pre-order, descendants-post-order, siblings, and tokens, and the four other choices then become second-class. `System.Xml.Linq` and Roslyn both refuse to implement `IEnumerable` on their node types for exactly this reason. The parser port takes the same stance.

## A Walkthrough With a Compiler Function

Parsing turns text into a tree. Most callers want to go one step further and turn the tree into their own domain types. The C++ version ships a `Compiler<T>` base class for this. In C# it is usually simpler to write a plain function that takes a rule and an input string and returns your target type:

```csharp
using static InductorParser.Rules;

var settingName  = Identifier().As(nameof(settingName));

var settingValue = FirstOf(
    Float().Flatten(FlattenType.Flatten),
    Integer().Flatten(FlattenType.Flatten),
    Identifier()
).As(nameof(settingValue));

var document = AllOf(
    OptionalWhitespace(),
    settingName,
    OptionalWhitespace(),
    Token('='),
    OptionalWhitespace(),
    settingValue,
    OptionalWhitespace(),
    Token(';'),
    OptionalWhitespace(),
    Eof()
).As(nameof(document)).Compile();

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

If you have several compilers that share the same scaffolding, or you want a consistent `TryCompile(out TResult, out string error)` contract on a public API, it is worth writing a small reusable base class once and inheriting from it. The library does not ship one because the right shape is opinionated (return nullable vs out-parameter vs throw, whether to forward `ParseOptions`, and so on). See [Recipes.md](Recipes.md) for the pattern and an example. For one-off compilers, the plain function shown above is simpler.

## A Bigger Example: Nested Rules

To show how this scales, here is a mini settings file grammar where a document can have multiple settings and settings can have multiple values:

```csharp
using static InductorParser.Rules;

var key = Identifier(extraStartRunes: RuneSet.Runes("_"))
    .As(nameof(key));

// Private helper, not named because it never appears in the final tree
// (its children are flattened directly under `values`).
var valueAtom = FirstOf(
    Float().Flatten(FlattenType.Flatten),
    Integer().Flatten(FlattenType.Flatten),
    key.Flatten(FlattenType.Flatten)
);

var values = AllOf(
    valueAtom,
    ZeroOrMore(
        AllOf(
            OptionalWhitespace(),
            Token(','),
            OptionalWhitespace(),
            valueAtom
        )
    )
).As(nameof(values));

var pair = AllOf(
    key,
    OptionalWhitespace(),
    Token('='),
    OptionalWhitespace(),
    values,
    OptionalWhitespace(),
    Token(';')
).As(nameof(pair));

var document = AllOf(
    OptionalWhitespace(),
    ZeroOrMore(
        AllOf(pair, OptionalWhitespace())
    ),
    Eof()
).As(nameof(document)).Compile();
```

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
        - [integerExpression] 3
```

## Tracing

The C++ version's tracing story (`SetTraceFilter(SystemTraceType::Parsing, TraceDetail::Diagnostic)` before calling `TryParse`) relies on a global trace switch. That does not belong in a library that might be loaded into the Unity editor alongside other systems. The C# port takes the sink (along with everything else you can tune per-parse) through a `ParseOptions` argument:

```csharp
var options = new ParseOptions
{
    TraceSink  = Console.Out,
    TraceLevel = TraceLevel.Diagnostic
};

var result = grammar.Parse(input, options);
```

`TraceSink` is `TextWriter?`. Set it to `Console.Out` for the C++ behavior, set it to a file writer to capture a trace, set it to a custom writer to filter or tag lines. Leave it null and tracing is off, with the trace statements compiled out via a cheap null check that IL2CPP devirtualizes.

The trace format matches the C++ version exactly, including the indentation-by-transaction-depth trick. We do this on purpose: the C++ test corpus has traced output captured in comments and docs, and matching the format lets us reuse those examples as reference material.

## Catastrophic Backtracking and Timeouts

PEG parsers can backtrack pathologically on certain grammar/input combinations. The library's defense is a set of budgets on `ParseOptions` that abort the parse if any trips. Two of them default to protective values so naive callers are safe without thinking about it. The third is opt-in.

```csharp
public sealed class ParseOptions
{
    /// Normalization form applied to the input before parsing. Default
    /// is the composed form (`NormalizationForm.FormC`). Set to null to
    /// skip normalization entirely.
    public NormalizationForm? NormalizeInput { get; set; } = NormalizationForm.FormC;

    /// Atomic unit the lexer reads. Default is Grapheme. See
    /// UnicodeInternalsArchitecture.md for details on the tradeoffs.
    public InputUnit InputUnit { get; set; } = InputUnit.Grapheme;

    /// Rule-count limit: maximum rule invocations before the parse aborts.
    /// A pure count, not a wall-clock measurement, so the same input and
    /// grammar trip at exactly the same point on every run regardless of
    /// machine speed. Default catches catastrophic backtracking without
    /// clipping legitimate multi-MB parses. Raise it for genuinely huge
    /// inputs. Lower it for tighter control. Set to null to disable (not
    /// recommended for untrusted input).
    public long? RuleCountLimit { get; set; } = 10_000_000;

    /// Recursion depth limit. Protects against stack overflow on
    /// pathologically nested input like ((((((...)))))). Default is
    /// ~10x deeper than any legitimate grammar produces; real data
    /// almost never nests past ~100 levels.
    public int? MaxDepth { get; set; } = 1000;

    /// Wall-clock limit. Polled from inside the parse loop with Stopwatch.
    /// Portable to every platform including WebGL. No default. Interactive
    /// callers set this for user-experience reasons. The rule-count limit
    /// above handles the security case with a count that doesn't vary
    /// across machines.
    public TimeSpan? Timeout { get; set; }

    /// Standard .NET cancellation. Polled alongside Timeout.
    public CancellationToken CancellationToken { get; set; }

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
}

public enum InputUnit { Grapheme, Rune }
```

**What to actually do as a caller:**

- For most code, the defaults are fine. The rule-count limit protects against pathological input, the depth limit protects against stack overflow, and you do not need to think about either.
- For interactive contexts (editor plugins, real-time feedback), add a `Timeout` so the user never waits too long: `new ParseOptions { Timeout = TimeSpan.FromMilliseconds(200) }`.
- For parsing genuinely huge input (multi-hundred-MB JSON or similar), raise `RuleCountLimit` explicitly. Lower it tighter if you know your grammar should be fast: a small config file should not need a million rule invocations.
- `MaxDepth = 1000` is enough for every real grammar. Only touch it if you have some exotic deeply-nested data format.

When a budget trips, the parse returns a `ParseResult` with `Outcome` set to `Timeout`, `RuleCountLimitExceeded`, `DepthLimitExceeded`, or `Canceled` (not `GrammarMismatch`). Callers who need to distinguish "input was invalid" from "we ran out of budget" switch on `Outcome`.

For the design rationale behind these choices (why three budgets and not one, why `Timeout` is opt-in but the others default on, why `CancellationTokenSource.CancelAfter` alone is insufficient on WebGL, and future ideas like the cut operator and packrat memoization), see [ProgrammingModel.md](ProgrammingModel.md).
