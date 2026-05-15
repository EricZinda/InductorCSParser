# EndOfLine / InlineWhitespace / AnyWhitespace factories break .As(name)

`Rules.EndOfLine`, `Rules.InlineWhitespace`, and `Rules.AnyWhitespace` build their composite via `Or(...).Flatten(FlattenType.Delete)` / `OneOrMore(...).Flatten(FlattenType.Delete)`. The `.Flatten(FlattenType.Delete)` call sets the rule's private `_flattenPolicyExplicitlySet` flag to true. A later `.As("name")` call then hits the `_flattenPolicyExplicitlySet` branch of `ApplyIdentificationFlattenPolicy` (see [src/InductorParser/Rule.cs:499-512](src/InductorParser/Rule.cs#L499-L512)) and throws `InvalidOperationException` with the message "its flatten policy was explicitly set to FlattenType.Delete..." even though the user never wrote `.Delete()` or `.Flatten(...)` themselves. The user has no way to know the factory pre-applied a flatten policy, and the exception points the finger at user code that didn't do anything wrong.

User-visible consequence: a grammar that wants to find line breaks or whitespace runs in the parse tree (for syntax highlighting, line-by-line diagnostics, etc.) writes `EndOfLine().As("eol")` or `InlineWhitespace().As("ws")` and gets an exception telling it to set the flatten policy to Preserve. The workaround (`EndOfLine().Preserve().As("eol")`) is undocumented. `Token('a').As("eol")`, `Literal("end").As("eol")`, `Integer().As("num")`, and `Identifier().As("id")` all work without the extra `.Preserve()`, so the broken factories are inconsistent with every other factory in `Rules.cs`.

## Verify the Bug (Write Test First)

The test goes in [src/InductorParser.Tests/Rules/EndOfLineRuleTests.cs](src/InductorParser.Tests/Rules/EndOfLineRuleTests.cs), alongside the existing CRLF and EOF behavior tests.

```csharp
[Test]
public void EndOfLine_factory_supports_As_for_tree_find()
{
    var lineBreak = EndOfLine().As("lineBreak");
    var rule = And(Token('a'), lineBreak, Token('b'));
    rule.Compile();
    var result = rule.Parse("a" + LF + "b");
    Assert.That(result.Success, Is.True, result.ErrorMessage);
    Assert.That(result.Find(lineBreak), Is.Not.Null,
        "EndOfLine().As(...) should produce a findable wrapper.");
}
```

Run with:

```
dotnet test --filter "EndOfLine_factory_supports_As_for_tree_find"
```

Pre-fix the test fails with `System.InvalidOperationException : .As("lineBreak") can't be applied to this rule: its flatten policy was explicitly set to FlattenType.Delete...`. The same shape applies to `InlineWhitespace().As(...)` and `AnyWhitespace().As(...)`; one regression test is enough to lock in the contract.

Two companion tests verify the rest of the surface: `EndOfLine_factory_default_flatten_is_overridable` (a later `.Flatten(...)` still overrides the factory default) and `User_factory_using_FlattenByDefault_stays_nameable` (a user-written factory built on the public `FlattenByDefault` method is nameable by its caller, which is the reason the method is public rather than internal).

## Fix

Add a public helper on [src/InductorParser/Rule.cs](src/InductorParser/Rule.cs) that sets the FlattenType as an overridable default without marking the policy as user-explicit. It's public, not internal, because the same trap bites any user who writes their own composing factory (`static Rule Comment() => And(Literal("//"), ScanUntil(...)).Flatten(FlattenType.Delete)`), so the escape hatch has to be reachable from grammar code:

```csharp
public virtual Rule FlattenByDefault(FlattenType type)
{
    CheckFlattenChangeAllowed(type, nameof(FlattenByDefault));
    FlattenType = type;
    return this;
}
```

`CheckFlattenChangeAllowed` is the shared precondition check factored out of `Flatten` (not-sealed, plus the non-Preserve-versus-`.As` contradiction). `FlattenByDefault` is the same as `Flatten` minus the `_flattenPolicyExplicitlySet = true` assignment. It's `virtual` so `LateBoundRule` can forbid it the same way it forbids `Flatten` (a FlattenType on a transparent forwarding rule is never consulted).

In [src/InductorParser/Rules.cs](src/InductorParser/Rules.cs), change the three composing factories from `.Flatten(FlattenType.Delete)` to `.FlattenByDefault(FlattenType.Delete)`:

- `EndOfLine` (around line 777)
- `InlineWhitespace` (around line 719)
- `AnyWhitespace` (around line 743)

`Identifier` uses `.Flatten(FlattenType.Preserve)` and doesn't need to change. `.As(name)` returns early when the FlattenType is already Preserve, so the user-explicit flag doesn't matter there.

Only FlattenType needs this treatment. It's the only configurable property with a *class default* a composing factory has to override (`Or` / `And` / `OneOrMore` default to `Flatten`). `Name`, the pinned `Id`, and the error message all start unset, so a factory that doesn't touch them leaves them genuinely overridable already — and a factory that does set them and expects the caller to override is contradicting itself, which the set-once throws on `.As` / `.WithError` correctly catch.

## Verify the Fix

Run the same command:

```
dotnet test --filter "EndOfLine_factory_supports_As_for_tree_find"
```

The test passes. Also run the full `EndOfLineRuleTests` and `WhitespaceRuleTests` fixtures to confirm the pre-existing behavior didn't regress:

```
dotnet test --filter "FullyQualifiedName~EndOfLineRuleTests|FullyQualifiedName~WhitespaceRuleTests"
```

All `EndOfLineRuleTests` and `WhitespaceRuleTests` tests pass, and the full suite stays green (5641 / 5643, the 2 skips are the pre-existing surrogate-not-normalizable cases).
