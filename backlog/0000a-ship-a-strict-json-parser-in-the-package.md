# Ship a strict JSON parser in the package

A real app (GuitarTabs, the Unity guitar tab app) is replacing its vendored copy of SimpleJSON with a JSON parser built on InductorParser. SimpleJSON accepts broken input without an error: cut-off text like `{"tokenData": {"accessToken": "abc"` comes back as a partial object. The app reads sync state files, cloud provider responses, and soon a 2.8 MB SongbookPro library export where a cut-off file would quietly import part of the library, so it needs a parser that rejects anything that isn't complete, valid JSON.

InductorParser already has a JSON grammar, but only as a test example (`src/InductorParser.Tests/E2EExamples/JsonGrammar.cs` with its consumer `JsonParserTyped.cs`). The benchmark grammar (`src/Benchmarks/Json/InductorParsers/InductorJsonGrammar.cs`) handles only strings, objects and arrays. For now the app is copying the example into its own repo and fixing it there. This item is to do the same work here, in the package, so the app (and anyone else) can use it and delete its copy.

## What the example grammar gets wrong

Its own comment says it skips "leading-zero checks on numbers, surrogate-pair validation on \u escapes". Reading it against RFC 8259 turns up a few more. Each one needs a failing test before it's fixed:

- Input normalization. `JsonGrammar.cs:126` calls `Json.Compile()`, which compiles with FormC (`Rule.cs:763`), so the input is NFC-normalized before parsing. A JSON string has to come back exactly as written. A decomposed "é" ("e" + U+0301) in a string value would come back precomposed. It should compile with `Compile(null)`, or the parser should otherwise guarantee string values are untouched.
- Whitespace. The grammar uses `Optional(AnyWhitespace())` between tokens, and `AnyWhitespace()` (`Rules.cs:736`) is `EndOfLine()` or `TokenSet.InlineWhitespace`, which is every Unicode space and line terminator (NO-BREAK SPACE, IDEOGRAPHIC SPACE, U+2028, VT, FF and so on). RFC 8259 section 2 allows only space, tab, LF and CR. So `{"a": 1}` with a real NO-BREAK SPACE parses, and it shouldn't.
- Leading zeros. `JsonNumber` is `Optional(sign), digits, ...` (`JsonGrammar.cs:63`), so `01` and `-007` parse. The RFC's `int` is `zero / ( digit1-9 *DIGIT )`.
- Control characters in strings. The string body is `ScanUntil(stopAt: "\"", ...)` (`JsonGrammar.cs:50`), so a raw TAB, LF or NUL inside a string is accepted. RFC 8259 section 7 says U+0000 through U+001F must be escaped.
- Unpaired surrogates. `DecodePiece` (`JsonParserTyped.cs:178`) turns each `\uXXXX` into one `char`, so `"\uD800"` decodes to a lone high surrogate, and `"\uDC00\uD800"` to two surrogates in the wrong order. Either reject them or decide on a documented replacement (U+FFFD), but don't hand back invalid UTF-16 without saying so.
- Grapheme tokens around quotes. One token is one grapheme cluster, so a string whose first character is a combining mark (`"́abc"`, U+0301 right after the opening quote) may merge with the quote into a single token that `Token('"')` doesn't match. Write a test to find out. If it fails, the string body probably needs a rune-level scan.

## What the app needs that the example doesn't have

- A parse error callers can catch by name (something like `JsonParseException`, not `FormatException` or `InvalidOperationException`), with the position where the JSON broke as a `SourcePosition` (line and column), so a corrupted state file can report where it's damaged.
- Reading a missing key without an exception. `JsonObjectValue`'s indexer (`JsonParserTyped.cs:259`) throws `KeyNotFoundException`. The app's callers read optional fields constantly, so it needs `TryGetValue`, or a "missing" value that reads as empty, or both. Pick one on purpose and document it.
- Exact numbers. Every number becomes a `double` (`JsonParserTyped.cs:54`), which loses precision past 2^53. Keep the number's source text so callers can read it as `long`, `int` or `decimal` exactly, the way System.Text.Json's `JsonElement.TryGetInt64` works.
- Member order. Objects are a `Dictionary`, which doesn't promise to keep order. Keep members in source order, which matters for round trips and for writing.
- Duplicate keys. `CreateObjectFrom` keeps the last one silently (`dictionary[key] = value`). RFC 8259 leaves this to the implementation, so choose (last wins, first wins, or an error) and document it.
- A writer. The app also builds JSON for request bodies and saved files, so it needs a tree it can build in code plus a writer that escapes correctly. One caller puts JSON in an HTTP header (Dropbox's `Dropbox-API-Arg`), where everything outside ASCII has to be written as `\uXXXX`, so the writer needs an "ASCII only" option.
- netstandard2.1 and Unity. The example uses records, which need the `IsExternalInit` shim under netstandard2.1. It must build in the package's netstandard2.1 target, avoid reflection (IL2CPP), and work without threads (WebGL).
- Speed on big input. The app's biggest file is a 2.8 MB, single-line JSON object with about 950 records. Add it (or a generated file of the same shape, since the real one is full of copyrighted song text) to the JSON benchmarks.

## Where it goes

A public `InductorParser.Json` namespace in the package: the grammar, the value types, the parse entry point and the writer. Moving the test example there (instead of writing a second one) keeps one JSON grammar in the repo. The E2E example tests can then test the shipped version.

## Verify

- RFC 8259 conformance: the JSONTestSuite cases (github.com/nst/JSONTestSuite). Every `y_` file parses, every `n_` file fails, and each `i_` file's result is written down in a test so it can't change by accident.
- Each gap above has a test that failed before the fix and passes after.
- Cut-off input fails at every cut point: take a valid document and check that every proper prefix of it fails to parse.
- Round trip: parse, write, parse again gives the same tree, and writing a tree built in code gives the expected bytes, including the ASCII-only header case.
- The netstandard2.1 build compiles and its tests pass, and the 2.8 MB benchmark has a recorded time.
