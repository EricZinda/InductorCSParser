# Error Reporting Architecture

Parse errors are easy to get wrong. A PEG parser tries many alternative paths through the grammar before settling on success or failure, and most of those paths fail. Each failure happens somewhere in the input. When the whole parse fails, the library has to pick *one* failure to show the user. This document is how InductorParser decides which failure wins, where it gets reported, and how grammar authors steer the *message* with `.WithError`.

The short version: the parser reports its *deepest* failure, the furthest it got before giving up. You write the message text with `.WithError`. You don't pick the position. (One opt-in exception lets you override the position, but it's rare and explained later.)

## What we want the user to see

When the parse fails, the user sees a position (a character offset) and a message. The position should point at the character where the parse stopped being able to continue. The message should describe what the parser expected, in language the grammar author chose if possible, or a mechanical "unexpected 'x'" fallback if not.

## Ranking

Every failure has an input position, an optional message, and a `forced` flag. A rule with no `.WithError` produces a **mechanical** failure (position only). A rule with `.WithError("msg")` produces a **named** failure (position plus message). A rule with `.WithError("msg", forced: true)` produces a **forced** failure.

A failure's **depth** is its input position: the character offset into the input where the failure happened. "Deeper" means further into the input text, nothing more. Depth isn't call-stack depth, and it isn't how deeply a rule is nested in the grammar. An `Or` tries every one of its branches from the same input position, so neither which branch a failure came from nor how the grammar is structured changes a failure's depth. The only thing that sets it is how far into the input the parser had read.

When the parse fails, the library picks one failure to report. Four comparisons decide the winner, in order. A later comparison only breaks ties left by the earlier ones:

1. Forced beats non-forced.
2. Deeper beats shallower.
3. Named beats mechanical.
4. First recorded beats later recorded.

The reported message is the winning failure's message. A mechanical winner has no message, so the report renders the generic positional template ("unexpected 'x'") or the end-of-input template.

The property that matters: **depth ranks first.** A named failure doesn't beat a deeper mechanical one. `.WithError` chooses the message and only wins an exact-depth tie.

`forced` is the single exception, and it exists precisely so an author can override the spot when they really mean to. It's rare. See "What forced is for" below.

## Why depth ranks first

The deepest failure is the parser's answer to "how far did this input get before it stopped being valid?" That position is the most specific true thing the parser can say. A `.WithError` is the author improving the wording of the report. It shouldn't also drag the caret to a shallower spot, because a shallower spot is, by definition, somewhere the parser was still doing fine.

An earlier version of this model let a named failure outrank a deeper mechanical one. It produced a recurring bug: an author would attach a `.WithError` to improve a message and, without intending to, move the reported caret backward, onto a closing bracket or a rule boundary, away from the deeper spot the mechanical failure had correctly identified. (See backlog item 0a04 for a concrete instance.) Making depth primary removes that coupling. Adding or changing a `.WithError` can never move the caret now. It can only change the words, or win an exact-depth tie.

The cost is that a `.WithError` only surfaces when its rule's failure is the deepest, or tied for deepest. If something else fails deeper, the author's message is shadowed by the deeper failure. Usually that's correct: the deeper failure is the better pointer. When it genuinely isn't, `forced` is the override.

## Where each rule records its failure

A **leaf** records its failure at the specific spot it got stuck:

| Leaf rule                                       | Records at                      |
| ----------------------------------------------- | -------------------------------- |
| `Literal`, `Grapheme`, `LiteralIgnoreAsciiCase` | the first mismatched token       |
| `OneOf`, `NoneOf`, `AnyToken`                   | the token it tried to read       |
| `ScanWhile`                                     | the position the scan got stuck  |
| `Eof`                                           | the lexer's current position     |

A **composite** with a `.WithError` records that named (or forced) failure at the **deepest input position any rule in its subtree reached**, "where the children left off", not at the composite's own start. This holds for `And`, `Or`, `BetweenInclusive`, `Alias`, `ScanUntil`, and `WithinToken`. The lookahead rules `Peek` and `Not` are the exception and record at their own start, for the reason given just below.

A composite with no `.WithError` records nothing of its own. Its descendants already recorded their failures at the right positions.

Why composites anchor at the deepest descendant failure: a composite's children, while trying to match, advance into the input and record their own failures at the spots they got stuck. Those child failures are normally deeper than the composite's own start. Under depth-first ranking, a named failure sitting at the composite's shallow start would lose to its children's deeper failures, and the composite's message would never surface. Anchoring it at the deepest descendant failure puts it level with the deepest child failure, where the named-beats-mechanical tie-break can let it win. That's exactly what Cases 2 and 3 below rely on.

`Peek` and `Not` are the exception, because they're lookahead (see "Lookahead failures are discarded" below). They run their inner rule as a probe whose failures are discarded, so when one of them fails there is no surviving descendant failure to anchor to. A `Peek` or `Not` with a `.WithError` records it at the rule's own start, the position where the lookahead was checked, which is where the user has to change something.

## Lookahead failures are discarded

`Peek` and `Not` are lookahead. They run their inner rule as a probe and consume nothing: whatever the inner did, the parser's position afterward is exactly where it was before. The inner's excursion into the input was a side-check, not progress.

So when a `Peek` or a `Not` finishes, succeed or fail, the failures its inner subtree produced are discarded. Those failures sit at positions the parser only *probed*, never *consumed*. Keeping them would let a probe's deep excursion outrank the real parse's failures on depth, and the parser would end up pointing at input it never actually tried to consume. The "Worked example: a lookahead leak" below shows this happening.

Discarding the inner failures doesn't make a failed lookahead silent. When a `Peek` or `Not` fails, the lookahead rule records one failure of its own, at its own start, the position where the lookahead was checked (see "Where each rule records its failure"). It includes the lookahead's `.WithError` if it has one.

Every other rule keeps its failures. Two cases are worth calling out, because they can look like they ought to be discarded and aren't.

**A rejected `Or` branch keeps its failure.** When `Or(branchA, branchB)` commits to `branchB`, `branchA`'s failure stays in the tracker. `branchA` was the parser genuinely trying to read the actual input, consuming real characters on the real parse path before it failed. Its failure is evidence, a near-miss (Case 4 below). It doesn't need deleting. If it sits behind `branchB`'s progress it's shallower than the eventual real failure and loses on depth. If it sits ahead, it is the deeper near-miss and deserves to win.

**A count rule keeps the failure of the iteration that stopped it.** `ZeroOrMore`, `OneOrMore`, `Optional`, and the rest of the `BetweenInclusive` family succeed by stopping their loop when an iteration fails. That failed iteration is real input the parser couldn't consume, not a probe. Keeping its failure is what lets an inner `.WithError` on the failed iteration surface, and the everyday "multi-line config grammar with a helpful message" shape depends on it.

The distinction is on-path versus off-path. An `Or` branch, a count iteration, an `And` child: all of them are the parser working through the actual input on the real parse path. A `Peek` or `Not` probe is off that path entirely, because the parser was always going to throw away whatever the probe consumed. On-path failures are kept and ranked by depth. Off-path failures are discarded.

## Alias and error attribution

`Alias` wraps one inner rule and gives it a fresh name (see `AliasRule`). On success it rebadges: the alias's Symbol stands in for the inner's in the parse tree, hiding the inner's identity along that path. That rebadge is a success-path tree-shape operation and has no effect on error reporting.

On failure, `Alias` records like any other composite. If the alias has a `.WithError`, that message is recorded at the deepest position the inner reached. If it doesn't, the alias records nothing of its own. Either way the inner's own failures, including a `.WithError` the inner has, are left untouched: `Alias` never discards them, and ranking picks the winner between the inner's failures and the alias's exactly as it would with no alias in the picture.

What this means in practice. Aliasing a rule doesn't change its error reporting. If the inner rule has a `.WithError`, that message keeps surfacing whether the rule is matched directly or through an alias: the alias doesn't inherit it or hide it, the inner simply records its own failure the way it always would.

An alias can also have a `.WithError` of its own. When the inner has none, the alias's message is the one that surfaces, and two aliases of the same shared rule can give two different messages. When the inner *already* has a `.WithError`, a plain `.WithError` on the alias can't override it: both failures land at the same depth (the alias anchors where the inner failed), the exact-depth tie goes to the first writer, and the inner records before the alias. To override an inner message from the alias, mark the alias's `.WithError` `forced` (see "What forced is for").

## Five cases

Let's see different cases using this architecture. 

### Case 1: a leaf rule with a friendly message

```csharp
var greeting = Literal("hello")
    .WithError("expected greeting");
```

On input `hxyz`, the `h` matches and the parse diverges at the `x`. `Literal` records its failure and associated message, at the first mismatched token, which is position 1. There's only one failure, so it wins, and the user sees "expected greeting" at position 1.

The simplest case: a `.WithError` on a leaf, the leaf failed, the message surfaces at the spot the leaf got stuck.

### Case 2: a friendly message on an alternation

```csharp
var equality = Or(Literal("=="), Literal("=~"))
    .WithError("expected equality operator");
```

On input `=!`, both branches consume the `=` and fail on the `!` at position 1. Each branch records a mechanical "unexpected '!'" failure at position 1, then rolls back.

The `Or` has a `.WithError` set. When a composite that has a `.WithError` fails, it records that message at the deepest position its children reached, which here is position 1, the same spot the branches got stuck. So position 1 holds two mechanical failures (from the branches) and one named failure (from the `Or`). They sit at the same depth, and a named failure beats a mechanical one at the same depth, so the user sees "expected equality operator" at position 1.

### Case 3: an inner message inside a wrapper

```csharp
var quotedString = And(
    Token('"'),
    ZeroOrMore(NoneOf(TokenSet.Runes("\""))),
    Token('"').WithError("unterminated string literal"));

var value = Or(quotedString, Literal("null"))
    .WithError("expected a value");
```

On input `"hello` (missing close quote), the quoted-string branch matches the opening `"`, consumes the body, and the inner `Token('"').WithError(...)` fails at EOF, position 6, recording a named failure there. The `quotedString` `And` has no `.WithError` of its own, so it adds nothing.

The outer `Or` then fails. It has `.WithError("expected a value")`, so it records that message at the deepest position its children reached, also position 6 (the quoted-string branch got that far before the inner `Token` failed).

Position 6 now holds two named failures: the inner `Token`'s "unterminated string literal" and the outer `Or`'s "expected a value". Both are at the same depth and are the same kind of error. The tie goes to the first one recorded, and the inner `Token` ran and recorded before the outer `Or` did. So the user sees "unterminated string literal" at position 6.

This is good. Once the user has typed `"`, they've committed to writing a string and we should give errors telling them how to do it right. Depth plus first-writer gets this right: the inner message is the one the user wants.

### Case 4: a rejected branch's near-miss

```csharp
var hexLiteral = And(
    Literal("0x"),
    OneOrMore(OneOf(TokenSet.Ascii.HexDigits))
        .WithError("expected hex digits after '0x'"));

var number = Or(hexLiteral, OneOrMore(OneOf(TokenSet.Digits)));

var document = And(number, Literal("."));
```

The grammar accepts either a hex literal like `0x1A` or a decimal number, followed by a required dot.

On input `0xZZZ`, the `Or` tries `hexLiteral` first. `Literal("0x")` matches and advances to position 2. `OneOrMore(HexDigits).WithError("expected hex digits after '0x'")` tries position 2, sees `Z`, fails its minimum count of 1, and records its named failure at position 2. The `hexLiteral` branch rolls back.

The `Or` then tries `OneOrMore(Digits)`, which matches the `0` at position 0, stops at `x`, and succeeds. The `Or` commits to the decimal branch.

The `document` `And` then tries `Literal(".")` at position 1 (the `x`) and fails, recording a mechanical failure at position 1.

Two failures are now in the running: the hex branch's named failure at position 2, and the mechanical failure at position 1. The hex branch lost the `Or`, but its failure isn't thrown away. A rejected `Or` branch keeps its failure. Depth decides, and position 2 is deeper than position 1, so the user sees "expected hex digits after '0x'" at position 2.

That's the right answer. Someone who typed `0xZZZ` was attempting a hex literal, and the hex branch is the interpretation that got *furthest* into the input before failing, the closest near-miss. A rejected `Or` branch is the parser genuinely trying to read the actual input one way, so its failure is real evidence, and the branch that reached deepest has the most specific diagnosis. 

The one exception is lookahead. `Peek` and `Not` run their inner rule as a throwaway probe, and a probe's failures are discarded rather than kept. That's covered in "Lookahead failures are discarded" above.

### Case 5: a summary message that has to override the default

```csharp
var unitDuration = And(
    OneOrMore(OneOf(TokenSet.Digits)),
    OneOf(TokenSet.Runes("smh"))
        .WithError("expected a unit: s, m, or h"));

var percentage = And(
    OneOrMore(OneOf(TokenSet.Digits)),
    Literal("%"));

var amount = Or(unitDuration, percentage)
    .WithError("expected a duration like 90m or a percentage like 90%", forced: true);
```

On input `90`, both branches read the digits and stop at EOF, position 2. The `unitDuration` branch tries `OneOf("smh")` there and fails, recording its named "expected a unit: s, m, or h". The `percentage` branch tries `Literal("%")` there and fails, recording a mechanical failure. The `Or` fails too.

Take the `forced` off for a moment and watch the default. Position 2 holds three failures: the `unitDuration` branch's named one, the `percentage` branch's mechanical one, and the `Or`'s own `.WithError`, which a composite anchors at the deepest position its branches reached, also position 2. Named beats mechanical, so the `percentage` branch's failure drops out. That leaves two named failures at the same depth, and an exact-depth tie goes to the first one recorded. The `unitDuration` branch ran and recorded before the `Or` did, so it wins. The user types `90` and is told "expected a unit: s, m, or h".

That isn't wrong. But `90` is a valid start of both forms, and the user hasn't committed to either one. Telling them to add `s`, `m`, or `h` quietly drops the percentage form on the floor. The author would rather show both.

The problem is that a plain `.WithError` on the `Or` doesn't get to win. It lands at position 2, tied with the `unitDuration` branch's message, and an exact-depth tie goes to the first writer, which is the branch, not the `Or`. The summary loses, and it loses silently. Marking it `forced` is the override: a forced failure beats every non-forced one at any depth, ties included. So "expected a duration like 90m or a percentage like 90%" is the message that surfaces.

The first four cases are the default model at work. Case 5 is the one place you override it on purpose. "What forced is for" below has more, including when not to.

## Worked example: the Newsboat operator case

Grammar: `Or(Literal("!~"), Literal("!="), ...).WithError("expected one of: =~, ==, ...")`.

Input: `a !! "b"`.

The parser matches `a` and the space, then enters the `Or` at position 2. `Literal("!~")` and `Literal("!=")` both consume the leading `!` and fail on the trailing `!`, recording mechanical failures at position 3. The other branches fail trivially at position 2. The deepest mechanical failure is at position 3.

All branches failed, so the `Or` fails. It has a `.WithError`, so it records that message at the deepest position its branches reached, position 3.

Position 3 holds the two mechanical failures and the `Or`'s named failure. Same depth, and a named failure beats a mechanical one at the same depth, so the `Or`'s message wins. The user sees "expected one of: =~, ==, ..." at position 3, the `!!` the user actually has to fix.

## Worked example: the Newsboat quoted-string case

Grammar:

```
QuotedStringLiteral = And(Token('"'),
                          ZeroOrMore(...body...),
                          Token('"')
                              .WithError("unterminated string literal"))
ComparisonValue = Or(QuotedStringLiteral,
                     RangeLiteral,
                     NumberLiteral)
                     .WithError("expected one of: quoted string, range, number")
```

Input: `a = "hello`.

The parser reaches ComparisonValue and tries the QuotedStringLiteral branch. The opening `"` matches, the body matches up to EOF, and the closing `Token('"')` is tried at EOF, position 10, and fails. The inner Token records a named failure at position 10 with "unterminated string literal".

QuotedStringLiteral's And rolls back. The inner Token's failure survives the rollback (failures keep on rollback). Or tries RangeLiteral and NumberLiteral, which fail trivially near the start.

Or fails. It has a `.WithError`, so it records "expected one of: ..." at the deepest position its branches reached, position 10. Position 10 now holds two named failures: the inner Token's and the Or's. Same depth, same kind, so the first recorded wins. The inner Token recorded first. The user sees "unterminated string literal" at position 10.

## Worked example: a lookahead leak

Grammar:

```csharp
var pathChar = TokenSet.Ascii.Letters | TokenSet.Ascii.Digits | TokenSet.Runes("/.");

var relativePath = And(
    Not(Literal("https://")),
    OneOrMore(OneOf(pathChar)));

var document = And(relativePath, Eof());
```

A `relativePath` is a sequence of path characters, accepted only if the input isn't an absolute `https://` URL. The `Not(Literal("https://"))` is the check.

Input: `https:X`.

To check the `Not`, the parser runs `Literal("https://")` as a probe. It matches `https:` (positions 0 through 5), expects `/` at position 6, sees `X`, and fails, recording a failure at position 6. Because the probe failed, `Not` *succeeds*: the input isn't an absolute URL. The parser's position is still 0, because `Not` consumes nothing.

The path-character run then matches `https` (positions 0 through 4) and stops at the `:`. `document`'s `Eof` is tried at position 5, finds the `:`, and fails, recording a mechanical failure at position 5.

If the probe's failure at position 6 were kept, it would be deeper than the real failure at position 5 and would win, pointing the user at position 6, a spot the parser only looked at while checking the lookahead and never actually consumed. So the probe's failure is discarded when `Not` finishes. The user sees the real failure: an unexpected `:` at position 5.

## What forced is for

Forced (`.WithError("msg", forced: true)`) is uncommon. Depth-first ranking usually does the right thing on its own: the deepest failure is the most specific spot, and a `.WithError` there provides a good specific message. 

Here's a case where forced is the right tool:

```
Or(
  ComplexInnerGrammarWithLotsOfWithErrors,
  AlternateForm
).WithError("expected a configuration statement", forced: true)
```

The author of this Or doesn't want the user to see whatever specific `.WithError` happened to fire deepest inside the inner grammar. They want the single summary message "expected a configuration statement" regardless of depth. A forced failure beats every non-forced failure at any depth, so it wins.

Most grammars never need this. It's an explicit opt-in for the unusual case where collapsing the message hierarchy to one summary is what the author wants. If you find yourself adding `forced` often, the grammar's `.WithError` annotations are probably over-named and worth pruning.

## What the rules look like to grammar authors

Three things matter at the source-code level:

`.WithError("msg")` is the everyday form. Attach it to a rule whose failure you want to surface a friendly message for. The message is reported whenever that rule's failure is the deepest the parse reached, and it beats a mechanical failure at the same depth.

`.WithError("msg", forced: true)` is the override. Use it when you specifically want a message to win even though it isn't the deepest failure. Avoid it otherwise.

Position isn't yours to set. The parser reports its deepest failure, and a `.WithError` rides along at whatever position its rule failed, which for a composite is the deepest position its subtree reached. So phrase messages to read well at that spot, which is wherever the parse got stuck. "Expected a closing tag", "expected a version after the operator", "unterminated string literal" all read naturally as descriptions of what was wanted at the point the parser gave up.

## Limits and pitfalls

A `.WithError` only surfaces when its rule's failure is the deepest, or tied for deepest. If another rule fails deeper, the author's message is shadowed by that deeper failure, which may be mechanical, so the user sees a generic "unexpected 'x'" even though a `.WithError` exists in the grammar. This is usually correct: the deeper failure is the better pointer. When it genuinely isn't, `forced` is the escape, but use it sparingly.

The mechanical "unexpected 'x'" template is intentionally generic. If a parse error consistently shows it at a particular position, that's a signal to add a `.WithError` to whatever rule fails deepest at that position. Note "deepest", not "outermost": a `.WithError` on an outer rule won't help if an inner rule is the one failing deeper.

Position attribution can still surprise people in deeply-nested grammars. A `.WithError` on a rule referenced from several places in the grammar surfaces that one message regardless of which use failed. If different uses want different messages, build separate rule instances with separate `.WithError`s.
