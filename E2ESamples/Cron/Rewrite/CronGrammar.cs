// 5-field POSIX crontab expression grammar.
//
//   <cron>    ::= <field> WS <field> WS <field> WS <field> WS <field> EOF
//   <field>   ::= <item> (',' <item>)*
//   <item>    ::= <base> ('/' <step>)?
//   <base>    ::= '*' | <number> '-' <number> | <number>
//   <number>  ::= [0-9]+
//   <step>    ::= [0-9]+
//
// The five fields are minute, hour, day-of-month, month, and
// day-of-week, with value ranges 0-59, 0-23, 1-31, 1-12, and 0-7
// (0 and 7 both meaning Sunday). Those ranges are integer-value
// constraints, not character classes, so the grammar parses the
// *structure* of every field and CronParser does the range, step,
// and descending-range checks after the parse. The README explains
// why that split lands where it does.
//
// Three grammar shapes here are deliberate and the README's friction
// notes explain each one:
//
//  1. The whitespace separator between fields is built into each
//     field rule (every field after the first starts with a required
//     InlineWhitespace) instead of sitting between fields in the
//     top-level And. That makes a too-short expression report
//     "expected a value for the <name> field" at end of input
//     instead of a bare "unexpected end of input".
//
//  2. The item at the head of a field (FirstItem) and the item after
//     a comma (ListItem) are two aliases of one shared item rule.
//     Only ListItem carries the "expected a value after ','" message.
//     One shared named instance would attach that message to
//     head-of-field failures too, and it would then shadow the
//     per-field message. See the README.
//
//  3. Each field is built by a factory so it gets its own name and
//     its own .WithError. .As and .WithError both mutate the rule and
//     are set-once, so the five fields can't share one instance.

using InductorParser;
using static InductorParser.Rules;

namespace CronSample.Rewrite;

public static class CronGrammar
{
    public static readonly Rule Cron;
    public static readonly Rule MinuteField;
    public static readonly Rule HourField;
    public static readonly Rule DayOfMonthField;
    public static readonly Rule MonthField;
    public static readonly Rule DayOfWeekField;

    // FirstItem is the item at the head of a field. ListItem is an
    // item after a comma. Same shape, two instances (see note 2 above).
    public static readonly Rule FirstItem;
    public static readonly Rule ListItem;

    // Item-level rules, shared across all five fields. Post-parse
    // validation finds them with FindAll scoped to one field node, so
    // one shared instance each is what makes that lookup work.
    public static readonly Rule Wildcard;
    public static readonly Rule SingleValue;
    public static readonly Rule RangeValue;
    public static readonly Rule RangeStart;
    public static readonly Rule RangeEnd;
    public static readonly Rule StepValue;

    static CronGrammar()
    {
        // A fresh run-of-digits leaf each call. Several positions need
        // their own named instance (SingleValue, RangeStart, RangeEnd,
        // StepValue) so post-parse validation can tell a bare value
        // from a range endpoint from a step.
        static Rule Digits() => ScanWhile(TokenSet.Ascii.Digits, minimumCount: 1);

        Wildcard = Token('*').As("wildcard");
        SingleValue = Digits().As("single");

        RangeStart = Digits().As("rangeStart");
        RangeEnd = Digits()
            .As("rangeEnd")
            .WithError("expected a number after '-' to finish the range");
        // '-' is flipped to Preserve so RangeValue.ToString()
        // round-trips the source text ("9-17", not "917").
        RangeValue = And(RangeStart, Token('-').Preserve(), RangeEnd)
            .As("range");

        StepValue = Digits()
            .As("stepValue")
            .WithError("expected a step number after '/'");
        // '/' Preserve for the same round-trip reason as '-' above.
        var step = And(Token('/').Preserve(), StepValue);

        // A range is tried before a bare number because both start
        // with a digit. Committing to the number first would leave the
        // "-17" tail of "9-17" dangling.
        var baseValue = Or(Wildcard, RangeValue, SingleValue);

        // One item shape, two aliases so each use can carry its own
        // name and message (see note 2 at the top of the file).
        // Only ListItem carries the after-comma message. The two names
        // differ because .As(string) names must be unique within a
        // grammar, even for two aliases of the same nonterminal.
        var item = And(baseValue, Optional(step));
        FirstItem = Alias(item).As("item");
        ListItem = Alias(item)
            .As("listItem")
            .WithError("expected a value after ',' in the list");

        var commaSeparatedItem = And(Token(','), ListItem);

        Rule Field(string name, bool leadingSeparator)
        {
            var body = leadingSeparator
                ? And(InlineWhitespace(), FirstItem, ZeroOrMore(commaSeparatedItem))
                : And(FirstItem, ZeroOrMore(commaSeparatedItem));
            return body
                .As(name)
                .WithError($"expected a value for the {name} field: " +
                           "a number, a range like 1-5, a list like 1,15,30, " +
                           "a step like */5, or '*'");
        }

        MinuteField = Field("minute", leadingSeparator: false);
        HourField = Field("hour", leadingSeparator: true);
        DayOfMonthField = Field("day-of-month", leadingSeparator: true);
        MonthField = Field("month", leadingSeparator: true);
        DayOfWeekField = Field("day-of-week", leadingSeparator: true);

        Cron = And(
            MinuteField,
            HourField,
            DayOfMonthField,
            MonthField,
            DayOfWeekField,
            Eof().WithError("a cron expression has exactly 5 fields " +
                "(minute hour day-of-month month day-of-week); remove the extra text")
        ).As("cron");

        Cron.Compile();
    }
}
