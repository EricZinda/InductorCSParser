using System.Text;

namespace InductorParser.SyntaxTree;

// Name-aware debug helpers over raw Symbol trees. Most callers will
// reach for ParseResult.PrintTree() rather than this extension, since
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
    // `c: "c"` form would just be noise. Every other node renders as
    // `<name>: "<text>"` where text is the concatenated matched input
    // under that subtree.
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
            // Character leaf: id is the code point. Name and text are the
            // same single character, so render as `'c'` rather than
            // `c: "c"`. NameOf handles surrogate-half validation for us.
            string? charName = rule.NameOf(symbol.Id);
            builder.Append('\'').Append(charName ?? "?").Append('\'');
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
