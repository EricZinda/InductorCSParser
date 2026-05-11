# `Tree.Find(rule)` returns null for a rule whose default FlattenType is Delete or Flatten

In the Versionize ConventionalCommit rewrite under [E2ESamples/Versionize/Rewrite/ConventionalCommitParserRewrite.cs](../E2ESamples/Versionize/Rewrite/ConventionalCommitParserRewrite.cs) the BreakingMarker rule is:

```csharp
public static readonly Rule BreakingMarker = Token('!')
    .As("breaking")
    .Flatten(FlattenType.Preserve);
```

The natural-looking version (without the explicit `.Flatten(FlattenType.Preserve)`) doesn't work: `Token('!')` defaults to `FlattenType.Delete`, so the wrapper Symbol is filtered out during tree assembly and `Tree.Find(BreakingMarker)` returns null even when the rule matched.

Same shape for the type rule:

```csharp
public static readonly Rule Type = ZeroOrMore(OneOf(WordChars))
    .As("type")
    .Flatten(FlattenType.Preserve);    // <- needed; ZeroOrMore default is Flatten
```

Without the explicit Preserve, `Tree.Find(Type)` returns null because ZeroOrMore is FlattenType.Flatten and lifts its children into the parent's child list.

The behavior is correct given the FlattenType semantics — Delete means "don't appear in the tree," Flatten means "lift my children up" — but it's surprising in combination with `.As(name)`. A grammar author writing `Token('!').As("breaking")` is signaling "I want to identify this rule by name later." The fact that the same `.As(name)` requires a separate `.Flatten(FlattenType.Preserve)` to make Find work is a footgun.

## Possible fixes

1. **`.As(name)` implicitly switches FlattenType to Preserve** when the rule's current FlattenType is Delete. The Preserve-by-default for named rules matches what most grammar authors expect: "I named this so I can find it." Risk: existing grammars that rely on a named-Delete rule's Symbol being filtered would change behavior. Worth surveying the existing E2EExamples grammars to see if this would silently break anything.
2. **Doc-comment on `Symbol.Find`** spelling out the FlattenType-awareness: "Find walks the tree depth-first matching by Id. A rule whose FlattenType is Delete or Flatten doesn't have a wrapper Symbol in the tree, so Find returns null even if the rule matched. Use `.Flatten(FlattenType.Preserve)` on the rule (after `.As(name)`) if you want Find to surface it." Currently the doc says what Find does, not what stops it from finding things.
3. **A diagnostic mode that warns** when a rule has `Name != null` but `FlattenType != Preserve`. Compile-time warning, no runtime cost. Probably the cleanest fix because it surfaces the friction at grammar-build time, not parse-time-with-mysterious-null.

Option 3 fits the existing CompileNormalizationOffenders / CollectPinnedIds machinery: another sweep that flags "this named rule is unfindable as written."

## Verify the friction (write test first)

```csharp
var marker = Token('!').As("marker");   // intentionally NO .Flatten(Preserve)
var rule = And(Token('a'), Optional(marker), Token('b')).Flatten(FlattenType.Preserve);
rule.Compile();

var result = rule.Parse("a!b");
Assert.That(result.Success, Is.True);
Assert.That(result.Tree!.Find(marker), Is.Not.Null);   // currently null; would pass under any of the proposed fixes
```

The fix's regression test goes in `RuleTests.cs` or a new `NamedFindTests.cs`.
