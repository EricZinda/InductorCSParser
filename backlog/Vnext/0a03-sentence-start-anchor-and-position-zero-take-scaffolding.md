# "Fires only at the start of a sentence" and "fires only at position 0" take scaffolding to express in a grammar

Found while rewriting btford/write-good in `E2ESamples/WriteGood/`. Two of the five rules in scope (starts-with-so and there-is) fire only at the start of a sentence. The original splits the input into sentences with one regex and then prefix-matches each sentence body with another:

```js
// lib/starts-with-so.js
const re = /([^\n.;!?]+)([.;!?]+)/gi;
const startsWithSoRegex = /^(\s)*so\b[\s\S]/i;
while (match = re.exec(text)) {
  if (innerMatch = startsWithSoRegex.exec(match[1])) {
    suggestions.push({ index: match.index + (innerMatch[1] || '').length, offset: 2 });
  }
}
```

That's straightforward: "find me each sentence" is one regex, and "is this string at a sentence start?" is the other. In a grammar, "fire only at the start of a sentence" has to be expressed by *where the rule sits* in the grammar.

The rewrite landed on this shape:

```csharp
var sentenceBoundary = And(
    OneOf(".;!?"),
    Optional(AnyWhitespace()),
    Optional(Or(FlagSo, FlagThereIs)));

var initialPrefix = And(
    Optional(AnyWhitespace()),
    Optional(Or(FlagSo, FlagThereIs)));

Prose = And(initialPrefix, ZeroOrMore(Or(
    sentenceBoundary,
    /* ... other rules ... */)));
```

Three things are friction here:

1. **The `initialPrefix` rule exists only because there's no way to say "this rule must be at input position 0".** Every other sentence boundary has a terminator before it. The very first sentence doesn't, so the grammar carries a separate rule whose only job is "if we're at position 0, try the prefix flags before falling into the loop." Five lines for a positional anchor.

2. **The same `Or(FlagSo, FlagThereIs)` group appears in both places.** Anything added to it (a future "starts with however" rule, say) has to be added to both. The duplication isn't extracted because there's no obvious shape for "this thing fires at sentence boundaries" as a named rule. It's defined in two places.

3. **"Sentence boundary" itself is reinvented from scratch.** No standard library helper for "terminator + whitespace, plus the start of input." Every grammar that wants to talk about sentences will spell this out the same way.

## Where it came up

`E2ESamples/WriteGood/Rewrite/WriteGoodGrammar.cs`, the `sentenceBoundary` / `initialPrefix` / `Prose` block near the bottom. The shape is on the simple end of what a sentence-aware grammar can need (write-good doesn't deal with abbreviations, quote-nested sentences, etc), but even this minimum took three rule definitions to express.

## Fix

Two additions, in roughly increasing scope:

1. **A `StartOfInput()` zero-width assertion that succeeds only at position 0.** This is the position-zero counterpart to `Eof()`. With this, the initialPrefix scaffolding collapses:

   ```csharp
   var atSentenceStart = Or(
       StartOfInput(),
       And(OneOf(".;!?"), Optional(AnyWhitespace())));

   var FlagSo = And(
       atSentenceStart,
       LiteralIgnoreAsciiCase("so"),
       /* ... */).As(nameof(FlagSo));
   ```

   One rule, "fires at sentence start", reusable across rules that share the anchoring. The duplication in (2) above goes away because `atSentenceStart` is captured once.

   The implementation is straightforward: a rule whose `TryParseRule` succeeds iff the lexer's current position equals zero. Zero-width like `Eof` and `Peek`. Probably one new file, fewer than 50 lines.

2. **A library `SentenceBoundary()` helper.** Even smaller scope: just bundle "terminator + whitespace, or start of input" as a named factory so every grammar doesn't reinvent it. Built on top of `StartOfInput()` from (1).

   ```csharp
   // Match a "start of sentence" position: either the start of input, or
   // immediately after a sentence terminator (".;!?") and any whitespace.
   // Zero-width if at start of input, otherwise consumes the terminator and following
   // whitespace otherwise.
   public static Rule SentenceBoundary();
   ```

(1) is the important addition. (2) is the convenience layer that follows from it. Together they collapse the WriteGood scaffolding from 9 lines to 2.
