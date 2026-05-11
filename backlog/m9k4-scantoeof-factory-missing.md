# Missing factory: "match the rest of the input" / `ScanToEof()`

The Versionize ConventionalCommit rewrite under [E2ESamples/Versionize/Rewrite/ConventionalCommitParserRewrite.cs](../E2ESamples/Versionize/Rewrite/ConventionalCommitParserRewrite.cs) needs a Subject rule that captures everything from the end of the header marker to end-of-input as one leaf. Today the cleanest expression is:

```csharp
public static readonly Rule Subject = ScanUntil(TokenSet.Empty).As("subject");
```

`ScanUntil(emptyStopperSet)` works because `ScanUntilRule.TryParseRule` falls out of its scan loop at EOF independently of the stopper, and an empty TokenSet never matches any token, so the scanner runs to EOF. That's the right behavior, but reading the rule it isn't obvious that "stopper = nothing" means "scan to EOF." A reader has to know `ScanUntil`'s implementation to understand why this idiom works.

The alternatives are uglier:

- `ZeroOrMore(AnyToken().Flatten(FlattenType.Preserve)).Flatten(FlattenType.Preserve)` produces N leaf symbols, one per cluster, instead of one leaf-over-the-whole-run.
- A custom Rule subclass.

A `ScanToEof()` factory in `Rules.cs` would document the intent at the call site:

```csharp
public static Rule Subject = ScanToEof().As("subject");
```

The factory would forward to the same ScanUntilRule with an empty stopper, or add a dedicated rule subclass that always advances to EOF. Either way the call site reads as what it means.

## Possible fix

Add to `Rules.cs`:

```csharp
/// <summary>
/// Match every remaining token to end-of-input. Always succeeds. Default
/// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>.
/// Useful for grammars whose final element is "the rest of this line"
/// or "everything left in the input."
/// </summary>
public static Rule ScanToEof() => new ScanUntilRule(TokenSet.Empty);
```

If the implementer wants to be tidier, a dedicated `ScanToEofRule` whose `ComputeRuleStart` is `MayAdvanceByAnyTokens` and whose `TryParseRule` is a one-line cursor-to-end advance is cheaper than going through `ScanUntilRule`'s loop body, but the savings are marginal.

## Verify

A regression test in `RulesTests.cs`:

```csharp
[Test]
public void ScanToEof_consumes_remainder_in_a_single_leaf()
{
    var rule = ScanToEof().As("rest");
    rule.Compile();

    var result = rule.Parse("hello world");
    Assert.That(result.Success, Is.True);
    Assert.That(result.Tree!.ToString(), Is.EqualTo("hello world"));
}

[Test]
public void ScanToEof_succeeds_on_empty_input()
{
    var rule = ScanToEof();
    rule.Compile();
    Assert.That(rule.Parse("").Success, Is.True);
}
```

Once the factory lands, the Versionize sample's Subject rule can switch to `ScanToEof().As("subject")` to make the intent visible.
