- Token / Literal / LiteralIgnoreAsciiCase with a stray-surrogate literal throws a generic ArgumentException out of default Compile()

When a grammar contains a literal-bearing rule (`Token(string)`, `Literal(string)`, or `LiteralIgnoreAsciiCase(string)`) whose expected text contains a stray surrogate half, calling `.Compile()` (or any Compile that picks a real `NormalizationForm`) throws a generic `System.ArgumentException` from inside .NET's `string.Normalize`. On .NET 8 the message reads `String contains invalid Unicode code points. (Parameter 'strInput')`. The grammar author has no way to tell from that message that the issue is a stray surrogate in their literal, which rule's literal is the offender, or that `Compile(null)` is the documented path for surrogate-bearing literals (called out for `Rules.Token(string)` and the WTF-8 / unpaired-surrogate use case the existing `Literal_with_lone_surrogate_first_char_compiles_under_null_normalization` test covers).

The throw originates in [`src/InductorParser/Rule.cs:1106`](src/InductorParser/Rule.cs#L1106) inside `CollectNormalizationOffenders`. For each `GraphemeRule`, `LiteralRule`, or `LiteralIgnoreAsciiCaseRule` the method calls `expected.Normalize(form)` without checking for stray-surrogate input first:

```csharp
if (expected != null)
{
    string normalized = expected.Normalize(form);  // throws on stray surrogates
    if (!string.Equals(expected, normalized, StringComparison.Ordinal))
        offenders.Add((r, expected, normalized));
}
```

`string.Normalize` rejects any string that contains an unpaired surrogate by throwing `ArgumentException`. The exception bubbles straight out of `Compile`, so the grammar author sees a stack trace pointing at `String.Normalize` rather than the much-more-helpful `BuildNormalizationErrorMessage` path that the same Compile uses for ordinary normalization-form mismatches.

Confirmed by reproduction on the current Infrastructure branch (2026-05-05): three failing tests, stack trace pointing at `Rule.CollectNormalizationOffenders` at `Rule.cs:1106` calling into `String.Normalize`, all three throwing `System.ArgumentException` with the "String contains invalid Unicode code points" message. None of the existing surrogate fixtures in `UnexpectedUnicodeTests.cs` cover this default-Compile path.

The user-visible consequence: a grammar like `Token(UnicodeExamples.HighSurrogateMinText)` (or `Literal("\uD800X")`) that compiles fine under `Compile(null)` (verified by `Literal_with_lone_surrogate_first_char_compiles_under_null_normalization`) silently breaks under default `Compile()` with a message that doesn't mention surrogates, normalization, or the suggested `Compile(null)` workaround.

## Verify the Bug (Write Test First)

Add these tests to `src/InductorParser.Tests/Core/UnexpectedUnicodeTests.cs`:

```csharp
[Test]
public void Token_with_stray_surrogate_under_default_Compile_throws_clear_error()
{
    // A surrogate-bearing literal can never be normalized to FormC (or any
    // other form). The user has to use Compile(null) for these, per the
    // documented WTF-8 / unpaired-surrogate round-tripping case. Compile()
    // should surface that in a clear InvalidOperationException with the
    // same helpful shape as other normalization mismatches, not let .NET's
    // generic ArgumentException leak out. The original ArgumentException
    // is preserved as InnerException (wrapped in AggregateException so the
    // shape stays uniform when multiple literals each trip Normalize) so
    // a programmatic caller can still drill in to the runtime cause.
    var rule = Token(UnicodeExamples.HighSurrogateMinText);

    var exception = Assert.Throws<InvalidOperationException>(() => rule.Compile());
    Assert.That(exception!.Message, Does.Contain("surrogate"),
        "Compile error should mention surrogates so the author knows what to fix.");
    Assert.That(exception.Message, Does.Contain("Compile(null)").Or.Contain("null"),
        "Compile error should suggest Compile(null) as the documented path.");
    Assert.That(exception.InnerException, Is.InstanceOf<AggregateException>(),
        "InnerException should expose the runtime cause.");
    var aggregate = (AggregateException)exception.InnerException!;
    Assert.That(aggregate.InnerExceptions, Has.Count.EqualTo(1));
    Assert.That(aggregate.InnerExceptions[0], Is.InstanceOf<ArgumentException>());
}

[Test]
public void Literal_with_stray_surrogate_under_default_Compile_throws_clear_error()
{
    // Same shape as Token, applied to LiteralRule. The form-validation
    // pass walks every literal-bearing rule, so the same fix has to cover
    // GraphemeRule, LiteralRule, and LiteralIgnoreAsciiCaseRule alike.
    var rule = Literal(UnicodeExamples.HighSurrogateMinText + "X");

    var exception = Assert.Throws<InvalidOperationException>(() => rule.Compile());
    Assert.That(exception!.Message, Does.Contain("surrogate"));
    Assert.That(exception.InnerException, Is.InstanceOf<AggregateException>());
}

[Test]
public void LiteralIgnoreAsciiCase_with_stray_surrogate_under_default_Compile_throws_clear_error()
{
    var rule = LiteralIgnoreAsciiCase(UnicodeExamples.HighSurrogateMinText + "x");

    var exception = Assert.Throws<InvalidOperationException>(() => rule.Compile());
    Assert.That(exception!.Message, Does.Contain("surrogate"));
    Assert.That(exception.InnerException, Is.InstanceOf<AggregateException>());
}

[Test]
public void Multiple_surrogate_literals_under_default_Compile_aggregate_inner_exceptions()
{
    // Every literal-bearing rule that trips string.Normalize contributes
    // one ArgumentException to the AggregateException. The grammar author
    // sees a single multi-rule message in InvalidOperationException.Message
    // and can walk InnerExceptions for the per-rule runtime cause.
    var rule = AllOf(
        Token(UnicodeExamples.HighSurrogateMinText),
        Literal(UnicodeExamples.HighSurrogateMinText + "X"),
        LiteralIgnoreAsciiCase(UnicodeExamples.HighSurrogateMinText + "y"));

    var exception = Assert.Throws<InvalidOperationException>(() => rule.Compile());
    Assert.That(exception!.InnerException, Is.InstanceOf<AggregateException>());
    var aggregate = (AggregateException)exception.InnerException!;
    Assert.That(aggregate.InnerExceptions, Has.Count.EqualTo(3));
    Assert.That(aggregate.InnerExceptions, Has.All.InstanceOf<ArgumentException>());
}
```

Run with:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "Token_with_stray_surrogate_under_default_Compile_throws_clear_error|Literal_with_stray_surrogate_under_default_Compile_throws_clear_error|LiteralIgnoreAsciiCase_with_stray_surrogate_under_default_Compile_throws_clear_error|Multiple_surrogate_literals_under_default_Compile_aggregate_inner_exceptions" --nologo
```

Before the fix, all four tests fail with `Expected: <System.InvalidOperationException> But was: <System.ArgumentException: String contains invalid Unicode code points. (Parameter 'strInput')>`. The exact message wording may vary across .NET runtimes. The type mismatch is the primary assertion. The substring assertions on the message lock in the helpfulness once the type is fixed, and the AggregateException assertions lock in that the runtime cause stays accessible programmatically (single rule case) and aggregates cleanly (multi-rule case).

## Fix

Form validation moves from a central type-switch to a per-rule virtual that each rule overrides to validate its own data shape. This is the foundational refactor; the actual "give a clear error for surrogate literals" change is a try/catch + AggregateException attached on top.

**1. Add a per-rule virtual on `Rule`.** The previous shape was a central `CollectNormalizationOffenders` walker that switched on rule type, enumerated every literal-bearing rule, and read each one's expected text. That's closed-set: a user-defined `Rule` subclass with its own literal was silently skipped, and rules whose data isn't a single string (`OneOf` / `NoneOf` carry sets, not strings) couldn't participate at all. Replace with an instance-level virtual each rule overrides:

```csharp
// In Rule.cs, near ComputeRuleStart:
internal virtual void CollectNormalizationOffenders(
    NormalizationForm form,
    List<(Rule rule, string original, string normalized)> offenders,
    List<ArgumentException> failures)
{
    // default no-op
}
```

**2. Add a `protected static` helper for the literal-bearing case.** Most rules that participate just want to call `string.Normalize` on one fixed string with a try/catch. Hoist that into one place so the three literal-bearing overrides stay one-liners:

```csharp
protected static void TryNormalizeLiteral(
    Rule rule, string text,
    NormalizationForm form,
    List<(Rule rule, string original, string normalized)> offenders,
    List<ArgumentException> failures)
{
    try
    {
        string normalized = text.Normalize(form);
        if (!string.Equals(text, normalized, StringComparison.Ordinal))
            offenders.Add((rule, text, normalized));
    }
    catch (ArgumentException exception)
    {
        // string.Normalize rejects strings it can't normalize in any
        // form (in practice, unpaired surrogates) by throwing
        // ArgumentException with a runtime-generic message. Surface
        // that message inline so the reader sees what .NET said,
        // framed by a hint about Compile(null). The exception itself
        // flows up via failures so a programmatic caller can drill in.
        failures.Add(exception);
        offenders.Add((rule, text,
            $"<string.Normalize rejected this literal: {exception.Message} " +
            $"This is usually an unpaired surrogate. Use Compile(null) to keep " +
            $"surrogate-bearing literals as-is.>"));
    }
}
```

**3. Override the virtual on the three literal-bearing rules.** Each becomes one short override that delegates to the helper:

```csharp
// GraphemeRule.cs, LiteralRule.cs, LiteralIgnoreAsciiCaseRule.cs:
internal override void CollectNormalizationOffenders(
    NormalizationForm form,
    List<(Rule rule, string original, string normalized)> offenders,
    List<ArgumentException> failures)
{
    TryNormalizeLiteral(this, _expected, form, offenders, failures);
}
```

**4. Replace the central walker with a thin recursive dispatcher.** The static walker no longer knows anything about rule types or what counts as "literal-bearing." It just calls each rule's override and recurses into children:

```csharp
private static void CollectNormalizationOffendersAll(
    Rule r,
    HashSet<Rule> visited,
    NormalizationForm form,
    List<(Rule rule, string original, string normalized)> offenders,
    List<ArgumentException> failures)
{
    if (!visited.Add(r)) return;
    r.CollectNormalizationOffenders(form, offenders, failures);
    foreach (var child in r.Children)
        CollectNormalizationOffendersAll(child, visited, form, offenders, failures);
}
```

**5. Aggregate the caught exceptions and attach them as `InnerException`.** In Compile's call site, allocate the failures list, pass it through, and wrap in `AggregateException` when non-empty so a programmatic caller doesn't have to branch on count:

```csharp
if (normalizeInput.HasValue)
{
    var offenders = new List<(Rule rule, string original, string normalized)>();
    var normalizeFailures = new List<ArgumentException>();
    visited.Clear();
    CollectNormalizationOffendersAll(this, visited, normalizeInput.Value, offenders, normalizeFailures);
    if (offenders.Count > 0)
    {
        Exception? inner = normalizeFailures.Count > 0
            ? new AggregateException(normalizeFailures)
            : null;
        throw new InvalidOperationException(
            BuildNormalizationErrorMessage(normalizeInput.Value, offenders),
            inner);
    }
}
```

`BuildNormalizationErrorMessage` doesn't need to special-case the surrogate offender — the synthetic suggestion string reads naturally inside the existing "X should be Y" template.

The polymorphic shape opens the door to fixing a parallel bug filed separately: `OneOf` / `NoneOf` set entries also need form-validation today (`OneOf("é").Compile(FormD).Parse("é")` silently fails to match because the set entry was stored under FormC but the input gets decomposed under FormD). Adding that fix is now a one-rule override on `OneOfRule` and `NoneOfRule` that walks `_set`'s rune intervals and multi-rune graphemes — no changes to the walker or any other rule.

## Verify the Fix

Run the same three tests. They should now pass. Also run the existing surrogate fixtures to confirm no regression on the `Compile(null)` paths:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "Lone_high_surrogate_handling|Lone_low_surrogate_handling|Literal_with_lone_surrogate_first_char_compiles_under_null_normalization|LiteralIgnoreAsciiCase_with_lone_surrogate_first_char_compiles_under_null_normalization" --nologo
```

All four should still pass. The fix only changes the default-FormC error path, not the `Compile(null)` happy path the existing tests cover.
