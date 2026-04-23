The Inductor Parser (IP) is a loose port of the [Inductor C++ Parser](https://github.com/EricZinda/InductorParser), designed for C#.

If you write grammars in IP, they are Unicode safe from the start. 

Each token presented to a rule is a Unicode *Grapheme Cluster* which represents characters [*as the user perceives them*](https://www.unicode.org/reports/tr29/#Grapheme_Cluster_Boundaries). This means you don't have to wonder if your grammar will break or improperly slice apart characters if it encounters a file with non-ASCII characters or (gasp) *emojis* in it.  

Default rules are smart about Unicode and use well thought through Unicode definitions for things like "whitespace" and "identifiers".

You can also pretend you never heard the word "Grapheme Cluster" and write rules naturally: it will still give you the right base to start from!

Here's a grammar for reading a simple setting, and examples that show how it handles classic Unicode edge cases.

```CSharp
// Parse: Key = Value (e.g. Foo=5, Bar = 1.05, Goo = "some string")
var settingName = Identifier().As("name");

var quotedString = And(
    Token('"'),
    StringChars(stoppers=RuneSet.Runes("\"")),
    Token('"'));

var settingValue = Or(
    Float(),
    Integer(),
    quotedString
).Flatten(FlattenType.Preserve).As("value");

var document = And(
    settingName,
    OptionalWhitespace(),
    Token('='),
    OptionalWhitespace(),
    settingValue
);

var result = document.Parse("setting = 5");
console.writeline(result) // name: "setting", value: "5"
// Identifier() follows UAX #31, so anything Unicode calls a letter works
document.Parse("Γειά = 5");    // name: "Γειά",    value: "5"
document.Parse("привет = 1");  // name: "привет",  value: "1"
document.Parse("你好 = 1");    // name: "你好",     value: "1"
// é written as e + U+0301 combining acute is two code points that form one user-perceived character. The parser accepts it and the name's flattened text reads as you'd expect:
document.Parse("café = 5");    // (é = e + U+0301) name: "café", value: "5"
//In a Devanagari example, each grapheme is a consonant joined to a virama or vowel sign, sometimes three or four runes long:
document.Parse("नमस्ते = 1;"); // name: "नमस्ते", value: "1"
// 𠮷 is U+20BB7, one rune but two UTF-16 chars. 
document.Parse("𠮷田 = 5;"); // name: "𠮷田", value: "5"
// OptionalWhitespace() matches the Unicode whitespace category, not just ASCII
document.Parse("setting\u00A0=\u00A05;");  // (non-breaking space)
document.Parse("setting\u3000=\u30005;");  // (ideographic space) name: "setting", value: "5"
// String values hold anything except the closing quote. 
// Mixed scripts, emoji, and multi-rune graphemes all pass through untouched
document.Parse("motto = \"你好 🎉 नमस्ते\";"); // name: "motto", value: "你好 🎉 नमस्ते"
document.Parse("motto = \"👨\u200D👩\u200D👧\";");  // (ZWJ family) name: "motto", value: "👨‍👩‍👧"
document.Parse("motto = \"🇺🇸\";");  // (regional-indicator flag) name: "motto", value: "🇺🇸"
// Emoji aren't in the UAX #31 identifier set, so the parser rejects them 
// the same way Python and Rust do:
document.Parse("setting🎉 = 5;"); // GrammarMismatch at char 7
```
Error positions are also designed for Unicode and reported in multiple units. When the input contains supplementary-plane letters, char index and rune index are different. When it contains multi-rune graphemes, rune index and grapheme index are different. This gives you the right tools for different jobs:

```CSharp
//
var result = document.Parse("𠮷田 = ;");
// ErrorCharIndex=6, ErrorRuneIndex=5, ErrorGraphemeIndex=5
// (each supplementary letter is two chars but one rune)

var result = document.Parse("नमस्ते = ;");
// ErrorCharIndex=9, ErrorRuneIndex=9, ErrorGraphemeIndex=7
// (Devanagari is BMP, so chars == runes, but four of the name's
//  six graphemes span two or three runes each)
```


The whole point of building this parser is to be able to replace hieroglyphic Regex patterns or complicated, hard to debug parsing code with something that more readable, debuggable and understandable. Especially as I'm doing more and more reviewing of code written by LLMs, I've found it invaluable to have an LLM write pattern matching and parsing code in a form that I can actually review for correctness. Compare:

Match a line that doesn't contain the word "hede" (From https://stackoverflow.com/q/406230): 

```re
Regex: ^((?!hede).)*$
```
```csharp
Inductor Parser:

var lineWithoutHede = 
    And(
        ZeroOrMore(And(
                       Not(Literal("hede")), 
                       AnyToken()
                      )),
        Eof()
    );
```

Match numbers only (From https://stackoverflow.com/q/273141)

```Re
Regex: ^\d+$
```
```CSharp
Inductor Parser:

var numbersOnly = And(
    OneOrMore(RuneIn(RuneSet.Digits)),
    Eof()
);

```

Regex expressions can sometimes introduce [denial-of-service attacks](https://en.wikipedia.org/wiki/ReDoS) (or just plain poor user experiences). Inductor Parser naturally avoids many of these patterns just by virtue of being a recursive descent parser and thus doesn't do backtracking. Further, it has 3 different stop modes to prevent runaway parses on unexpected or large documents.

As I've been building a variant of a text editor, I wanted to make sure the fundamentals were solid for quickly and safely parsing worldwide text on many platforms. The parser:

- **Is built for Unicode**. Its Lexer can choose between Graphemes (default) and Code Points. It defaults to Graphemes so that your parse natively sees characters that humans see in an editor (and that they expect to be treated as single characters)
- **Is designed to avoid "catastrophic backtracking"** and to have plenty of ways to avoid all the pitfalls like it that can hang your app, blow your stack, etc.
- **Supports .NET Standard 2.1 and later** and doesn't use Reflection.Emit or threads so that it can run in Unity targeting WebGL or IL2CPP on iPhone
- **Is fast enough to be used in production**


Simple to use and maybe more importantly to read
Simple, understandable, debuggable parsing algorithm
Unicode
Designed to avoid catastrphic backtracking
Fast enough to be used in production
Supports older .Net runtimes, support Unity and WebGL and IL2CPP on iPhone
    - Targets all the way down to .NET Standard 2.1
    - Doesn't use Reflection.Emit, threads

Scenarios:
Parsing
Regex Replacement
Clauding