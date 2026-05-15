using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InductorParser.Tests;

// Failsafe for unicode literals in test sources. Some editors and tools
// silently rewrite non-ASCII characters: NFC<->NFD normalization, stripped
// zero-width joiners, reordered combining marks, glyph-vs-escape conversions.
// The Canary helper wraps a literal at its use site and asserts at runtime
// that the literal's codepoint sequence matches the expected list. The
// codepoint args are int literals, which stay ASCII in source no matter what
// an editor does. If the literal gets rewritten, the runtime check throws
// with the description so the failure message names what was supposed to be
// there.
//
// Use via `using static InductorParser.Tests.CanaryHelper;` so the bare name
// Canary(...) is callable at the site.
internal static class CanaryHelper
{
    // Verify that `literal` decodes to exactly the given `codepoints`. Throws
    // InvalidOperationException naming `description` if they don't match.
    // Returns `literal` so the call can be used as a field initializer:
    //
    //     public static readonly string LatinEAcute =
    //         Canary("é", "Latin e with acute, precomposed", 0x00E9);
    //
    // The lone-surrogate case (a string holding a single unpaired surrogate
    // code unit, valid in .NET strings but not a valid Unicode scalar) is
    // reported with the code unit value, so the spec for "\uD800" is
    // 0xD800. Rune.EnumerateRunes would collapse it to U+FFFD and lose the
    // information.
    public static string Canary(string literal, string description, params int[] codepoints)
    {
        if (literal is null) throw new ArgumentNullException(nameof(literal));
        if (description is null) throw new ArgumentNullException(nameof(description));
        if (codepoints is null) throw new ArgumentNullException(nameof(codepoints));

        var actual = EnumerateCodepoints(literal).ToArray();
        if (actual.Length == codepoints.Length)
        {
            bool match = true;
            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] != codepoints[i]) { match = false; break; }
            }
            if (match) return literal;
        }

        var message = new StringBuilder();
        message.Append("Canary fired: '").Append(description).Append("'.\n");
        message.Append("  Expected codepoints: ").Append(FormatCodepoints(codepoints)).Append('\n');
        message.Append("  Actual codepoints:   ").Append(FormatCodepoints(actual)).Append('\n');
        message.Append("An editor or tool likely rewrote the source. ");
        message.Append("Non-ASCII codepoints in test sources go through Canary(literal, description, codepoints).");
        throw new InvalidOperationException(message.ToString());
    }

    // Walk the string by UTF-16 code units. Paired surrogates collapse to
    // one supplementary codepoint. Lone surrogates report their code unit
    // value (in the U+D800..U+DFFF range) so the canary can verify
    // intentional malformed-input fixtures like HighSurrogateMinText.
    private static IEnumerable<int> EnumerateCodepoints(string literal)
    {
        int index = 0;
        while (index < literal.Length)
        {
            char current = literal[index];
            if (char.IsHighSurrogate(current)
                && index + 1 < literal.Length
                && char.IsLowSurrogate(literal[index + 1]))
            {
                yield return char.ConvertToUtf32(current, literal[index + 1]);
                index += 2;
            }
            else
            {
                yield return current;
                index += 1;
            }
        }
    }

    private static string FormatCodepoints(int[] codepoints)
    {
        if (codepoints.Length == 0) return "[]";
        var builder = new StringBuilder();
        builder.Append('[');
        for (int i = 0; i < codepoints.Length; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append("U+").Append(codepoints[i].ToString("X4"));
        }
        builder.Append(']');
        return builder.ToString();
    }
}
