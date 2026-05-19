# Recipes gap: fold the separator so a short fixed-arity record names the missing field

Surfaced building the Cron expression sample under `E2ESamples/Cron/`
(backlog item 018a, the errors-first appbuilding exercise).

## The friction

A cron expression is exactly five whitespace-separated fields. The
natural top-level grammar for a fixed-arity record like that is:

```csharp
And(minute, ws, hour, ws, dayOfMonth, ws, month, ws, dayOfWeek, Eof())
```

Feed it a too-short expression like `* * *` and the deepest failure is
the bare `ws` rule (an `InlineWhitespace`) hitting end of input. `ws`
carries no `.WithError`, so the user gets the mechanical fallback:

```
unexpected end of input
```

True, and useless. The user has no idea a record needs five fields or
which ones are missing.

Putting a `.WithError` on the top-level `And` doesn't help: it anchors
at the deepest descendant failure, which is still that EOF spot, and
the message it produces is one fixed string for every possible failure
of the whole record.

## The fix the sample had to use

Fold the separator into the field. Every field after the first starts
with its own required `InlineWhitespace`, and the separator no longer
sits between fields in the top-level `And`:

```csharp
// before: separators between fields
And(minute, ws, hour, ws, dayOfMonth, ws, month, ws, dayOfWeek, Eof())

// after: each field owns its leading separator, and carries a message
Rule Field(string name, bool leadingSeparator) =>
    (leadingSeparator
        ? And(InlineWhitespace(), item, ZeroOrMore(listItem))
        : And(item, ZeroOrMore(listItem)))
    .As(name)
    .WithError($"expected a value for the {name} field: ...");

And(minuteField, hourField, dayOfMonthField, monthField, dayOfWeekField, Eof())
```

Now a too-short `* * *` fails inside `monthField`, whose own
`.WithError` anchors at end of input, and the user gets:

```
column 6: expected a value for the month field: a number, a range
like 1-5, a list like 1,15,30, a step like */5, or '*'
```

That names the first missing field. See
`E2ESamples/Cron/Rewrite/CronGrammar.cs` for the working version.

## What would make it easy

A `Recipes.md` entry: "fixed-arity records: fold the separator so a
missing element names itself." It's the same teaching shape as the
existing no-leading-zeros recipe: show the natural translation, show
the bad `unexpected end of input` output, walk through folding the
separator into each element rule plus the per-element `.WithError`.

Worth stating plainly in the recipe: a literal count message ("found 3
fields, expected 5") is not reachable from a failed parse, because
counting needs a parse tree and a failed parse has none. "Expected a
value for the month field" is the best available answer, and it is
arguably better than a count because it names the specific thing the
user has to add.

This pairs naturally with the comma-separated-list backlog item ("a
.WithError on a comma-list element fires when the comma is simply
absent"). Both are "where does the `.WithError` go when there's a
separator" recipes and could land as one Recipes section with two
subsections.
