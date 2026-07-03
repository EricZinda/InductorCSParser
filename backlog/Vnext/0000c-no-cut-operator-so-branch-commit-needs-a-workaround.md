# No cut operator, so a branch the parser is committed to has to be made unreachable by other means

Found while rewriting serde_json's JSON string parser (`E2ESamples/JsonStringEscapes/`). The grammar has three classification branches for what comes after a `\u` escape, tried in order by an `Or`:

1. **leading-surrogate-pair**: `D8xx` then `\u` then `DCxx` (the whole pair).
2. **lone-trailing-surrogate**: `DCxx` then always-fail.
3. **plain BMP escape**: any four hex digits.

The intent: once `D8xx` has been matched by branch 1, the parser is committed to "this is a surrogate pair." If the rest of branch 1 fails (no trailing surrogate after), the whole `Or` should fail with "lone leading surrogate," not silently fall through to branch 3 and let the BMP-escape rule accept the lone surrogate as a perfectly ordinary four-hex-digit value.

PEG's `Or` doesn't express that. If branch 1 fails after matching `D8xx`, `Or` tries branch 2 (fails on the second hex digit), then branch 3, and branch 3 happily matches `\uD800` because four hex digits is four hex digits. The "lone leading surrogate" error never surfaces.

Some PEG dialects have a *cut* operator. After matching a committing prefix in a branch, `cut` declares the branch committed: if the rest of it fails, the whole `Or` fails, and the other branches don't get a chance. A `Cut()` after `D8xx` in branch 1 would express the intent of the grammar directly, in the rule the intent belongs to.

InductorParser has no `Cut()`. The workaround is to make the wrong branch unable to take the input. Branch 3 carries a `Not(And(dDigit, anySurrogateSecondDigit))` in front of its four hex digits, refusing to match anything whose first two digits look like surrogate territory. When `\uD800` reaches branch 3 after branch 1 fails, the `Not` sees `D` followed by `8` and fails, branch 3 fails, the `Or` fails as a whole, and the deepest recorded failure (branch 1's `Token('\\')` looking for the second escape) becomes the reported error.

It works, but the "commit to branch 1" is now spread across two rules: branch 1 declares the prefix that commits, and branch 3 has to carry a `Not(...)` that mirrors that same prefix. Any change to the surrogate classification has to be reflected in two places. A reader looking at the `Not(...)` in branch 3 has to reason backwards to figure out what other branch it's conceding to. And the natural way to write branch 3 (just "four hex digits") quietly produces wrong behavior until that `Not(...)` is added.

This is the same kind of friction the leading-zero recipe in `docs/Recipes.md` already documents (the "natural Or-first translation looks like it should work, and does for happy paths, but positions the error one column past the real problem"). The cut version is more general: any time one branch's match commits the parser to a particular interpretation, every later branch needs an explicit exclusion in its prefix.

## Where it came up

`E2ESamples/JsonStringEscapes/Rewrite/JsonStringGrammar.cs`: the `Not(And(dDigit, anySurrogateSecondDigit))` prefix on `bmpEscape`, mirroring the `D` + leading-second / trailing-second digits that `leadingSurrogatePair` and `loneTrailingSurrogate` consume.

## Fix

A grammar-level `Cut()` operator is the PEG community's standard tool for committing past a point so a later failure propagates up instead of retrying earlier alternatives. This item is the concrete grammar case the sample hit, plus a sketch of what the rewrite would look like with the operator in hand:

```csharp
var leadingSurrogatePair = And(
    dDigit, leadingSecondDigit, hexDigit, hexDigit, Cut(),   // committed past here
    Token('\\').WithError(UnexpectedEndOfHexEscapeMessage),
    Token('u').WithError(UnexpectedEndOfHexEscapeMessage),
    trailingSurrogate);

var bmpEscape = And(hexDigit, hexDigit, hexDigit, hexDigit);  // no Not(...) prefix needed
```

When branch 1's `Cut()` fires inside an `Or`, the surrounding `Or` doesn't try the other branches if the rest of branch 1 then fails. The commit lives in the rule it belongs to, the BMP escape rule reads as just "four hex digits," and a future grammar change to the surrogate classification only touches the surrogate rules.

The implementation has to choose the scope of the cut (whether `Cut()` commits the nearest enclosing `Or` or all enclosing `Or`s up to some boundary). PEG variants differ, and the design decisions doc is the right place for that conversation. This item is the use-case evidence that the feature would have somewhere to land.
