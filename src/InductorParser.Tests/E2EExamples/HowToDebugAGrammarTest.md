# Writing and debugging a grammar test

This is a starting point for trying out a grammar under the debugger. The
companion file [ScratchGrammarTests.cs](ScratchGrammarTests.cs) is throwaway
scratch space you can edit freely. The real coverage conventions live in
[docs/TestArchitecture.md](../../../docs/TestArchitecture.md). This note is
just about getting a grammar in front of you with breakpoints set.

## The shape of a grammar test

Every grammar test in this repo follows the same three steps, and
[SettingExampleTests.cs](SettingExampleTests.cs) is the canonical example:

1. A `BuildGrammar()` helper composes rules with `And` / `Or` / `Token` /
   `Optional` / `Identifier()` / `Integer()` / `AnyWhitespace()` and the rest
   of the factory methods, all from `using static InductorParser.Rules;`. It
   returns the rules you want to inspect after parsing.
2. `rule.Parse("input")` returns a result with `.Success`, `.Tree`,
   `.ErrorMessage`, `.ErrorCharIndex`, and `.Outcome`.
3. `result.Tree!.Find(someRule)!.ToString()` pulls a named subtree's matched
   text back out by rule reference, which is what you assert on.

The scratch file builds a tiny `NAME = VALUE` grammar that way, with one test
for a successful parse and one for a failure.

## Debugging it in VS Code

The one prerequisite is the C# Dev Kit extension. It provides the test runner
and the debugger. You don't need a `launch.json`, and there isn't a `.vscode`
folder in this repo. C# Dev Kit debugs tests directly.

With the workspace open at the repo root:

1. Open [ScratchGrammarTests.cs](ScratchGrammarTests.cs). Click in the left
   gutter on the `var result = pair.Parse("count = 42");` line to set a
   breakpoint.
2. Just above the `[Test]` method name there's a CodeLens reading
   `Run Test | Debug Test`. Click `Debug Test`. (Same thing from the Testing
   panel, the beaker icon in the left bar: find the test, right-click, Debug
   Test.)
3. Execution stops at your breakpoint. From there:
   - `F10` steps over `Parse`. Then expand `result` in the Variables panel and
     look at `Success`, `ErrorMessage`, `ErrorCharIndex`, and drill into `Tree`
     to see the parsed `Symbol` nodes and their children.
   - `F11` steps into the parser instead. InductorParser is a project
     reference, not a NuGet package, so its source is fully debuggable. You can
     step through `And`, `Or`, the lexer, and set breakpoints inside
     [src/InductorParser/](../../InductorParser/) too.
   - Hover any expression, or use the Watch panel / Debug Console to evaluate
     something like `result.Tree.Find(name).ToString()` live.

The second test, `Reports_where_it_failed_when_the_value_is_missing`, shows the
failure side. `ErrorCharIndex` is where the failing read started and
`ErrorMessage` says why. It also writes both with `TestContext.Out.WriteLine`,
so they show up in the test's output.

## Running it from the command line

You can run just this file with the test script at the repo root:

    ./test.sh recursive --filter "FullyQualifiedName~ScratchGrammar"

For learning the API the `Debug Test` CodeLens is the faster path, but the
filter is handy for a quick green/red check.

