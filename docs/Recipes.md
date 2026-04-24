# Grammar Recipes

Common grammar patterns and how to express them with this library. Each recipe is a short, self-contained code example you can copy into your grammar and adapt.

The programming model lives in [ProgrammingAGrammar.md](ProgrammingAGrammar.md). Unicode internals live in [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md). Caller-side Unicode concerns live in [UnicodeGotchas.md](UnicodeGotchas.md). This doc is for grammar-authoring patterns that keep coming up.

## Pass-Through Text: Matching "All Text"

A very common grammar shape: you care about specific formatting markers, and everything else is body text that should flow through unchanged. Markdown, XML/HTML text nodes, configuration comments, JSON string bodies, chat message parsers. For these grammars the idiom is `OneOrMore(NoneOf(formattingChars))`:

```csharp
using static InductorParser.Rules;

var formatting = RuneSet.Runes("*_#`[]()\\");
var textChar   = NoneOf(formatting);
var text       = OneOrMore(textChar).As(nameof(text));

var bold = And(
    Literal("**"),
    OneOrMore(NoneOf(RuneSet.Runes("*"))),
    Literal("**")
).As(nameof(bold));

var code = And(
    Token('`'),
    OneOrMore(NoneOf(RuneSet.Runes("`"))),
    Token('`')
).As(nameof(code));

var inline    = Or(bold, code, text);
var paragraph = OneOrMore(inline).As(nameof(paragraph));
```

Parse `Hello 🎸 **world** 你好 ` + "`code`" + ` done` and you get a tree where the guitar emoji lives in the first text node, the CJK in another, and `ToString()` reassembles each node losslessly. The default GraphemeLexer treats 🎸 and each of 你 and 好 as individual graphemes, so the text nodes see them as single tokens. Even under RuneLexer the round-trip would still work (every rune gets captured), just with multi-rune graphemes showing up as multiple child nodes.

### Stopping at a Multi-Character Terminator

The `NoneOf` form above works when the stop is a small set of single characters. When the stop is a sequence, like `*/` closing a block comment or `-->` closing an XML comment, a character class can't express it. The idiom there is `ZeroOrMore(And(Not(stopRule), AnyToken()))`:

```csharp
var closeMarker = And(Token('*'), Token('/'));
var blockComment = And(
    Token('/'), Token('*'),
    ZeroOrMore(And(Not(closeMarker), AnyToken())),
    closeMarker);
```

Each iteration first checks that `closeMarker` does not match at the current cursor (`Not` is negative lookahead, zero-width), and only then consumes one token with `AnyToken()`. When `closeMarker` would fire, `Not` fails, the `And` fails, and the `ZeroOrMore` stops with the cursor sitting just before `*/`. The outer `And` then matches the terminator for real. `AnyToken()` handles multi-rune graphemes naturally under GraphemeLexer, same as `NoneOf`, so emoji and CJK in the comment body pass through unchanged.

## Matching an Identifier

Use `Identifier()` for a Unicode-aware identifier per UAX #31 (`XID_Start XID_Continue*`). It accepts `foo`, `café`, `καλημέρα`, Devanagari, Thai, and Arabic-with-vowels under the default grapheme lexer. No lexer mode switch required.

```csharp
var name = Identifier().As(nameof(name));
```

For the common "programming-language profile" that also allows leading underscore, add `_` to the Start set:

```csharp
var name = Identifier(extraStartRunes: RuneSet.Runes("_"));
```

For the broader profile that allows `_` and `$` (ECMAScript-style), add both:

```csharp
var name = Identifier(
    extraStartRunes: RuneSet.Runes("_$"),
    extraBodyRunes:  RuneSet.Runes("$"));   // "_" is already in XID_Continue
```

For NFKC equivalence (Python 3 and Rust behavior, where fullwidth `ｆｏｏ` and plain `foo` match the same identifier), set normalization to `FormKC`:

```csharp
var result = name.Parse(input, new ParseOptions
{
    NormalizeInput = NormalizationForm.FormKC,
});
```

Per-language recipes and the full detail of what the Start and Continue sets cover live in [UnicodeGotchas.md § Identifier Matching](UnicodeGotchas.md#identifier-matching).

## Organizing a Large Grammar as a Class

Local variables work fine for small grammars. For anything bigger, you will want to organize rules across files and reference them by name from outside their defining scope. The natural C# shape for that is a static class, treated purely as a namespace for rule fields. Nothing in the library requires it, but the convention is worth documenting because most production grammars will end up here.

```csharp
using static InductorParser.Rules;

public static class NameValueGrammar
{
    public static readonly Rule SettingName =
        Identifier().As(nameof(SettingName));

    public static readonly Rule SettingValue =
        Or(
            Float().Flatten(FlattenType.Flatten),
            Integer().Flatten(FlattenType.Flatten),
            Identifier()
        ).As(nameof(SettingValue))
         .Flatten(FlattenType.None);

    public static readonly Rule Document =
        And(
            OptionalWhitespace(),
            SettingName,
            OptionalWhitespace(),
            Token('='),
            OptionalWhitespace(),
            SettingValue,
            OptionalWhitespace(),
            Token(';'),
            OptionalWhitespace(),
            Eof()
        ).As(nameof(Document)).Compile();
}
```

Callers use it through the class name:

```csharp
var result = NameValueGrammar.Document.Parse(input);
var name   = result.Tree.Find(NameValueGrammar.SettingName).ToString();
```

The pattern has four pieces worth naming explicitly:

**`.As(nameof(X))` on every public field**, including the root. The field name and the rule name stay in sync because `nameof` is compile-checked. IDE renames propagate. Trace output and error messages read naturally.

**`.Flatten(FlattenType.None)` on any rule you want to `Find`.** `Parse` applies the flatten pass before returning, so rules with the default `FlattenType.Flatten` have their children lifted up and their own wrapper removed from the tree, so `Tree.Find(rule)` cannot locate them. `.Flatten(FlattenType.None)` preserves the wrapper. Rules that only show up for their text content (repetitions, `And` compositions whose children are individually findable) can stay at the default and skip this call.

**`.Compile()` on the root field.** This forces the full finalization pass (id stamping, `LateBoundRule` resolution, freeze, validation) to run at type-init time rather than at first parse. Any grammar-construction error surfaces immediately when the class is first touched, which is a much better debugging experience than waiting for the first parse to reveal a broken grammar.

**Private fields for helpers that do not need to be part of the public surface.** Internal helper rules can be declared without `.As(...)` if they never show up in the tree (flattened away), or with `.As(...)` if you want them visible in traces.

## A Reusable Compiler Base Class

The walkthrough in [ProgrammingAGrammar.md](ProgrammingAGrammar.md) shows a "compiler" as a plain function: take a rule and an input, return a typed result plus an optional error message. That form is simplest for one-off cases. When you have several compilers that share the same scaffolding, or you want a consistent `TryCompile` contract on a public API, it is worth writing a small base class once and inheriting from it. The library does not ship this as a built-in because the right shape is opinionated and every codebase tends to want it slightly different. Here is the pattern to copy and adapt.

```csharp
// User-space base class. Put this somewhere reusable in your codebase.
public abstract class Compiler<TResult>
{
    private readonly Rule _root;

    protected Compiler(Rule root) => _root = root;

    public bool TryCompile(string input, out TResult result, out string error)
    {
        var parsed = _root.Parse(input);
        if (!parsed.Success)
        {
            result = default!;
            error  = $"Line {parsed.ErrorLine}: {parsed.ErrorMessage}";
            return false;
        }

        result = ProcessTree(parsed.Tree!);
        error  = "";
        return true;
    }

    protected abstract TResult ProcessTree(Symbol tree);
}
```

Usage against the class-based grammar above:

```csharp
public sealed record Setting(string Name, string Value);

public sealed class NameValueCompiler : Compiler<Setting>
{
    public NameValueCompiler() : base(NameValueGrammar.Document) { }

    protected override Setting ProcessTree(Symbol tree)
    {
        var nameNode  = tree.Find(NameValueGrammar.SettingName);
        var valueNode = tree.Find(NameValueGrammar.SettingValue);
        return new Setting(nameNode.ToString(), valueNode.ToString());
    }
}

// Call site
var compiler = new NameValueCompiler();
if (compiler.TryCompile(input, out var setting, out var error))
    Console.WriteLine($"{setting.Name} = {setting.Value}");
else
    Console.WriteLine($"Parse failed: {error}");
```

The base class bundles up two small things: running the root rule and branching on success/failure before handing the tree to the user. Each of those is one or two lines, so skipping the base class for a one-off compiler is fine. The scaffolding pays for itself when you have multiple compilers sharing the pattern, or when the `TryCompile(out TResult, out string error)` shape is what your public API needs to speak.

Obvious variations if the shape above does not fit your codebase: return `TResult?` with a nullable result instead of an out-parameter, throw a `CompileException` on failure instead of returning a bool, add a `CompileOrThrow` overload, add `ParseOptions` forwarding, etc. The base class is small enough that adapting it is usually easier than shoehorning a library-supplied version into your conventions.
