# TOML 1.0 — InductorParser sample

A real-world parser ported to InductorParser as part of the recurring
backlog item `z2wr-rewrite-a-real-world-github-parser-using-inductor`.

What's parsed: TOML 1.0 (https://toml.io/en/v1.0.0). Configuration
file format with tables, key/value pairs, arrays, inline tables, four
string flavors, three integer bases, floats, booleans, four date-time
flavors, comments. The full spec, not a subset.

## Where the "original" came from

TOML's canonical home is https://toml.io. We discovered the format
through that home page (rather than through a github code search), and
the spec's formal ABNF grammar is the artifact in `Original/`. See
`Original/NOTICE.md` for the rationale.

For behavioral comparison the test project pulls in Tomlyn
(https://www.nuget.org/packages/Tomlyn, BSD-2-Clause) via NuGet so the
rewrite's parse outputs and reject-on-bad-input behavior can be checked
against a published reference. Tomlyn's source isn't copied into this
sample; the version is pinned in `Toml.csproj`.

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
      Toml.csproj
      README.md

## Side-by-side line counts

The headline metric for one of these ports: how many lines of
InductorParser code does it take to implement the formal spec?

    Artifact                               Lines     Notes
    -------------------------------------  --------  --------------------------------
    Original/toml-1.0.0.abnf                 243     Full ABNF, comments + blank lines
    Rewrite/TomlGrammar.cs                   505     Grammar + factories + comments
    Rewrite/TomlValue.cs                     134     Typed AST records
    Rewrite/TomlParser.cs                    430     Tree walker + value decoders
    Rewrite/ total                          1069

The grammar file alone is a fair "what does it take to spell out TOML
in InductorParser" answer. The other two files cover the consumer
side (typed AST, escape decoding, datetime parsing, table-merging
semantics) which the ABNF doesn't describe at all. Tomlyn (the
reference C# parser we compare against) is several thousand lines of
hand-rolled lexer+parser+models for the same job, so the
parser-combinator shape buys real concision when the cost of the
runtime is acceptable.

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

## What round 1 covered

The four levels of testing:

1. SmokeTests — accept/reject sanity. 8 tests.
2. AstTests — input → typed AST projection, drawn from the spec's
   worked examples. 18 tests, including dotted keys, nested tables,
   arrays of tables, every value kind.
3. ErrorPositionTests — pin the line/column of the deepest failure
   for four malformed inputs. 5 tests.
4. TomlynComparisonTests — round-trip the same inputs through
   Tomlyn and assert equivalent typed values. Plus assert both
   parsers reject the same bad inputs. 8 tests.

Total: 39 tests, all green.

What's NOT covered by round 1:

- Full TOML semantic-validation rules (table-defined-twice
  detection beyond the duplicate-key case, the rule that an inline
  table can't be extended later, the rule that dotted-key prefixes
  mark sub-tables as "implicitly closed for future [section]
  redefinition"). The grammar accepts these inputs; the consumer's
  validation is partial.
- Strict control-character rejection inside basic-string bodies.
  The grammar's character class follows the spec, but a
  comprehensive scrubber test that walks 0x00..0x1F isn't in the
  test corpus.
- Performance characterization. The grammar isn't tuned for hot
  paths; ScanWhile-vs-ZeroOrMore tradeoffs in string bodies are
  picked for correctness, not throughput.

These would each be follow-up rounds.

## Friction filed as backlog items

Each piece of friction encountered during this port became a separate
backlog item under `backlog/`:

- `srct-symbol-needs-raw-source-text-accessor-that-byp.md` — Token /
  Literal / EndOfLine factories default to FlattenType.Delete, which
  silently strips chars from `symbol.ToString()` of containing rules.
  Bit the port on date-time punctuation, float decimal point,
  integer-base prefix, basic-string escape backslash, multi-line
  string newlines, and special-float inf/nan mnemonics — five
  separate decoding bugs from one underlying default. Proposes a
  `ParseResult.RawSourceTextOf(symbol)` accessor.
- `prwr-or-preserve-wrapper-vs-inner-rule-dispatch-doc.md` — when an
  Or is wrapped with `.As(name).Preserve()`, `symbol.Is(InnerRule)`
  returns false on the wrapper; consumer code has to drill into
  `Children[0]` to dispatch. Gap in the JsonGrammar example.

## License

The TOML 1.0 specification is MIT, by Tom Preston-Werner et al. See
`Original/LICENSE.md`. The Tomlyn package is BSD-2-Clause and is
consumed via NuGet (no source copied into this tree).

The InductorParser-side code in `Rewrite/` and `Tests/` is part of
this repo and follows the repo's license.
