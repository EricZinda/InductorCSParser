The Inductor Parser (IP) is a loose port of the [Inductor C++ Parser](https://github.com/EricZinda/InductorParser), designed for C#.

If you write grammars in IP, they are Unicode safe from the start. Each token presented to a rule is a Unicode *Grapheme Cluster* which represents characters [*as the user perceives them*](https://www.unicode.org/reports/tr29/#Grapheme_Cluster_Boundaries). This means you don't have to wonder if your grammar will break or improperly slice apart characters if it encounters a file with non-ASCII characters or (gasp) *emojis* in it.  You can also pretend you never heard the word "Grapheme Cluster" and write rules naturally: it will still work right!

```CSharp
// Parse: Key = Value (e.g. Foo=5, Bar = 1.05, Goo =  AValue)
var settingName = Identifier().As("name");

var settingValue = Or(
    Float(),
    Integer(),
    Identifier()
).Flatten(FlattenType.Preserve).As("value");

var document = And(
    settingName,
    OptionalWhitespace(),
    Token('='),
    OptionalWhitespace(),
    settingValue,
    OptionalWhitespace(),
    Token(';')
);

var result = document.Parse("setting = 5;");
console.writeline(result) // name: "setting", value: "5"


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