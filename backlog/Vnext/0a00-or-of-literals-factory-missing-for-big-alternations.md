# Or of literals factory missing, so a big word-list alternation takes three LINQ steps and a hand-rolled length sort

Found while rewriting btford/write-good in `E2ESamples/WriteGood/`. The natural shape of a write-good rule is a word or phrase list, alternated and matched case insensitively. The original is:

```js
var weasels = ['remarkably', 'few', 'very', /* 22 more */];
var re = new RegExp('\\b(' + weasels.join('|') + ')\\b', 'gi');
```

The natural rewrite in InductorParser is `Or` over `LiteralIgnoreAsciiCase` for each item. There's no factory that takes a list of strings and builds the alternation, so the grammar code reads:

```csharp
var weasels = WeaselList
    .OrderByDescending(weasel => weasel.Length)
    .Select(weasel => LiteralIgnoreAsciiCase(weasel))
    .ToArray();
FlagWeasel = And(Or(weasels), wordBoundary).As(nameof(FlagWeasel));
```

Three things wrong with that:

1. **Three LINQ steps plus a `.ToArray()`** to feed the `params Rule[]` of `Or`. Every grammar that wants this shape writes the same boilerplate.

2. **The `OrderByDescending` is critical and easy to forget.** PEG's `Or` is first-match, so if the list contains both "in" and "in accordance with", and "in" comes first, "in accordance with" never matches at all. The JS regex doesn't care about order: the engine finds the longest match anyway. A user porting a regex over to a grammar can lose hours to this if they don't already know the rule.

3. **For `tooWordy`'s 224-phrase list with internal spaces**, we needed a helper that builds an `And` of `LiteralIgnoreAsciiCase` + `OneOrMore(InlineWhitespace)` for each phrase, then alternates them all. Got written once as a private helper in `WriteGoodGrammar.cs`. Would benefit from being a library factory.

## Where it came up

`E2ESamples/WriteGood/Rewrite/WriteGoodGrammar.cs`. The five-rule rewrite uses this shape twice: once for the 25-word weasel list and once for the 224-phrase tooWordy list. The grammar has roughly 10 lines that would collapse to two given the right factory.

## Fix

Two factories. The first is the basic shape:

```csharp
// Match any of the given strings, case-sensitively (Literal) or case-
// insensitively under ASCII (LiteralIgnoreAsciiCase). Longest-first by
// length so the alternation behaves like a regex alternation: the longest
// candidate wins regardless of declaration order. Default FlattenType: Delete.
public static Rule OneOfLiteral(IEnumerable<string> values);
public static Rule OneOfLiteralIgnoreAsciiCase(IEnumerable<string> values);
```

Implementation note: sort the input by descending length once at construction, then build `Or(LiteralIgnoreAsciiCase(...), ...)`. The sort is what makes the factory better than "do it yourself."

The second factory handles the phrase-with-flexible-whitespace case write-good's tooWordy needs:

```csharp
// Like OneOfLiteralIgnoreAsciiCase, but each space in an input string
// is treated as "one or more whitespace tokens" (intra-line whitespace by
// default, or a caller-supplied rule). Internal whitespace in a phrase
// doesn't have to be a single space in the input.
public static Rule OneOfPhraseIgnoreAsciiCase(
    IEnumerable<string> phrases,
    Rule? whitespaceBetween = null);
```

That collapses the WriteGoodGrammar code to:

```csharp
FlagWeasel = And(
    OneOfLiteralIgnoreAsciiCase(WeaselList),
    wordBoundary).As(nameof(FlagWeasel));

FlagTooWordy = And(
    OneOfPhraseIgnoreAsciiCase(WordyList),
    wordBoundary).As(nameof(FlagTooWordy));
```

Two factories, two lines per rule, with the longest-first ordering trap handled by the factory instead of by every caller.
