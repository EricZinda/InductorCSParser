namespace InductorParser.Prefilter;

// LiteralRule's contribution to the required-literal prefilter: every
// match consumes exactly the expected string, so the expected text
// itself is both the required literal and a fixed-text contribution
// that And can concatenate. Case-sensitive (false) — the case-
// insensitive sibling lives in LiteralIgnoreAsciiCaseRule.cs.
internal static class LiteralRulePrefilter
{
    internal static (string Text, bool IgnoreCase)? ComputeRequiredLiteral(LiteralRule rule) =>
        (rule.ExpectedText!, false);

    internal static (string Text, bool IgnoreCase)? ComputeConcatenableText(LiteralRule rule) =>
        (rule.ExpectedText!, false);
}
