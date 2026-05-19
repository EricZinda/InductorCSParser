# WithinToken drops a forced inner .WithError's forced flag when surfacing it

`WithinToken` runs its inner rule on a separate sub-lexer. When the inner rule fails, `WithinTokenRule.TryParseRule` transfers the inner's deepest failure onto the outer lexer:

```csharp
outerLexer.RecordFailure(outerTransaction.StartPosition, subLexer.DeepestFailureMessage ?? ErrorMessage, ErrorForced);
```

`RecordFailure`'s third argument is the `forced` flag. This call passes `ErrorForced` (*WithinToken's own* forced flag), not the inner rule's. `subLexer.DeepestFailureMessage` carries the inner's deepest message but not the slot it sat in. So a rule with `.WithError("msg", forced: true)` *inside* a `WithinToken` records a forced failure on the sub-lexer, and when WithinToken transfers it, it lands in the outer lexer's non-forced (named) slot whenever WithinToken itself has no `.WithError` (the common case, where `ErrorForced` is false).

`docs/ErrorArchitecture.md` ranks failures depth-first with one exception, rule 1: "Forced wins. If any forced failure exists, it is reported." That rule only sees failures actually recorded in the forced slot. A forced inner `.WithError` demoted to the named slot loses that protection. A *shallower* `forced` `.WithError` elsewhere in the grammar now outranks it, and a deeper non-forced failure shadows it, exactly the two outcomes `forced` exists to override. The mirror half of the same conflation: because the transferred message is `subLexer.DeepestFailureMessage ?? ErrorMessage`, WithinToken's *own* `.WithError` is only a fallback used when the inner has no message, so it can never compete with (let alone, when forced, dominate) an inner message.

The user-visible consequence: a grammar author who marks a rule used inside `WithinToken` with `forced: true` (a `WithinToken(emojiSequence.WithError("invalid emoji sequence", forced: true))`-style strictness check, say) sees that message silently outranked by an unrelated shallower `forced` message, and a `.WithError` placed on the `WithinToken` itself never surfaces while the inner carries one of its own. The depth-primary rework (commit `8a78e72`) leans on the forced/non-forced split harder than the old model did, so the demotion is more consequential now, not less.

## Verify the Bug (Write Test First)

The tests go in the existing `src/InductorParser.Tests/Rules/WithinTokenRuleTests.cs`. They use a real `WithinToken` scenario. An emoji "reaction" is a thumbs-up emoji optionally carrying a skin-tone modifier, which is one grapheme cluster built from a base rune plus a modifier rune, so the cluster's runes have to be validated inside the token.

```csharp
private const int ThumbsUp = 0x1F44D;
private const int ThumbsDown = 0x1F44E;
private static readonly TokenSet SkinToneModifiers = TokenSet.Range(0x1F3FB, 0x1F3FF);

[Test]
public void Forced_WithError_inside_WithinToken_keeps_its_forced_flag()
{
    // A message is an emoji reaction (a '+' then the emoji) or a
    // slash-command, each with a forced .WithError summary. On a '+'
    // followed by the wrong emoji the reaction branch fails at offset 1
    // and the command branch at offset 0. The deeper forced failure wins.
    var reaction = And(
        Token('+'),
        WithinToken(
            And(Token(ThumbsUp), Optional(OneOf(SkinToneModifiers)))
                .WithError("a reaction must be a thumbs-up emoji", forced: true)));
    var command = And(Token('/'), OneOrMore(OneOf(TokenSet.Ascii.Letters)))
        .WithError("a command must start with '/'", forced: true);

    var result = Or(reaction, command).Parse("+" + char.ConvertFromUtf32(ThumbsDown));

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorMessage, Is.EqualTo("a reaction must be a thumbs-up emoji"));
    Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
}

[Test]
public void WithinToken_own_forced_WithError_outranks_an_inner_named_hint()
{
    // The inner emoji check carries a named hint. The WithinToken carries
    // a forced summary. The forced summary outranks the named hint.
    var reaction = WithinToken(
        And(Token(ThumbsUp).WithError("expected a thumbs-up"),
            Optional(OneOf(SkinToneModifiers))))
        .WithError("not a recognized reaction", forced: true);

    var result = reaction.Parse(char.ConvertFromUtf32(ThumbsDown));

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorMessage, Is.EqualTo("not a recognized reaction"));
}
```

A third test, `Forced_inner_WithError_surfaces_when_WithinToken_fails`, is a sanity check that a forced inner `.WithError` surfaces at all (it passes both before and after the fix).

Run with:

```
dotnet test --filter "FullyQualifiedName~WithinTokenRuleTests"
```

Pre-fix both tests above fail: the first reports `"a command must start with '/'"` at offset 0 (the demoted inner message lost to the shallower forced one), the second reports `"expected a thumbs-up"` (WithinToken's own `.WithError` never competed).

## Fix

In `src/InductorParser/WithinTokenRule.cs`, the inner-failed branch must transfer the inner's deepest message *preserving its forced flag*, then record WithinToken's own message so ranking can pick between them. `Lexer.SaveFailureState()` exposes the sub-lexer's `FailureStateSnapshot`. Its `ForcedMessage` is non-null exactly when `subLexer.DeepestFailureMessage` is the forced one:

```csharp
var innerFailureState = subLexer.SaveFailureState();
string? innerMessage = subLexer.DeepestFailureMessage;
if (innerMessage != null)
    outerLexer.RecordFailure(outerTransaction.StartPosition, innerMessage,
        forced: innerFailureState.ForcedMessage != null);
outerLexer.RecordFailure(outerTransaction.StartPosition, ErrorMessage, ErrorForced);
```

The inner record is written first, so a same-slot same-position tie goes to the inner, matching `And` / `Alias`. The trailing call records WithinToken's own `.WithError` (or a mechanical fallback when `ErrorMessage` is null) so it competes via normal ranking. Both land at `outerTransaction.StartPosition`, the cluster boundary WithinToken already reports for inner failures, so the position handling from backlog `1000` is unchanged. The EOF branch and the inner-matched-only-a-prefix branch are left alone, since those are WithinToken's own failures and correctly use its own flag.

## Verify the Fix

Re-run:

```
dotnet test --filter "FullyQualifiedName~WithinTokenRuleTests"
```

The two new tests pass. The full `InductorParser.Tests` suite reports 5724 pass / 2 pre-existing skips / 1 pre-existing unrelated failure. That failure is `WithinToken_WithError_surfaces_over_deeper_orphan_from_abandoned_Or_alternative`, a stale test whose premise (a shallow `.WithError` beating a deeper rejected-branch orphan) contradicts the depth-primary model the `8a78e72` rework landed. It fails on a clean checkout too. The E2E samples stay green. The `ExperimentalSrc` state-machine suite is unaffected at 101 pass. Its one failure, `LiteralIgnoreAsciiCase_does_not_fold_non_ascii`, is a pre-existing `LiteralIgnoreAsciiCaseRule` constructor mismatch unrelated to this change.
