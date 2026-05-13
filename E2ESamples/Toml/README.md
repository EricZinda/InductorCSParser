# TOML 1.0 — InductorParser sample

A TOML 1.0 parser (https://toml.io/en/v1.0.0) implemented on
InductorParser. Configuration file format with tables, key/value pairs,
arrays, inline tables, four string flavors, three integer bases, floats,
booleans, four date-time flavors, comments. Full spec, not a subset.

`Original/` holds the spec's formal ABNF grammar plus its license. The
test project pulls in Tomlyn (https://www.nuget.org/packages/Tomlyn,
BSD-2-Clause) via NuGet for behavioral comparison; Tomlyn's source is
not copied into this tree, the version is pinned in `Toml.csproj`.

## Layout

    E2ESamples/Toml/
      Original/                  spec ABNF + license + notice
      Rewrite/                   InductorParser TOML grammar + AST
        TomlGrammar.cs
        TomlValue.cs
        TomlParser.cs
      Tests/                     NUnit tests
        SmokeTests.cs
        AstTests.cs
        ErrorPositionTests.cs
        TomlynComparisonTests.cs
        UnicodeTests.cs
      Toml.csproj
      README.md

## Line counts

    Artifact                               Lines     Notes
    -------------------------------------  --------  --------------------------------
    Original/toml-1.0.0.abnf                 243     Full ABNF, comments + blank lines
    Rewrite/TomlGrammar.cs                   548     Grammar + factories + comments
    Rewrite/TomlValue.cs                     134     Typed AST records
    Rewrite/TomlParser.cs                    454     Tree walker + value decoders
    Rewrite/ total                          1136

`TomlGrammar.cs` is what it takes to spell TOML out in InductorParser.
The other two files cover the consumer side (typed AST, escape decoding,
datetime parsing, table-merging semantics) which the ABNF doesn't
describe. Tomlyn is several thousand lines of hand-rolled
lexer+parser+models for the same job.

## Worked example

Input:

    title = "TOML Example"

    [database]
    server = "192.168.1.1"
    ports = [ 8001, 8001, 8002 ]
    enabled = true

    [servers.alpha]
    ip = "10.0.0.1"

    [servers.beta]
    ip = "10.0.0.2"

Code:

    var root = TomlParser.Parse(input);
    var title = (string)root["title"];                    // "TOML Example"
    var server = (string)root["database"]["server"];      // "192.168.1.1"
    var alphaIp = (string)root["servers"]["alpha"]["ip"]; // "10.0.0.1"

Bad-input example:

    Input:   key = "unclosed
    Output:  ParseResult.Success = false
             ErrorLine = 0
             ErrorColumn = 15
             ErrorMessage = "Expected closing '\"' to end basic string"

The error position is reported in LSP's zero-based line/column
convention (also available as a flat character index via
`result.ErrorCharIndex`). The wording comes from the `WithError(...)`
hooks the grammar attaches to closing-delimiter rules.

## Running the tests

From the repo root:

    dotnet test E2ESamples/Toml/Toml.csproj

Or as part of the full solution:

    dotnet test InductorParser.sln

## What's covered

Five test fixtures, 62 tests total:

1. SmokeTests — accept/reject sanity. 8 tests.
2. AstTests — input to typed AST projection, drawn from the spec's
   worked examples. 18 tests covering dotted keys, nested tables,
   arrays of tables, every value kind.
3. ErrorPositionTests — pin the line/column of the deepest failure
   for four malformed inputs. 5 tests.
4. TomlynComparisonTests — round-trip inputs through Tomlyn and
   assert equivalent typed values; assert both parsers reject the
   same bad inputs. 8 tests.
5. UnicodeTests — control-character rejection in basic and literal
   strings, surrogate / out-of-range rejection in `\uXXXX` and
   `\UXXXXXXXX` escapes, and a pin on the parser's NFC-normalization
   behavior for keys (which diverges from Tomlyn; see
   `backlog/nfcd-...`). 23 tests.

What's NOT covered:

- Full TOML semantic-validation rules (table-defined-twice detection
  beyond the duplicate-key case, the rule that an inline table can't
  be extended later, the rule that dotted-key prefixes mark sub-tables
  as "implicitly closed for future [section] redefinition"). The
  grammar accepts these inputs; the consumer's validation is partial.
- Performance characterization. The grammar isn't tuned for hot paths;
  ScanWhile-vs-ZeroOrMore tradeoffs in string bodies are picked for
  correctness, not throughput.

## License

The TOML 1.0 specification is MIT, by Tom Preston-Werner et al. See
`Original/LICENSE.md`. The Tomlyn package is BSD-2-Clause and is
consumed via NuGet (no source copied into this tree).

The InductorParser-side code in `Rewrite/` and `Tests/` is part of
this repo and follows the repo's license.
