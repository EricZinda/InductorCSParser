# RESP sample (custom rules ported from nom)

A worked example of taking two combinators someone actually shipped and
rewriting them with InductorParser, where the interesting part is a pair of
*custom rules* that the built-ins genuinely can't express. The originals are
nom's `length_data` and `length_count` (from its `multi` module): read a number
out of the input, then consume input driven by that number. The rewrite's
centerpiece is two user-defined `Rule` subclasses, `LengthDataRule` and
`LengthCountRule`, used to parse RESP, the framing protocol Redis uses.

One thing to be straight about up front: this parses the RESP *grammar* from a
decoded .NET string and counts lengths in UTF-16 code units. It's a faithful
demonstration of the length-prefix combinators and byte-for-byte correct for
ASCII / text payloads (commands and simple replies), but it is not a drop-in
Redis wire codec. Real RESP is a byte protocol whose bulk strings are
binary-safe and byte-counted, and arbitrary binary can't be handed in as a
string. The "What the InductorParser version adds" section spells out exactly
where it diverges.

## What's the original

nom (https://github.com/rust-bakery/nom) is a widely used Rust
parser-combinator library, MIT-licensed, Copyright (c) 2014-2019 Geoffroy
Couprie. Two of the combinators it ships in `src/multi/mod.rs` are
length-prefix parsers. Quoting the doc comments verbatim from upstream commit
`fcc9f16b31804448d5cc71f6474c883ac7cf5624`:

    length_data:  "Gets a number from the parser and returns a subslice of the
                   input of that size."
    length_count: "Gets a number from the first parser, then applies the second
                   parser that many times."

`Original/resp_nom_example.rs` is a representative RESP parser built on them,
written from nom's documented API (it's Rust, so it doesn't build in this .NET
solution, and it's kept for reference only). It parses the five RESP value
types, with `length_data` doing the bulk strings and `length_count` doing the
arrays.

## Why this needs a custom rule

These two combinators are *context-sensitive*: how much input the parser
consumes is decided at parse time by a number that's in the input. A RESP bulk
string is `$5\r\nhello\r\n`, where the `5` says the payload is five characters.
A RESP array is `*2\r\n...two elements...`, where the `2` says how many
elements follow. There's no fixed grammar built from `And` / `Or` / `OneOrMore`
/ `BetweenInclusive` that expresses "now match exactly N more things," because N
isn't known until the count is read. This is the textbook case that takes a
parser past what context-free combinators can do, and it's exactly why nom
ships these as primitives instead of leaving them to its other combinators. The
matching logic has to live below the combinator layer.

So `Rewrite/LengthPrefixedRules.cs` subclasses `Rule` (via a small shared base,
`LengthPrefixedRule`, that does the count read both rules need) and overrides
`TryParseRule`:

- `LengthDataRule` reads the count, then consumes exactly that many characters
  as one leaf. The payload can contain anything, including a CRLF, because the
  length tells the rule precisely where it ends.
- `LengthCountRule` reads the count, then runs an item rule exactly that many
  times and collects the results. It drives a single child rule a
  data-determined number of times, which no built-in repetition rule does
  (`OneOrMore` is greedy-until-failure, `Exactly(n, ...)` takes a compile-time
  constant, not a value read from the input).

Both are built entirely on the public + protected surface: `ParseChild`
runs the count, separator, and array item rules, each with its own
transaction (the count's and separator's symbols don't reach the tree, since
only the count's value matters), `lexer.Read()` advances the cursor one grapheme cluster at a time,
`lexer.TickBudget()` bounds every loop so the Timeout / Cancellation /
RuleCountLimit budget can observe the work, and `lexer.RecordCompositeFailure(...)`
anchors the failure. No internal members.

## What the InductorParser version adds

nom returns the count parser's *value* (a `u64`). The InductorParser analog of
a rule's value is its Symbol's text, so the rewrite reads the count rule's
`ToString()` and parses a non-negative integer out of it. `ToString()` is the
text of the leaves that reached the tree, so a count rule can `Delete` framing
it doesn't want counted (a sigil, a radix marker) and still hand back a clean
number. That keeps the same decoupling nom has between the count's value and the
bytes it consumed, and it means the rule never has to snapshot positions and
`Substring` the raw input to recover the number.

The rewrite also adds positioned, named errors: a truncated payload, a
length/CRLF mismatch, an array short of its count, and a bad type byte each come
back as a `(charIndex, line, column)` triple with a message pointing at the
problem. And it handles the two RESP null forms (`$-1\r\n`, `*-1\r\n`) that the
reference program leaves out, as a plain `Or` branch ahead of the
length-bearing one.

One divergence is worth stating up front, because it's a genuine behavior change
and not just a different counting unit. nom's `length_data` splits the input
freely at the count boundary: on byte input it takes exactly N bytes, on a
string it takes exactly N code points, cutting through a grapheme cluster
without complaint. The rewrite can't do that, and the reason is the lexer
surface rather than a choice. The only way a rule advances the cursor is
`lexer.Read()`, whose smallest step is one grapheme cluster (one rune in the
rune-mode sub-lexer), and it never stops mid-cluster. But the `length` these
rules
count against is in UTF-16 code units (C# chars), a finer unit than `Read` can
land on. So a length pointing inside a multi-char grapheme (a surrogate pair, a
combining sequence, a flag emoji) names an offset `Read` can't reach, and the
rule rejects it where nom would have returned a result. There's no public API to
advance the cursor by a raw char count, so it can't be worked around at the rule
level. This is the friction filed in the backlog item "No primitive to advance
the cursor by a fixed code-unit (or byte) count". For ASCII payloads, the common
case for these text protocols, none of it bites: one char per rune per grapheme,
and byte / code-unit / code-point counts all agree.

The grammar compiles with normalization disabled (`Resp.Compile(null)`) so a
bulk-string length stays an exact count of input characters and every reported
position is an exact offset into the user's input.

## Side-by-side

Same inputs, both parsers.

```
+OK\r\n              -> SimpleString "OK"
:1000\r\n            -> Integer 1000
$5\r\nhello\r\n      -> BulkString "hello"
$6\r\nab\r\ncd\r\n   -> BulkString "ab\r\ncd"   (the CRLF is payload, not a terminator)
*2\r\n:1\r\n:2\r\n   -> Array [Integer 1, Integer 2]
```

The fourth line is the headline: a bulk string whose payload contains a CRLF.
The only reason it parses is that the `6` tells the rule to read six characters
no matter what they are. Strip the length and no grammar can find the end.

For bad inputs the rewrite reports a position (line and column count the
protocol's own CRLFs, so a payload error lands on a later line):

```
$5\r\nhi\r\n          line 3, column 1: bulk string payload doesn't match its declared length
$2\r\nhello\r\n       line 2, column 3: expected CRLF after the bulk string payload
?nope\r\n             line 1, column 1: expected a RESP value starting with one of + - : $ *
```

## Layout

```
E2ESamples/Resp/
  README.md                      this file
  Resp.csproj                    one project, the rewrite plus its tests
  Original/
    resp_nom_example.rs          the nom reference program, reference-only
  Rewrite/
    LengthPrefixedRules.cs       the two custom Rules (the ports of nom's combinators)
    RespParser.cs                the grammar glue + the RespValue projection
  Tests/
    RespTests.cs                 golden inputs, reject corpus, custom-rule unit tests
```

`Resp.csproj` is wired into `InductorParser.sln`, so `dotnet build` at the repo
root builds it, and `dotnet test E2ESamples/Resp/Resp.csproj` runs the corpus.

## Lines of code

|                                       | Lines |
| ------------------------------------- | ----- |
| Original/resp_nom_example.rs          |    97 |
| Rewrite/LengthPrefixedRules.cs        |   304 |
| Rewrite/RespParser.cs                 |   174 |

The raw totals aren't apples to apples. The nom `Original` is 97 lines counting
its header comment. The actual program (the `Resp` enum and the five parsers) is
about 50 lines, and `length_data` / `length_count` ship inside the library, so a
nom user writes zero lines for them.

`LengthPrefixedRules.cs` is 304 lines, well over half comment: the attribution,
the "why context-free built-ins won't do" rationale, the consume-by-grapheme
divergence from nom, the count-via-ToString rule, and inline notes on the
two loops. The matching logic across the shared base and both rules is roughly
110 lines. That's the cost of the two combinators nom gives its users
for free, written once.
`RespParser.cs` (the grammar glue and the typed-AST projection) is comparable in
size to the nom program once you discount its header.

## Worked example

```csharp
RespValue value = RespParser.Parse("*2\r\n$4\r\nLLEN\r\n$6\r\nmylist\r\n");
// value == new RespArray(new RespValue[] {
//     new RespBulkString("LLEN"),
//     new RespBulkString("mylist"),
// })
```

That's the exact byte sequence a Redis client sends for the command
`LLEN mylist`. The parse tree is `resp` -> `array` -> [the two `bulkString`
nodes], with the type bytes, the lengths, and the CRLFs all deleted. Each
`bulkData` leaf carries exactly the characters its length declared.

```csharp
RespParser.TryParse("$2\r\nhello\r\n", out _, out var error);
// error.ToString() == "line 2, column 3: expected CRLF after the bulk string payload"
```

The `2` makes the payload `"he"`, so the trailing-CRLF check lands on `"llo"`
and reports where the mismatch is.

## What this exercise found

The length-prefix shape is a genuine gap, the same kind the permutation sample
turned up. There's no built-in that consumes a data-determined amount of input,
and the matching has to live below the combinator layer in a `Rule` subclass.
The friction is filed as backlog items:

- "No length-prefixed combinators for count-driven matching" proposes
  `LengthData(...)` and `LengthCount(...)` factories so grammars that parse
  length-prefixed formats (RESP, netstrings, bencode, most binary-ish wire
  protocols) don't each reinvent these rules.
- "No primitive to advance the cursor by a fixed code-unit (or byte) count" is
  the deeper gap underneath them. A rule advances only through `lexer.Read()`
  (whole graphemes, or runes in rune mode), but the count is in code units, so a
  length landing inside a grapheme can't be reached and the rule has to reject
  it rather than split. A byte- or code-point-faithful port of nom's
  `length_data` isn't reachable at the rule level without a lexer method that
  advances by a fixed count.

Nothing in the custom-rule surface fought back beyond that. `ParseChild`'s
per-item transaction gave each array element a clean "try, fail, roll back"
on its own, `lexer.Read()` plus a `TickBudget()` loop matched how the built-in
bulk scanners consume input, and `RecordCompositeFailure` put the failure where
a user would look. The `Rule` base class handled the outer transaction, the
budget, and the Delete/Flatten/Preserve normalization without extra work.

## Running it

    dotnet build E2ESamples/Resp/Resp.csproj
    dotnet test  E2ESamples/Resp/Resp.csproj

32 tests in three fixtures. The golden fixture parses one value of every RESP
type, including a bulk string with an embedded CRLF and nested arrays. The
reject fixture asserts the right position for a truncated payload, a length
mismatch, a short array, a bad type byte, trailing data, and empty input. The
custom-rule fixture exercises `LengthDataRule` and `LengthCountRule` directly on
a netstring-style shape, independent of the RESP grammar, including the
length-splits-a-character rejection and a count rule that Deletes its framing.
