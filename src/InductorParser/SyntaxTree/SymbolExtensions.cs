using System.Text;
using InductorParser.Tracing;

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
            // Character leaf: id is the rune's code point. When the user
            // gave the rule a .As(...) name, use the long `name: "text"`
            // form so the name is visible alongside the matched text.
            // Otherwise use the compact `'c'` form, with U+FFFD standing
            // in when the id isn't a valid scalar (a surrogate half).
            string? userName = rule.UserNameOf(symbol.Id);
            if (userName != null)
            {
                builder.Append(userName).Append(": \"").Append(symbol.ToString()).Append('"');
            }
            else
            {
                string runeText = Rune.IsValid(idValue) ? new Rune(idValue).ToString() : "�";
                builder.Append('\'');
                DisplayEscape.AppendEscaped(builder, runeText);
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
}
