# Inductor Parser Primer 2: Walking the Tree

Primer 1 built a grammar that succeeds or fails and that's it. But most of the time, parsing isn't the goal. You parse so you can do something with what you parsed: look settings up by name, check that the right things are there, point at the spot where it went wrong. Once the parser hands you back a tree, all of that's just walking the tree.

Let's parse a tiny INI-style config file. Something like this:

```ini
[server]
host = "localhost"
port = 8080

[client]
timeout = 30
```

Two sections, each with a couple of `key = value` lines. We'll parse it, walk the tree to look up `[server] port`, and report errors for the cases where things go wrong.

A quick spec, so the rules below don't surprise you:

- A line is one of a section header, a key/value pair, or blank.
- A section header is `[name]` on its own line. Names are tokens that aren't single-rune whitespace and not `]`. So `[a=b]` is legal (`=` only has special meaning between a key and a value), but `[my server]` and `[ server ]` aren't.
- A key/value pair is `key = value`. Keys are tokens that aren't single-rune whitespace and not `=`. Whitespace around `=` is optional.
- Values are typed: an integer, a float, a double-quoted string, or a bare word (a single run of tokens that aren't single-rune whitespace or quotes). Multi-word strings need quotes, so `name = "my favorite thing"` works but `name = my favorite thing` doesn't.
- Line terminators are the full Unicode set (LF, CR, CRLF, NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR, VT, FF), not just `\n`.

The grammar:

```CSharp
// All the runes that end a line in Unicode (LF, CR, VT, FF, NEL,
// LINE SEPARATOR, PARAGRAPH SEPARATOR), so we can exclude them from
// the body of a name, key, or value. EndOfLine() then consumes the
// terminator itself, including CRLF as a single unit.
var lineEndRunes = TokenSet.LineTerminators;

// "Any single-rune whitespace, line terminators included." We need
// this in the NoneOf stop sets below: a name or key should stop
// either at horizontal whitespace OR at a line terminator. The
// built-in TokenSet.InlineWhitespace is intra-line only by design,
// so we union with the line terminators here to get a single set
// that covers both cases for use inside NoneOf.
var anySpaceRunes = TokenSet.InlineWhitespace | lineEndRunes;

// Section names and keys: one or more non-whitespace runes, stopping
// at the relevant terminator (']' for a name, '=' for a key).
var name = OneOrMore(NoneOf(TokenSet.Runes("]") | anySpaceRunes))
    .As("name").Preserve();
var key = OneOrMore(NoneOf(TokenSet.Runes("=") | anySpaceRunes))
    .As("key").Preserve();

var section = AllOf(Token('['), name, Token(']'), Optional(InlineWhitespace()), EndOfLine())
    .As("section").Preserve();

// Typed values. Each alternative is .As(name).Preserve() so the
// matching one survives flattening as a discoverable child of value.
// Order matters in FirstOf: Float before Integer because "3.14" would
// otherwise commit to Integer on the leading "3" and stall.
var quotedString = AllOf(
    Token('"'),
    ZeroOrMore(NoneOf(TokenSet.Runes("\"") | lineEndRunes)),
    Token('"')).As("quotedString").Preserve();

var bareWord = OneOrMore(NoneOf(anySpaceRunes | TokenSet.Runes("\"")))
    .As("bareWord").Preserve();

var floatValue = Float().As("float").Preserve();
var integerValue = Integer().As("integer").Preserve();

var value = FirstOf(floatValue, integerValue, quotedString, bareWord)
    .As("value").Preserve();

var keyValue = AllOf(key, Optional(InlineWhitespace()), Token('='), Optional(InlineWhitespace()), value, Optional(InlineWhitespace()), EndOfLine())
    .As("keyValue").Preserve();

var blankLine = AllOf(Optional(InlineWhitespace()), EndOfLine());

var line = FirstOf(section, keyValue, blankLine);
var config = AllOf(ZeroOrMore(line), Eof()).As("config").Preserve();
```

`name` and `key` are the same shape: one or more tokens that aren't single-rune whitespace and not the stop character (`]` for names, `=` for keys). `NoneOf(set)` matches a token when it isn't exactly one rune from the set, and `|` is set union.

`value` is where typing happens. Each alternative is `.As(name).Preserve()` so the matching one lands in the tree as a typed child. `Float()` and `Integer()` are built-in rules, `quotedString` is the standard open-quote/body/close-quote shape and `bareWord` catches everything else. Order in `FirstOf` matters because it stops at the first match: `Float` is before `Integer` so `3.14` doesn't commit to `3` and leave `.14` for the next rule to choke on.

`EndOfLine()` accepts CRLF as a unit plus any of the seven Unicode single-rune line terminators. `Token('\n')` only handles LF and would silently cause a bug on a CRLF Windows file or anything using NEL, LINE SEPARATOR, or PARAGRAPH SEPARATOR.

`.As(name).Preserve()` is the same pattern as primer1: name the rule so you can find it later, keep its wrapper in the tree so there's something to find.

# What the tree looks like

If we run `config.Parse("[server]\nhost = \"localhost\"\nport = 8080\n")`, the tree looks like this:

```
config
├── section
│   └── name ── "server"
├── keyValue
│   ├── key ── "host"
│   └── value
│       └── quotedString ── "localhost"
└── keyValue
    ├── key ── "port"
    └── value
        └── integer ── "8080"
```

The `'['`, `']'`, `'='`, the surrounding quote tokens of a quotedString, and the line terminator are all gone after flattening (their default flatten policy is Delete). The `Optional(InlineWhitespace())` around `=` are gone too. What's left is the structure we care about: each `value` carries one named child indicating which alternative matched, and the consumer can use it without re-parsing the text.

INI doesn't nest sections. The `[server]` header and the keys that belong to it sit as siblings under the root rather than as children. To find "the keys belonging to section X" we just look for siblings of the section that are keyValues.

# Walking the tree

Walk the children of config, remembering each `section` name you pass. When you hit a `keyValue`, it lives in the last section you passed:

```CSharp
public static Symbol? FindSetting(Symbol config, string sectionName, string keyName)
{
    string? currentSection = null;
    foreach (var child in config.Children)
    {
        if (child.Is(section))
        {
            currentSection = child.Children[0].ToString();
        }
        else if (child.Is(keyValue) && currentSection == sectionName)
        {
            string thisKey = child.Children[0].ToString();
            if (thisKey == keyName)
                return child.Children[1];  // the value node, with its typed child
        }
    }
    return null;
}
```

`symbol.Is(rule)` checks whether the symbol was produced by the rule. 

`symbol.Children` is the list of children that survived flattening. For a section, that's a single `name` leaf, so `child.Children[0].ToString()` gives the section's name as a string. For a keyValue, the children are `[key, value]`, so index 0 is the key and index 1 is the value's container.

We return the value `Symbol` so the caller can still read the type of the child. To read `[server]/port` as an integer:

```CSharp
var portValue = FindSetting(result.Tree!, "server", "port");
if (portValue == null)
    throw new InvalidOperationException("Missing required setting: [server] port");

var typed = portValue.Children[0];
if (!typed.Is(integerValue))
    throw new FormatException($"[server] port: expected an integer");

int port = int.Parse(typed.ToString(), CultureInfo.InvariantCulture);
```

The grammar already verified the value's shape. If the input was `port = abc`, the typed child would be a `bareWord` (not an `integerValue`). If the input was `port = "8080"`, the typed child would be a `quotedString` and we can either coerce or reject it. 

`symbol.ToString()` returns the matched text. For a leaf, that's the consumed string. For a composite like `AllOf`, it's the concatenation of every leaf underneath. Either way, you get back what the rule consumed.

If you want every section regardless of context, two helpers besides `.Is()` come up enough to be worth knowing:

- `symbol.Find(rule)` does a depth-first search and returns the first matching descendant (or null). Use it when you expect one match in a known position.
- `symbol.FindAll(rule)` does the same but yields every match. Use it for "give me every section" or "every keyValue."

```CSharp
foreach (var sectionSymbol in result.Tree!.FindAll(section))
{
    Console.WriteLine($"Found section: {sectionSymbol.Children[0]}");
}
```

For our setting-lookup problem we aren't using FindAll, because we care about where in the file each section header appears (it groups the keys that follow it). Find and FindAll are for "go grab the title node" or "give me every link" cases where order isn't meaningful.

# Walking the tree with LINQ

Find and FindAll are convenience helpers. Underneath, every traversal on `Symbol` is a direct LINQ target because each one is typed as `IReadOnlyList<Symbol>` or `IEnumerable<Symbol>`. Four entry points cover the four things you usually want to do with a Symbol tree:

```CSharp
// Direct children only (no recursion)
result.Tree!.Children.Where(c => c.Is(section))

// Entire subtree, pre-order walk
result.Tree!.Walk().Where(s => s.Is(integerValue))

// All descendants matching a specific rule
result.Tree!.FindAll(keyValue).Select(kv => kv.Children[0].ToString())

// Flattened tree as a list of every Symbol
result.Tree!.FlattenInto().OfType<Symbol>()
```

`Symbol` itself doesn't implement `IEnumerable<Symbol>` on purpose, because iterating a tree node would have to silently pick one of children, descendants pre-order, descendants post-order, siblings, or tokens, and the four other choices then become second-class. Naming the traversal you want keeps the code unambiguous.

# When the parse fails

`Parse()` returns a `ParseResult`, and on failure it carries enough to point at the problem:

```CSharp
var result = config.Parse("[server]\nport oops\n");
if (!result.Success)
{
    Console.WriteLine($"Parse failed at line {result.ErrorLine}, column {result.ErrorColumn}");
    Console.WriteLine($"  {result.ErrorMessage}");
}
```

That input tries to use `port oops` as a key/value pair without an `=` sign. The output looks like:

```
Parse failed at line 1, column 5
  Parse failed at offset 14: unexpected 'o'.
```

The default error message is generic. To upgrade it, attach `.WithError(...)` to the rule that's most likely to be where the user went wrong:

```CSharp
var keyValue = AllOf(
    key,
    Optional(InlineWhitespace()),
    Token('=').WithError("Expected '=' after the setting name"),
    Optional(InlineWhitespace()),
    value,
    Optional(InlineWhitespace()),
    EndOfLine())
    .As("keyValue").Preserve();
```

If `Token('=')` is the deepest failure when a parse fails (the rule that got furthest before giving up), `result.ErrorMessage` will be your custom string instead of the default. Re-running the same `[server]\nport oops\n` input now reports:

```
Parse failed at line 1, column 5
  Expected '=' after the setting name
```

`ErrorLine` and `ErrorColumn` follow the Language Server Protocol convention used by text editors and developer tools: zero-based, with line breaks at `\n`, `\r\n`, or lone `\r`.

`.WithError` covers the rules you can predict will fail. For the catch-all the parser falls back to when nothing was decorated at the deepest failure, `ParseOptions` carries a set of templates with `{name}`-style placeholders. The placeholders match the position units `ParseResult` already names, so a template author uses the same vocabulary the rest of the API does. Going back to the basic grammar (the version before we attached `.WithError`), suppose you want the catch-all rendered in French:

```CSharp
var options = new ParseOptions
{
    PositionalErrorTemplate = "Erreur à la position {charIndex}: caractère '{character}' inattendu.",
    EndOfInputErrorTemplate = "Fin d'entrée inattendue.",
};

var result = config.Parse("[server]\nport oops\n", options);
Console.WriteLine(result.ErrorMessage);
```

Output:

```
Erreur à la position 14: caractère 'o' inattendu.
```

The position placeholders work in every template: `{charIndex}`, `{runeIndex}`, `{tokenIndex}`, `{line}`, `{column}`. The positional template gets one extra, `{character}`, for the input character that didn't match. Four matching templates exist for the budget aborts (timeout, rule-count limit, recursion-depth limit, cancellation) with their own unit-specific placeholders like `{timeout}` and `{limit}`. 

Semantic errors happen after the parse: a duplicate section, a missing required key, a number out of range. The parse already succeeded so now you need to walk the tree and check things.

For example, you might want to disallow duplicate section names. Here's how you'd catch a duplicate using `FindAll` to grab every section header in the tree, then a `HashSet` to spot the repeat. Every Symbol exposes its position back into the input through `SourceRange`, so we can include the line number in the error to point the user at the offending header:

```CSharp
var seen = new HashSet<string>();
foreach (var sectionSymbol in result.Tree!.FindAll(section))
{
    string sectionName = sectionSymbol.Children[0].ToString();
    if (!seen.Add(sectionName))
    {
        int line = sectionSymbol.SourceRange!.Value.Start.Line + 1;
        throw new FormatException($"Duplicate section [{sectionName}] on line {line}");
    }
}
```

`SourceRange` returns a `Start` and `End` pair, each a `SourcePosition` carrying the same four units as `ParseResult`'s error position: `CharIndex`, `TokenIndex`, `Line`, `Column`. The `+ 1` here is because Language Server Protocol lines are zero-based but humans count from 1.

A composite node's range covers every leaf underneath it. Ask `keyValue.SourceRange` and you get the whole `host = "localhost"` line. Ask `value.SourceRange` and you get just the value. Pick the node and you pick the span.

A range with both ends is also exactly what you need to draw a compiler-style underline. The grammar already accepts any integer for `port`, but ports are 1..65535. Catch out-of-range values after the parse and point at the offending value:

```CSharp
string sourceText = "[server]\nhost = \"localhost\"\nport = 99999\n";
var result = config.Parse(sourceText);

var portValue = FindSetting(result.Tree!, "server", "port");
var typed = portValue!.Children[0];
int port = int.Parse(typed.ToString(), CultureInfo.InvariantCulture);

if (port < 1 || port > 65535)
{
    var range = typed.SourceRange!.Value;
    string offendingLine = sourceText.Split('\n')[range.Start.Line];
    int startColumn = range.Start.Column;
    int width = range.End.Column - range.Start.Column;
    Console.WriteLine($"Line {range.Start.Line + 1}: port {port} is out of range");
    Console.WriteLine($"  {offendingLine}");
    Console.WriteLine($"  {new string(' ', startColumn)}{new string('^', width)}");
}
```

For the input above, the output is:

```
Line 3: port 99999 is out of range
  port = 99999
         ^^^^^
```

`Start.Line` picks the right line out of the input, `Start.Column` indents the underline to the value, and `End.Column - Start.Column` sizes it. No re-scanning the input to figure out where things are, the parser already knew.


# Unicode and where the error actually is

Here's where it gets interesting. `ErrorColumn` and a sibling field `ErrorCharIndex` both count chars (UTF-16 code units), which is what `string.Substring`, `Span<char>`, and the Language Server Protocol all use. That works fine for ASCII. But suppose this is a config for a family-shared device and the user types a section header in emoji:

```ini
[👨‍👩‍👧]
port oops
```

That's a section name made of a single family emoji, then a malformed key/value line. The family emoji is the demo's whole point: it's one of the few characters that pulls chars and tokens apart by a wide margin. A bare guitar emoji 🎸 is 2 chars but 1 token (one user-visible character). The family emoji is 8 chars but still 1 token. So the char count and the token count give very different numbers, which is what makes "which one do I report?" a real question instead of a hypothetical one.

The section header itself parses fine: `name` rejects single-rune whitespace and `]`, but a multi-rune token like the family emoji isn't any single rune in any rune-only set, so `NoneOf` accepts it as one token. The parser gets past the header and fails on line 2 at the same spot it would for an ASCII version: where the `=` should be.

But the position numbers diverge. To a human, the family is one character and the failure happens 5 characters into the second line. In memory, the family is eight UTF-16 code units (each emoji is a surrogate pair, plus two code units for the two ZWJs). So which "position" should the parser report?

Inductor Parser reports it three ways plus line/column, because the right unit depends on what the caller is going to do with the number:

```CSharp
result.ErrorCharIndex   // 16 - UTF-16 code units, what string.Substring uses
result.ErrorTokenIndex  // 9  - tokens (user-visible characters)
result.ErrorLine        // 1
result.ErrorColumn      // 5  - same unit as ErrorCharIndex, used by the Language Server Protocol
```

All four point at the same place in the input. They just count it in different units.

Use `ErrorCharIndex` (or `ErrorColumn`) when you're going to feed the number into something that thinks in chars: `string.Substring`, `ReadOnlySpan<char>.Slice`, a Language Server Protocol diagnostic, a regex offset. That's most production code, because chars are the unit .NET strings index in.

Use `ErrorTokenIndex` for anything that faces a human. "Error at character 9" is what a person sees on screen. "Error at character 16" would seem to point past the end of what they typed, because they don't think of an emoji as taking up 8 of anything.

Most of the time you won't care, because most input is ASCII and the two numbers are equal. But the moment a user pastes in an emoji, a flag, or a letter with a combining accent, the indices diverge, and "which one do I show in the error message" stops being a question you can ignore.

The same multi-unit story applies to every Symbol's SourceRange, not just to errors. If the input has the family emoji as a section name and we want to underline it, the two units give two different widths:

```CSharp
var result = config.Parse("[👨‍👩‍👧]\n");
var sectionName = result.Tree!.Find(section)!.Children[0];  // the "name" leaf
var range = sectionName.SourceRange!.Value;
int charWidth  = range.End.CharIndex  - range.Start.CharIndex;   // 8
int tokenWidth = range.End.TokenIndex - range.Start.TokenIndex;  // 1
```

Both are right. The right one to use is whichever your consumer counts in: chars to feed `string.Substring` or send a Language Server Protocol diagnostic, tokens to draw a `^` under each thing the user sees on screen.
