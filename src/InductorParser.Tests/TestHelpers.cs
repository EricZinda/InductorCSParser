using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;

namespace InductorParser.Tests;

// Which line break Lines(...) joins with. A test body that takes this
// as a [TestCase] parameter runs once per convention, so one body
// covers both LF and CRLF input. Public because it appears as a
// parameter on public [TestCase] methods.
public enum LineBreak { Lf, Crlf }

// Cross-cutting helpers shared by more than one test fixture. The
// trace-format helpers live in TraceTestHelpers.cs (a separate file
// because they have a distinct one-helper-pattern story). Everything
// else gathers here.
//
// Two categories so far:
//   - Structural snapshots. SnapshotGrammar dumps every reachable rule's
//     post-Compile state, Fingerprint dumps a Symbol list's tree shape.
//     Both produce deterministic strings so an assertion failure shows
//     up as a string diff naming the exact node that drifted, instead
//     of a generic "the trees differ."
//   - Corpus helpers. The E2E grammar tests parse arrays of canned
//     inputs and want one Assert.Fail listing every input that
//     rejected (or, for negative corpora, every input that wrongly
//     accepted). AssertAllParse / AssertAllReject do that, and Display
//     escapes control characters so failure output stays readable.
//
// Pulled in via `using static InductorParser.Tests.TestHelpers;` at
// the consumer's top, matching the TraceTestHelpers convention.
internal static class TestHelpers
{
    // ---- Structural snapshots ---------------------------------------

    // Walk the rule graph in deterministic DFS pre-order and emit one
    // line per reachable rule capturing its post-Compile state: type,
    // id, name, flatten policy. Two grammars with the same snapshot are
    // structurally identical for every property a Compile pass can
    // mutate. NUnit's string-diff output then names the exact rule and
    // field that drifted on a mismatch, instead of a bare "the grammars
    // differ."
    public static string SnapshotGrammar(Rule root)
    {
        var builder = new StringBuilder();
        var visited = new HashSet<Rule>();
        WalkRule(root, builder, visited);
        return builder.ToString();
    }

    private static void WalkRule(Rule rule, StringBuilder builder, HashSet<Rule> visited)
    {
        if (!visited.Add(rule)) return;
        builder.Append(rule.GetType().Name)
            .Append("|Id=").Append(rule.Id.Value)
            .Append("|Name=").Append(rule.Name ?? "<null>")
            .Append("|Flatten=").Append(rule.FlattenType)
            .Append('\n');
        foreach (var child in rule.Children)
            WalkRule(child, builder, visited);
    }

    // Walk a list of Symbols and emit a deterministic string capturing
    // each one's Id, FlattenType, rendered text, and child structure.
    // Two Symbol lists with the same fingerprint are structurally
    // equivalent in everything FlattenInto cares about.
    public static string Fingerprint(IEnumerable<Symbol> symbols)
    {
        var builder = new StringBuilder();
        foreach (var symbol in symbols)
            FingerprintSymbol(symbol, builder);
        return builder.ToString();
    }

    private static void FingerprintSymbol(Symbol symbol, StringBuilder builder)
    {
        builder.Append('(')
            .Append(symbol.Id.Value)
            .Append('|').Append(symbol.FlattenType)
            .Append('=').Append(symbol.ToString());
        if (symbol.Children.Count > 0)
        {
            builder.Append(":[");
            foreach (var child in symbol.Children)
                FingerprintSymbol(child, builder);
            builder.Append(']');
        }
        builder.Append(')');
    }

    // ---- Multi-line input construction ------------------------------

    // Join `lines` with the chosen line break. Use this instead of a
    // raw or verbatim string literal whenever a test input spans more
    // than one line. An embedded newline in a raw/verbatim literal is
    // a real source byte, and git's autocrlf rewrites it: the same
    // fixture is LF on a Linux checkout and CRLF on a Windows one, so
    // the test silently exercises a different input per platform. The
    // UnicodeLiteralCanaryTests cross-line-literal check bans those
    // literals for that reason. Lines builds the input from
    // single-line string literals (which autocrlf can't touch) plus an
    // explicit LineBreak, so a fixture means the same thing on every
    // checkout. A test that takes LineBreak as a [TestCase] parameter
    // then covers both line endings from one body.
    //
    // Joins, doesn't append: there's no trailing break. A fixture that
    // needs a trailing newline ends with an explicit "" element, the
    // same way a blank line in the middle is written.
    public static string Lines(LineBreak lineBreak, params string[] lines) =>
        string.Join(lineBreak == LineBreak.Crlf ? "\r\n" : "\n", lines);

    // ---- Corpus helpers ---------------------------------------------

    // Quote an input string and escape backslash plus the three
    // common whitespace control characters (\n, \r, \t) so corpus
    // failure messages stay on one line and the offending characters
    // show as visible escapes. Inputs longer than DisplayMaxLength
    // get truncated with "..." so a corpus of multi-KB documents
    // doesn't blow out the failure output.
    private const int DisplayMaxLength = 200;

    public static string Display(string input)
    {
        string truncated = input.Length > DisplayMaxLength
            ? input.Substring(0, DisplayMaxLength) + "..."
            : input;
        return "\"" + truncated
            .Replace("\\", "\\\\")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t") + "\"";
    }

    // Parse every input in `corpus` against `rule` and Assert.Fail
    // listing each input that the grammar rejected, with the column
    // and message from the parse failure. `label` names the grammar
    // for the failure-message heading. Used by E2E tests to exercise
    // large positive corpora through a single assertion.
    public static void AssertAllParse(Rule rule, string[] corpus, string label)
    {
        var rejected = new List<string>();
        foreach (var input in corpus)
        {
            var result = rule.Parse(input);
            if (!result.Success)
                rejected.Add($"  {Display(input)} (col {result.ErrorCharIndex}: {result.ErrorMessage})");
        }
        if (rejected.Count > 0)
            Assert.Fail(
                $"{label} grammar rejected {rejected.Count} of {corpus.Length} valid input(s):\n"
                + string.Join("\n", rejected));
    }

    // Mirror of AssertAllParse for negative corpora: every input
    // should fail to parse, and any that wrongly succeeded shows up
    // in the Assert.Fail message.
    public static void AssertAllReject(Rule rule, string[] corpus, string label)
    {
        var accepted = new List<string>();
        foreach (var input in corpus)
        {
            if (rule.Parse(input).Success)
                accepted.Add(Display(input));
        }
        if (accepted.Count > 0)
            Assert.Fail(
                $"{label} grammar accepted {accepted.Count} of {corpus.Length} invalid input(s):\n  "
                + string.Join("\n  ", accepted));
    }

    // ---- Error position assertions ----------------------------------

    // Assert that result describes a failure at the expected position in
    // every unit ParseResult exposes: ErrorCharIndex, ErrorLine,
    // ErrorCharColumn, ErrorTokenIndex, plus the all-in-one ErrorPosition
    // struct. Use this in tests where error-position
    // behavior is the actual subject (position translation through
    // normalization, char-index to line/column conversion, etc.) so a
    // regression in any one unit shows up as a single named assertion
    // failure. Per-rule fixtures that just spot-check ErrorCharIndex
    // don't need this, since ErrorPositionTests covers the conversion
    // math on its own.
    public static void AssertErrorPosition(
        ParseResult result,
        int charIndex,
        int line,
        int column,
        int TokenIndex)
    {
        Assert.That(result.ErrorCharIndex, Is.EqualTo(charIndex), nameof(result.ErrorCharIndex));
        Assert.That(result.ErrorLine, Is.EqualTo(line), nameof(result.ErrorLine));
        Assert.That(result.ErrorCharColumn, Is.EqualTo(column), nameof(result.ErrorCharColumn));
        Assert.That(result.ErrorTokenIndex, Is.EqualTo(TokenIndex), nameof(result.ErrorTokenIndex));

        var position = result.ErrorPosition;
        Assert.That(position, Is.Not.Null, nameof(result.ErrorPosition));
        Assert.That(position!.Value.CharIndex, Is.EqualTo(charIndex), "ErrorPosition.CharIndex");
        Assert.That(position.Value.Line, Is.EqualTo(line), "ErrorPosition.Line");
        Assert.That(position.Value.CharColumn, Is.EqualTo(column), "ErrorPosition.CharColumn");
        Assert.That(position.Value.TokenIndex, Is.EqualTo(TokenIndex), "ErrorPosition.TokenIndex");
    }

    // Assert that two ParseResults report the same error position in
    // every unit. Used by tests that compare positions across two
    // configurations (e.g. with normalization vs without) to verify
    // they agree.
    public static void AssertErrorPositionsEqual(ParseResult expected, ParseResult actual)
    {
        Assert.That(actual.ErrorCharIndex, Is.EqualTo(expected.ErrorCharIndex), nameof(actual.ErrorCharIndex));
        Assert.That(actual.ErrorLine, Is.EqualTo(expected.ErrorLine), nameof(actual.ErrorLine));
        Assert.That(actual.ErrorCharColumn, Is.EqualTo(expected.ErrorCharColumn), nameof(actual.ErrorCharColumn));
        Assert.That(actual.ErrorTokenIndex, Is.EqualTo(expected.ErrorTokenIndex), nameof(actual.ErrorTokenIndex));
        Assert.That(actual.ErrorPosition, Is.EqualTo(expected.ErrorPosition), nameof(actual.ErrorPosition));
    }
}
