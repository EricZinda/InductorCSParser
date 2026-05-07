# EofRule trace renders lone surrogate or partial grapheme cluster for unconsumed token


EofRule's failure trace at [src/InductorParser/EofRule.cs:19](../src/InductorParser/EofRule.cs#L19) writes `$"found {lexer.Input[lexer.Position]}"`, which indexes the input as one UTF-16 code unit. When the unconsumed character at the current lexer position is a supplementary-plane rune (one rune, two chars: every emoji past the BMP, the mathematical bold A `𝐀`, etc.) the indexer returns just the lone high surrogate, so the trace line reads `found ?` with the replacement glyph. Under `Compile(null)` an unconsumed multi-rune grapheme cluster (`e` + combining acute, the CRLF cluster, an emoji ZWJ sequence) returns just the first rune, so a parse failure trace shows `found e` for an input the user perceives as `é`.

Same pattern fixed for the default `{character}` placeholder in `BuildErrorMessage` (closed backlog c9p3) and called out in [docs/PotentialBugSources.md](../docs/PotentialBugSources.md) under "Char-unit rendering in user-facing strings". Trace lines that quote the current token are the next instance to find. Other rules (Token, Literal, OneOf, etc.) already render the full token via `lexer.Input.Substring(token.Offset, token.Length)` after a `Read`. EofRule doesn't read (it's checking, not consuming), so it has to peek the next token's length explicitly.

The fix is one line: swap `lexer.Input[lexer.Position]` for `lexer.Input.Substring(lexer.Position, lexer.PeekTokenLength(lexer.Position))`. ASCII messages are unchanged (one char equals one grapheme there).

## Verify the Bug (Write Test First)

In [src/InductorParser.Tests/Rules/EofRuleTests.cs](../src/InductorParser.Tests/Rules/EofRuleTests.cs), import `using static InductorParser.Tests.UnicodeExamples;` and add:

```csharp
[Test]
[RecursiveEngineOnly]
public void Eof_trace_failure_renders_full_grapheme_for_supplementary_plane()
{
    // The unconsumed character in EofRule's failure trace should be
    // the full token the lexer would have read, not the first UTF-16
    // code unit. A waving-hand emoji is one rune / one grapheme but
    // two UTF-16 chars (a surrogate pair), so input[position] is the
    // lone high surrogate and rendering that lies about what the
    // parser actually saw. Same shape as the {character}-placeholder
    // bug fixed in BuildErrorMessage; trace lines that quote the
    // current token are the next instance documented in
    // PotentialBugSources.md "Char-unit rendering in user-facing strings."
    var sink = NewSink();
    AllOf(Eof()).Parse(WavingHandGrapheme, new ParseOptions { TraceSink = sink });

    string expected = Lines(
        "   FAIL | Eof: found " + WavingHandGrapheme,
        "   FAIL | AllOf: symbol #0"
    );
    Assert.That(sink.ToString(), Is.EqualTo(expected));
}

[Test]
[RecursiveEngineOnly]
public void Eof_trace_failure_renders_full_grapheme_for_decomposed_cluster()
{
    // Under Compile(null), a decomposed grapheme like "e + combining
    // acute" stays as two chars / one cluster. EofRule's trace should
    // report the full cluster the lexer would have read, not just
    // the first rune. Without this, a parse failure trace shows "e"
    // for an input the user perceives as "é".
    var sink = NewSink();
    var rule = AllOf(Eof());
    rule.Compile(null);
    rule.Parse(LatinEAcuteGrapheme, new ParseOptions { TraceSink = sink });

    string expected = Lines(
        "   FAIL | Eof: found " + LatinEAcuteGrapheme,
        "   FAIL | AllOf: symbol #0"
    );
    Assert.That(sink.ToString(), Is.EqualTo(expected));
}
```

Run:

```
dotnet test --filter "FullyQualifiedName~EofRuleTests"
```

Both tests fail before the fix. The supplementary-plane test reports `found ?` (the high surrogate alone, which displays as the replacement character and eats the following newline in the assertion message). The decomposed-grapheme test reports `found e` instead of `found é`.

## Fix

In [src/InductorParser/EofRule.cs](../src/InductorParser/EofRule.cs), replace the trace call:

```csharp
int tokenLength = lexer.PeekTokenLength(lexer.Position);
TraceFailure(lexer, $"found {lexer.Input.Substring(lexer.Position, tokenLength)}");
```

`Lexer.PeekTokenLength` already exists as the non-advancing way to get the length of the next token (one grapheme cluster in normal mode, one rune in the WithinToken sub-lexer mode). It mirrors what `lexer.Read` would return without moving the cursor, so the substring is exactly the token the parser was looking at when it failed.

## Verify the Fix

Re-run the same command. Both new tests pass. The full suite still passes.

```
dotnet test
```
