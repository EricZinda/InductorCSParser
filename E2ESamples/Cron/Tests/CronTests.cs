// End-to-end tests for the Cron appbuilding sample. Four fixtures:
//
// 1. Golden corpus: valid 5-field cron expressions. Each must parse,
//    and the typed projection must round-trip the field text.
//
// 2. Reject corpus: every category of bad input. Each must fail with a
//    positioned error whose char index is inside the input.
//
// 3. Error-position fixture: the headline of the sample. Each case
//    names a bad input, the 0-based column the error should point at,
//    and a substring the message must contain. This is the "the errors
//    are actually good" check that backlog item 018a asks for.
//
// 4. Dump: writes every reject case's real error text to a file, so
//    the README's friction report can quote real output.

using System.IO;
using System.Linq;
using NUnit.Framework;
using CronSample.Rewrite;

namespace CronSample.Tests;

[TestFixture]
public class CronGoldenTests
{
    private static readonly string[] ValidInputs =
    {
        "* * * * *",
        "0 0 * * *",
        "*/15 * * * *",
        "0 9-17 * * 1-5",
        "0,15,30,45 * * * *",
        "0 0 1 1 0",
        "59 23 31 12 7",
        "0 0 * * 7",
        "*/5 */2 1-15 */3 *",
        "5-10/2 * * * *",
        "30 4 1,15 * 5",
        "0 22 * * 1-5",
    };

    [TestCaseSource(nameof(ValidInputs))]
    public void Valid_input_parses(string input)
    {
        bool ok = CronParser.TryParse(input, out var expression, out var error);
        Assert.That(ok, Is.True, $"'{input}' should parse but failed: {error}");
        Assert.That(expression, Is.Not.Null);
    }

    [Test]
    public void Typed_projection_round_trips_each_field()
    {
        bool ok = CronParser.TryParse("*/15 9-17 * * 1-5", out var expression, out _);
        Assert.That(ok, Is.True);
        Assert.That(expression!.Minute.Items, Is.EqualTo(new[] { "*/15" }).AsCollection);
        Assert.That(expression.Hour.Items, Is.EqualTo(new[] { "9-17" }).AsCollection);
        Assert.That(expression.DayOfMonth.Items, Is.EqualTo(new[] { "*" }).AsCollection);
        Assert.That(expression.Month.Items, Is.EqualTo(new[] { "*" }).AsCollection);
        Assert.That(expression.DayOfWeek.Items, Is.EqualTo(new[] { "1-5" }).AsCollection);
    }

    [Test]
    public void Typed_projection_keeps_every_list_item()
    {
        bool ok = CronParser.TryParse("0,15,30,45 * * * *", out var expression, out _);
        Assert.That(ok, Is.True);
        Assert.That(expression!.Minute.Items, Is.EqualTo(new[] { "0", "15", "30", "45" }).AsCollection);
    }
}

[TestFixture]
public class CronRejectTests
{
    public record RejectCase(string Input, string Describes)
    {
        public override string ToString() => $"\"{Input}\" ({Describes})";
    }

    public static readonly RejectCase[] InvalidInputs =
    {
        new("",                  "empty"),
        new("   ",               "all whitespace"),
        new("* * *",             "too few fields (3)"),
        new("* * * *",           "too few fields (4)"),
        new("* * * * * *",       "too many fields (6)"),
        new("60 * * * *",        "minute out of range"),
        new("* 24 * * *",        "hour out of range"),
        new("* * 0 * *",         "day-of-month below range"),
        new("* * 32 * *",        "day-of-month above range"),
        new("* * * 13 *",        "month out of range"),
        new("* * * 0 *",         "month below range"),
        new("* * * * 8",         "day-of-week out of range"),
        new("*/0 * * * *",       "step is zero"),
        new("30-10 * * * *",     "descending range"),
        new("* * x * *",         "stray character in a field"),
        new("1,,2 * * * *",      "empty list element"),
        new("1, * * * *",        "trailing comma"),
        new("5- * * * *",        "range missing its end"),
        new("*/ * * * *",        "step missing its number"),
        new("70-80 * * * *",     "both range endpoints out of range"),
    };

    [TestCaseSource(nameof(InvalidInputs))]
    public void Invalid_input_is_rejected_with_a_position(RejectCase reject)
    {
        bool ok = CronParser.TryParse(reject.Input, out var expression, out var error);
        Assert.That(ok, Is.False, $"'{reject.Input}' ({reject.Describes}) should be rejected");
        Assert.That(expression, Is.Null);
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Message, Is.Not.Empty);
        Assert.That(error.CharIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(error.CharIndex, Is.LessThanOrEqualTo(reject.Input.Length));
    }
}

[TestFixture]
public class CronErrorPositionTests
{
    private static CronParseError ErrorOf(string input)
    {
        bool ok = CronParser.TryParse(input, out _, out var error);
        Assert.That(ok, Is.False, $"'{input}' should have been rejected");
        Assert.That(error, Is.Not.Null);
        return error!;
    }

    [Test]
    public void Minute_out_of_range_points_at_the_value()
    {
        var error = ErrorOf("60 * * * *");
        Assert.That(error.Column, Is.EqualTo(0));
        Assert.That(error.Message, Does.Contain("minute"));
        Assert.That(error.Message, Does.Contain("0-59"));
    }

    [Test]
    public void Hour_out_of_range_points_at_the_value()
    {
        var error = ErrorOf("* 24 * * *");
        Assert.That(error.Column, Is.EqualTo(2));
        Assert.That(error.Message, Does.Contain("hour"));
        Assert.That(error.Message, Does.Contain("0-23"));
    }

    [Test]
    public void Day_of_month_below_range_points_at_the_value()
    {
        var error = ErrorOf("* * 0 * *");
        Assert.That(error.Column, Is.EqualTo(4));
        Assert.That(error.Message, Does.Contain("day-of-month"));
        Assert.That(error.Message, Does.Contain("1-31"));
    }

    [Test]
    public void Step_of_zero_points_at_the_step_number()
    {
        var error = ErrorOf("*/0 * * * *");
        Assert.That(error.Column, Is.EqualTo(2));
        Assert.That(error.Message, Does.Contain("step"));
        Assert.That(error.Message, Does.Contain("minute"));
    }

    [Test]
    public void Descending_range_points_at_the_start()
    {
        var error = ErrorOf("30-10 * * * *");
        Assert.That(error.Column, Is.EqualTo(0));
        Assert.That(error.Message, Does.Contain("descending"));
    }

    [Test]
    public void Stray_character_names_the_field_it_broke_in()
    {
        var error = ErrorOf("* * x * *");
        Assert.That(error.Column, Is.EqualTo(4));
        Assert.That(error.Message, Does.Contain("day-of-month"));
    }

    [Test]
    public void Too_few_fields_names_the_first_missing_field()
    {
        // "* * *" has 3 fields. The month field (the 4th) is the first
        // one missing. The error lands at end of input.
        var error = ErrorOf("* * *");
        Assert.That(error.CharIndex, Is.EqualTo(5));
        Assert.That(error.Message, Does.Contain("month"));
    }

    [Test]
    public void Too_many_fields_points_past_the_fifth()
    {
        var error = ErrorOf("* * * * * *");
        Assert.That(error.Message, Does.Contain("5 fields"));
    }

    [Test]
    public void Empty_list_element_points_at_the_comma()
    {
        var error = ErrorOf("1,,2 * * * *");
        Assert.That(error.Column, Is.EqualTo(2));
        Assert.That(error.Message, Does.Contain("after ','"));
    }

    [Test]
    public void Range_missing_its_end_says_so()
    {
        var error = ErrorOf("5- * * * *");
        Assert.That(error.Message, Does.Contain("range"));
    }

    [Test]
    public void Empty_input_asks_for_a_cron_expression()
    {
        var error = ErrorOf("");
        Assert.That(error.Message, Does.Contain("cron expression"));
    }
}

[TestFixture]
public class CronErrorDump
{
    // Writes every reject case's real error text to a file next to the
    // test binary. The README's friction report quotes this output, so
    // running the suite regenerates the material the README is built
    // from.
    [Test]
    public void Dump_every_reject_message_to_a_file()
    {
        var lines = CronRejectTests.InvalidInputs.Select(reject =>
        {
            CronParser.TryParse(reject.Input, out _, out var error);
            return $"input: \"{reject.Input}\"  ({reject.Describes})\n  -> {error}";
        });
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cron-errors.txt");
        File.WriteAllText(path, string.Join("\n\n", lines));
        TestContext.Out.WriteLine($"wrote {path}");
        Assert.Pass();
    }
}
