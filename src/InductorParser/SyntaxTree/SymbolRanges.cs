namespace InductorParser.SyntaxTree;

// Every Symbol in the parse tree carries a SymbolId, which is just an int.
// The int is carved into three non-overlapping ranges so different kinds
// of symbol never collide, regardless of how big a grammar gets:
//
//   0x000000..0x10FFFF   Rune symbols. The id IS the Unicode code point
//                        of the matched rune. A leaf symbol for the
//                        letter 's' has id 0x73, for the guitar emoji 🎸
//                        it has id 0x1F3B8.
//
//   0x110000..0x1FFFFF   Built-in expression symbols: AllOf, FirstOf, OneOrMore,
//                        Integer, Float, InlineWhitespace, Eof. These live just
//                        above the Unicode range so they can't collide
//                        with a rune id.
//
//   0x200000..           Custom symbols. Everything a grammar author
//                        creates that isn't a character or a built-in:
//                        named rules via .As(), anonymous rules, user
//                        Rule subclasses. Assigned at Compile time.
//
// Putting the rune range at the bottom is what makes this work cheaply:
// a lexer that matches one rune can build its Symbol by treating the
// rune itself AS the id, no translation needed. Built-in and custom ids
// live past the Unicode ceiling (0x10FFFF is the highest Unicode code
// point), so they're guaranteed not to overlap.
public static class SymbolRanges
{
    public const int CharacterRangeEnd = 0x110000;   // one past the last Unicode code point
    public const int BuiltinRangeStart = 0x110000;   // first id past the Unicode range
    public const int CustomRangeStart = 0x200000;    // first id for user-defined or anonymous rules
}
