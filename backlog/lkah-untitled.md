# Untitled

- Lookahead shortcut in BetweenInclusive / Or / SM Lowerer silently suppresses descendant WithError messages

The "can I skip this rule?" lookahead shortcut shows up in three sites that all gate on `ErrorMessage == null`:

1. `BetweenInclusiveRule.TryParseRule` outer shortcut (src/InductorParser/BetweenInclusiveRule.cs lines 60-82). Bypasses Inner.TryParse on a peeked-rune mismatch when AtLeast == 0 (success-path) or the count rule will fail anyway (failure-path).
2. `OrRule.TryParseRule` per-child shortcut (src/InductorParser/OrRule.cs line 46). Skips a child whose FirstConsumedTokens doesn't include the peeked rune.
3. `Lowerer.CanSkipUnreachableAlt` in the state-machine engine (ExperimentalSrc/InductorParser/StateMachine/Lowerer.cs line 523). The SM mirror of OrRule's per-child shortcut: lowers a `CheckPeekedRuneInSet` opcode ahead of an alternative's `PushBacktrack` so the engine can route past the alternative without ever opening its frame.

All three intend "if the rule itself has a friendly .WithError, run it so the message can reach DeepestFailureMessage." The bug is that the gate consults only the rule's OWN ErrorMessage, not its descendants'. When the rule is a composite (Or, And, BetweenInclusive) whose own ErrorMessage is null but whose subtree contains a leaf with `.WithError("...")`, the shortcut fires, the composite is never entered, every `RecordFailure` call inside its subtree is silently skipped, and the user-facing error falls back to the generic positional template instead of the grammar author's text.

Example shapes that bite:

* `OneOrMore(Or(Token('a').WithError("expected 'a'"), Token('b')))` parsing input that doesn't start with `a` or `b`. The Or has no own ErrorMessage. BetweenInclusive's shortcut sees `Inner.ErrorMessage == null && Inner.CannotMatchLookahead == true`, fires the failure-path shortcut, and "expected 'a'" never gets recorded.
* `And(ZeroOrMore(Or(Token('a').WithError("expected 'a'"), Token('b'))), Token('z'))` parsing input that starts with neither `a` nor `b` nor `z`. ZeroOrMore's success-path shortcut fires (AtLeast == 0), commits empty, and Token('a').WithError never records. The And then fails on Token('z') at offset 0 with the generic positional message instead of "expected 'a'".
* `Or(Or(Token('a').WithError("expected 'a'"), Token('b')), Token('c'))` parsing input that starts with none of `a`/`b`/`c`. The outer Or's per-child shortcut skips the inner Or because the inner Or's own ErrorMessage is null, and "expected 'a'" never records. Same shape fires in the SM engine via `Lowerer.CanSkipUnreachableAlt` lowering a `CheckPeekedRuneInSet` ahead of the inner Or's `PushBacktrack`.

The bug only fires when the lookahead shortcut would prove the composite can't match. For inputs the shortcut doesn't apply to (the composite is entered and runs its own loop or per-child shortcut), descendant WithError messages reach DeepestFailureMessage normally. So the bug shows up specifically when grammar authors write `OneOrMore(Or(... .WithError(...) ...))` style and the user hands the parser an input that misses the very first lookahead. That's a common misuse pattern, exactly the case where a friendly error message would help.

The scanner-skip optimization in `BetweenInclusiveRule.TryCreateScannerSkip` (lines 130-200) has a related but ultimately benign shape: it gates on `alternative.ErrorMessage != null` too, but the optimization only fast-forwards the lexer to the next candidate or EOF, where the regular Inner.TryParse runs anyway. With the OrRule per-child fix in place, the descendant's RecordFailure call still fires at those landing positions, so the message reaches the deepest-failure slot. No code change needed at the scanner-skip site, but a regression test pins the combined behavior.

## Verify the Bug (Write Test First)

Four regression tests, three for the recursive engine and one for the state-machine engine.

Tests in `src/InductorParser.Tests/Rules/BetweenInclusiveRuleTests.cs` next to the existing `BetweenInclusive_inner_WithError_wins_at_equal_depth`:

```csharp
[Test]
public void BetweenInclusive_descendant_WithError_surfaces_when_lookahead_shortcut_would_fire()
{
    // OneOrMore(Or(Token('a').WithError("want 'a'"), Token('b'))) against "c".
    // BetweenInclusive failure-path shortcut.
    var rule = OneOrMore(Or(Token('a').WithError("want 'a'"), Token('b')));
    var result = rule.Parse("c");

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
}

[Test]
public void BetweenInclusive_descendant_WithError_surfaces_when_zero_or_more_shortcut_would_fire()
{
    // ZeroOrMore success-path shortcut variant.
    var rule = And(
        ZeroOrMore(Or(Token('a').WithError("want 'a'"), Token('b'))),
        Token('z'));
    var result = rule.Parse("c");

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
}

[Test]
public void BetweenInclusive_descendant_WithError_surfaces_under_scanner_skip_pattern()
{
    // Scanner-skip pattern: ZeroOrMore(Or(realMatch, AnyToken.Delete)).
    // Pinning test that the OrRule per-child fix correctly handles this
    // case at the landing position the scanner-skip jumps to.
    var realMatch = And(Token('a').WithError("want 'a'"), Token('b'));
    var rule = And(
        ZeroOrMore(Or(realMatch, AnyToken().Flatten(FlattenType.Delete))),
        Token('z'));
    var result = rule.Parse("y");

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
}
```

Test in `src/InductorParser.Tests/Rules/OrRuleTests.cs` next to `Or_all_children_fail_at_same_position_first_writer_wins`:

```csharp
[Test]
public void Or_descendant_WithError_surfaces_when_per_child_shortcut_would_skip_composite()
{
    // Or(Or(Token('a').WithError("want 'a'"), Token('b')), Token('c')) against "d".
    var rule = Or(Or(Token('a').WithError("want 'a'"), Token('b')), Token('c'));
    var result = rule.Parse("d");

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    Assert.That(result.ErrorMessage, Is.EqualTo("want 'a'"));
}
```

Test in `ExperimentalSrc/InductorParser.Tests/StateMachineParserTests.cs` next to `WithError_on_OneOf_surfaces_at_failure`, exercising the same grammar shape through the SM engine:

```csharp
[Test]
public void Or_descendant_WithError_surfaces_when_per_alt_peek_skip_would_skip_composite()
{
    var rule = Or(Or(Token('a').WithError("want 'a'"), Token('b')), Token('c'));
    var stateMachine = StateMachineParser.Parse(rule, "d", new ParseOptions());

    Assert.That(stateMachine.Success, Is.False);
    Assert.That(stateMachine.ErrorCharIndex, Is.EqualTo(0));
    Assert.That(stateMachine.ErrorMessage, Is.EqualTo("want 'a'"));
}
```

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~Or_descendant_WithError_surfaces|FullyQualifiedName~BetweenInclusive_descendant_WithError"
dotnet test ExperimentalSrc/InductorParser.Tests/InductorParser.StateMachine.Tests.csproj --filter "FullyQualifiedName~Or_descendant_WithError_surfaces_when_per_alt_peek_skip"
```

Expected before any fix: all five failures with the generic positional template ("Parse failed at offset 0: unexpected 'c'." / "...'d'." / "...'y'.") instead of the WithError text.

## Fix

Four-file change across two engines.

In `src/InductorParser/Rule.cs`, add an `internal bool HasErrorMessageInSubtree` property populated by Compile. The default is `true` (pessimistic: shortcuts that consult it stay disabled for any rule not yet sealed). A post-order Compile walker ORs each rule's own `_errorMessage != null` with every descendant's value:

```csharp
// Alongside FirstConsumedTokens / Advance at the top of the class:
internal bool HasErrorMessageInSubtree { get; private set; } = true;

// In Compile, after ComputeRuleStartAll:
visited.Clear();
var computingErrors = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
ComputeHasErrorMessageInSubtreeAll(this, visited, computingErrors);

// New private static, alongside ComputeRuleStartAll / SealAll:
private static void ComputeHasErrorMessageInSubtreeAll(Rule r, HashSet<Rule> visited, HashSet<Rule> computing)
{
    if (visited.Contains(r)) return;
    if (!computing.Add(r)) return; // cycle: leave at pessimistic default (true)
    bool any = r._errorMessage != null;
    foreach (var child in r.Children)
    {
        ComputeHasErrorMessageInSubtreeAll(child, visited, computing);
        any |= child.HasErrorMessageInSubtree;
    }
    r.HasErrorMessageInSubtree = any;
    computing.Remove(r);
    visited.Add(r);
}
```

In `src/InductorParser/BetweenInclusiveRule.cs`, change the outer shortcut gate:

```csharp
if (!lexer.PreserveAllSymbols && !Inner.HasErrorMessageInSubtree)
{
    // ... shortcut body unchanged
}
```

In `src/InductorParser/OrRule.cs`, change the per-child shortcut gate the same way:

```csharp
if (!loneSurrogate && child.CannotMatchLookahead(peekValue) && !child.HasErrorMessageInSubtree)
{
    continue;
}
```

In `ExperimentalSrc/InductorParser/StateMachine/Lowerer.cs`, change `CanSkipUnreachableAlt`:

```csharp
private static bool CanSkipUnreachableAlt(Rule child)
{
    if (child.Advance != Advance.Always) return false;
    if (child.HasErrorMessageInSubtree) return false;  // was: child.ErrorMessage != null
    if (child.FirstConsumedTokens.Equals(TokenSet.Universe)) return false;
    return true;
}
```

The `HasErrorMessageInSubtree` flag is the right replacement for `ErrorMessage == null` at every site that's deciding "is this rule safe to skip on a lookahead miss?" The old half-check would skip a composite child whose own ErrorMessage is null even when a deeper rule in its subtree carries the user's message. The new check is the OR of every descendant's setting, which is the actual question the shortcut needs to answer.

The scanner-skip optimization in `TryCreateScannerSkip` (lines 130-200) has the same gate shape on `alternative.ErrorMessage != null` but doesn't need a code change. The optimization only fast-forwards the lexer to the next candidate or EOF, then the regular Inner.TryParse runs at the landing position. With the OrRule per-child fix in place, the per-child skip at the landing position respects HasErrorMessageInSubtree and the descendant's RecordFailure still runs. The scanner-skip regression test pins the combined behavior so a future change to either site that breaks the interaction shows up immediately.

The performance cost is minor: one extra TryParse / opened-frame call per shortcut firing, only for grammars where some descendant has `.WithError`. The shortcut still applies to subtrees with no WithError descendants (the common case).

## Verify the Fix

Re-run the four tests:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~Or_descendant_WithError_surfaces|FullyQualifiedName~BetweenInclusive_descendant_WithError"
dotnet test ExperimentalSrc/InductorParser.Tests/InductorParser.StateMachine.Tests.csproj --filter "FullyQualifiedName~Or_descendant_WithError_surfaces_when_per_alt_peek_skip"
```

Expected: all five pass. Then run the full suite and the state-machine suite to confirm nothing else broke. Pre-fix the main suite passes 1908 / 1910 (2 timing skips); post-fix the suite passes 1912 / 1914 (the four new regression tests in the main suite plus the existing 2 skips). State-machine suite: 96 / 96 pass before, 97 / 97 pass after (the new SM regression test).

## Sibling notes

The recursive engine's leaf-emit shortcut for `OneOfRule` / `NoneOfRule` / `AnyTokenRule` / `WithinTokenRule` (the four rune-as-leaf-id rules) has its own user-pin gate, fixed earlier as backlog `p1nd`. That fix added `IsUserSymbolIdPinned` to `Rule`. The state-machine engine's `Stepper.Step_EmitLeafOneOf` opcode reads from `SymbolMetadata`, which doesn't carry user-identification fields yet. Same metadata-extension follow-up applies: when the SM compare suite grows a fixture for the `.As(SymbolId)` shape, the metadata struct needs both `IsUserSymbolIdPinned` and `HasErrorMessageInSubtree` flags so the SM mirrors of those leaf gates can be written.
