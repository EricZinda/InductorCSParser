# Error Reporting Architecture

Parse errors are easy to get wrong. A PEG parser tries many alternative paths through the grammar before settling on success or failure and most of those paths fail. Each failure happens somewhere in the input. When the whole parse fails, the library has to pick ONE failure to show the user, and "the right one" is not always the deepest, the shallowest, or the most recent in isolation. This document is how InductorParser decides which failure wins, where it gets reported, and how grammar authors steer the choice with `.WithError`.

## What we want the user to see

When the parse fails, the user sees a position (a character offset) and a message. The position should point at the character where their input went wrong. The message should describe what the parser expected, in language the grammar author chose if possible, or a mechanical "unexpected 'x'" fallback if not.

Here are four different cases we need to solve for. Each is small enough to fit in a handful of rules. The first two are the everyday cases that a parse error has to get right. The last two are where naive implementations break down.

### Case 1: a leaf rule with a friendly message

Grammar:

```csharp
var greeting = Literal("hello")
    .WithError("expected greeting");
```

On input `hxyz`, the parser should report position 0 (the start of the input) with "expected greeting". Not column 1 with "unexpected 'x'" because that's the character where the Literal noticed the mismatch.

This is the simplest case. The user attached a `.WithError` to a rule. The rule failed. The message surfaces.

### Case 2: a friendly message on an alternation

Grammar:

```csharp
var equality = Or(Literal("=="), Literal("=~"))
    .WithError("expected equality operator");
```

On input `=!`, both branches consume the `=` and then fail on the `!` at position 1. Each branch records a mechanical "unexpected '!'" failure at position 1 before rolling back. The outer `Or` records its `.WithError` at position 0 (where it began trying).

The user should see position 0 with "expected equality operator", not position 1 with "unexpected '!'". The author named the rule for a reason; the inner failures are mechanical noise.

A named message has to beat a deeper mechanical one.

### Case 3: an inner message inside a wrapper

Grammar:

```csharp
var quotedString = And(
    Token('"'),
    ZeroOrMore(NoneOf(TokenSet.Runes("\""))),
    Token('"').WithError("unterminated string literal"));

var value = Or(quotedString, Literal("null"))
    .WithError("expected a value");
```

On input `"hello` (missing close quote), the quoted-string branch matches the opening `"`, consumes the body, and then the inner `Token('"').WithError(...)` fails at EOF (position 6). The outer `Or` then fails and records its own `.WithError` at position 0.

The user should see "unterminated string literal" at position 6, not "expected a value" at position 0. Once the user has typed `"`, they've committed to writing a string. Telling them "try null instead" is unhelpful. The inner WithError is more specific and at a deeper position, and that's what they want to see.

This is the flip case. A naive "outer always wins" rule would regress this case. A naive "deepest always wins" rule would regress Case 2. Both rules have to coexist.

### Case 4: a rejected branch leaks its error message

Grammar:

```csharp
var hexLiteral = And(
    Literal("0x"),
    OneOrMore(OneOf(TokenSet.Ascii.HexDigits))
        .WithError("expected hex digits after '0x'"));

var number = Or(hexLiteral, OneOrMore(OneOf(TokenSet.Digits)));

var document = And(number, Literal("."));
```

The grammar accepts either a hex literal like `0x1A` or a decimal number, followed by a required dot. The `.WithError` says "if you wrote the `0x` prefix, you'd better follow it with hex digits" — a perfectly reasonable error to attach to that spot.

On input `0xZZZ`, the inner `Or` tries `hexLiteral` first. `Literal("0x")` matches at position 0 and advances to 2. `OneOrMore(HexDigits).WithError("expected hex digits after '0x'")` tries position 2, sees `Z` (not a hex digit), fails to reach its minimum count of 1, and records its named WithError at position 2. The hex branch rolls back.

The inner `Or` then tries the second branch, `OneOrMore(Digits)`. That matches `0` at position 0, stops at `x`, and succeeds with one matched digit. The inner `Or` commits.

The outer `And` then tries `Literal(".")` at position 1 (the `x`) and fails. It records a mechanical failure at position 1.

What does the user see? Without intervention, the rejected hex branch's named `expected hex digits after '0x'` record at position 2 is still sitting in the tracker. The named tier beats the mechanical tier. The user sees `expected hex digits after '0x'` at position 2, which is misleading: the parse went down the decimal branch and the real problem is the unexpected `x` at position 1 trailing the decimal `0`.

The fix: when `Or` succeeds via one of its branches, the failure records contributed by the rejected branches are cleared. The inner `Or`'s commit (after the decimal branch succeeded) wipes the hex branch's stranded `.WithError` record. The user sees the real problem: a mechanical fallback at position 1 about the unexpected `x`.

This is the rejected-branch pollution case. Records from a path the parser explicitly rejected shouldn't haunt later failures. `Or` and `Not` clear inner records on success because their success means rejecting some sub-path; other rules don't.

These four cases are what the system is designed around.

## Three tiers

Every failure record carries a tier. The reported error is the record at the highest-occupied tier; ties within a tier are broken by depth.

**Tier 1, mechanical.** The fallback. Set when a rule fails and has no `.WithError` attached. Carries only a position, no message. The position is the character the parser was trying to read when it gave up. If no higher tier has any record, the parse error reports this position with a generated message ("unexpected 'x'" or "Unexpected end of input").

**Tier 2, named.** Set when a rule with `.WithError("msg")` fails. Carries the position the rule chose plus the message. Beats any tier-1 record regardless of position. Among multiple tier-2 records, the one at the deepest input position wins, on the theory that deeper failures are closer to where the user went wrong and are more specific.

**Tier 3, forced.** Set when a rule with `.WithError("msg", forced: true)` fails. Same shape as tier 2 but beats it at any depth. Among multiple tier-3 records, deepest wins. Forced is the escape hatch for a grammar author who wants a top-level summary message ("expected configuration statement") to override inner specifics even when an inner WithError is deeper.

The reporting code reads the tiers in order: if a forced record exists, report it; otherwise if a named record exists, report it; otherwise report the mechanical position with the generated template.

## Why deeper named beats shallower named

The Newsboat quoted-string case is the one that fixes the natural direction. When inside a rule that committed to "this is a quoted string" (the opening `"` matched), an inner `Token('"').WithError("unterminated string literal")` failing on the missing closer is genuinely a more specific error than the outer Or's "expected one of: quoted string, range, number". The user can't usefully be told "try a number instead" once they've typed `"`. So at the same tier, depth wins.

The flip case (Newsboat operators) doesn't have this conflict: the inner branches don't have their own `.WithError`, so their failures sit in tier 1. The outer Or's `.WithError` in tier 2 beats them regardless of depth.

## Where a compound's message lands

When a compound rule's `.WithError` is the winner (its message lands in tier 2 or tier 3 and wins), the position reported depends on the compound's shape. For `Or`, `Alias`, `WithinToken`, `ScanUntil`, `Peek`, and `Not`, the position is the rule's own start — the position where the rule began trying. For `And` and `BetweenInclusive`, the position is the start of the failing child rather than the compound's own start, so the cursor lands where the user needs to fix the input instead of at the boundary of a partial match that already succeeded.

`Alias` is the rule where the own-start and failing-child categories collapse into one. `Alias` runs a single child as the first and only thing it does, so the alias's start, the child's start, and the position the child rolls back to on failure are all the same offset. There is no shifting attempt position the way `And` has, so the alias isn't picking a category — both categories name the same position. The alias's `.WithError` lands there no matter which way you reason about it.

The difference is whether the compound has a uniform attempt position or a shifting one. `Or` tries every branch from the same starting position, so "where Or began trying" is unambiguous and is also where the user has to change something. The same is true for the other rules that record at their own start. `And` and `BetweenInclusive` work differently: their failures happen partway through a sequence of children that have already succeeded, and reporting at the rule's overall start would point at input the parser was happy with. `And(Literal("ab"), Token('!')).WithError("expected ab!")` on input `axyz` records at position 1 (where `Literal("ab")` failed mid-read), not position 0. For `OneOrMore(Letter).WithError("expected letters")` on input `abc1`, the rule records at position 3 (where the failing iteration started), not position 0.

For `Or` specifically: in the Newsboat operator case (`Or(...).WithError("expected one of: =~, ==, ...")` on `a !! "b"`), this puts the error at column 3 (the `!!` position) rather than column 4 (mid-`!~` or mid-`!=`).

For leaf rules and scanner-style rules (ScanWhile, ScanUntil), the position they record is the position relevant to the rule's failure mode: leaves record at the bad token's start, scanners record at the rule's own start (the message anchor for the scan body). The asymmetric case is ScanWhile/ScanUntil without `.WithError`: the mechanical record uses the deepest position the scan reached, so a fallback "Unexpected end of input" still lands where the parser actually ran out, not at the rule's start.

## Success clears records (Or and Not only)

When `Or` succeeds via one of its branches, every failure record contributed by the rejected branches is cleared. When `Not` succeeds (its inner failed), every failure record the inner left behind is cleared. Other rules — `And`, `Alias`, `BetweenInclusive` / `OneOrMore` / `ZeroOrMore` / `Optional`, `Peek`, `WithinToken`, `ScanUntil`, `ScanWhile`, leaf rules — do NOT clear records on success. Their inner failure records survive their commits.

The principle: records from a path the parser explicitly rejected are pollution; records from a path the parser tried and couldn't make work are evidence.

**Or rejects branches.** When `Or(branchA.WithError("A"), branchB)` matches via `branchB`, branchA was attempted and rejected. Any records branchA left behind describe an alternative the parser explicitly didn't take. Letting them survive would mean branchA's `.WithError` could haunt later failures the parser eventually reported. Clearing on Or's success removes that pollution.

**Not's inner failing was the EXPECTED outcome.** `Not(inner)` succeeds iff inner fails. Inner failing is what the author wanted. Surfacing inner's failure record as the parse error would be misleading: the parser saw exactly what Not wanted it to see. Clearing on Not's success removes the "expected noise."

**Count rules preserve failure records.** `ZeroOrMore`, `OneOrMore`, `Optional`, and the rest of the `BetweenInclusive` family succeed by stopping their loop when an iteration fails. That failed iteration represents real input the parser couldn't consume, not a rejected alternative. Keeping the record lets inner WithErrors on the failed iteration surface as the parse error. This is what makes the common "multi-line config grammar with helpful WithError" shape produce useful messages.

**Structural composites have nothing to clear.** `And`, `Alias`, `WithinToken`, `ScanUntil`: when they succeed, every inner rule on the success path either committed (and managed its own records) or rolled back without commits the composite would clear. There's no rejected-alternatives or expected-failures shape for the composite itself to scrub. `Alias` is a transparent naming wrapper: the rebadge that hides the inner's identity is a success-path tree-shape operation only, and it touches no failure records.

**Peek doesn't need to clear either.** When Peek succeeds, inner committed; inner's own commit handled inner's internal records. When Peek fails, inner failed; inner's records survive its rollback. Peek's own commit on the success path doesn't have anything additional to clear.

## How each rule shape behaves

`Or` and `Not` clear inner failure records when they succeed; other rules don't. So at the moment the parse finishes, the failure tracker holds records from rules along the failed path, plus records from any failed iterations that a count rule (`ZeroOrMore`, `OneOrMore`, `Optional`, ...) caught along the success path. Records from rejected `Or` branches and from expected `Not` failures are scrubbed.

Rules fall into two groups based on whether they have child rules, and the two groups record at different positions:

**Composites** have child rules they call into during their own execution. `Or`, `And`, `Alias`, `BetweenInclusive`, `WithinToken`, `Peek`, `Not`, and `ScanUntil` (whose stopper, escape start, or escape end can be child rules). The reported error comes from the deepest failing child rule, unless the composite itself has a `.WithError` that overrides them via tier resolution.

**Leaves** have no child rules. `Literal`, `Grapheme`, `LiteralIgnoreAsciiCase`, `OneOf`, `NoneOf`, `AnyToken`, `Eof`, and `ScanWhile` (which uses a lexer primitive rather than a child rule). They record at the specific position where reading hit the problem — the first mismatched token for the multi-token reads, the token's own position for single-token reads, or the position the scan got stuck at for `ScanWhile`.

"WithError Records Error At" is where this rule puts its own record. "Winning record comes from" is what the user actually sees after tier resolution.

The "deeper" intuition has one edge case worth noting: when an inner rule fails on the very first character, its record lands at the same position as the composite's start, not at a deeper position. The inner still wins in that case because the inner's `RecordFailure` call ran before the composite's call, and at the same position within a tier the first writer keeps the slot. The cells below say "unless this rule's WithError dominates" for that reason.

### Composites

| Rule                                                                       | WithError Records Error At                | Winning record comes from                                       |
| -------------------------------------------------------------------------- | ----------------------------------------- | --------------------------------------------------------------- |
| `Or`                                                                       | Rule's own `transaction.StartPosition`    | A failing inner rule, unless this rule's WithError dominates    |
| `And`                                                                      | The failing child's start position        | A failing inner rule, unless this rule's WithError dominates    |
| `BetweenInclusive` (`OneOrMore`, `ZeroOrMore`, `Optional`, ...)            | The failing iteration's start position    | A failing inner rule, unless this rule's WithError dominates    |
| `WithinToken`                                                              | Rule's own `transaction.StartPosition`    | A failing inner rule, unless this rule's WithError dominates    |
| `ScanUntil`\*                                                              | Rule's own `transaction.StartPosition`    | A failing inner stopper at EOF in strict mode or escapeEnd rule at EOF (if either is configured), otherwise itself, unless this rule's WithError dominates |
| `Peek`                                                                     | Rule's own `transaction.StartPosition`    | A failing inner rule, unless this rule's WithError dominates    |
| `Not`                                                                      | Rule's own `transaction.StartPosition`    | Itself (when Not fails, inner had succeeded and inner's commit already cleared its records) |
| `Alias`                                                                    | Rule's own `transaction.StartPosition`    | A failing inner rule, unless this rule's WithError dominates    |

### Leaves

| Rule                                                                       | WithError Records Error At                | Winning record comes from                                       |
| -------------------------------------------------------------------------- | ----------------------------------------- | --------------------------------------------------------------- |
| `ScanWhile`                                                                | Position where the scan got stuck         | Itself (no child rules contribute)                              |
| `Literal`, `Grapheme`, `LiteralIgnoreAsciiCase`                            | Start of the first mismatched token       | Itself                                                          |
| `OneOf`, `NoneOf`, `AnyToken`                                              | The token's start (= transaction start)   | Itself                                                          |
| `Eof`                                                                      | Lexer's current position                  | Itself                                                          |

### \*ScanUntil's asymmetric position

The "WithError Records Error At" column says where ScanUntil records its WithError message — at the rule's own start, the message anchor. That's only the WithError case. For the no-WithError case, ScanUntil records the mechanical fallback at the position where the failure actually happened, so the user sees "unexpected end of input" (or similar) where the parser truly got stuck:

- **Strict mode, no stopper found.** The loop runs to EOF without ever matching the stopper. ScanUntil records mechanical at the EOF position the scan reached, not at its own start. For input `"abcXYZ"` with `ScanUntil("|")` and no WithError, the user sees the failure at offset 6 (EOF), not offset 0.
- **Bad escape end (general rule form).** `escapeStart` matched, ScanUntil committed to the escape sequence, but `escapeEnd.TryParse` returned null. ScanUntil records mechanical at where `escapeEnd` was attempted (the position after `escapeStart` ran), not at the rule's own start.
- **Bad escape end (single-rune fast path).** Same shape: ScanUntil records mechanical at `pos + tokenLen` (just past the escape-start rune), where `escapeEnd` was attempted.

If ScanUntil has a `.WithError`, all three cases instead record at the rule's `transaction.StartPosition` — the message anchor for the named tier. The asymmetry: WithError lands at the rule boundary the user sees in the message; mechanical lands at the actual stuck position.

This is the same asymmetry ScanWhile has, just expressed differently — ScanWhile always records at where the scan got stuck (its WithError column reads "Position where the scan got stuck") because there's no distinction worth tracking. ScanUntil's WithError column reads "Rule's own start" because the WithError typically describes the body as a whole, not the failure point.

## Alias and error attribution

`Alias` wraps one inner rule and gives it a fresh name (see `AliasRule`). On success it rebadges: the alias's Symbol stands in for the inner's in the parse tree, hiding the inner's identity along that path. That rebadge is a success-path tree-shape operation and has no effect on error reporting.

On failure, `Alias` records like any other single-child composite. If the alias has a `.WithError`, the message goes into the named tier (or the forced tier, when `forced: true` was passed) at the alias's own start. If it doesn't, the alias records a mechanical fallback there. Either way the inner's own records — including a `.WithError` the inner carries — are left untouched: `Alias` never clears them, and tier resolution picks the winner between the inner's records and the alias's exactly as it would if no alias were in the picture.

The consequence: a `.WithError` keeps surfacing whether or not its rule is reached through an alias, because error attribution follows the `.WithError` text, not the rebadged tree identity. To attach a message to the aliased view specifically, put `.WithError` on the alias.

## Worked example: the Newsboat operator case

Grammar: `Or(Literal("!~"), Literal("!="), ...).WithError("expected one of: =~, ==, ...")`.

Input: `a !! "b"`.

The parser matches `a` and the space, then enters the Or at position 2. `Literal("!~")` and `Literal("!=")` both consume the leading `!`, fail on the trailing `!`, and record tier-1 mechanical failures at position 3. The other ten branches fail trivially at position 2. The tier-1 slot ends up holding position 3 (the deepest mechanical record).

All branches failed. Or records its own failure at its start (position 2) in tier 2 with "expected one of: =~, ==, ...".

Reporting picks tier 2 (non-empty) over tier 1: position 2, "expected one of: =~, ==, ...". Done.

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

The parser reaches ComparisonValue and tries the QuotedStringLiteral branch. The opening `"` matches. The body matches up to EOF. The closing `Token('"')` is tried at EOF and fails. The inner Token records tier-2 at the EOF position with "unterminated string literal".

QuotedStringLiteral's And rolls back. The inner Token's tier-2 record *survives* the rollback (records keep on rollback). Or tries the next branch (RangeLiteral), which fails trivially. Same for NumberLiteral.

Or records its own tier-2 at its start with "expected one of: ...". The two tier-2 records compete by depth: EOF beats the start position. The inner "unterminated string literal" wins.

## Worked example: rejected-branch pollution

Grammar:

```csharp
var email = And(
    OneOrMore(OneOf(TokenSet.Letters)),
    Token('@'),
    OneOrMore(OneOf(TokenSet.Letters)).WithError("expected domain after '@'"));

var username = OneOrMore(OneOf(TokenSet.Letters));

var identifier = Or(email, username);

var document = And(identifier, Literal("."));
```

Input: `alice@`.

The `email` branch matches `alice` (5 letters), then matches `@`, then tries `OneOrMore(letters).WithError("expected domain after '@'")` at position 6 (EOF). No letters available, the inner OneOrMore fails, records its named WithError at position 6. The `email` branch as a whole then fails and rolls back.

The Or tries `username`. That matches `alice` and stops at `@` (not a letter). Succeeds at position 5. The Or commits.

The outer `And` then tries `Literal(".")` at position 5 (the `@`) and fails. Records a mechanical failure at position 5.

Without the success-clears-errors rule, the email branch's "expected domain after '@'" record at position 6 outranks the mechanical at position 5 (named beats mechanical regardless of depth). The user sees the misleading message at position 6.

With it, the Or's success clears every record added during the Or's run, including the email branch's stranded WithError. The user sees the real problem: an unexpected `@` at position 5.

## What forced is for

Forced (`.WithError("msg", forced: true)`) is uncommon. The default tier-2 deepest-wins behavior usually does the right thing because deeper named errors are more specific. The case where forced is right:

```
Or(
  ComplexInnerGrammarWithLotsOfWithErrors,
  AlternateForm
).WithError("expected a configuration statement", forced: true)
```

The author of this Or doesn't want the user to see whatever specific WithError happened to fire deepest inside the inner grammar. They want the single summary message "expected a configuration statement" regardless. Without forced, the deepest inner WithError would win at tier 2. With forced, the outer wins at tier 3.

Most grammars never need this. It's an explicit opt-in for the unusual case where flattening the message hierarchy is what the author wants.

## What the rules look like to grammar authors

Three things matter at the source-code level:

`.WithError("msg")` is the everyday form. Attach it to a rule whose failure you want to surface a friendly message for. The library will pick this message over generic "unexpected 'x'" fallbacks and over other rules' messages at shallower positions.

`.WithError("msg", forced: true)` is the override. Use it when you specifically want a top-level message to win over inner WithErrors. Avoid otherwise.

Position semantics: when a compound's `.WithError` wins, `Or` / `Alias` / `WithinToken` / `ScanUntil` / `Peek` / `Not` report at the rule's own start, while `And` and `BetweenInclusive` report at the failing child's start. The "rule's own start" cases want a message phrased in terms of that anchor: "Expected an operator here" reads correctly when "here" is where the operator was supposed to start. For `And` and `BetweenInclusive`, the position will land on whatever the parser was trying to read when the sequence (or the next iteration) gave up, so messages like "expected a closing tag" or "expected more digits" read naturally — they describe the thing the failing child wanted to find at that spot.

## Limits and pitfalls

The tier-2 deepest-wins rule is a heuristic. There are cases where a less-deep WithError is what the user actually wants to see. The forced flag is the escape. If you find yourself reaching for forced often, the grammar's WithError annotations might be over-named and worth pruning.

The mechanical tier's "unexpected 'x'" template is intentionally generic. If a parse error consistently shows this template at a particular position, that's a signal to add a `.WithError` to the rule that fails there.

Position attribution can still surprise people in deeply-nested grammars. A WithError on a rule referenced from three different places in the grammar will surface that one message regardless of which use of the rule failed. If different uses want different messages, factor them into separate rules with separate WithErrors or override the errors using WithErrors above them at each point in the tree.
