# Cron expression sample

This one is different from the other samples under `E2ESamples/`. The
others (SemVer, Newsboat, TOML, ...) take a parser someone already
shipped and rewrite it with InductorParser. This sample is backlog item
`018a`: pick a small real-world format, build the grammar you actually
want from scratch, and see how hard it is to get *good error messages*
for every failure a user could hit. There's no `Original/` directory
because there's nothing being ported. The "spec" is the POSIX crontab
field format.

## Why cron

A cron expression is the five space-separated fields in front of every
crontab line and every CI scheduler's "run on a schedule" box:

```
minute  hour  day-of-month  month  day-of-week
  */15   9-17       *          *      1-5
```

It's a good errors-first test case for three reasons. It's small enough
to build in a sitting. It's everywhere, so the errors land in front of
real users. And the typical thing people write to validate it is a
regex or a hand-rolled splitter that returns a bare `false`. Type `* 25
* * *` into a scheduler and "invalid cron expression" is all you get,
even though the parser knew perfectly well that `25` is out of range
for the hour field. We wanted to see whether InductorParser makes the
*good* version, the one that says exactly what's wrong and where, the
natural thing to build.

## What the grammar covers

The five fields, each a comma-separated list of items, where an item is
`*`, a number, a range `a-b`, or any of those with a `/step` suffix:

```
<cron>   ::= <field> WS <field> WS <field> WS <field> WS <field> EOF
<field>  ::= <item> (',' <item>)*
<item>   ::= <base> ('/' <step>)?
<base>   ::= '*' | <number> '-' <number> | <number>
```

Each field has its own value range: minute 0-59, hour 0-23,
day-of-month 1-31, month 1-12, day-of-week 0-7 (0 and 7 both Sunday).

Deliberately left out, because they don't add anything to the
error-message question and would just pad the grammar: the three-letter
month and weekday names (`JAN`, `MON`), the `@hourly` / `@daily`
macros, and the Quartz `L` / `W` / `?` extensions.

## The errors we wanted, and what we got

The whole point of the exercise. Here is every failure category and the
actual message the sample produces (copied from the test run, not
typed by hand):

```
""              column 1: expected a cron expression: 5 space-separated
                 fields (minute hour day-of-month month day-of-week)
"* * *"         column 6: expected a value for the month field: a
                 number, a range like 1-5, a list like 1,15,30, a step
                 like */5, or '*'
"* * * * * *"   column 10: a cron expression has exactly 5 fields
                 (minute hour day-of-month month day-of-week); remove
                 the extra text
"* 24 * * *"    column 3: hour field value '24' is out of range (0-23)
"* * 0 * *"     column 5: day-of-month field value '0' is out of range
                 (1-31)
"*/0 * * * *"   column 3: minute field step '0' must be 1 or greater
"30-10 * * * *" column 1: minute field range '30-10' is descending; the
                 start must not be greater than the end
"* * x * *"     column 5: expected a value for the day-of-month field:
                 a number, a range like 1-5, ...
"1,,2 * * * *"  column 3: expected a value after ',' in the list
"5- * * * *"    column 3: expected a number after '-' to finish the
                 range
"*/ * * * *"    column 3: expected a step number after '/'
```

Every one of those names the field, points a column at the actual bad
character, and says what was expected. That's the bar. The regex
`bool IsValid` version gets you `false` for all eleven, and a regex
that only checks structure (`[\d*/,-]+` per field) doesn't even reject
the out-of-range ones.

## How the work split

Two layers, and the split is forced by what a PEG grammar can and can't
say.

The grammar parses *structure*: five fields, the comma lists, the `*` /
range / step shapes. Everything structural produces a positioned error
straight out of the parse, with the message coming from a `.WithError`
on the rule that failed deepest.

The value ranges (0-59 and friends), the step-must-be-positive rule,
and the descending-range check (`30-10`) can't live in the grammar at
all. They're integer-value predicates, not character classes. There's
no `TokenSet` for "a number between 0 and 23". So `CronParser` walks
the parse tree afterward, pulls each numeric node, and checks it,
reading `Symbol.SourceRange` to point the error at the right token.
That's the same "validate after parsing" pattern the SemVer sample uses
for its Int32 range check.
No surprise there, and it was smooth.

## Where the friction was

Getting those eleven messages right was *not* smooth. Three things
tripped us up, and the order matters because the first one is the
interesting one.

### 1. A `.WithError` on a comma-list element fires when the comma is simply absent

This is the big one, and it's filed as a backlog item titled "a
.WithError on a comma-list element fires when the comma is simply
absent".

The natural way to write "a list of items" is:

```csharp
var listItem = And(Token(','), Item).WithError("expected a value after ','");
var field    = And(Item, ZeroOrMore(listItem));
```

It parses every valid input correctly. Every golden test passed. Then
we ran the reject corpus and `* * *` (a three-field expression, two
fields short) came back with:

```
column 6: expected a value after ',' in the list
```

That's wrong. There's no comma anywhere in `* * *`. The real problem is
two missing fields. What happened: every field ends by trying one more
`listItem`, that `listItem`'s `Token(',')` fails because the field is
over, and a composite anchors its `.WithError` at the deepest failure
in its subtree, including the failure of its *first* child. So a
comma-that-was-never-going-to-be-there records "expected a value after
','" at the end of the field, and that position is the same one the
real missing-field error wants, and it was recorded first, so it wins
the tie. The grammar was correct. The error attribution was silently
wrong, and only on inputs we hadn't tested yet.

The fix took three moves, none of them obvious. First, take the
`.WithError` off the `And(',', Item)` so a missing comma stays a
mechanical failure that loses to a named one. Second, move the message
onto the item *after* the comma. Third, make that item a separate rule
instance from the head-of-field item so its message doesn't shadow the
per-field message. The working grammar in `Rewrite/CronGrammar.cs` has
all three, with comments. A `SeparatedList` factory in the library would build the
right shape once and nobody would ever hit this.

### 2. The separator has to be folded into the field

Filed as a backlog item titled "recipes gap: fold the separator so a
short fixed-arity record names the missing field".

The natural top-level shape for a five-field record is:

```csharp
And(minute, ws, hour, ws, dayOfMonth, ws, month, ws, dayOfWeek, Eof())
```

Feed that `* * *` and the deepest failure is the bare whitespace rule
hitting end of input, which has no message, so the user gets "unexpected
end of input". True, but useless.

The fix is to fold the separating whitespace into each field rule
(every field after the first starts with a required `InlineWhitespace`)
and put a `.WithError` on the field. Then the field's message anchors at
end of input and the user gets "expected a value for the month field",
which actually tells them what's missing. It works, but it's a
non-obvious restructuring, and there's no recipe for it.

One thing that genuinely can't be done: a literal "found 3 fields,
expected 5" count. Counting needs a tree to walk and a failed parse
doesn't have one. "Expected a value for the month field" is the best
available answer, and it's arguably better than a count anyway because
it names the specific thing to add.

### 3. Two instances of one nonterminal can't share a name

Once friction 1 forced a separate rule instance for the after-comma
item, both instances wanted to be named `"item"` (they *are* the same
nonterminal). `Compile` rejects that:

```
Two reachable rules share the name 'item'. Each .As(string) name
must be unique within a grammar.
```

This is already backlog item `0a05`, surfaced by the PEP 508 sample.
This exercise hit it again from a different direction, which is worth
noting on the item. We named them `"item"` and `"listItem"`.

A couple of smaller things, already covered by existing backlog items
and not refiled: structural punctuation (`/` and `-`) defaults to
`FlattenType.Delete`, so `item.ToString()` returns `*5` instead of
`*/5` until you `.Preserve()` the punctuation (backlog `7s7s`, the
raw-source-text accessor).

## What was smooth

Honest accounting, because it wasn't all friction.

Positioned errors needed zero ceremony. `result.ErrorCharIndex`,
`ErrorLine`, `ErrorCharColumn` are right there on the parse result, and
`Symbol.SourceRange` gives the same thing for any node you reach in
post-parse validation. The four-line `ErrorAt(symbol, message)` helper
in `CronParser.cs` is all the position plumbing this sample needed.

`.WithError` on the *inner* rules did exactly the right thing. "Expected
a number after '-' to finish the range" and "expected a step number
after '/'" both surface at precisely the spot the parse got stuck,
because depth-primary ranking lets a deeper inner message win over the
outer field message. Cases like `5-` and `*/` just worked once the
message was on the right rule.

`FindAll` scoped to a field node made the post-parse validation walk
clean: `fieldNode.FindAll(SingleValue)`, `FindAll(RangeValue)`,
`FindAll(StepValue)`, each returning exactly the nodes that check needs.

## So how hard was it

Medium. The happy path and the leaf-level errors were quick. The hard
part was entirely in friction 1: the grammar that parses every valid
input correctly can still attribute its *errors* to the wrong place,
and you only find out by running a reject corpus and reading every
message. "It parses" is not the same as "its errors are good," and
nothing warns you about the gap. The eleven good messages above are
real, and they're better than what most cron-using apps ship. Getting
there took understanding the depth-and-first-writer ranking model well
enough to predict where a `.WithError` would land. The two backlog
items propose the recipes (and one factory) that would have let the
next person skip that.

## Layout

```
E2ESamples/Cron/
  README.md              this file
  Cron.csproj            one project: grammar, parser, tests
  Rewrite/
    CronGrammar.cs       the InductorParser grammar
    CronParser.cs        typed projection + semantic validation
  Tests/
    CronTests.cs         golden corpus, reject corpus, error-position
```

| File             | Lines |
| ---------------- | ----- |
| CronGrammar.cs   |   142 |
| CronParser.cs    |   194 |
| CronTests.cs     |   240 |

`Cron.csproj` is wired into `InductorParser.sln`.

## Running it

    dotnet build E2ESamples/Cron/Cron.csproj
    dotnet test  E2ESamples/Cron/Cron.csproj

46 tests in four fixtures: a golden corpus of valid expressions, a
reject corpus that checks every bad input gets a positioned error, an
error-position fixture that asserts the exact column and message for
each failure category, and a dump test that writes every reject
message to a file so this README can quote real output.
