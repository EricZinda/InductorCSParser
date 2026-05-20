# JSON string-escape sample (serde_json)

A worked end-to-end example of rewriting the string-escape decoder from a
real-world JSON parser, and then investigating how hard it is to make
InductorParser's errors as good as the original's.

The parser is the JSON string decoder from [serde_json](https://github.com/serde-rs/json),
the Rust JSON library. It's the code that turns a JSON string literal into
the actual string value, and it's the code that has to decide what to do
when the escapes are malformed.

This sample was built to answer the backlog item "do some appbuilding to
ensure it is natural to give good error messages." So the README leads with
that exercise, not with the rewrite.

## Why this parser

We went looking for real code that has to deal with **lone surrogates**, and
JSON string escapes are where that problem actually bites people. A JSON
`\uXXXX` escape can name any UTF-16 code unit, including the surrogate halves
`U+D800..U+DFFF`. A character outside the Basic Multilingual Plane is written
as two escapes, a leading surrogate then a trailing one. When a `\uXXXX`
names a surrogate that isn't part of a valid pair, you have a lone surrogate,
and every JSON parser has to decide what to do with it.

It's a genuine, shipped source of bugs. CVE-2022-31116 in the Python library
UltraJSON was exactly this: lone surrogates in escapes were decoded wrong,
which let one JSON object key silently overwrite another. serde_json, Glaze
(C++), and CPython have all had issues and back-and-forth about it.
serde_json's decoder rejects unpaired surrogates with a named, positioned
error, which makes it a good thing to measure against.

- Upstream: https://github.com/serde-rs/json, `src/read.rs` and
  `src/error.rs`, commit `5d30df60e916e9b8fc46c74794007ff271fdfbbf`
  (2026-05-18).
- License: serde_json is dual-licensed Apache-2.0 / MIT, both permissive.
- The verbatim section of `read.rs` we modelled the twin on is in
  `Original/upstream-serde-json-read.rs`, license header included, so a
  future reader can diff against upstream.

## Scope

A full JSON parser is out of scope, and JSON-the-format is already covered by
the parser's own benchmark and `E2EExamples/JsonGrammar.cs`. This sample is
just the **string production**: one quoted literal, opening quote through
closing quote, with all of JSON's escape handling.

- **Kept:** the simple escapes (`\" \\ \/ \b \f \n \r \t`), the `\uXXXX`
  escape, surrogate-pair validation, and serde_json's six string errors.
- **Dropped:** everything else in a JSON document (numbers, objects, arrays,
  `true`/`false`/`null`). The string body is the only place escapes and
  surrogates live.

`E2EExamples/StringLiteralGrammars.cs` already has a JSON string grammar, and
it's a useful starting point to compare against. It matches `\uXXXX`
*structurally* (a `u` and four hex digits) and does no surrogate validation
at all, so it quietly accepts a lone surrogate as if it were a valid
character. This sample is what it takes to go from that to serde_json-grade
diagnostics.

`Original/SerdeJsonStringParser.cs` is a clean, hand-written C# port of
serde_json's decoder, faithful to its control flow and to its `ErrorCode`
strings. It contains no upstream Rust code. We wrote an independent port for
the same reason the Pep508 and Newsboat samples did: a behavioral twin we can
run in the same solution and diff against, case for case. One faithfulness
note: serde_json scans UTF-8 *bytes* and the twin scans UTF-16 *chars*. Every
character in a `\u` escape is ASCII, so byte offset equals char offset for
everything the error exercise touches.

## The error-message exercise

This is the part the backlog item asked for. serde_json reports six distinct
errors while parsing a string. The exercise: write down a malformed input for
each, decide the position and the words a user would want, and see how
naturally the grammar reproduces them.

### Step 1: the six errors

serde_json's `ErrorCode` (from `src/error.rs`) has these six string errors:

```
EofWhileParsingString                EOF while parsing a string
ControlCharacterWhileParsingString   control character (U+0000-U+001F) found while parsing a string
InvalidEscape                        invalid escape
UnexpectedEndOfHexEscape             unexpected end of hex escape
LoneLeadingSurrogateInHexEscape      lone leading surrogate in hex escape
InvalidUnicodeCodePoint              invalid unicode code point
```

### Step 2: the natural-first grammar

The natural way to write a JSON string grammar in InductorParser is the one
already sitting in `E2EExamples/StringLiteralGrammars.cs`: a `ScanUntil` over
the body with a structural escape rule, `\u` plus four hex digits, and no
`.WithError` anywhere. That grammar is a fine recognizer. But two things go
wrong if you want serde_json's diagnostics out of it.

First, **it accepts lone surrogates.** An unpaired surrogate of any kind
matches the structural "`u` then four hex digits" rule. The natural grammar
doesn't reject it because nothing in it knows what a surrogate is. That's
the CVE-class bug, and it's invisible until you go looking.

Second, where the natural grammar *does* reject (an unterminated string, a
bad escape letter, a raw control character), the position it reports is
already right, because InductorParser reports its deepest failure and that's
a good pointer here, but every message is the mechanical fallback
"unexpected 'x'". A recognizer with a caret, not a diagnostic. This is the
same finding the Pep508 sample reached.

Two different gaps, then. The surrogate errors need new *rules* (the grammar
literally can't see the problem). The structural errors just need *messages*.

### Step 3: surrogate-aware rules

`Rewrite/JsonStringGrammar.cs` classifies a `\uXXXX` escape from its first two
hex digits, no arithmetic required:

```
D800..DBFF  leading (high) surrogate  -> 'D' then 8 9 A B
DC00..DFFF  trailing (low) surrogate  -> 'D' then C D E F
```

A leading surrogate is then *required* to be followed by `\u` and a trailing
surrogate. A trailing surrogate on its own, or a plain BMP escape that
quietly stands in for a lone leading surrogate, are both rejected. The
ordinary BMP-escape rule carries a `Not(...)` so it can't swallow a surrogate
the surrogate rules were supposed to catch. A cut operator would say this
directly, but InductorParser doesn't have one (filed as backlog `0000c`),
so the commit has to live in the structure of the `Not(...)`. Once a
leading surrogate is seen, no other branch will take it.

### Step 4: the messages, and where the friction was

With the rules in place, the structural errors got a `.WithError` each, the
way `docs/ErrorArchitecture.md` says to. Five of serde_json's six errors land
as grammar errors. Here is what the rewrite produces, against the twin:

```
input                    twin (serde_json)                  rewrite
unterminated string       char  4  EOF ...                   char  4  EOF while parsing a string ...
a raw TAB in the body     char  3  control character ...      char  2  control character (U+0000-U+001F) ...
an unknown escape \x      char  3  invalid escape             char  2  invalid escape sequence; expected ...
\u with two hex digits    char  6  EOF ...                    char  3  invalid \u escape; expected four hex ...
\u with non-hex digits    char  7  invalid escape             char  3  invalid \u escape; expected four hex ...
lone leading surrogate    char  8  unexpected end of hex ...  char  7  unexpected end of hex escape; ...
leading then non-surr.    char 13  lone leading surrogate ..  char  9  lone surrogate in hex escape; ...
leading then leading      char 13  lone leading surrogate ..  char 10  lone surrogate in hex escape; ...
lone trailing surrogate   char  7  lone leading surrogate ..  char  7  lone surrogate in hex escape; ...
bare surrogate code unit  (accepted)                          char  1  invalid unicode code point; ...
```

The positions differ by a few characters, on purpose. serde_json advances its
read cursor *before* recording an error, so several of its errors land one or
more characters past the offending character. The rewrite points *at* the
character. Both are defensible. The exercise only asks that the position be
right and the message point at the real problem, and the rewrite's caret is
arguably the better of the two every time it differs.

Three pieces of friction came up, each now a backlog item:

- **The sixth error couldn't be a grammar rule at all.**
  `InvalidUnicodeCodePoint` fires for a bare unpaired surrogate code unit
  sitting literally in the input (an actual surrogate char, not the six-char
  text `\uD800`). The grammar can't reject that, because the surrogate block
  `0xD800..0xDFFF` isn't expressible as a `TokenSet`: `TokenSet.Range`
  rejects surrogate endpoints and the `~` operator strips the surrogate
  block out. So `Rewrite/JsonStringParser.cs` handles it as a post-parse
  scan. Filed as backlog `0000a`.

- **There's no `Fail(message)` rule.** "A lone trailing surrogate is always
  an error here" needs a rule that just fails with a message. There isn't
  one, so the grammar builds `AlwaysFails` out of `Not(Optional(AnyToken()))`.
  It works and it's a one-liner, but it reads as a riddle. Filed as backlog
  `0000b`.

- **No cut operator, so a committed branch had to be locked in by structure.**
  Once the leading-surrogate-pair branch has matched `\uD8xx`, the parser is
  committed: a missing trailing surrogate is an error, not a fall-through to
  the plain BMP-escape branch. PEG's `Or` doesn't express commit, so the
  BMP-escape branch carries a `Not(D followed by 8-F)` in front to refuse
  anything that looks like a surrogate. The commit lives in two rules
  instead of one (the prefix in the surrogate-pair branch is mirrored as a
  `Not(...)` in the BMP branch), and any future change to the surrogate
  classification has to be reflected in both places. A `Cut()` after `\uD8xx`
  would put the commit in the surrogate-pair branch alone. Filed as backlog
  `0000c`.

A note on the `Peek(Exactly(4, hexDigit)).WithError(...)` in front of the
`\u` classification, in case it looks like a workaround. The four digits
are consumed by three different branches (surrogate-leading,
surrogate-trailing, plain BMP), so no single rule in the classification
owns the whole run, and the `Peek` is that missing owner. A grammar that
didn't split the run wouldn't need it: `Exactly(4, hexDigit).WithError(...)`
on its own positions an "expected four hex digits" message just fine.

### The mapping

How the rewrite's six errors line up with serde_json's:

```
EofWhileParsingString                the closing Token('"').WithError(...)
ControlCharacterWhileParsingString   Not(OneOf(controlCharacter)).WithError(...)
InvalidEscape                        the AlwaysFails(...) catch-all in Escape
UnexpectedEndOfHexEscape             the required \ and u of the trailing-surrogate escape
LoneLeadingSurrogateInHexEscape      trailingSurrogate's .WithError, and loneTrailingSurrogate
InvalidUnicodeCodePoint              post-parse FindUnpairedSurrogate (backlog 0000a)
```

serde_json reports both a lone *leading* and a lone *trailing* surrogate
under the one name `LoneLeadingSurrogateInHexEscape`, and its own source
comments call that name wrong ("XXX: This is actually a trailing
surrogate"). The rewrite splits them into two messages that each say which
half is missing, which is one place the rewrite's diagnostic is plainly
better than the original's.

## Line counts (non-comment, non-blank)

```
                                      code lines
  Original/SerdeJsonStringParser.cs       198
  ----
  Rewrite/JsonStringGrammar.cs             73
  Rewrite/JsonStringParser.cs             107
  Rewrite total                           180
```

About the same size, which is the honest result here, and a different shape.
Of the grammar's 73 lines, roughly 20 are the eight error-message strings
(each wrapped across two or three lines) and 3 are the `AlwaysFails` helper,
so the rule definitions proper are about 35 lines. The twin packs scanning,
surrogate validation, decoding, and error positioning into one imperative
loop. The rewrite splits validation (the grammar) from decoding (the parser),
and gets a walkable parse tree and four-unit error positions for free,
neither of which the twin has.

## Worked example

Input is a complete JSON string literal, quotes included:

```
"a\nb"
```

`JsonStringParser.TryParse` parses it and walks the tree:

```
JsonString
  Body
    text      SourceText: a
    escape    SourceText: \n
    text      SourceText: b
```

The body is a run of `text` and `escape` nodes in source order. A `\uXXXX`
escape is one `escape` node, and a surrogate pair (`𝄞`, the
two-escape spelling of U+1D11E) is also a single `escape` node, because
serde_json and this grammar both treat the two `\u` halves as one logical
unit. The decoded value here is the three characters `a`, U+000A, `b`.

For a bad input like `"\uD800"` (a leading surrogate with nothing to pair
with), the rewrite produces:

```
unexpected end of hex escape; a \uD800-\uDBFF leading surrogate must be followed immediately by a \uDC00-\uDFFF \u escape
    "\uD800"
           ^
```

The caret lands on char 7, the closing quote, the spot the parser reached
expecting the `\u` of a trailing surrogate.

## Layout

```
E2ESamples/JsonStringEscapes/
  README.md                      this file
  JsonStringEscapes.csproj        one project, both parsers, the tests
  Original/
    upstream-serde-json-read.rs   verbatim section of serde_json's read.rs
    SerdeJsonStringParser.cs      faithful C# port of serde_json's decoder
  Rewrite/
    JsonStringGrammar.cs          the InductorParser grammar
    JsonStringParser.cs           typed entry point: TryParse, the decoder
  Tests/
    JsonStringTests.cs            valid corpus, reject corpus, side-by-side
    ErrorMessageTests.cs          the ten-case error-message exercise
```

## License and attribution

serde_json is dual-licensed Apache-2.0 / MIT, both permissive.
`Original/upstream-serde-json-read.rs` is a verbatim excerpt of serde_json's
`src/read.rs` at commit `5d30df6`, kept for diffing, with the upstream
license noted in its header. `Original/SerdeJsonStringParser.cs` contains no
upstream code: it's an independent C# port of the same control flow and the
same `ErrorCode` strings, written for this ergonomics comparison.
