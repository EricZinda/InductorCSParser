- Regex → InductorCSParser grammar converter webpage

Single-page static converter that takes a regex and emits the equivalent
Rules.cs fluent expression. Blocked on a JavaScript port of InductorCSParser:
the converter should parse the input regex using a regex grammar written in
Rules.cs, not a hand-rolled JS parser.

	- Blocked on:
    - A JS port of InductorCSParser published in a way a plain <script> tag can
      consume (single inlined file, or ES module served alongside the page).
    - A regex-syntax grammar written in Rules.cs, living in a permanent location
      (not a test fixture). That grammar is independently useful as a non-trivial
      E2E example and doubles as the parser inside this tool.

	- Why defer: the point of this tool is to convince users that InductorCSParser
  replaces regex for their use case. Building it on a hand-rolled JS regex parser
  undercuts that pitch. Using the library to parse its own input language is
  the demo.

	- Scope: single index.html under tools/regex-to-grammar/. All JS inlined in
  <script> blocks, no external assets, no build step. Flavor dropdown: PEG-safe
  subset, JavaScript/ECMAScript, .NET Regex, PCRE/Perl. Output shape toggles
  between bare Rule expression, public static readonly field declaration, and
  full class wrapper. Cache-busting via no-cache meta tags, a visible version
  stamp in the footer, and single-file delivery (removes the JS-vs-HTML skew
  failure mode entirely).

	- Feature handling decisions (locked in up front):
    - Captures preserve data, not just match. (?<name>...) → .As("name");
      (...) → .As("group1"), .As("group2") numbered in source order;
      (?:...) → plain rule. Users convert regexes to parsers, not validators.
    - Lazy quantifier with a following rule → Not(nextRule)/AnyChar scanner
      idiom (the block-comment pattern from PassThroughTextTests.cs). Same
      captured span as lazy regex.
    - ASCII case-insensitive → LiteralIgnoreAsciiCase / expanded character class.
    - Unanchored patterns → ZeroOrMore(And(Not(target), AnyChar())) scanner wrap.
    - Word boundary \b → Peek/Not over the flavor-appropriate word-char RuneSet
      (ASCII for JS, Unicode for .NET/PCRE).
    - Direct translations for everything else: char classes, alternation,
      sequencing, greedy/possessive/atomic quantifiers (PEG is already
      possessive), anchors ^/$/\A/\z, dot, \p{L} Unicode categories.
    - Refuse with a specific error: backreferences (PEG is context-free,
      (\w+) \1 can't be expressed), lookbehind (Rules.cs exposes forward-only
      Peek/Not), lazy-at-end inside a capture group (no terminator to rewrite
      against; greedy fallback would capture the wrong span), case-insensitive
      flag on a pattern containing non-ASCII (related to 9lll; InductorCSParser
      case folding is ASCII-only).

	- Examples loaded by default: the six BacklogGrammar.cs regexes on the
  Performance branch (H1, H2, Bullet, HrRun, HrSpaced, Paragraph). Each acts as
  a self-test: the converter's output for them should match the hand-written
  rule bodies modulo whitespace and the Literal("##") vs two Char('#') choice.

	- Done when:
    - tools/regex-to-grammar/index.html exists as a single self-contained file.
    - Clicking each example button produces output matching the
      BacklogGrammar.cs rule body. For H1/H2/Bullet, captured text round-trips
      via result.Tree.Find(...).ToString() on a sample input.
    - Backreferences, lookbehind, lazy-at-end-in-capture, and non-ASCII
      case-insensitive each produce a specific refusal error in the page.
    - Version stamp visible in the footer; no-cache meta tags present in <head>.

	- Verification: manual, against the six-example corpus. The translator
  factoring out into a module is a natural follow-up if anyone adds a test
  suite later.

	- Related: 9lll-need-to-do-proper-case-folding.md — lifting the ASCII-only
  case folding restriction in InductorCSParser would also lift the refusal
  in this tool for /i on Unicode patterns.
