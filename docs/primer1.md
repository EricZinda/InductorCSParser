# Inductor Parser Primer 1: Getting Started
Let's answer a top stackoverflow question, but use the Inductor Parser instead of Regex: [How can I match "anything up until this sequence of characters"?](https://stackoverflow.com/questions/7124778/)

To parse text using the Inductor Parser, you build up a set of rules that "consume" the text, in the order they're written. The set of rules is called a "grammar". More often than not it will read very close to the way you'd describe it in words. 

In this case:
```
"Anything"
"Until I hit this sequence of characters"
```
There are rules that consume text units, like `Token` (one user-perceived character), `Literal` (an sequence of those tokens, aka a string) and `Integer`. These are your basic building blocks. In this example, let's replace the second part with:

```
"Anything"
Literal("this sequence of characters") 
```
The `Literal("this sequence of characters")` will consume what we're looking for at the end. Now we need to describe "Anything" with rules so it consumes everything up until the end.

The rule in the parser that consumes literally any character is called `AnyToken()`, but will only consume one of them, so it isn't enough by itself.

The parser also has rules that consume a specific number of "something" you want, such as: `ZeroOrMore(rule)`, `AtLeast(n, rule)`, `BetweenInclusive(n, m, rule)`. These rules need to know what "something" you're counting, by giving it a rule. 

So lets start by combining the `ZeroOrMore` and `AnyToken` rules:
```
ZeroOrMore(AnyToken())
Literal("this sequence of characters")
```
This is close, but it won't work yet. Inductor Rules are always *greedy*, meaning they always consume as much as they can. So, `ZeroOrMore(AnyToken())` will consume literally any string, including the thing we want to stop on. For a parse to succeed, the parser must get through *all* the rules and this version never will. The `Literal` rule will never have anything left to consume.

We need the first part to consume all text *except* what the second part consumes. For that, we'll use `Not()`. Since rules are reusable, we can make this more readable by declaring the stop text up front and reusing it:

```CSharp
var target = Literal("this sequence of characters");
ZeroOrMore(And(Not(target), AnyToken()))
target
```
Instead of just consuming `AnyToken`, we now start by checking to see if it's `Not` what we want to end with. We glue those together with `And` which requires that all of the rules you pass it succeed, in the order they're given.  We have to put `Not` first for the same greedy reason: If `AnyToken()` was first it would consume all the characters before we ever get to `Not`.

But this won't actually compile, yet. The second and third lines aren't valid C#, we need to combine them and assign them to a variable. 

So, we'll join our rules together, using composite rules like `And` or `Or`. `And` requires *all* the rules you give it succeed, in order:
```
var target = Literal("this sequence of characters");
var example = And(ZeroOrMore(And(Not(target),
                               AnyToken())), 
                    target);
```
This will now compile. 

We've now defined a simple "grammar", which is just a set of rules that go together to parse something. To use it, we just call `.Parse()` on it:

```CSharp
var target = Literal("this sequence of characters");
var example = And(ZeroOrMore(And(Not(target),
                               AnyToken())), 
                    target);

var result = example.Parse("How can I match anything up until this sequence of characters");
if (!result.Success)
    throw new FormatException(result.ErrorMessage);
Console.WriteLine(result.ToString());
```
The output is (with one space at the end):

```
How can I match anything up until 
```
The output works like this: Every rule is able to create a `Symbol` object to represent it and what it found in the tree. Whether it does this or not is controlled by a property on the rule called `FlattenType` which says whether to:

- `FlattenType.Delete` it and what it found along with its children (i.e. remove it completely)
- `FlattenType.Flatten` (i.e. remove) that rule, but keeping its children and what they found
- `FlattenType.Preserve` that rule and all of its children and everything they found so it's available in the final tree

Many rules have their default set to `Flatten` or `Delete` since you usually don't want them. In our case, the only rule that was set to `Preserve` by default is `AnyToken` since that usually represents text the developer wants to capture.

So, when you call `ToString()` on the result of a parse, all the symbols left in the tree print out what they consumed. All that remained in our tree:

```CSharp
var target = Literal("this sequence of characters");
var example = And(ZeroOrMore(And(Not(target),
                               AnyToken())), 
                    target);

```
... were the `AnyToken()` Symbols, one for each token that was consumed.

`ToString()` gives you what the surviving tree says, not the verbatim section of input the rule covered. If you want the verbatim section (including the characters Delete'd rules consumed), use `symbol.SourceText` instead. It ignores FlattenType and returns all of the original source between the Symbol's start and end. 

To help with debugging, you can flip them all to `Preserve` with options on the `Parse()` method like this: 

```CSharp
var target = Literal("this sequence of characters");
var example = And(ZeroOrMore(And(Not(target),
                               AnyToken())), 
                    target);

var options = new ParseOptions { PreserveAllSymbols = true };
var result = example.Parse("How can I match anything up until this sequence of characters", options);
if (!result.Success)
    throw new FormatException(result.ErrorMessage);
Console.WriteLine(result.PrintTree());
```
`result.PrintTree()` walks the parse tree and prints each Symbol on its own line, indented by its depth. The output looks like this (how to decode it is described right after): 

```CSharp
And: "How can I match anything up until this sequence of characters"
  ZeroOrMore: "How can I match anything up until "
    And: "H"
      Not: ""
      'H'
    And: "o"
      Not: ""
      'o'
    [... 32 more And/Not/char triples, one per consumed token ...]
    And: " "
      Not: ""
      ' '
  Literal: "this sequence of characters"
```
First, each symbol is shown indented based on where in the tree it was, followed by ":" and what `ToString()` would return for it. This means the root node should always show the full document.

Next, `Token` just prints out its value without `Token` in front of it. This is why you see bare `'H'` and `'o'` in the output.

Note that `Not` doesn't actually consume anything so it has nothing to print out. It just ensures that whatever is inside it isn't coming up.

# What about Unicode?

Notice we've not even thought about Unicode anything so far. Let's try the same grammar with emoji in both the input *and* the text we're matching on:

```CSharp
var target = Literal("this 👨‍👩‍👧 sequence of characters");
var example = And(ZeroOrMore(And(Not(target),
                               AnyToken())), 
                    target);

var result = example.Parse("How can I match 👋🏽 anything up until this 👨‍👩‍👧 sequence of characters");
Console.WriteLine(result.ToString());
```

The output (with one space at the end):

```
How can I match 👋🏽 anything up until 
```

Two different multi-rune tokens are at work here. The waving hand 👋🏽 is a base emoji plus a skin-tone modifier (two runes, one token). The family 👨‍👩‍👧 is built from five runes joined by zero-width joiners (man, ZWJ, woman, ZWJ, girl) and takes eight UTF-16 code units to encode. The grammar didn't need to know any of that. `AnyToken()` asked for "one token" in the middle and got the waving hand as a single unit. `Literal(...)` walks the input the same way the rest of the grammar does, so the family emoji in the target text matched as one token too. The exact-match string and the input string are both read as a stream of user-perceived characters, and they line up.

The same thing works with accented letters typed as a base letter plus a combining mark, with regional-indicator flag pairs, and with combining-mark scripts like Devanagari or Thai. They all come through as one token each, both inside `AnyToken()` and inside `Literal(...)`.

If you want to define a character class that includes a multi-rune token (an emoji, say) alongside ordinary letter ranges, `TokenSet` accepts both:

```CSharp
// Letters of any script, plus the US flag emoji as a single token.
var letterOrUSFlag = OneOf(TokenSet.Letters | TokenSet.Runes("🇺🇸"));
```

`TokenSet.Runes(...)` adds whatever the runtime treats as one user-visible character to the set. Single runes go into the rune-range part. Multi-rune tokens like 🇺🇸 go into a separate multi-rune list. `OneOf` checks both halves on each token.

This matters because the most common Unicode bug in parsers is silently splitting a multi-rune token into pieces. A grammar that consumes "one rune" from 👨‍👩‍👧 and stops would leave six dangling runes for the next rule to trip over. The lexer avoids this by walking the input one user-perceived character at a time. If you want to look *inside* a token (to inspect combining marks individually, say) there's a `WithinToken(...)` helper. But for normal text processing, you don't have to think about any of this. The grammar above already does the right thing on emoji, accented letters, CJK text, and complex scripts.

For the bigger picture (normalization, line terminators beyond `\n`, position tracking in chars and tokens) see [UnicodeInternalsArchitecture.md](UnicodeInternalsArchitecture.md). For the surprises that *do* come up and how to handle them, see [UnicodeGotchas.md](UnicodeGotchas.md).
