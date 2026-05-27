using System.Globalization;
using System.Text;

namespace InductorParser.SyntaxTree;

// Name-aware debug helpers for raw Symbol trees. Most callers will
// use ParseResult.PrintTree() rather than this extension, since
// ParseResult already carries the grammar reference implicitly. This
// extension exists for the occasional case where someone has a bare
// Symbol and the Rule it came from but not the ParseResult (e.g.
// inside a rule's own ChildRules traversal).
public static class SymbolExtensions
{
    // Render a parse tree to a string for debug output. Each node gets
    // its own line, indented two spaces per level. Character-leaf nodes
    // (ids in the Unicode scalar range) render as `'c'` because their
    // name and matched text are the same character and a separate
    // `c: "c"` form would just be noise. A character leaf whose rule
    // was named via .As("...") falls through to the long
    // `<name>: "<text>"` form so the user-supplied name is visible.
    // Every non-character node renders in the long form regardless.
    //
    // Intended for grammar debugging on small inputs. The root's quoted
    // text is the entire matched input, so don't point this at a 100 MB
    // parse tree and expect anything useful.
    public static string PrintTree(this Symbol symbol, Rule rule)
    {
        var builder = new StringBuilder();
        AppendNode(symbol, rule, builder, depth: 0);
        return builder.ToString();
    }

    private static void AppendNode(Symbol symbol, Rule rule, StringBuilder builder, int depth)
    {
        for (int i = 0; i < depth; i++) builder.Append("  ");

        int idValue = symbol.Id.Value;
        if (idValue >= 0 && idValue < SymbolRanges.CharacterRangeEnd)
        {
            // Character leaf: id is the code point. NameOf returns either
            // the user-supplied .As("...") name (when set) or the rune's
            // own text (the default for unnamed character rules). When the
            // returned name matches the rune text, the leaf is unnamed and
            // we use the compact `'c'` form. Otherwise the user named it,
            // so we fall through to the long form so the name is visible
            // alongside the matched text.
            string? charName = rule.NameOf(symbol.Id);
            // Invalid scalar values (surrogate halves in the 0xD800..0xDFFF
            // gap) aren't representable as a Rune. Render them as U+FFFD
            // REPLACEMENT CHARACTER, the Unicode-designated marker for
            // "this code point can't be encoded." Same character .NET's
            // decoders use for ill-formed input.
            string runeText = Rune.IsValid(idValue) ? new Rune(idValue).ToString() : "�";
            if (charName != null && charName != runeText)
            {
                builder.Append(charName).Append(": \"").Append(symbol.ToString()).Append('"');
            }
            else
            {
                builder.Append('\'');
                if (charName == null)
                    builder.Append('�');
                else if (IsControlOrLineSeparator(idValue))
                    builder.Append("U+").Append(idValue.ToString("X4"));
                else
                    builder.Append(charName);
                builder.Append('\'');
            }
        }
        else
        {
            string? name = rule.NameOf(symbol.Id) ?? "<unknown>";
            builder.Append(name).Append(": \"").Append(symbol.ToString()).Append('"');
        }
        builder.Append('\n');

        foreach (var child in symbol.Children)
            AppendNode(child, rule, builder, depth + 1);
    }

    // True for runes that corrupt a single-line tree dump when written
    // verbatim. Same set TokenSet.ToString escapes for the same reason:
    //   * Control (Cc): LF, CR, VT, FF, NEL, and the rest of the C0/C1
    //     block. (This is exactly what char.IsControl reports.)
    //   * LineSeparator (Zl): U+2028.
    //   * ParagraphSeparator (Zp): U+2029.
    // Format characters (Cf) like ZWJ are deliberately NOT included: they
    // pass through the multi-rune render path unmolested and the same is
    // expected here. Supplementary-plane runes can't land in any of these
    // categories, so the BMP-only fast path is enough.
    private static bool IsControlOrLineSeparator(int codepoint)
    {
        if (codepoint < 0 || codepoint > char.MaxValue) return false;
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory((char)codepoint);
        return category == UnicodeCategory.Control
            || category == UnicodeCategory.LineSeparator
            || category == UnicodeCategory.ParagraphSeparator;
    }
}
