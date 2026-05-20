# No Fail() leaf for "this construct is always an error here"

Found while rewriting serde_json's JSON string parser (`E2ESamples/JsonStringEscapes/`). Some grammar positions are reached only when the input is already known to be wrong, and the grammar wants to fail there with a specific message. Two cases in that sample need it:

- A lone trailing surrogate `\uDC00`: four well-formed hex digits, structurally a complete `\u` escape, but semantically always invalid. Nothing in it "fails" (the shape matched), so there's no rule whose failure carries the message.
- The catch-all branch after the known escape letters: `\` followed by something that's neither a simple-escape letter nor `u`.

InductorParser has no leaf that just "fails here with this message." The sample builds one out of `Not`:

```csharp
private static Rule AlwaysFails(string message) =>
    Not(Optional(AnyToken())).WithError(message);
```

`Optional(AnyToken())` always succeeds, so `Not` of it always fails, and `Not` records its failure (carrying the message) at its own start. It works, and it's the idiom the sample ships, but a reader has to run that double-negative in their head to see it's an unconditional failure, and `Optional(AnyToken())` does a real (immediately rolled-back) token match on every call.

## Where it came up

`E2ESamples/JsonStringEscapes/Rewrite/JsonStringGrammar.cs`, the `AlwaysFails` helper, used by `loneTrailingSurrogate` and the invalid-escape catch-all inside `Escape`.

## Fix

A one-line leaf factory in `Rules`:

```csharp
// Always fails, consumes nothing, records `message` at the current
// position. The grammar-author spelling of "this point is only reached
// on invalid input."
public static Rule Fail(string message);
```

`loneTrailingSurrogate` then ends `..., Fail("lone trailing surrogate ..."))` and reads directly, and `Fail(...)` is the natural terminal branch of an `Or` whose earlier branches are the only valid forms. Internally it can be exactly what `AlwaysFails` does today (a `NotRule` over an always-succeed inner), just given a name and a factory so the intent is on the page.
