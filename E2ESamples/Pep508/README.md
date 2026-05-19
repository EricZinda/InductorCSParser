# PEP 508 dependency-specifier sample

A worked end-to-end example of rewriting a real-world parser whose
authors cared a lot about error messages, and then investigating how
hard it is to make InductorParser's errors as good.

The parser is the PEP 508 dependency-specifier parser from Python's
`packaging` library. It's the thing that reads a line like

```
requests[security]>=2.8.1
```

out of a `requirements.txt` file or an `install_requires` list, every
time anyone runs `pip install`.

This sample was built to answer the backlog item "do some appbuilding
to ensure it is natural to give good error messages." So the README
leads with that exercise, not with the rewrite.

## Why this parser

We went looking for a parser where the original authors visibly cared
about error messages, because that's the bar we wanted to measure
against. `packaging` is a good pick. Its maintainers replaced an earlier
`pyparsing`-based parser with a hand-written tokenizer specifically to
produce better diagnostics, and the result is a parser that reports
every failure as a message, the source line, and a caret:

```
Expected package name at the start of dependency specifier
    @ git+https://github.com/pypa/packaging
    ^
```

That's the standard we're trying to hit.

- Upstream: https://github.com/pypa/packaging (the `_parser.py` and
  `_tokenizer.py` modules), main branch, fetched 2026-05-15.
- License: `packaging` is dual-licensed Apache-2.0 / BSD-2-Clause. Both
  are permissive. This sample contains no upstream code (see below).

## Scope

PEP 508 has two halves: the requirement (`name[extras](version spec) @
url`) and the environment marker (`; python_version < "3.8"`).

- **Kept:** the requirement core. Name, extras list, version specifier
  list (parenthesized or bare), and the `@ url` form.
- **Dropped:** environment markers. The marker grammar is a
  boolean-expression DSL (`and` / `or` / parentheses / comparison
  operators), and that shape is already the subject of the Newsboat
  filter sample next door. Re-doing it here wouldn't surface anything
  new.
- **Dropped:** PEP 440 version *validation*. Both `packaging` and this
  rewrite accept a loose version character run at the parser level
  (`>=2.8.1`, `==2.28.*`, `>=1.0+local` all tokenize fine). Whether the
  version is semantically well-formed, and the rule that a `.*` suffix
  only goes with `==` / `!=`, are post-parse concerns left out of both
  parsers here.

`Original/RequirementParser.cs` is a clean, hand-written C# port of
`packaging`'s tokenizer-based parser, faithful to its grammar and to its
error-message strings (including the caret rendering). It contains no
upstream code. We wrote an independent port for the same reason the
Newsboat sample did: a faithful behavioral twin we can run in the same
solution and diff against, line for line.

## The error-message exercise

This is the part the backlog item asked for. The process was: build the
grammar the most obvious way with no thought given to error messages,
write down what the errors *should* say, see how far the obvious grammar
gets on its own, then fix it.

### Step 1: the natural-first grammar

`Rewrite/RequirementGrammar.cs` was first written with zero `.WithError`
calls. Just the rules: `And`, `Or`, `ScanWhile`, `Optional`, `OneOf`,
`Token`, `Eof`. The ABNF at the top of that file translated almost
line-for-line into rules. That draft is in this file's git history.

### Step 2: write down the right errors

`Tests/ErrorMessageTests.cs` has 16 pieces of malformed input, each with
the char offset the caret should land on and the words the message
should contain. They were written against `packaging`'s behavior and
plain common sense, before any diagnostics went into the grammar.

### Step 3: run them against the natural grammar

Here's what the bare grammar produced. "pos" is the caret offset.

```
input                      natural grammar                       pos  ideal pos
""                         Unexpected end of input.                0    0  ok
==1.0                      unexpected '='.                         0    0  ok
.foo                       unexpected '.'.                         0    0  ok
requests<>1.0              unexpected '>'.                          9    9  ok
requests>=                 Unexpected end of input.               10   10  ok
requests >=                Unexpected end of input.               12   12  ok
requests[extra1 extra2]    unexpected 'e'.                        16   16  ok
requests[extra1,]          unexpected ']'.                        16   16  ok
requests[extra1            Unexpected end of input.               15   15  ok
requests(>=1.0             Unexpected end of input.               14   14  ok
requests@                  Unexpected end of input.                9    9  ok
requests @                 Unexpected end of input.               11   11  ok
requests==1.0 oops         unexpected 'o'.                        14   14  ok
requests oops              unexpected 'o'.                         9    9  ok
requests=1.0               unexpected '1'.                         9    9  ok
requests>=1.0,             unexpected ','.                        13   13  ok
```

Two findings.

**The position is always right already.** All 16 land exactly where we
want, with no help from us. InductorParser reports its deepest failure,
the furthest the parse got before giving up, and that turns out to be a
good pointer every time here. `requests=1.0` is worth a look: the caret
lands on the `1` (char 9), not the `=` (char 8), because the `==` /
`===` operator branches consume the first `=` before noticing the
second character isn't a `=`. Char 9 is genuinely how far the parse got
trying to read an operator, so depth-primary ranking keeps it there.
(The earlier tier-based model would let a `.WithError` pull this caret
back to char 8, which `docs/ErrorArchitecture.md` and backlog item 0a04
explain was the wrong call.)

**Every message is useless.** All 16 are the mechanical fallback:
"unexpected 'x'" or "Unexpected end of input." Not one of them tells the
user they were missing a comma, or a version, or a closing bracket. The
natural grammar is a recognizer with a position, not a diagnostic.

### Step 4: fix it the way the docs say

`docs/ErrorArchitecture.md` says the fix for a consistently-mechanical
error at some position is to attach a `.WithError` to the rule that
fails *deepest* at that position. So we did that. Seven messages, one
per kind of failure:

- `Name` → "expected a package name at the start of the dependency
  specifier"
- `ComparisonOperator` → "expected a version comparison operator"
- `Version` → "expected a version after the comparison operator"
- the closing `]` → "expected ',' or ']' after the extra name"
- the closing `)` → "expected ',' or ')' after the version specifier"
- the URL scan → "expected a URL after '@'"
- the final `Eof` → "expected the end of the dependency specifier"

That fixed all 16 messages. The positions were already right (Step 3),
and depth-primary ranking guarantees a `.WithError` can't move them: a
named message either surfaces at the depth its rule failed or is
shadowed by a deeper record, but it never drags the caret to a
shallower spot. So adding the seven messages changed only words, never
carets.

One case needed more than a bare `.WithError`, and that's where the
friction was.

**`requests[extra1,]` (trailing comma in the extras list).** The natural
grammar already had the caret in the right place (char 16, the `]`,
right after the missing extra name). The catch is *which rule* the
message goes on. The closing `Token(']')` fails one char earlier, at
char 15 (the comma). A message there is shadowed by the deeper char-16
failure, so it never surfaces. Depth-primary ranking reports the
deepest record, and char 16 is deeper. To put a message *at* char 16
the `.WithError` has to go on the rule that actually fails there: a
separate "extra name after a comma" identifier rule. Which then ran
into the next thing.

**`.WithError` is set-once per rule, and two rules can't share a name.**
The first extra in a list and the extras after a comma are the same
nonterminal, but they want different messages, so they have to be two
`Rule` instances. The obvious move is to name both `.As("extra")`.
`Compile` rejects that: "Each .As(string) name must be unique within a
grammar." The after-comma instance ended up unnamed-but-`.Preserve()`d,
which leaves the parse tree slightly asymmetric.

**`requests>=1.0,` (trailing comma in the version list).** An earlier
draft of this sample couldn't fully fix this case: the genuinely-ideal
message points at the spot just past the comma where another version
constraint was expected, and under the old tier-based error model that
message was unreachable. The version list lives inside
`Or(parenthesized, bare)`, and the `Or` used to wipe the failed
list-iteration record when it committed to the bare branch (backlog
item 0a03). The depth-primary model removed that success-clear, so the
record now survives: the after-comma version constraint's
`ComparisonOperator` fails at char 14 (EOF) and its `.WithError`,
"expected a version comparison operator", surfaces there.
`Tests/OrClearedRecordsTests.cs` keeps two three-rule grammars as
regression coverage that the fix holds.

### Step 5: file the friction

Four backlog items came out of this, all in `backlog/`. Two of them,
0a03 and 0a04, were resolved by the depth-primary error model (backlog
item 0a06). The other two still stand:

- **0a02**: there's no `SeparatedList` factory. The `item (',' item)*`
  shape is five pieces written out, and it appears twice here.
- **0a03** *(resolved)*: `Or`'s success-clear wiped the *winning*
  branch's count-rule failed-iteration records, which contradicted what
  `ErrorArchitecture.md` says about count rules preserving them. The
  depth-primary model dropped `Or`'s success-clear entirely.
- **0a04** *(resolved)*: adding a `.WithError` could pull the caret to
  a shallower position than the grammar had already found, because a
  named record used to beat a deeper mechanical one. Depth-primary
  ranking makes depth the primary key, so a `.WithError` can no longer
  move the caret.
- **0a05**: two rule instances for the same nonterminal can't share an
  `.As` name, even though each legitimately needs its own `.WithError`.

The headline: the obvious grammar gets *positions* right for free, gets
*messages* completely wrong, and `.WithError` closes the gap cleanly.
The remaining friction is concentrated in one spot (lists with
separators) where giving the trailing-separator error its own message
fights the set-once `.WithError` and the name-uniqueness rule.

## Side by side

The same bad inputs through both parsers. Both render a message, the
source line, and a caret.

```
requests<>1.0
  Original:  Expected end of dependency specifier
                 requests<>1.0
                         ^
  Rewrite:   expected a version after the comparison operator
                 requests<>1.0
                          ^
```

The rewrite points at the stray `>` and names what's missing. The
`packaging` port can only say "expected end" at the `<`, because an
operator with no version doesn't tokenize as a specifier at all, so
`packaging` never realizes a version was expected. Here the rewrite wins.

```
requests[extra1 extra2]
  Original:  Expected comma between extra names
                 requests[extra1 extra2]
                                 ^
  Rewrite:   expected ',' or ']' after the extra name
                 requests[extra1 extra2]
                                 ^
```

Same caret. Here `packaging` wins on the message: it peeks, sees a
second identifier, and says exactly "expected comma." The rewrite
doesn't peek, so it offers both legal continuations ("',' or ']'").

```
requests(>=1.0
  Original:  Expected matching RIGHT_PARENTHESIS for LEFT_PARENTHESIS, after version specifier
                 requests(>=1.0
                         ~~~~~~^
  Rewrite:   expected ',' or ')' after the version specifier
                 requests(>=1.0
                               ^
```

`packaging` underlines the whole unclosed group back to the `(`. The
rewrite points at the spot the `)` should go. `packaging`'s message
leaks its internal token-rule names (`RIGHT_PARENTHESIS`) into the
user-facing text, which the rewrite avoids.

## Line counts (non-comment, non-blank)

```
                                  code lines
  Original/RequirementParser.cs       230
  ----
  Rewrite/RequirementGrammar.cs        82
  Rewrite/RequirementParser.cs         64
  Rewrite total                       146
```

The rewrite is about two-thirds the size of the hand-written port, and
the 146 lines include the typed AST builder. The grammar itself is 82
lines, of which roughly 35 are the rule body and the rest is the ABNF
comment and the class scaffolding. The rewrite also gets a walkable
parse tree and a token index on every error for free, neither of which
the port has.

## Worked example

Input:

```
requests[security] >= 2.8.1
```

The parse produces this tree (each named node carries its own
`SourceText` and `SourceRange`):

```
requirement
  name        SourceText: requests
  extras
    extra     SourceText: security
  specifier
    constraint
      operator  SourceText: >=
      version   SourceText: 2.8.1
```

Which the AST builder in `Rewrite/RequirementParser.cs` collapses into:

```
Requirement(
  Name: "requests",
  Extras: ["security"],
  Specifiers: [VersionConstraint(">=", "2.8.1")],
  Url: null)
```

For a bad input like `requests[extra1` (no closing bracket), the rewrite
produces:

```
expected ',' or ']' after the extra name
    requests[extra1
                   ^
```

## Layout

```
E2ESamples/Pep508/
  README.md                  this file
  Pep508.csproj              one project, both parsers, the test corpus
  Original/
    RequirementParser.cs     hand-written C# port of packaging's parser
  Rewrite/
    RequirementGrammar.cs    InductorParser grammar (mirrors the PEP 508 ABNF)
    RequirementParser.cs     typed AST builder + Parse / TryParse
  Tests/
    RequirementTests.cs      golden corpus, reject corpus, side-by-side
    ErrorMessageTests.cs     the 16-case error-message exercise
    OrClearedRecordsTests.cs regression coverage for backlog item 0a03
```

## License and attribution

The `packaging` project is dual-licensed Apache-2.0 / BSD-2-Clause. This
sample contains no upstream code: `Original/RequirementParser.cs` is an
independent C# implementation of the same grammar and the same error
messages, written for an ergonomics comparison. The grammar shape, the
operator set, the error-message strings, and the caret rendering follow
`packaging`'s `_parser.py` and `_tokenizer.py` on the main branch as
fetched 2026-05-15.
