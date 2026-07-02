# InductorParser project notes for Claude

## Use Invariant.That for internal invariants, not silent defensive code

When you'd otherwise write a silent fixup like `if (len <= 0) len = 1; // defensive` or a verbose `throw new InvalidOperationException("Internal: ...")`, use `Invariant.That(condition, message)` (defined in `src/InductorParser/Invariant.cs`). Failure throws an `InductorParserBugException` whose message starts with "Invariant violated:" and ends with "This is an invariant assertion that should never happen." `Invariant` is public so a user-defined rule can assert its own invariants the same way, which is why the message stays neutral instead of blaming InductorParser.

The check is `[MethodImpl(AggressiveInlining)]` and the throw is `[MethodImpl(NoInlining)]`, same hot-path pattern as `Lexer.ThrowBudgetExceeded`, so it's safe on the parser's inner loops.

The message is an `[InterpolatedStringHandler]` parameter, same shape as `Lexer.Trace`. When the condition holds, the compiler skips every `AppendLiteral` / `AppendFormatted` inside the `$"..."`, so a call like `Invariant.That(len > 0, $"len={len} at {_position}")` pays nothing for the formatting on the success path. Use `$"..."` freely to include state like positions, lengths, or rule names that would help debug a real bug if it ever fires. A plain string literal works too (resolves to the string overload), but lose nothing by writing `$"..."` even when there are no holes. There's a regression test in `InductorParser.Tests/Core/InvariantTests.cs` that puts an `Interlocked.Increment` inside the interpolation hole to prove the deferral really happens.

This is for conditions a user couldn't trigger by misusing the public API. Things like "NextTokenLength returned <= 0 inside a `position < endPosition` loop" or "the rune-only AdvanceWhileRuneIn was handed a multi-rune set". The point is to fail predictably the moment an internal invariant breaks instead of silently producing wrong output.

Don't use it for user-API errors. `.As("x")` called twice, `.Bind(...)` on an already-bound `LateBoundRule`, two reachable rules sharing a name or explicit `SymbolId`, "rule was never bound" at parse time, etc. are all bugs in the user's grammar and keep their existing `InvalidOperationException` with a helpful message that points at the fix. The two exception types are how you tell "InductorParser bug" from "user grammar bug" apart.

Tests for invariant breaks should expect `InductorParserBugException` specifically, not `InvalidOperationException`. The two types are deliberately unrelated. `InductorParserBugException` derives straight from `Exception` so a stray `catch (InvalidOperationException)` somewhere up the stack can't swallow a parser bug report.
