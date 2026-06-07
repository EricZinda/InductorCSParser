// Cron expression parser built on InductorParser.
//
// Two layers:
//
//  1. The grammar (CronGrammar.cs) parses the structure. A structural
//     failure (a stray character, a missing field, a trailing comma)
//     comes back as a positioned error straight from the parse result.
//
//  2. This file does the semantic checks the grammar can't express,
//     because they are integer-value predicates, not character
//     classes: every value within its field's numeric range, every
//     step at least 1, every range non-descending. Each check walks
//     to the offending Symbol and reads its SourceRange so the error
//     points at the actual bad token.
//
// The headline a cron-using app gets out of this, versus the typical
// `bool IsValid(string)` regex validator:
//
//   "* 25 * * *"   regex: false        here: "column 3: hour field
//                                             value '25' is out of
//                                             range (0-23)"
//   "*/0 * * * *"  regex: true (!)     here: "column 3: minute field
//                                             step '0' must be 1 or
//                                             greater"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InductorParser;
using InductorParser.SyntaxTree;
using static CronSample.Rewrite.CronGrammar;

namespace CronSample.Rewrite;

/// One parsed field: its items as source text ("*/5", "9-17", "30").
public sealed record CronFieldValue(string Name, IReadOnlyList<string> Items);

/// A parsed, validated 5-field cron expression.
public sealed record CronExpression(
    CronFieldValue Minute,
    CronFieldValue Hour,
    CronFieldValue DayOfMonth,
    CronFieldValue Month,
    CronFieldValue DayOfWeek);

/// A parse or validation failure with a position into the input.
/// Line/Column follow the LSP 0-based convention the parser uses.
/// ToString renders a 1-based column for humans.
public sealed record CronParseError(string Message, int CharIndex, int Line, int Column)
{
    public override string ToString() => $"column {Column + 1}: {Message}";
}

public static class CronParser
{
    // Field rule, display name, and inclusive value range. The order
    // is the order the fields appear in the expression.
    private sealed record FieldSpec(Rule Rule, string Name, int Min, int Max);

    private static readonly FieldSpec[] Fields =
    {
        new(MinuteField,     "minute",       0, 59),
        new(HourField,       "hour",         0, 23),
        new(DayOfMonthField, "day-of-month", 1, 31),
        new(MonthField,      "month",        1, 12),
        new(DayOfWeekField,  "day-of-week",  0,  7),
    };

    /// Parse and validate. Throws FormatException on a bad expression.
    public static CronExpression Parse(string input)
    {
        if (!TryParse(input, out var expression, out var error))
            throw new FormatException(error!.ToString());
        return expression!;
    }

    /// Parse and validate. Returns false and a positioned error on a
    /// bad expression, true and the typed result on a good one.
    public static bool TryParse(string input, out CronExpression? expression, out CronParseError? error)
    {
        expression = null;
        error = null;

        // An all-blank input fails inside the first field with a
        // field-specific message. "expected a cron expression" reads
        // better, and only the consumer can tell "empty" apart from
        // "first field is bad" (see the README friction notes).
        if (string.IsNullOrWhiteSpace(input))
        {
            error = new CronParseError(
                "expected a cron expression: 5 space-separated fields " +
                "(minute hour day-of-month month day-of-week)", 0, 0, 0);
            return false;
        }

        var result = Cron.Parse(input);
        if (!result.Success)
        {
            error = new CronParseError(
                result.ErrorMessage, result.ErrorCharIndex, result.ErrorLine, result.ErrorColumn);
            return false;
        }

        var tree = result.Tree!;

        // Collect every semantic problem, then report the leftmost one
        // so the caret lands on the first thing the user has to fix.
        var problems = new List<CronParseError>();
        var fieldValues = new CronFieldValue[Fields.Length];

        for (int fieldIndex = 0; fieldIndex < Fields.Length; fieldIndex++)
        {
            var spec = Fields[fieldIndex];
            var fieldNode = tree.Find(spec.Rule)!;

            foreach (var value in fieldNode.FindAll(SingleValue))
                CheckValueInRange(value, spec, problems);

            foreach (var range in fieldNode.FindAll(RangeValue))
            {
                var startNode = range.Find(RangeStart)!;
                var endNode = range.Find(RangeEnd)!;
                bool startOk = CheckValueInRange(startNode, spec, problems);
                bool endOk = CheckValueInRange(endNode, spec, problems);
                if (startOk && endOk
                    && int.Parse(startNode.ToString(), CultureInfo.InvariantCulture)
                       > int.Parse(endNode.ToString(), CultureInfo.InvariantCulture))
                {
                    problems.Add(ErrorAt(startNode,
                        $"{spec.Name} field range '{range}' is descending; " +
                        "the start must not be greater than the end"));
                }
            }

            foreach (var stepNode in fieldNode.FindAll(StepValue))
            {
                var text = stepNode.ToString();
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var step)
                    || step < 1)
                {
                    problems.Add(ErrorAt(stepNode,
                        $"{spec.Name} field step '{text}' must be 1 or greater"));
                }
            }

            // FirstItem and ListItem are two rule instances (see
            // CronGrammar note 2), so a field's items come from two
            // FindAll calls. Order by source position to get them
            // left-to-right.
            var items = fieldNode.FindAll(FirstItem)
                .Concat(fieldNode.FindAll(ListItem))
                .OrderBy(item => item.SourceRange?.Start.CharIndex ?? 0)
                .Select(item => item.ToString())
                .ToList();
            fieldValues[fieldIndex] = new CronFieldValue(spec.Name, items);
        }

        if (problems.Count > 0)
        {
            error = problems.OrderBy(problem => problem.CharIndex).First();
            return false;
        }

        expression = new CronExpression(
            fieldValues[0], fieldValues[1], fieldValues[2], fieldValues[3], fieldValues[4]);
        return true;
    }

    // Adds an out-of-range problem if the node's value is outside the
    // field's [Min, Max]. Returns true when the value is a parseable
    // int in range. An unparseable run of digits (too long for Int32)
    // is by definition out of range, so it reports the same way.
    private static bool CheckValueInRange(Symbol node, FieldSpec spec, List<CronParseError> problems)
    {
        var text = node.ToString();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= spec.Min && value <= spec.Max)
            return true;

        problems.Add(ErrorAt(node,
            $"{spec.Name} field value '{text}' is out of range ({spec.Min}-{spec.Max})"));
        return false;
    }

    private static CronParseError ErrorAt(Symbol node, string message)
    {
        var range = node.SourceRange;
        return new CronParseError(message,
            range?.Start.CharIndex ?? 0,
            range?.Start.Line ?? 0,
            range?.Start.Column ?? 0);
    }
}
