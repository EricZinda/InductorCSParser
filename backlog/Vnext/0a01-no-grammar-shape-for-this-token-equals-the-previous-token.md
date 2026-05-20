# No grammar shape for "this token equals the previous token", so checks like lexical illusions have to be post-parse

Found while rewriting btford/write-good in `E2ESamples/WriteGood/`. One of the five rules in scope is lexical illusions: a word that equals the immediately preceding word ("the the"). The original is six lines of state and a regex tokenizer:

```js
// lib/lexical-illusions.js
const re = /(\s*)([^\s]+)/gi;
const word = /\w+/;
while (match = re.exec(text)) {
  if (word.test(match[2]) && match[2].toLowerCase() === lastMatch) {
    suggestions.push({ index: match.index + match[1].length, offset: match[2].length });
  }
  lastMatch = match[2].toLowerCase();
}
```

The check is "current word equals previous word, case insensitive." There's no way to express that in a PEG grammar in InductorParser. The rule that matches the second word would need to refer to the text of the first word, which is a back-reference. PEG doesn't have back-references, and InductorParser doesn't have semantic predicates that could let the user write the check by hand.

The rewrite handles it by *not* trying to express it in the grammar. `WriteGoodChecker.FindLexicalIllusions` is a raw-text scan in the same shape as the original. The grammar pass and the illusion pass run independently and merge at the dispatcher.

That works for write-good, but it's worth filing as friction because the "post-parse scan that produces suggestions, then merge with grammar output" pattern is *not* a natural shape in InductorParser's mental model. Every other rule produces a tree node. This one produces suggestions out of band, and the dispatcher carries the merge logic on its own.

## Where it came up

`E2ESamples/WriteGood/Rewrite/WriteGoodChecker.cs:69-92`, `FindLexicalIllusions`. It's the only place in the rewrite that runs a regex, and the grammar handles the other four rules. The asymmetry shows in code review: a reader has to know that one of the five rules isn't in the grammar at all.

## What would help

This isn't a single API gap. It's a shape question, and three options sit at increasing scope:

1. **A `Captured(rule, callback)` rule** that runs a callback against the matched span. The callback can compare against earlier-captured spans (passed in via a context object the grammar carries through), and return success/fail to drive backtracking. This is the semantic-predicate option. It's powerful and would let illusion be a grammar rule, but it adds a new shape (executable code in grammar definitions) that the framework doesn't have today.

2. **A documented "post-parse scan" extension point.** Add a small helper or pattern that says "here's the right way to wire a post-parse pass that produces suggestions and merges with grammar output." Today the WriteGood rewrite has to invent this from scratch. A documented shape would let future rewrites copy it.

3. **Accept the asymmetry and document it as expected.** "If your check needs to compare two tokens by value, that's a post-parse pass, not a grammar rule." If the framework's design intent is "grammars don't have value-level predicates," then the friction is just clarity: a paragraph in the docs explaining when to use a post-parse pass and why, with the illusion case as the example.

The lowest-cost option is (3). The most-useful option for users who hit this is (1). (2) is in between: doesn't add a new shape, but gives users a recipe.

For the write-good rewrite, the post-parse pattern in `WriteGoodChecker.cs` is what the recipe would look like. It can be lifted into a doc-page example.
