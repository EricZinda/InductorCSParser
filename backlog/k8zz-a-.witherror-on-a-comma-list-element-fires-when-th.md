# A .WithError on a comma-list element fires when the comma is simply absent

Surfaced building the Cron expression sample under `E2ESamples/Cron/`
(backlog item 018a, the errors-first appbuilding exercise).

## The friction

The natural way to write "a comma-separated list of items" is:

```csharp
var listItem = And(Token(','), Item).WithError("expected a value after ','");
var field    = And(Item, ZeroOrMore(listItem));
```

This parses every valid input correctly. It also silently produces the
wrong error message for a whole class of invalid input, and you only
find out by running a reject corpus and reading every message by hand.

In the Cron sample, `* * *` (a three-field cron expression, two fields
short) came back with:

```
column 6: expected a value after ',' in the list
```

There is no comma anywhere in `* * *`. The real problem is two missing
fields.

## Why it happens

Every field is `And(Item, ZeroOrMore(listItem))`. When a field ends,
`ZeroOrMore` tries one more `listItem`, whose `Token(',')` fails because
there is no comma there (the list is simply over). Per
`docs/ErrorArchitecture.md`, a composite that carries a `.WithError`
anchors that message at the deepest failure anywhere in its subtree,
and the failure of its *first* child counts. So a comma that was never
going to be there records "expected a value after ','" at the end of
the field.

That position is the same one the real "missing field" error wants to
report at, the named comma-list failure was recorded earlier than the
field-level failure, and an exact-depth tie goes to the first writer.
The spurious message wins.

The grammar is correct. The error attribution is wrong, silently, and
only on inputs a happy-path test suite never exercises.

## The fix the sample had to use

Three separate, non-obvious moves (see `E2ESamples/Cron/Rewrite/CronGrammar.cs`):

1. Take the `.WithError` off the `And(Token(','), Item)`. A missing
   comma then records a mechanical failure, which loses to a named one
   at the same depth instead of beating it.
2. Move the message onto the item *value* after the comma, so it only
   records once the comma has actually matched and the value is what's
   missing.
3. Make that after-comma item a separate `Rule` instance from the
   head-of-field item, so its message doesn't also shadow the
   per-field message on a head-of-field failure.

Move 3 then trips backlog item `0a05` (two instances of one nonterminal
can't share an `.As` name), so the two items have to be named `"item"`
and `"listItem"`.

That is a lot of depth-model knowledge to produce a correct error for
`a,,b`.

## What would make it easy

Two options, not mutually exclusive:

- A `Recipes.md` entry: "comma-separated lists: where the `.WithError`
  goes." Show the natural-but-wrong `And(Token(','), Item).WithError(...)`,
  show the `* * *`-style wrong output, and walk through the three-move
  fix. This is the cheap, immediate win and matches how the leading-zero
  recipe is already documented.

- A `SeparatedList(element, separator)` factory (or `SeparatedList(min,
  element, separator)`), which builds the correct shape once: the
  separator never carries the element's message, the after-separator
  element is its own instance, and the "expected another element after
  the separator" message is attached in the one right place. Callers
  writing the single most common list shape in every grammar would never
  see this. (`0a05` already gestures at a `SeparatedList` factory for a
  related reason.)

Recommend doing the Recipes entry now and tracking the factory
separately, since the factory is a larger design question (does it emit
a node of its own, how does it name the repeated element, does it take
a trailing-separator policy).
