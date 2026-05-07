using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InductorParser;

namespace InductorParser.Benchmarks;

// Diagnostic tool: counts how many times each Rule subclass (And, Or,
// BetweenInclusive, etc.) gets invoked during a single parse, split by
// SUCC and FAIL outcome. Exposed via the --rule-counts CLI flag in
// the bench program, which prints a table sorted by frequency:
//
//   Rule                           Count       % total
//   Or                        12345        23.5%
//   And                           9876        18.8%
//   ...
//
// The point is optimization targeting. If Or accounts for 23% of all
// interpreter invocations, that's where to spend time. Both p500 (Or's
// required-runes dispatch) and p750 (first-rune skip on ZeroOrMore /
// Optional / OneOrMore) came out of this kind of analysis: find the
// hottest Rule subclass, find work inside it to skip.
//
// How it works:
//   1. Set ParseOptions.TraceSink to a custom TextWriter and
//      TraceLevel to Diagnostic.
//   2. Run one parse. The library emits a line per rule attempt,
//      formatted as "{indent}SUCC | {label}: ..." or "FAIL | ...".
//   3. The TextWriter parses each line, pulls the Rule subclass name
//      out of the label, and bumps a counter in the counts dict.
//
// Counts are per Rule *subclass* (the interpreter type), not per
// grammar-rule-name. That's what you want for library-level
// optimization: "how much work does the And machinery do" rather than
// "how much work does the JsonObject rule do."
//
// Runs ~10-15x slower than an untraced parse because of the per-rule
// tracing overhead, but that's fine for a one-shot profile.
//
// Benchmark-project-local on purpose. Library users don't need it.
public static class RuleProfiler
{
    public static Dictionary<string, long> CountByType(string input, int iterations = 1)
    {
        var counts = new Dictionary<string, long>();
        var sink = new CountingWriter(counts);
        var options = new ParseOptions
        {
            MaxDepth = 0,
            TraceSink = sink,
            TraceLevel = Tracing.TraceLevel.Diagnostic,
        };
        for (int i = 0; i < iterations; i++)
        {
            Json.InductorParsers.InductorJsonParser.JsonRule.Parse(input, options);
        }
        return counts;
    }

    private sealed class CountingWriter : System.IO.TextWriter
    {
        private readonly Dictionary<string, long> _counts;
        private readonly System.Text.StringBuilder _line = new();
        public CountingWriter(Dictionary<string, long> counts) { _counts = counts; }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                ProcessLine();
                _line.Clear();
                return;
            }
            if (value == '\r') return;
            _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            foreach (var c in value) Write(c);
        }

        public override void WriteLine()
        {
            ProcessLine();
            _line.Clear();
        }

        public override void WriteLine(string? value)
        {
            if (value != null) foreach (var c in value) Write(c);
            ProcessLine();
            _line.Clear();
        }

        private void ProcessLine()
        {
            // Rule outcome lines are of the form:
            //   {indent}SUCC | {label}: {message}
            //   {indent}FAIL | {label}: {message}
            // Info lines (Lexer.Read / RecordFailure) don't have SUCC/FAIL,
            // so filter those out. Label looks like "name:TraceName" or
            // just "TraceName". Use the TraceName portion as the class key.
            string line = _line.ToString();
            int pipe = line.IndexOf('|');
            if (pipe < 0) return;
            string prefix = line.Substring(0, pipe).TrimEnd();
            if (!prefix.EndsWith("SUCC") && !prefix.EndsWith("FAIL")) return;
            int bodyStart = pipe + 2;
            if (bodyStart >= line.Length) return;
            string body = line.Substring(bodyStart);
            int colon = body.IndexOf(':');
            string label = colon < 0 ? body : body.Substring(0, colon);
            // Strip any "name:" qualifier that the grammar author set via As()
            // e.g. "member:And" should key as "And". WriteTraceLine prepends
            // name with a `:` when set, so take everything after the final colon.
            int lastColon = label.LastIndexOf(':');
            string key = lastColon < 0 ? label : label.Substring(lastColon + 1);
            string outcome = prefix.EndsWith("SUCC") ? "SUCC" : "FAIL";
            string mapKey = key + " " + outcome;
            _counts.TryGetValue(mapKey, out long c);
            _counts[mapKey] = c + 1;
        }
    }
}
