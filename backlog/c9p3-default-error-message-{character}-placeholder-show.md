- Default error message {character} placeholder shows lone surrogate or partial grapheme cluster

The default `PositionalErrorTemplate` is `"Parse failed at offset {charIndex}: unexpected '{character}'."`. The `{character}` substitution in `Rule.BuildErrorMessage` ([src/InductorParser/Rule.cs:746](../src/InductorParser/Rule.cs#L746)) does `parseInput[posInParseInput].ToString()`, which returns just one UTF-16 code unit. When the unexpected token is a supplementary-plane rune (one rune, two chars: `𝐀` U+1D400, every emoji past the BMP, etc.) the substitution returns the lone high surrogate and the rendered message is `unexpected '\uD835'.`, which an editor or terminal shows as `unexpected '�'.`. When the grammar was compiled with `Compile(null)` and the input has a multi-rune grapheme cluster (`e` + combining acute, `\r\n`, an emoji ZWJ sequence) the substitution returns just the first char of the cluster, so the user sees `unexpected 'e'.` for an input the user perceives as `é`. Both shapes lie about what the parser actually saw.

The fix is one line: render the full grapheme cluster (the same unit the lexer reads as one token) instead of one char. `StringInfo.GetNextTextElement(parseInput, posInParseInput)` returns it. That keeps ASCII messages identical, fixes supplementary-plane output, and matches what the user types under `Compile(null)`.

## Verify the Bug (Write Test First)

In [src/InductorParser.Tests/Core/ErrorMessageTemplateTests.cs](../src/InductorParser.Tests/Core/ErrorMessageTemplateTests.cs):

```csharp
[Test]
public void Character_placeholder_renders_full_supplementary_plane_rune()
{
    // Bold-A (U+1D400) is one rune but two UTF-16 chars (a high
    // surrogate at offset 0, a low surrogate at offset 1). The
    // {character} placeholder is supposed to render the unexpected
    // user-perceived character. If the substitution only takes
    // parseInput[pos] it grabs a lone surrogate half, which is
    // malformed Unicode that displays as garbage. The right answer
    // is the full rune, "𝐀".
    string boldA = char.ConvertFromUtf32(0x1D400);
    var rule = Token('a');
    var result = rule.Parse(boldA);

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorMessage,
        Is.EqualTo($"Parse failed at offset 0: unexpected '{boldA}'."));
}

[Test]
public void Character_placeholder_renders_full_grapheme_cluster_under_no_normalization()
{
    // Compile(null) keeps the input verbatim, so a decomposed
    // grapheme like "e" + combining acute survives to the parser
    // as two chars / one grapheme cluster. The user perceives one
    // character ("é") and the {character} placeholder should match
    // what they see, not the bare 'e' before the combining mark.
    string eAcute = "é";
    var rule = Token('a');
    rule.Compile(null);
    var result = rule.Parse(eAcute);

    Assert.That(result.Success, Is.False);
    Assert.That(result.ErrorMessage,
        Is.EqualTo($"Parse failed at offset 0: unexpected '{eAcute}'."));
}
```

Run:

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj --filter "FullyQualifiedName~Character_placeholder"
```

Both tests fail before the fix. The supplementary-plane test reports `unexpected '\uD835'.` (the high surrogate alone, rendered as the replacement character). The decomposed-grapheme test reports `unexpected 'e'.` instead of `unexpected 'é'.`.

## Fix

In [src/InductorParser/Rule.cs](../src/InductorParser/Rule.cs), add `using System.Globalization;` to the import block, then change the `{character}` value provider in `BuildErrorMessage`:

```csharp
return FormatTemplate(options.PositionalErrorTemplate,
    PositionPlaceholders(failurePos, input),
    ("character", () => StringInfo.GetNextTextElement(parseInput, posInParseInput)));
```

`StringInfo.GetNextTextElement` is the same primitive the lexer uses to read one token, so the substituted value is exactly the token the parser was looking at when it failed.

## Verify the Fix

Re-run the same command. Both new tests pass. The full suite still passes (1789 / 1791, the two skips are pre-existing perf-timing skips).

```
dotnet test src/InductorParser.Tests/InductorParser.Tests.csproj
```
