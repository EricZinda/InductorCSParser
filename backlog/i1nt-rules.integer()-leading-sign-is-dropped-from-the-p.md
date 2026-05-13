# Rules.Integer() leading sign is dropped from the parse tree

`Rules.Integer()` matches `[+-]?\d+`, but the leading sign disappears from the rendered tree. The factory uses `Optional(Or(Token('+'), Token('-')))` for the sign in `src/InductorParser/Rules.cs:633`. `Token` has the default `FlattenType.Delete`, so when the input is `"-5"`, the parser successfully consumes the `-`, then the leaf `Symbol` for `Token('-')` is dropped during the parse-time flatten step and never reaches the tree. `Integer().Parse("-5").ToString()` returns `"5"`, not `"-5"`. A named `Integer().As("number")` rule reads the same way: `result.Find(number).ToString()` returns `"5"`. `Symbol.SourceText` still returns `"-5"` because the recorded span covers the full match, so the two accessors disagree.

The sibling `Rules.Float()` factory ran into the same shape during backlog `fl0a` and was fixed to use `Optional(OneOf("+-").Flatten(FlattenType.Flatten))`. `Integer()` was overlooked. A grammar that names `Integer()` and reads `match.ToString()` to recover the numeric text (the natural pattern for "build a `Setting(name, value)` record from the parse") silently loses the sign on negative numbers.

## Verify the Bug (Write Test First)

The test goes in a new `src/InductorParser.Tests/Rules/IntegerRuleTests.cs` following the same shape as the existing `FloatRuleTests.cs` (one fixture per rule factory). Lock in the tree-shape consequence on both the unnamed and the named call patterns.

```csharp
[TestFixture]
public class IntegerRuleTests
{
    [Test]
    public void Negative_sign_is_in_tree_text()
    {
        var result = Integer().Parse("-5");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.ToString(), Is.EqualTo("-5"));
    }

    [Test]
    public void Positive_sign_is_in_tree_text()
    {
        var result = Integer().Parse("+5");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.ToString(), Is.EqualTo("+5"));
    }

    [Test]
    public void Named_integer_renders_full_match()
    {
        var integer = Integer().As("number");
        var result = integer.Parse("-42");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        var match = result.Find(integer);
        Assert.That(match!.ToString(), Is.EqualTo("-42"));
    }
}
```

Run with:

```
dotnet test --filter "FullyQualifiedName~IntegerRuleTests"
```

Pre-fix the three assertions all fail: `ToString()` returns `"5"` / `"5"` / `"42"`.

## Fix

In `src/InductorParser/Rules.cs:633` replace `Optional(Or(Token('+'), Token('-')))` with `Optional(OneOf("+-").Flatten(FlattenType.Flatten))`, matching the shape `Float()` already uses. The `Flatten` override makes the sign leaf bubble up into the parent tree instead of being dropped, so `result.ToString()` and the named-rule `Find(rule).ToString()` paths render the full matched text.

Update the docstring's `<remarks>` block to point at the symmetric Float shape and call out that the sign is in the tree.

## Verify the Fix

Re-run the same command:

```
dotnet test --filter "FullyQualifiedName~IntegerRuleTests"
```

All six `IntegerRuleTests` should pass (the three new sign-in-tree ones plus the three positive / negative / unsigned-match sanity checks added alongside). The full `InductorParser.Tests` suite should report the same pre-existing pass count as before the change (two `RecipesExamples` failures predate this work and aren't related). The state-machine companion suite (`ExperimentalSrc/InductorParser.Tests`) should also stay at 101 / 101.
