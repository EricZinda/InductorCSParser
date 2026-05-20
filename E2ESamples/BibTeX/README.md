# BibTeX sample (Unicode citation keys)

A worked end-to-end example of taking a real character-scanning, regex-anchored BibTeX parser and rewriting it as a grammar. The target is the entry shape used by `bibtex-parser` (JS) and `pybtex` (Python), the two most-installed BibTeX parsers outside of TeX itself. BibTeX is the canonical bibliography format for academic publishing, used by LaTeX, Pandoc, Hugo, Quarto, Zotero, JabRef, and pretty much every reference manager.

This sample is the third sample with non-Latin text in `E2ESamples/`, after KoreanNumbers (Hangul syllables enumerated by the grammar) and the JSON / TOML samples (Unicode in string values only). Where those samples either enumerate specific Unicode characters or treat Unicode as opaque bytes, BibTeX needs to detect Unicode identifier characters in citation keys, because real bibliographies routinely cite authors named `gärtner`, `müller`, `张`, `καραμβάρης`, or `горбачёв`, and biber (the modern BibLaTeX backend) accepts those keys verbatim. A naive port that uses `/[A-Za-z_][A-Za-z0-9_:.\-+/]*/` for citation keys silently breaks on every paper with a Unicode-named lead author.

## What's the original

`bibtex-parser` (https://github.com/digitalheir/bibtex-js-parser, MIT) is a small JavaScript BibTeX parser by Maarten Trompper. `pybtex` (https://pybtex.org, MIT) is the equivalent Python project by Andrey Golovizin. Both take the same general shape: a character scanner that advances a position cursor, a regex matched against the cursor for entry headers and citation keys, and a recursive function for balanced braces. Both target the same .bib subset: `@type{key, field = value, ...}` entries with quoted, braced, or integer values.

`Original/BibTexParser.cs` is a hand-written C# port of that shape. Cursor-based scanner, regex-driven entry detection, recursive `ParseBracedValue` for nested braces, public `Parse` returning a list of `BibTexEntry` records. We wrote it as a clean port (not a literal transliteration) because the comparison we care about is "what does this look like written normally in C#, versus what does it look like as an InductorParser grammar." A line-for-line transliteration of the JS or Python source isn't the closest fair comparison for that.

The citation-key shape is the part that matters for the Unicode angle. Both upstreams use ASCII-only regex for citation keys (`/^[a-zA-Z_][a-zA-Z0-9_:.\-+/]*/` and variants), and the C# port carries the same restriction so the comparison stays honest. Some modern forks of bibtex-parser have started adding `\p{L}` to the citation-key regex, but the broad ecosystem still hasn't. The shape of the friction this exposes is what the rewrite fixes.

## What the InductorParser version adds

The Original parser raises `BibTexParseException` on grammar mismatches with a char-index `position`. `Rewrite/` keeps the same public shape (`BibTexReader.Parse(input)` returns the same `BibTexEntry` records) and adds:

1. **Unicode citation keys** via `Rules.Identifier(extraStartRunes: TokenSet.Runes("_"), extraBodyRunes: TokenSet.Runes("_:.-+/"))`. The UAX #31 XID_Start / XID_Continue properties pick up Latin with diacritics (`gärtner2020`), CJK (`张2019`), Greek (`καραμβάρης1998`), and Cyrillic (`горбачёв1985`) without any special-casing. The grammar isn't doing anything special for Unicode. It's just using the right factory.
2. **Line and column on every error**, not just a char index. The Original reports `position 42`. The rewrite reports `line 3, column 8`.
3. **A walkable parse tree.** `BibTexGrammar.Document.Parse(...)` returns a `ParseResult` whose top-level Symbols include named nodes (`entry`, `entryType`, `citationKey`, `field`, `fieldName`, `quotedValue`, `bracedValue`, `integerValue`). Every node exposes `SourceText` (verbatim) and `SourceRange` (start/end with line, column, char index), so a downstream feature (a bibliography editor, a duplicate-entry detector, a citation-key auto-completer) walks the existing tree instead of re-parsing.
4. **Position-anchored expectation messages.** A truncated `@article{x, author = "A"` returns `line 1, column 24: expected '}' to close entry`. The input `@article{x, author "A"}` returns `line 1, column 20: expected '=' after field name`. The input `@article{x author = "A"}` returns `line 1, column 11: expected ',' after citation key`.

## Side-by-side

Same input, both parsers.

```bibtex
@article{einstein1905, author = "Albert Einstein", year = 1905}
```

Original: 1 entry, type `article`, key `einstein1905`
Rewrite: 1 entry, type `article`, key `einstein1905`

```bibtex
@book{knuth1984, title = {The {\TeX}book}, year = 1984}
```

Original: 1 entry, title = `The {\TeX}book` (nested braces preserved)
Rewrite: 1 entry, title = `The {\TeX}book` (nested braces preserved)

```bibtex
@article{gärtner2020, author = "Anna Gärtner", year = 2020}
```

Original: `BibTexParseException: position 9: expected a citation key`
Rewrite: 1 entry, type `article`, key `gärtner2020`

```bibtex
@book{张2019, title = "解析入门", year = 2019}
```

Original: `BibTexParseException: position 6: expected a citation key`
Rewrite: 1 entry, type `book`, key `张2019`, title = `解析入门`

```bibtex
@article{x, author = "A"
```

Original: `BibTexParseException: position 24: expected ',' or '}' after field value`
Rewrite: `line 1, column 25: expected '}' to close entry`

```bibtex
@article{x, author "A"}
```

Original: `BibTexParseException: position 19: expected '=' after field name`
Rewrite: `line 1, column 20: expected '=' after field name`

## Layout

```
E2ESamples/BibTeX/
  README.md                          this file
  BibTeX.csproj                      one project, both parsers, the test corpus
  Original/
    BibTexParser.cs                  cursor-based scanner + regex-driven recognizer
  Rewrite/
    BibTexGrammar.cs                 InductorParser grammar
    BibTexReader.cs                  thin tree walker that yields the same records
  Tests/
    BibTexTests.cs                   side-by-side small cases + Unicode + error positions
    XamplCorpusTests.cs              both parsers vs. xampl.bib, the canonical corpus
    Fixtures/
      xampl.bib                      Oren Patashnik's standard test file (public domain),
                                     bundled with every TeX distribution since 1985
```

## Line counts (non-comment, non-blank)

Counting code lines only (no header comments, no blank lines):

```
                                            code lines
  Original/BibTexParser.cs                       ~245
    of which: regex + scanner + parser           ~195
              braced/quoted scanner               ~40
              preamble + string + concat          ~30
              exception + record types            ~5
  ----
  Rewrite/BibTexGrammar.cs                       ~145
  Rewrite/BibTexReader.cs                         ~95
  Rewrite total                                  ~240
```

The apples-to-apples comparison is "code that turns a string into a list of entries":

```
                                            code lines
  Original (cursor + regex + recursive braces)   ~240
  Rewrite (grammar + tree walker)                ~240
```

The two sides land at roughly the same size on this comparison once both handle the full xampl.bib feature set. The rewrite still picks up Unicode-correct citation keys, line and column on errors, and a walkable tree with verbatim source text on every node, for the same line budget. The grammar is more declarative than the cursor-based scanner. The tree walker is bigger than the bare value extractor would be because it has to dispatch across three entry shapes and four value-part shapes, where the scanner's recursive descent routes that dispatch through the call graph.

## Worked example

Input:

```bibtex
@book{gärtner2020,
  author = "Anna Gärtner",
  title = {Datenbanken: {\LaTeX} und mehr},
  year = 2020
}
```

The grammar parses this into the following tree. Only `.As(name)` Preserve nodes are shown. The `And` / `Or` / `ZeroOrMore` rules around them are FlattenType.Flatten and dissolve at parse time:

```
entry                                  SourceText: @book{gärtner2020, ... }
  entryType                            SourceText: book
  citationKey                          SourceText: gärtner2020
  field                                SourceText: author = "Anna Gärtner"
    fieldName                          SourceText: author
    quotedValue                        SourceText: "Anna Gärtner"
  field                                SourceText: title = {Datenbanken: {\LaTeX} und mehr}
    fieldName                          SourceText: title
    bracedValue                        SourceText: {Datenbanken: {\LaTeX} und mehr}
  field                                SourceText: year = 2020
    fieldName                          SourceText: year
    integerValue                       SourceText: 2020
```

`BibTexReader.Parse(...)` walks that tree and returns one `BibTexEntry` with `EntryType = "book"`, `CitationKey = "gärtner2020"`, and three `BibTexField` records. The walk is `entrySymbol.FindAll(BibTexGrammar.Field)` then for each field, find the value child and read its text. The grammar already validated structure. The walker only handles extraction.

For an input the grammar rejects, like `@article{x, author "A"}` (missing `=`), the rewrite produces:

```
line 1, column 20: expected '=' after field name
```

The Original parser would say the same thing with a position number instead of a line+column. The story this sample exists to surface isn't the error-message wording (both parsers do fine there) but the citation-key Unicode handling.

## Corpus test

`Tests/XamplCorpusTests.cs` parses `Tests/Fixtures/xampl.bib` (Oren Patashnik's standard BibTeX test file, public domain) end-to-end with both parsers and asserts they agree on every entry, citation key, field name, and field value. The file has 40 entries: 1 `@preamble`, 3 `@string` macros, and 36 regular entries spanning all 13 standard BibTeX entry types. It exercises bareword field values (`month = jul`, `organization = ACM`), `#` string concatenation (`booktitle = "Proc. Fifteenth Annual ACM" # STOC`), TeX-escape sequences in author names (`{\"{U}}nderwood`, `T{\'{e}}rrific`), cross-reference fields (`crossref = "whole-journal"`), and stray prose between entries. Anything either parser breaks on shows up here as a corpus disagreement rather than as a silent gap in the small-case suite.

## What's still cut from the upstream

The braced-value escape model in this sample is simplified: `\X` consumes the next token regardless of what `X` is. Real BibTeX has TeX-defined escape sequences (`\"o` for ö, `\'e` for é, `\^{a}` for â, etc.) that a bibliography processor expands during rendering. We keep them as raw text on both sides, which is what every parser does at parse time. Rendering is downstream.

Macro expansion is also not in this port: `@string{ACM = "..."}` defines a macro, and a later `organization = ACM` field carries the bareword `ACM` verbatim through both parsers without substituting the macro's definition. Both parsers behave identically on this, and biber (the modern BibLaTeX backend) does the substitution as a separate post-parse pass anyway.

`@comment` is intentionally not handled as a special entry. The .bib format already treats any non-`@` text between entries as a comment, so an `@comment{...}` block parses either as a regular entry with no fields (if it has the right shape) or as part of the stray text. The standard tooling tolerates both readings.

## Friction this exercise surfaced

Three pieces of friction came up during the rewrite, and all three turned out to be by-design library behavior with the right idiom already available:

- `Symbol.ToString()` on a recursive braced-value rule drops the inner `Token('{')` / `Token('}')` (they default to FlattenType.Delete), so a nested-brace string like `{The {\TeX}book}` renders as `TheTeXbook`. The right tool is `Symbol.SourceText`, which returns the verbatim source range and ignores FlattenType. `primer1.md` and `primer2.md` now name `SourceText` alongside `ToString()` so a reader following the primers won't fall into this on their own grammar.
- The escape sequence `And(Token('\\'), AnyToken())` drops the backslash and keeps the trailing character in `ToString()`, again because `Token` defaults to Delete and `AnyToken` defaults to Preserve. Same fix: read `SourceText` off the wrapping value rule. Covered by the same primer additions.
- `.WithError(...)` mutates the receiving rule, so attaching an error message to a shared `Token(',')` instance leaks the message to every other reference. By design (the docs say WithError is per-instance and set-once), and the sample's one-line factory is the intended idiom. `clll-witherror-on-a-shared-rule-instance-throws-but-the.md` already tracks the sibling double-call diagnostic.

## License and attribution

Both upstream BibTeX parsers (`bibtex-parser` and `pybtex`) are MIT-licensed. This sample contains no upstream code: `Original/BibTexParser.cs` is an independent C# implementation of the same shape (cursor-based scanner with regex anchors and a recursive function for balanced braces) and the same BibTeX subset, written for ergonomics comparison. The grammar shape, the entry-type set, and the test inputs follow the entry format documented at https://www.bibtex.org/Format/ and the BibLaTeX manual (https://ctan.org/pkg/biblatex) for the Unicode citation-key handling. Test inputs include synthetic entries (Schrödinger's 1926 paper, Knuth's TeXbook) chosen for their well-known authors and titles. The exact `@article{...}` blocks aren't copied from any one upstream test fixture.

`Tests/Fixtures/xampl.bib` is copyright Oren Patashnik (1988, 2010), distributed with unlimited copying and redistribution rights for the unmodified file. The file ships with every TeX distribution. The CTAN copy lives at https://ctan.org/pkg/bibtex.
