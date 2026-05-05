- WithinTokenRule records mid-grapheme-cluster failure position when inner rule consumes only a prefix of the outer token

`WithinTokenRule.TryParseRule` runs its inner rule against a bounded one-rune-per-token sub-lexer over the runes of one outer grapheme cluster. When the inner rule succeeds on a prefix but doesn't consume the whole token, [src/InductorParser/WithinTokenRule.cs:99](../src/InductorParser/WithinTokenRule.cs#L99) records the failure at `subLexer.Position`. That position is in rune coordinates inside the outer token, so it lands MID-grapheme-cluster from the outer parser's view. The same shape exists at [src/InductorParser/WithinTokenRule.cs:87-89](../src/InductorParser/WithinTokenRule.cs#L87-L89) when an inner composite (e.g. `AllOf`) fails after one of its children advanced the sub-lexer past the first rune: `subLexer.DeepestFailure` carries that mid-cluster offset out into `outerLexer.RecordFailure`.

The parser-wide invariant is that recorded failure positions sit at outer-token boundaries (UAX #29 grapheme cluster boundaries). Every other rule's `RecordFailure` either uses `transaction.StartPosition` or `lexer.Position`, both of which are at cluster boundaries. WithinToken is the one place where a sub-lexer in rune mode breaks the invariant, and its rune-level offset flows straight into `lexer._deepestFailure` without being snapped back to the outer cluster's start.

User-visible fallout when this becomes the parse's deepest failure: `ParseResult.ErrorCharIndex` lands inside a cluster, `ErrorTokenIndex` over-counts (returning the index of the next cluster, since `GraphemeClusterIndex.CountClustersUpTo` counts cluster STARTS in `[0, charIndex)` and the cluster start at offset 0 falls inside the half-open range), and the `{character}` placeholder via `StringInfo.GetNextTextElement(parseInput, posInParseInput)` renders a "defective" cluster of just the inner rune the parser stopped at. For decomposed `é` (e + combining acute) under `Compile(null)` the message ends up as `Parse failed at offset 1: unexpected '́'.`, where the user actually typed the single character `é` at offset 0 and the parser saw the whole cluster as one token. This is the same shape called out under "Char-unit rendering in user-facing strings" in [docs/PotentialBugSources/c000-char-unit-rendering-in-user-facing-strings.md](../docs/PotentialBugSources/c000-char-unit-rendering-in-user-facing-strings.md), but reached via mid-cluster position recording rather than mid-cluster char indexing.

The fix is to snap both failure positions back to the outer cluster's start (`outerTransaction.StartPosition`, same value as `token.Offset`). The outer view sees the WithinToken match as one atomic token, and the failure semantically belongs at "this whole token didn't satisfy the rule," which is the cluster's start offset. Trace messages can keep the rune-level offset for debugging since they're already labeled as inner-token relative.

## Verify the Bug (Write Test First)

Add to [src/InductorParser.Tests/Rules/WithinTokenRuleTests.cs](../src/InductorParser.Tests/Rules/WithinTokenRuleTests.cs):

```csharp
[Test]
public void WithinToken_partial_inner_match_reports_outer_cluster_position()
{
    // WithinToken on a multi-rune cluster ("é" decomposed = 'e' +
    // combining acute) where the inner rule consumes only the first
    // rune. The whole grapheme is one outer token, so the failure
    // belongs at offset 0 (the cluster's start), not offset 1 (mid-
    // cluster, INSIDE the outer token). Asserts every position unit
    // because the rest of the parser only ever reports cluster-
    // boundary positions and a divergence here means user-visible
    // diagnostics lie about where the parser was looking.
    var rule = WithinToken(OneOf(TokenSet.Ascii.Letters));
    rule.Compile(null);
    var result = rule.Parse(LatinEAcuteGrapheme);

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorCharIndex, Is.EqualTo(0),
        "failing cluster starts at offset 0; offset 1 is inside the cluster");
    Assert.That(result.ErrorTokenIndex, Is.EqualTo(0),
        "input has exactly one token; an index of 1 is past-the-end");
    Assert.That(result.ErrorLine, Is.EqualTo(0));
    Assert.That(result.ErrorColumn, Is.EqualTo(0));
    Assert.That(result.ErrorPosition!.Value.CharIndex, Is.EqualTo(0));
    Assert.That(result.ErrorPosition!.Value.TokenIndex, Is.EqualTo(0));
    Assert.That(result.ErrorMessage,
        Is.EqualTo("Parse failed at offset 0: unexpected '" + LatinEAcuteGrapheme + "'."));
}

[Test]
public void WithinToken_inner_composite_failure_reports_outer_cluster_position()
{
    // The other failure path: inner is an AllOf whose first child
    // succeeds and advances the sub-lexer past the first rune, then
    // the second child fails. WithinTokenRule's inner-failure branch
    // reads subLexer.DeepestFailure (set by the second child at the
    // rune offset where it failed) and feeds that mid-cluster offset
    // to outerLexer.RecordFailure. Same outer-view invariant
    // violation as the prefix-match path above.
    var rule = WithinToken(AllOf(Token('e'), Token('b')));
    rule.Compile(null);
    var result = rule.Parse(LatinEAcuteGrapheme);

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    Assert.That(result.ErrorTokenIndex, Is.EqualTo(0));
    Assert.That(result.ErrorLine, Is.EqualTo(0));
    Assert.That(result.ErrorColumn, Is.EqualTo(0));
    Assert.That(result.ErrorMessage,
        Is.EqualTo("Parse failed at offset 0: unexpected '" + LatinEAcuteGrapheme + "'."));
}
```

Run:

```
dotnet test --filter "FullyQualifiedName~WithinTokenRuleTests"
```

Both tests fail before the fix. The first reports `ErrorCharIndex=1`, `ErrorTokenIndex=1`, message `Parse failed at offset 1: unexpected '́'.` (the combining acute alone). The second reports `ErrorCharIndex=1` similarly. After the fix both tests pass.

## Fix

In [src/InductorParser/WithinTokenRule.cs](../src/InductorParser/WithinTokenRule.cs), snap both failure positions to the outer cluster's start. Replace the inner-failure block:

```csharp
if (innerResult == null && innerOutputs.Count == 0)
{
    int innerFailurePos = Math.Max(subLexer.DeepestFailure, subLexer.Position);
    TraceFailure(outerLexer, $"inner rule failed at token rune offset {innerFailurePos - token.Offset}");
    outerLexer.RecordFailure(outerTransaction.StartPosition, subLexer.DeepestFailureMessage ?? ErrorMessage);
    return null;
}
```

And the prefix-match block:

```csharp
if (!subLexer.IsEof)
{
    int consumed = subLexer.Position - token.Offset;
    TraceFailure(outerLexer, $"inner rule consumed only {consumed}/{token.Length} of the token");
    outerLexer.RecordFailure(outerTransaction.StartPosition, ErrorMessage);
    return null;
}
```

Both `RecordFailure` calls now pass `outerTransaction.StartPosition` (== `token.Offset`) instead of the sub-lexer's rune-scoped position.

## Verify the Fix

Re-run the same command. Both new tests pass. The full suite still passes.

```
dotnet test
```
