# Korean numbers sample

A worked end-to-end example of taking a real regex-validated, linear-scan parser and rewriting it as a grammar. The target is `korean-numbers.js`, a small JavaScript library that converts Sino-Korean numeral expressions like `백이십삼만` or `12만8천` into integers. It's the kind of code people actually ship into Korean-language apps to handle voice input, OCR'd amounts, and free-text number fields.

This sample is the first non-Latin entry in `E2ESamples/`. Every keyword in the grammar is a Hangul syllable. Every test case in the upstream corpus contains Hangul, ASCII digits, or both. If the parser had a bug in how it handles non-ASCII literals, this is where it would show up.

## What's the original

`korean-numbers.js` (https://github.com/ohgyun/korean-numbers, commit master, MIT license) is a small JavaScript utility by ohgyun. The whole library is one 180-line file. Three exports:

- `parse(text)`: convert a Korean number expression to an integer.
- `parseMoney(text)`: strip a trailing `원` then `parse`.
- `parseCount(text)`: lookup a native Korean count word (`하나`, `둘`, `셋` ...) in a flat table.

The grammar `parse` accepts is the Sino-Korean numeral system: digits drawn from `일이삼사오육칠팔구` (1-9) or `0-9`, scales `십백천` (10, 100, 1000) and `만억` (10,000, 100,000,000). The algorithm regex-validates the input against `^[일이삼사오육칠팔구십백천만억0-9]+$`, then walks it left to right, buffering digits and flushing them when a scale character appears.

`Original/KoreanNumberParser.cs` is a hand-written C# port of that algorithm. Same character maps, same regex, same `parseInt(text) || 0` fallback for inputs the regex rejects. We wrote it as a port (not a copy) because the comparison we care about is "what does this look like in a typical .NET codebase, versus what does it look like as an InductorParser grammar." A line-for-line JavaScript-to-C# rewrite is the closest fair comparison for that.

## What the InductorParser version adds

The original parser tells the caller exactly one thing: a number. Invalid input silently becomes 0. `Rewrite/` keeps the same public API (`Parse`, `ParseMoney`, `ParseCount`) and adds:

1. A `FormatException` instead of a silent 0 when the input doesn't match the grammar. The exception's message includes line, column, and char index. Original behavior on `백a` is to return 0; the rewrite throws `line 1, column 2: ...`.
2. A `TryParse(string, out long, out KoreanNumberParseError)` so callers who want to distinguish "0 because the input said zero" from "0 because the input was garbage" can.
3. A walkable parse tree. `KoreanNumberGrammar.KoreanNumber.Parse(input).Tree` returns a tree of named Symbol nodes (`scaledTerm`, `digits`, `scale`), each carrying `SourceText` and `SourceRange`, so a number-input UI can highlight which scale section of `백이십삼만` produced which part of `1,230,000`.
4. Strict rejection of mid-string non-number content. The original's `parseInt` fallback silently accepts `-100` (returns -100, which a Korean numeral parser arguably shouldn't), `123abc` (returns 123, truncating), `0x10` (returns 0, hex-stripping silently). The rewrite rejects all three at a position.

## Side-by-side

Same input, both parsers.

```
백이십삼만
```

Original: `1230000`
Rewrite: `1230000`

```
백a
```

Original: `0` (regex rejected, parseInt failed, fallback to 0)
Rewrite: `line 1, column 2: expected one of: 만, 억, 천, 백, 십`

```
12만8천
```

Original: `128000`
Rewrite: `128000`, plus a two-node tree: `ScaledTerm(12, 만)` then `ScaledTerm(8, 천)`, each with its own `SourceRange`.

```
백 만
```

Original: `0` (regex rejected because of the space)
Rewrite: `line 1, column 2: expected one of: 만, 억, 천, 백, 십`

```
123abc
```

Original: `123` (regex rejected, parseInt silently truncated)
Rewrite: `line 1, column 4: expected one of: 만, 억, 천, 백, 십`

## Layout

```
E2ESamples/KoreanNumbers/
  README.md                          this file
  KoreanNumbers.csproj               one project, both parsers, the test corpus
  Original/
    KoreanNumberParser.cs            hand-written linear-scan C# port
  Rewrite/
    KoreanNumberGrammar.cs           InductorParser grammar (32 code lines)
    KoreanNumberParser.cs            grammar driver: Parse / TryParse / ParseMoney / ParseCount
  Tests/
    KoreanNumberTests.cs             upstream-corpus, reject-corpus, parse-tree shape
```

## Line counts (non-comment, non-blank)

```
                                      code lines
  Original/KoreanNumberParser.cs         108
  ----
  Rewrite/KoreanNumberGrammar.cs          32
  Rewrite/KoreanNumberParser.cs          117
  Rewrite total                          149
```

The 117-line `Rewrite/KoreanNumberParser.cs` is mostly not parsing code:

```
  ~44 lines   character-lookup tables (SmallUnitMap / MediumUnitMap /
              BigUnitMap / CountMap / MoneySuffix), the same tables the
              Original keeps; CountMap alone is ~30 lines and isn't
              grammar-related at all
   ~5 lines   KoreanNumberParseError record (the Original returns 0 on
              failure, no error type)
  ~30 lines   public surface (Parse / TryParse / ParseMoney / ParseCount)
   ~5 lines   ApplyScaledTerm helper (the upstream evaluation algorithm)
   ~5 lines   ParseDigitsValue helper (base-10 digit run -> long)
  ~20 lines   the TryParse body: drive KoreanNumber.Parse, walk the
              Symbol tree, accumulate the total
```

So the apples-to-apples "code that turns a string into a number" comparison is:

```
                                      code lines
  Original (regex + line scan)           108
  Rewrite (grammar + tree walk + evaluator)   60  (32 + ~30)
```

The rewrite is roughly half the size on that comparison, plus it adds positioned errors that the original doesn't offer at any line count. The Rewrite's remaining ~50 lines are the same character-lookup tables the Original carries (CountMap is the bulk, kept for parity with the Original's three-function public surface) plus the error type and the public entry points.

Downstream tools that want to highlight individual scale sections in the input can walk `result.Tree` from `KoreanNumberGrammar.KoreanNumber.Parse` directly. Each `scaledTerm` and `digits` node already carries `SourceText` and `SourceRange`. The InductorParser Symbol tree is the AST. The rewrite doesn't materialize a second typed-record layer because nothing in this sample uses one, and adding one later from the Symbol tree is a ~30-line job.

## Worked example

Input:

```
백이십삼만
```

The InductorParser parse produces this tree (each named node carries its own `SourceText` and `SourceRange`):

```
koreanNumber
  scaledTerm                    SourceText: 백
    scale                       SourceText: 백
  scaledTerm                    SourceText: 이십
    digits                      SourceText: 이
    scale                       SourceText: 십
  scaledTerm                    SourceText: 삼만
    digits                      SourceText: 삼
    scale                       SourceText: 만
```

`KoreanNumberParser.Parse("백이십삼만")` walks that tree and returns `1230000L`. The walk is one foreach over the top-level children, applying the upstream algorithm in `ApplyScaledTerm` as each `scaledTerm` is encountered: 백 contributes 100 (implicit one), 이십 contributes 20, then 만 acts as a group separator and scales `100 + 20 + 3 = 123` up to 1,230,000.

A consumer that wants more than the number (a breakdown view, a syntax-highlighting overlay) can use the Symbol tree directly:

```csharp
var result = KoreanNumberGrammar.KoreanNumber.Parse("백이십삼만");
foreach (var scaledTerm in result.Tree!.Children)
{
    var scale = scaledTerm.Find(KoreanNumberGrammar.Scale);
    var digits = scaledTerm.Find(KoreanNumberGrammar.Digits);
    // scale!.SourceText, scale.SourceRange, digits?.SourceText, ...
}
```

For an input the grammar rejects, like `백a`, the rewrite produces:

```
line 1, column 2: expected one of: 만, 억, 천, 백, 십
```

The Original parser returns `0` with no explanation, which is the friction this sample exists to surface.

The message wording is a little awkward, though: the user could legitimately have wanted to type a digit there (a continuation of the number, a trailing `일` ones). The grammar accepts either a scale or more digits at that position, but the deepest-failure tracker reports the inner Scale rule's `WithError` rather than the outer `OneOrMore(Or(...))`'s `WithError` that names both alternatives. See `backlog/z0af-witherror-on-or-rule-doesnt-fire-when-inner-branch-goes-deeper.md` for the underlying issue and the related new backlog items spun off from this sample.

## What we cut from the upstream API

`parseCount` is kept on the rewrite as a table lookup with the same vocabulary the upstream uses, because the count words (`하나`, `둘`, `셋` ...) are a completely separate naming system from the Sino-Korean digits the grammar handles. There is no grammar to write for `parseCount` — it's `Dictionary.TryGetValue` in both implementations. Keeping it on the rewrite preserves parity with the upstream public surface even though the grammar isn't involved.

`parseMoney` is a one-line wrapper around `parse` that strips a trailing `원`. Kept on both sides for parity.

## Friction this exercise surfaced

Filed as separate backlog items in `backlog/`:

- The `.As(name)` set-once rule prevents reusing one rule under two names. The library suggests "build a factory function that returns a fresh rule each call" in the error message, but a more ergonomic shape might be `Rule.WithName(name)` that returns a renamed clone, or having `.As` on an already-named rule clone the rule rather than throw.

## License and attribution

The upstream `korean-numbers` project is MIT-licensed (https://github.com/ohgyun/korean-numbers/blob/master/LICENSE). This sample contains no upstream code: `Original/KoreanNumberParser.cs` is an independent C# implementation of the same algorithm and character tables, written for ergonomics comparison. The grammar shape, the scale set, and the test inputs follow `korean-numbers.js` and `tests/test.js` at commit `master` (fetched 2026-05-13).
