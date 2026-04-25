Let's answer a top stackoverflow question, but use the Inductor Parser instead of Regex: [How can I match "anything up until this sequence of characters"?](https://stackoverflow.com/questions/7124778/)

To parse text using the Inductor Parser, you build up a set of rules that "consume" the text, in the order they are written. The set of rules is called a "grammar" More often than not it will read very close to the way you'd describe it in words. In this case:
```
"Anything"
"Until I hit this sequence of characters"
```
There are rules that consume characters, like `Token` (meaning a single human perceived character), `Literal` (a sequence of tokens), `Integer`. These are your basic building blocks. In this example, let's replace the second part with:

```
"Anything"
Literal("this sequence of characters") 
```
The `Literal("this sequence of characters")` will consume what we are looking for at the end. Now we need to describe "Anything" with rules so it consumes everything up until the end.

The parser has rules about how many of something you want, such as: `ZeroOrMore(rule)`, `AtLeast(n, rule)`, `BetweenInclusive(n, m, rule)`. These rules need to know what "something" you are counting, so you add a rule as an argument to tell it what to count. 

In this case, "Anything" can be represented as "zero or more of any token" (remember that a `Token` is just a character), so lets start by using the `ZeroOrMore` and `AnyToken` rules:
```
ZeroOrMore(AnyToken())
Literal("this sequence of characters")
```
This is close, but it won't work yet. Inductor Rules are always *greedy*, meaning they always consume as much as they can. So, `ZeroOrMore(AnyToken())` will consume literally any string, including thing thing we want to stop on. For a parse to succeed, the parser must get through *all* the rules and this version never will. The `Literal` rule will never have anything left to consume.

We need it to say "anything but *not* the stopping text", to leave that text for the last rule to consume. For that we'll use `not()`. Since rules are reusable, we can make this more readable by declaring the stop text up front and reusing it:

```CSharp
var target = Literal("this sequence of characters");
ZeroOrMore(And(Not(target), AnyToken()))
target
```
This won't actually compile, yet. To fix it, we need to join our rules together, using logical rules like `And`, `Or`, `Not`. Inductor Parser uses the rules in order, so `And` will match the first rule and then the second rule. Both must work to succeed (i.e. `and`):
```
var target = Literal("this sequence of characters");
var example = And(ZeroOrMore(And(Not(target), AnyToken())),
                  target);
```
This will now compile. We had to put `not` first in `And(Not(target), AnyToken())` for the same greedy reason. If `AnyToken()` was first it would consume all the characters before we ever got to `not` and all rules need to succeed for the parse to succeed.

This is a simple "grammar", which is just a set of rules that go together. To use it, we just call `.Parse()` on it:

```CSharp
var target = Literal("this sequence of characters");
var example = And(ZeroOrMore(And(Not(target), AnyToken())),
                  target);

var result = example.Parse("How can I match anything up until this sequence of characters");
if (!result.Success)
    throw new FormatException(result.ErrorMessage);
Console.WriteLine(result.ToString())
```
The output is (with one space at the end):

```
How can I match anything up until 
```
The output works like this: Every rule is able to create a `Symbol` object to represent what it found in the tree. Whether it does this or not is controlled by a property on the rule called `FlattenType` which says whether to:

- `FlattenType.Delete` the symbol along with its children
- `FlattenType.Flatten` the symbol by removing it, but keeping its children
- `FlattenType.Preserve` the symbol and all of its children

Many rules have their default set to `Flatten` or `Delete` since you usually don't want them. In our case the only rule that was set to `Preserve` by default is `AnyToken` since that usually represents text the developer wants to capture.

So, when you call `ToString()` on the result of a parse, all the symbols left in the tree print out what they consumed. All that was left in our tree:

```CSharp
var target = Literal("this sequence of characters");
var example = And(ZeroOrMore(And(Not(target), AnyToken())),
                  target);

```
... were the `AnyToken()` Symbols, one for each token that was consumed.

To help with debugging, you can flip them all to `Preserve` with options on the `Parse()` method like this: 

```CSharp
var target = Literal("this sequence of characters");
var example = And(ZeroOrMore(And(Not(target), AnyToken())),
                  target);

var options = new ParseOptions { PreserveAllSymbols = true };
var result = example.Parse("How can I match anything up until this sequence of characters", options);
if (!result.Success)
    throw new FormatException(result.ErrorMessage);
Console.WriteLine(result.ToString())
```
Then the output will show you all of the Symbols. How to decode this is described right after it:

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

Note that `Not` doesn't actually consume anything so it has nothing to print out. It just ensure that whatever inside it is not coming up.


 


, here are the most common ones. 

|                        |                |                    |
| ---------------------- | -------------- | ------------------ |
| And              | Float      | OptionalEndOfLine  |
| AnyToken         | Identifier | OptionalWhitespace |
| AtLeast          | Integer    | Or                 |
| AtMost           | Literal    | Peek               |
| BetweenInclusive | NoneOf     | StringBody         |
| EndOfLine        | Not        | Token              |
| EndOfLineOrEof   | OneOf      | Whitespace         |
| Eof              | OneOrMore  | ZeroOrMore         |
| Exactly          | Optional   |                    |

build something that confirms a password conforms to a set of rules (from [StackOverflow](https://stackoverflow.com/questions/19605150) ):

- contains at least eight characters
- including at least one number and
- includes both lower and uppercase letters and
- include at least one special characters, #, ?, !.
- cannot be your old password
- cannot contain your username, "password", or "websitename"

The Inductor Parser pattern matches against the characters in a .Net string value using a set of rules. It can "capture"