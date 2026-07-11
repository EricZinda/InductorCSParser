# IL2CPP pass: every test fails, TokenSet.Graphemes CRLF throws under legacy StringInfo

Running `./src/InductorParser.Tests/runil2cpptest.sh` now gets all the way to executing tests on the IL2CPP player, and every one of the 2,426 discovered tests fails, including the three hand-written smoke tests. One root cause takes them all down:

```
System.ArgumentException : Graphemes element at index 0 ("\r\n") contains
more than one grapheme cluster. Each element must be exactly one cluster.
If you want a set of runes, use Runes(string).
Parameter name: clusters
  at InductorParser.TokenSet.Graphemes (System.String[] clusters)
```

`TokenSet.LineTerminators` is a `static readonly` field built by `BuildLineTerminators()`, which starts with `Graphemes("\r\n")` (TokenSet.cs). The validation inside `Graphemes` asks the runtime how many grapheme clusters the string holds. On .NET 5+ the answer is one: UAX #29 rule GB3 (CR x LF) keeps CRLF glued together. On Unity's Mono and IL2CPP runtimes, `StringInfo` is the legacy pre-UAX-#29 implementation, which splits CRLF into two text elements, so `Graphemes("\r\n")` throws. That exception breaks TokenSet's static initialization, and since essentially every grammar touches a TokenSet, every test dies on the same `TypeInitializationException`. `TokenSet.Ascii.AnyWhitespace` has a second `Graphemes("\r\n")` with the same problem.

## Why there's no small fix

Special-casing CRLF inside `Graphemes` would let TokenSet initialize, but it wouldn't make the tests pass. The lexer segments input with the same runtime machinery, so on legacy runtimes CRLF arrives as two tokens, `Literal("\r\n")` and `EndOfLine()` behave differently than on .NET 5+, and the whole grapheme-heavy part of the suite (ZWJ sequences, regional-indicator flags, Thai SARA AM, keycaps) still diverges. The suite is written against UAX #29 extended grapheme clusters, and the legacy segmenter simply doesn't implement them.

The real fix is the one the conformance suite's header comment already anticipates: vendor a UAX #29 grapheme segmenter into the library and stop depending on the runtime's `StringInfo`. Then every runtime (.NET 8, Mono, IL2CPP) segments identically, the `GraphemeBreakConformanceTests` corpus verifies the vendored implementation directly, and the IL2CPP pass becomes a real signal again.

## What already works

The harness chain is fixed and verified end to end from WSL: Unity detection on both `/c` and `/mnt/c`, `wslpath` conversion for paths handed to Windows executables, LF line endings on both `.sh` scripts, the sync script copying all root-level helper files, and four test-source portability fixes (two C# 12 collection expressions, one `string.EnumerateRunes` call, one `Assert.Multiple`) that Unity's compiler rejected. The run compiles, builds the IL2CPP player, executes all 2,426 tests, and writes `test-results/il2cpp-playmode-results.xml`. The failures are the library's runtime dependency, not the harness.

## Repro

```
./src/InductorParser.Tests/runil2cpptest.sh
```

Then look at any failure in `test-results/il2cpp-playmode-results.xml`.
