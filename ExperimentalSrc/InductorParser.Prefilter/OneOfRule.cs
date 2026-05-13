namespace InductorParser.Prefilter;

// OneOfRule's contribution to the prefilter: when the set is small
// enough to name (one BMP rune, or an ASCII-letter pair that case-
// folds), every match consumes that one rune so the rune is both the
// required literal and a concatenable fixed-text contribution. Anything
// broader has too many candidate chars to act as a useful substring-
// search trigger, so we return null and the caller falls back to the
// first-rune skip.
//
// Multi-rune set entries are NOT analyzable by this static analysis.
// A set like Single('a') | Graphemes("\r\n") can match either the rune 'a'
// or the CRLF cluster, and the CRLF match's consumed text contains no
// 'a'. Returning the rune-only chars as a required literal would break
// the "every match contains this literal" contract for multi-rune
// matches. Backlog n4q7 caught this; the fix is the early null at the
// top.
internal static class OneOfRulePrefilter
{
    internal static (string Text, bool IgnoreCase)? ComputeRequiredLiteral(OneOfRule rule) =>
        ComputeConcatenableText(rule);

    internal static (string Text, bool IgnoreCase)? ComputeConcatenableText(OneOfRule rule)
    {
        // A set with any multi-rune entry can match those clusters,
        // whose consumed text doesn't have to contain any of the
        // rune-only chars. Drop the prefilter for mixed sets and let
        // the caller fall back to the first-rune lookahead skip.
        if (rule.LoweringSet.HasMultiRuneGraphemes)
            return null;

        if (!rule.LoweringSet.TryGetBmpChars(maxChars: 2, out char[] chars) || chars.Length == 0)
            return null;
        if (chars.Length == 1)
            return (chars[0].ToString(), false);
        char a = chars[0];
        char b = chars[1];
        if (IsAsciiLetter(a) && IsAsciiLetter(b) && (a | 0x20) == (b | 0x20))
            return (((char)(a | 0x20)).ToString(), true);
        return null;
    }

    private static bool IsAsciiLetter(char c) =>
        (uint)((c | 0x20) - 'a') <= ('z' - 'a');
}
