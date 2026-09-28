namespace InductorParser.SyntaxTree;

/// <summary>
/// The three non-overlapping id ranges a SymbolId can fall into: rune,
/// built-in, and custom. Carving the int this way keeps the three kinds of
/// symbol from ever colliding, however big a grammar gets.
/// </summary>
/// <remarks>
/// The rune range sits at the bottom so a lexer that matches one rune can use
/// the rune value as the leaf id directly, with no translation. Built-in and
/// custom ids live past 0x10FFFF, the highest Unicode code point, so they
/// can't overlap a rune id.
/// </remarks>
public static class SymbolRanges
{
    /// <summary>
    /// Exclusive upper bound of the rune range. An id below this value is a
    /// rune leaf whose id is the Unicode code point of the matched rune: e.g. 's'
    /// has id 0x73, the guitar emoji 🎸 has id 0x1F3B8.
    /// </summary>
    public const int CharacterRangeEnd = 0x110000;

    /// <summary>
    /// Start of the range reserved for future built-in symbol ids, just past
    /// the Unicode range. Unused today: anonymous rules (<see cref="Rules.And">Rules.And</see>, <see cref="Rules.Or">Rules.Or</see>, <see cref="InductorParser.Rules.OneOrMore(InductorParser.Rule)">Rules.OneOrMore</see>,
    /// and the rest) get custom-range ids assigned at <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see>, so no Symbol
    /// currently has an id in this range.
    /// </summary>
    public const int BuiltinRangeStart = 0x110000;

    /// <summary>
    /// First id for custom symbols: named rules from <see cref="Rule.As(string)">Rule.As</see>(), anonymous rules,
    /// and user Rule subclasses. Assigned at <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> time.
    /// </summary>
    public const int CustomRangeStart = 0x200000;
}
