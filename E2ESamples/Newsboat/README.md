# Newsboat filter expression sample

A worked end-to-end example of taking a real filter-expression DSL embedded inside a real shipping app and rewriting it with InductorParser. The DSL lives inside Newsboat, the terminal RSS reader, and lets users write things like `title =~ "rust" and unread = "yes" and age < 7` to filter their feed.

## What's the original

Newsboat (https://github.com/newsboat/newsboat, MIT license) is a terminal RSS / Atom feed reader written in C++ with a Rust core. Inside it lives a small filter-expression DSL used for killfiles, query feeds, the `f` filter prompt, and the article-list filter. The grammar is small (12 comparison operators, two boolean connectives, parentheses for grouping), and the parser ships in two flavors:

- C++, Coco/R-generated from `filter/filter.atg` plus a hand-written `FilterParser.cpp` driver. Source: https://github.com/newsboat/newsboat/tree/master/filter
- Rust, hand-written `nom` combinator port at `rust/libnewsboat/src/filterparser.rs`

`Original/FilterParser.cs` is a clean hand-written recursive-descent C# port of the same grammar. We picked this shape for the comparison because parser-generator output (the C++) reads very differently from idiomatic application code, and `nom` combinator code (the Rust) is a different style from what a typical C# developer would write. A hand-written recursive descent in C# is what someone porting this DSL into a .NET feed reader would actually write, and it's what makes the side-by-side fair.

## What the InductorParser version adds

The Original parser tells the caller two things: did it match, and if it didn't, where in the input it gave up plus a single-sentence error. `Rewrite/` keeps the same public API (`Parse` / `TryParse`, the same AST shape) and adds:

1. Line and column on every error, not just a char index.
2. A walkable parse tree. Downstream tooling (a Newsboat config editor, a syntax highlighter, a query builder UI) can call `Symbol.Find(FilterGrammar.Comparison)` and operate on the tree without re-parsing. Every Symbol exposes its `SourceText` (verbatim original input) and `SourceRange` (start / end with line, column, char index), so highlighting one comparison in a chain is a one-line operation.
3. Deeper error positions. InductorParser tracks "where the parse got furthest before failing" rather than "where parsing stopped after backing out." For `x = 42andy=0` the upstream nom port reports position 6 (start of the trailing `andy=0`); the rewrite reports column 10 (the `y` that broke the space-after-`and` rule). The deeper position is closer to where the user has to put the cursor to fix it.

## Side-by-side

Same input, both parsers.

```
title =~ "rust" and unread = "yes" and age < 7
```

Original: `OK`
Rewrite: `OK`

```
title =¯ "foo"
```

Original: `position 7: expected one of: quoted string, range, number`
Rewrite: `line 1, column 8: expected one of: quoted string, range, number`

```
(((a="b")
```

Original: `position 9: expected ')'`
Rewrite: `line 1, column 10: expected ')'`

```
x = 42andy=0
```

Original: `position 6: trailing characters: andy=0`
Rewrite: `line 1, column 10: unexpected 'y'.`

```
AAAA between 0:15:30
```

Original: `position 17: trailing characters: :30`
Rewrite: `line 1, column 18: unexpected ':'.`

## Layout

```
E2ESamples/Newsboat/
  README.md              this file
  Newsboat.csproj        one project, both parsers, the test corpus
  Original/
    FilterParser.cs      hand-written recursive-descent C# port
  Rewrite/
    FilterGrammar.cs     InductorParser grammar (mirrors filter.atg)
    FilterParser.cs      typed AST builder + Parse / TryParse entry points
  Tests/
    FilterTests.cs       golden-corpus / reject-corpus / side-by-side tests
```

## Line counts (non-comment, non-blank)

```
                              code lines
  Original/FilterParser.cs       255
  ----
  Rewrite/FilterGrammar.cs        93
  Rewrite/FilterParser.cs        130
  Rewrite total                  223
```

The grammar by itself is 93 lines, of which ~30 are the rule body (the rest is the file's class scaffolding). The 130-line `FilterParser.cs` is the typed-AST walker; it's a "right-fold the flat children list into a right-associative tree" loop plus extractors for attribute / operator / value text. Together they're a bit shorter than the hand-written port, and they get positioned errors and a walkable parse tree without us writing code for either.

## Worked example

Input:

```
unread = "yes" and age < 7
```

The InductorParser parse produces this tree (each named node carries its own `SourceText` and `SourceRange`):

```
filter
  comparison              SourceText: unread = "yes"
    attribute             SourceText: unread
    operator              SourceText: =
    value                 SourceText: "yes"
      quotedString        SourceText: "yes"
        quotedStringBody  SourceText: yes
  and                     SourceText: and
  comparison              SourceText: age < 7
    attribute             SourceText: age
    operator              SourceText: <
    value                 SourceText: 7
      number              SourceText: 7
```

Which the AST builder collapses into:

```
RewriteAnd(
  RewriteComparison("unread", Equals,    "yes"),
  RewriteComparison("age",    LessThan,  "7"))
```

For an input like `unread = "yes` (missing closing quote), the rewrite produces:

```
line 1, column 10: unexpected end of input.
```

The Original parser produces:

```
position 9: expected one of: quoted string, range, number
```

Same position, same severity, slightly different framing.

## License and attribution

The newsboat project is MIT-licensed. This sample contains no upstream code: `Original/FilterParser.cs` is an independent C# implementation of the same grammar, written for ergonomics comparison. The grammar shape, the operator set, and the test inputs follow the upstream filter parser at `rust/libnewsboat/src/filterparser.rs` (commit master, fetched 2026-05-08).
