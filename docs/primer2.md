# Inductor Parser Primer: Parsing and Processing

The first primer built a grammar that succeeds or fails and that's it. But most of the time, parsing isn't the goal. You parse so you can do something with what you parsed: look settings up by name, check that the right things are there, point at the spot where it went wrong. Once the parser hands you back a tree, all of that's just walking the tree.

Let's parse a tiny INI-style config file. Something like this:

```ini
[server]
host = "localhost"
port = 8080

[client]
timeout = 30
```

The example is two sections, each with a couple of `key = value` lines. We'll parse it, walk the tree to look up `[server] port`, and report errors for the cases where things go wrong.

Here's the grammar:

```CSharp
// Everything Unicode treats as ending a line (LF, CR, CRLF, VT, FF, NEL,
// LINE SEPARATOR, PARAGRAPH SEPARATOR), so we can exclude them from the
// body of a name, key, or value. 
var lineTerminators = TokenSet.LineTerminators;

// A name or key should stop either at any whitespace
var anyWhitespace = TokenSet.InlineWhitespace | lineTerminators;

// Section names and keys can be anything 
// that isn't whitespace or the character that ends them.
// `NoneOf(set)` matches a token when that token isn't in the 
// set, and `|` is set union
var name = OneOrMore(NoneOf(TokenSet.Single(']') | anyWhitespace)).As("name");
var key = OneOrMore(NoneOf(TokenSet.Single('=') | anyWhitespace)).As("key");

var section = And(Token('['), 
                  name, 
                  Token(']'), 
                  Optional(InlineWhitespace()), 
                  EndOfLine())
              .As("section");

// Typed values: .As(name) does two things: it attaches a 
// name to find later, and it sets the rule to `FlattenType.Preserve` 
// so the rule's wrapper survives flattening and can be found
var quotedString = And(Token('"'),
                       ZeroOrMore(NoneOf(TokenSet.Single('"') |  lineTerminators)),
                       Token('"'))
                    .As("quotedString");
var bareWord = OneOrMore(NoneOf(anyWhitespace | TokenSet.Single('"'))).As("bareWord");
var floatValue = Float().As("float");
var integerValue = Integer().As("integer");

// Or tries left to right, so Float must come before Integer.
var value = Or(floatValue, 
               integerValue, 
               quotedString, 
               bareWord)
            .As("value");

var keyValue = And(key, 
                   Optional(InlineWhitespace()), 
                   Token('='), 
                   Optional(InlineWhitespace()), 
                   value, 
                   Optional(InlineWhitespace()), 
                   EndOfLine())
    .As("keyValue");

var blankLine = And(Optional(InlineWhitespace()), 
                    EndOfLine());

var line = Or(section, 
              keyValue, 
              blankLine);

var config = And(ZeroOrMore(line), 
                 Eof())
             .As("config");
```

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

The `'['`, `']'`, `'='`, the surrounding quotes of a quotedString, and the line terminator are all gone after flattening (their default flatten policy is Delete, the flatten process is covered in [Primer: Building a Grammar](primer1.md)). The `Optional(InlineWhitespace())` around `=` are gone too. What's left is the structure we care about: each `value` carries one named child indicating which alternative matched. The drawing does simplify one thing: a node like `name` really holds one child Symbol per matched character (`NoneOf` and `OneOf` matches default to Preserve), and `ToString()` concatenates them back into "server". The drawing collapses those runs into the string they spell.

The INI grammar doesn't nest sections. The `[server]` header and the keys that belong to it are siblings under the root. To find "the keys belonging to section X" we just look for siblings after the section that are keyValues.

# Walking the tree

To process the tree, you'd walk the children of config, remembering each `section` name you pass. When you hit a `keyValue`, it lives in the last section you passed:

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
                // the value node, with its typed child
                return child.Children[1];  
        }
    }
    return null;
}
```

`symbol.Is(rule)` checks whether the symbol was produced by the rule in the `rule` variable. 

`symbol.Children` is the list of children that survived flattening. For a section, that's a single `name` child, so `child.Children[0].ToString()` gives the section's name as a string. For a keyValue, the children are `[key, value]`, so index 0 is the key and index 1 is the value's container.

We return the whole `Symbol` object so the caller can still read the type of the child. To do this with `[server]/port` you'd write code like this:

```CSharp
var portValue = FindSetting(result.Tree!, "server", "port");
if (portValue == null)
    throw new InvalidOperationException("Missing required setting: [server] port");

var typed = portValue.Children[0];
if (!typed.Is(integerValue))
    throw new FormatException($"[server] port: expected an integer");

int port = int.Parse(typed.ToString(), CultureInfo.InvariantCulture);
```

The grammar already interpreted the value as one of our defined types. So, if the input was `port = abc`, the typed child would be a `bareWord` (not an `integerValue`). If the input was `port = "8080"`, the typed child would be a `quotedString` and we can either coerce or reject it. 

`symbol.ToString()` returns the matched text that wasn't removed via flattening. For a leaf, that's the consumed string. For a composite like `And`, it's the concatenation of every leaf underneath after FlattenType has been applied: Delete'd nodes are gone, Flatten'd composites have their children lifted into the parent. That's almost always what you want for reading a value out of the tree.

To illustrate how the deleted nodes work, let's look at the `host = "localhost"` line. The `quotedString` rule has two `Token('"')` subrules that are Delete'd by default, so the quotes never enter the tree. Here's that rule for reference:

```CSharp
var quotedString = And(Token('"'),
                       ZeroOrMore(NoneOf(TokenSet.Single('"') |  lineTerminators)),
                       Token('"'))
                    .As("quotedString");
```

And here's an example of using it:

```CSharp
var quoted = FindSetting(result.Tree!, "server", "host")!.Children[0];  
// returns: localhost
// the quotes were Delete'd, so they're gone
quoted.ToString()   
```
If you want the verbatim text, you can use a different property: `symbol.SourceText`. It returns the verbatim section of input the Symbol covered. It ignores FlattenType, so it includes the characters that Delete'd rules matched (the quotes in this example). Use it when an error message should show the raw text, or when you don't want to flip a bunch of rules to a different flatten mode just to read them back.

```CSharp
// returns "localhost"
// which is the verbatim input range, quotes and all
quoted.SourceText   
```

Two helpers are available if you want to find symbols in the tree regardless of context:

- `symbol.Find(rule)` does a depth-first search, starting with the symbol itself, and returns the first match (or null). Use it when you expect one match in a known position.
- `symbol.FindAll(rule)` does the same but yields every match. Use it for "give me every section" or "every keyValue."

```CSharp
foreach (var sectionSymbol in result.Tree!.FindAll(section))
{
    Console.WriteLine($"Found section: {sectionSymbol.Children[0]}");
}
```

For our setting-lookup problem we aren't using FindAll, because we care about where in the file each section header appears (it groups the keys that follow it). Find and FindAll are for "go grab the title node" or "give me every link" cases where location in the tree isn't meaningful.

# Walking the tree with LINQ

Every accessor that finds things on `Symbol` is a direct LINQ target because they are typed as `IReadOnlyList<Symbol>` or `IEnumerable<Symbol>`. Three accessors exist for things you usually want to do with a Symbol tree:

```CSharp
// Direct children only (no recursion)
result.Tree!.Children.Where(c => c.Is(section))

// Entire subtree, pre-order walk (every Symbol)
result.Tree!.Walk().Where(s => s.Is(integerValue))

// All descendants matching a specific rule
result.Tree!.FindAll(keyValue).Select(kv => kv.Children[0].ToString())
```

`Symbol` itself doesn't implement `IEnumerable<Symbol>` on purpose, because iterating a tree node would have to silently pick one of the traversals. Naming the traversal you want keeps the code unambiguous.

# Semantic errors

Semantic errors happen after the parse: a duplicate section, a missing required key, a number out of range. The parse already succeeded so now you need to walk the tree and check things.

For example, you might want to disallow duplicate section names. Here's how you'd catch a duplicate using `FindAll` to grab every section header in the tree, then a `HashSet` to spot the repeat. Every Symbol exposes its position back into the input through `SourceRange`, so we can include the line number in the error to point the user at the offending header:

```CSharp
var seen = new HashSet<string>();
foreach (var sectionSymbol in result.Tree!.FindAll(section))
{
    string sectionName = sectionSymbol.Children[0].ToString();
    if (!seen.Add(sectionName))
    {
        int line = sectionSymbol.SourceRange!.Value.Start.LineNumber;
        throw new FormatException($"Duplicate section [{sectionName}] on line {line}");
    }
}
```

`SourceRange` gives a `Start` and `End`, each a `SourcePosition`. `Start.LineNumber` is the one-based line the message wants (its zero-based `Line` and the position's other units come up in the next section).

A composite node's range covers every leaf underneath it. `keyValue.SourceRange` returns the whole `host = "localhost"` line. `value.SourceRange` returns just the value. 

A range with both ends is also exactly what you need to draw a compiler-style underline. The grammar already accepts any integer for `port`, but ports are 1..65535. So, you can catch out-of-range values after the parse and point at the offending value:

```CSharp
string sourceText = "[server]\nhost = \"localhost\"\nport = 99999\n";
var result = config.Parse(sourceText);

var portValue = FindSetting(result.Tree!, "server", "port");
var typed = portValue!.Children[0];
int port = int.Parse(typed.ToString(), CultureInfo.InvariantCulture);

if (port < 1 || port > 65535)
{
    var range = typed.SourceRange!.Value;
    string offendingLine = range.SourceLine();
    int startColumn = range.Start.CharColumn;
    int width = range.End.CharColumn - range.Start.CharColumn;
    Console.WriteLine($"Line {range.Start.LineNumber}: port {port} is out of range");
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

`range.SourceLine()` pulls out the offending line, `Start.CharColumn` indents the underline to the value, and `End.CharColumn - Start.CharColumn` sizes it. `SourceLine` finds the line boundaries the same way the parser found `Start.Line`, so it handles CRLF and the rarer Unicode terminators that splitting the input on `\n` would get wrong.


# Unicode and where the error actually is

[Primer: Parsing Errors](primerFailure.md) showed the error message itself. This section is about the units the parser counts positions in, which start to matter the moment the input isn't plain ASCII, and which apply to a Symbol's `SourceRange` just as much as to an error.

`ParseResult.ErrorCharColumn` and a sibling field `ParseResult.ErrorCharIndex` both count chars (UTF-16 code units), which is what `string.Substring`, `Span<char>`, and the Language Server Protocol all use. That works fine for ASCII. But suppose this is a config for a family-shared device and the user types a section header in emoji:

```ini
[👨‍👩‍👧]
port oops
```

That's a section name made of a single family emoji, then a malformed key/value line. The family emoji is the demo's whole point: it's one of the few characters that pulls chars and tokens apart by a wide margin. A bare guitar emoji 🎸 is 2 chars but 1 token (one user-visible character). The family emoji is 8 chars but still 1 token. So the char count and the token count give very different numbers.

The section header itself parses fine: `name` only rejects whitespace and `]`, and accepts anything else. The parser gets past the header and fails on line 2 at the same spot it would for an ASCII version: where the `=` should be.

But the position numbers diverge. To a human, the family is one character and the failure happens 5 characters into the second line. In memory, the family is eight UTF-16 code units (each emoji is a surrogate pair, plus two code units for the two ZWJs). So which "position" should the parser report?

Inductor Parser reports it two ways plus line/column, because the right unit depends on what the caller is going to do with the number:

```CSharp
result.ErrorCharIndex    // 16 - UTF-16 code units, what string.Substring uses
result.ErrorTokenIndex   // 9  - tokens (user-visible characters)
result.ErrorLine         // 1
result.ErrorCharColumn   // 5  - char column, the unit the Language Server Protocol uses
result.ErrorTokenColumn  // 5  - grapheme column; matches ErrorCharColumn here since line 2 is ASCII
```

All five point at the same place in the input. They just count it in different units.

Use `ErrorCharIndex` (or `ErrorCharColumn`) when you're going to feed the number into something that thinks in chars: `string.Substring`, `ReadOnlySpan<char>.Slice`, a Language Server Protocol diagnostic, a regex offset. That's most production code, because chars are the unit .NET strings index in.

Use `ErrorTokenIndex` for anything that faces a human. "Error at character 9" is what a person sees on screen. "Error at character 16" would seem to point past the end of what they typed, because they don't think of an emoji as taking up 8 of anything.

The column has the same two flavors. `ErrorCharColumn` counts chars (the Language Server Protocol unit) and `ErrorTokenColumn` counts graphemes. They match on this example because line 2 is plain ASCII, but move the family emoji onto the failing line ahead of the error and `ErrorCharColumn` jumps by its 8 code units while `ErrorTokenColumn` moves by 1. The default error message reports the grapheme column because, to a person, "column 6" should be the 6th character they see.

Most of the time you won't care, because most input is ASCII and the two numbers are equal. But the moment a user pastes in an emoji, a flag, or a letter with a combining accent, the indices diverge, and "which one do I show in the error message" stops being a question you can ignore.

The same multi-unit story applies to every Symbol's SourceRange, not just to errors. If the input has the family emoji as a section name and we want to underline it, the two units give two different widths:

```CSharp
var result = config.Parse("[👨‍👩‍👧]\n");
var sectionName = result.Tree!.Find(section)!.Children[0];  // the "name" node
var range = sectionName.SourceRange!.Value;
int charWidth  = range.End.CharIndex  - range.Start.CharIndex;   // 8
int tokenWidth = range.End.TokenIndex - range.Start.TokenIndex;  // 1
```

Both are right. The right one to use is whichever your consumer counts in: chars to feed `string.Substring` or send a Language Server Protocol diagnostic, tokens to draw a `^` under each thing the user sees on screen.
