# State-machine engine: composite Symbols carry no consumed span, so SourceRange / SourceText are empty on every composite

The state-machine engine builds its parse tree in `ExperimentalSrc/InductorParser/StateMachine/TreeBuilder.cs`. `BuildComposite` (the `parentSink.Add(new Symbol(open.SymbolId, declared, children, default, context))` call near line 152) passes `default` for the composite's `consumedSpan` because the lowered `OutputOp`s for `OpenComposite` / `CloseComposite` carry no input offsets (`OutputOp.Open` hard-codes `offset 0, length 0`).

`Symbol.SourceRange` and `Symbol.SourceText` recover a composite's match bounds from that span (`_leafChars`). With no span, `Symbol.TryGetCharSpan` returns false, so on any tree produced by `StateMachineParser.Parse`, every composite Symbol's `SourceRange` is `null` and `SourceText` is `""`. The recursive engine records the span (`lexer.Input.AsMemory(matchStart, matchLength)`) at every composite construction site, so it returns the real range and text. A consumer that reads `SourceText` off a named composite (the canonical TOML-style "recover the verbatim matched text of this node" use case from backlog 7s7s) gets an empty string from the SM engine and the right text from the recursive engine, a silent divergence.

This is the second half of the 2026-05-18 find-a-bug hunt. The first half, the SM `TreeBuilder` building Symbols with no `ParseContext` at all, is already fixed (`Symbol.DisplayName` / `Is(string)` and leaf `SourceRange` / `SourceText` under normalization now work on SM trees). This item is the remaining gap: composites still have no span.

## Verify the Bug (write test first)

Add to `ExperimentalSrc/InductorParser.Tests/StateMachineParserTests.cs`:

```csharp
[Test]
public void Composite_SourceText_recovers_the_matched_text_on_a_state_machine_parse()
{
    // The recursive engine records each composite's consumed span, so
    // Symbol.SourceText returns the verbatim matched text. The SM
    // TreeBuilder records no span, so SourceText is empty even though
    // the composite plainly matched "abcd".
    var rule = And(Literal("ab").Preserve(), Literal("cd").Preserve()).As("root");

    var recursive = rule.Parse("abcd");
    var stateMachine = StateMachineParser.Parse(rule, "abcd");

    Assert.That(recursive.Tree!.SourceText, Is.EqualTo("abcd"));
    Assert.That(stateMachine.Tree!.SourceText, Is.EqualTo("abcd"),
        "SM-built composite should report the same SourceText as the recursive engine.");
    Assert.That(stateMachine.Tree!.SourceRange, Is.Not.Null);
}
```

Run it:

```
dotnet test ExperimentalSrc/InductorParser.Tests --filter "FullyQualifiedName~Composite_SourceText_recovers_the_matched_text_on_a_state_machine_parse"
```

It fails: `stateMachine.Tree.SourceText` is `""` and `SourceRange` is `null`.

## Fix

Give `TreeBuilder` the composite's parseInput offsets so it can pass a real `consumedSpan` to the composite `Symbol` constructor. Two options:

- Preferred: have the lowerer stamp the composite's start offset on the `OpenComposite` `OutputOp` and the end offset on the matching `CloseComposite` op (extend `OutputOp.Open` / `OutputOp.Close` and the lowering states that emit them). `BuildComposite` then passes `input.AsMemory(start, end - start)` instead of `default`. This matches the recursive engine, including leading and trailing `FlattenType.Delete` children that aren't emitted in fast mode.
- Cheaper but lossy: derive the span from the first and last emitted child's bounds (`Symbol.LeafMemory` plus `MemoryMarshal.TryGetString`). This misses edge `Delete`-matched characters in fast mode (where Delete children aren't emitted), so the SM composite span would be narrower than the recursive engine's. Only acceptable if the lowerer's offset plumbing is judged too invasive.

## Verify the Fix

Re-run the test above. It passes. Also run the full SM suite and confirm no regressions:

```
dotnet test ExperimentalSrc/InductorParser.Tests
```
