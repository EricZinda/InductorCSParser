using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using InductorParser.SyntaxTree;

namespace InductorParser.Tests;

// Cross-cutting helpers shared by more than one test fixture. The
// trace-format helpers live in TraceTestHelpers.cs (a separate file
// because they have a distinct one-helper-pattern story); everything
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
    // id, name, flatten policy, FirstConsumedRunes, Advance. Two
    // grammars with the same snapshot are structurally identical for
    // every property a Compile pass can mutate. NUnit's string-diff
    // output then names the exact rule and field that drifted on a
    // mismatch, instead of a bare "the grammars differ."
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
            .Append("|FirstRunes=").Append(rule.FirstConsumedRunes)
            .Append("|Advance=").Append(rule.Advance)
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
}
