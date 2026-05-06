namespace InductorParser.Prefilter;

// LiteralIgnoreAsciiCaseRule's contribution: every match consumes a
// string that compares ASCII-case-insensitively equal to the expected
// text, so the expected text is the required literal under the
// IgnoreCase flag. Same shape as LiteralRulePrefilter but with the
// case-insensitive bit set.
internal static class LiteralIgnoreAsciiCaseRulePrefilter
{
    internal static (string Text, bool IgnoreCase)? ComputeRequiredLiteral(LiteralIgnoreAsciiCaseRule rule) =>
        (rule.ExpectedText!, true);

    internal static (string Text, bool IgnoreCase)? ComputeConcatenableText(LiteralIgnoreAsciiCaseRule rule) =>
        (rule.ExpectedText!, true);
}
