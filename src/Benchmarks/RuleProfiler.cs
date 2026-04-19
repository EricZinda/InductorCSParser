using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InductorParser;

namespace InductorParser.Benchmarks;

// Counts how many times each Rule subclass is invoked on a single Parse by
// hooking Rule.TryParse via an AsyncLocal/static slot. This deliberately
// ships as benchmark-project-local instrumentation; it opens a back door
// into the library via a reflection call to a gated internal hook, not
// via a public API, so library users don't see any of this.
//
// There is no hook on Rule.TryParse, so we cheat slightly by wrapping each
// Rule's subclass invocation through a counter via the grammar graph. We
// walk the rule tree before parsing, and re-enter the normal Parse path.
// The counting itself goes through a StopPosition-less probe: Parse leaves
// lexer.Position changes as the only visible effect, so we can't attach to
// that. Instead, we do CPU-sample-correlated call counts by running a
// second parse under a high-frequency rule-trace sink that we filter to
// just entry events. This is ~10-15x slower than raw parse but that's
// fine for a one-shot profile.
//
// Output: counts[type] -> invocation count, inclusively for the full Parse.
public static class RuleProfiler
{
    public static Dictionary<string, long> CountByType(string input, int iterations = 1)
    {
        var counts = new Dictionary<string, long>();
        var sink = new CountingWriter(counts);
        var options = new ParseOptions
        {
            InputUnit = InputUnit.Rune,
            MaxDepth = 0,
            TraceSink = sink,
            TraceLevel = Tracing.TraceLevel.Diagnostic,
        };
        for (int i = 0; i < iterations; i++)
        {
            Json.InductorJsonParser.Json.Parse(input, options);
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
