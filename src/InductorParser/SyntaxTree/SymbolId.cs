using System;
using System.Text;

namespace InductorParser.SyntaxTree;

/// <summary>
/// Identifies which rule produced a <see cref="Symbol"/> in a parse tree. Every Symbol stores one
/// as <see cref="Symbol.Id">Symbol.Id</see>, and every rule gets one when the grammar is compiled,
/// so a tree walker can ask "did this node come from that rule?" with an integer compare instead
/// of holding on to Rule references.
/// </summary>
/// <remarks>
/// Most code never touches a SymbolId directly. <see cref="Symbol.Is(Rule)">Symbol.Is(Rule)</see>,
/// <see cref="Symbol.Find(Rule)">Symbol.Find(Rule)</see>, and <see cref="ParseResult.Find(Rule)">ParseResult.Find(Rule)</see>
/// take the Rule and compare ids for you. You'll need it when a tree walker dispatches on
/// <see cref="Symbol.Id">Symbol.Id</see>, when a rule needs a stable numeric id for serialization via
/// <see cref="Rule.As(SymbolId)">Rule.As(SymbolId)</see>, or when writing your own Rule subclass.
/// <para>
/// The integer's range says what kind of id it is (see <see cref="SymbolRanges"/>). Below 0x110000
/// it's a leaf that matched one rune and the id is that rune's code point, so an anonymous
/// <c>Token('a')</c> leaf has id 0x61. From 0x110000 to 0x1FFFFF is reserved for built-in rules.
/// From 0x200000 up are the ids of named, anonymous, and user-defined rules, assigned by
/// <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> in
/// grammar order. Those compile-time ids change when the grammar changes, so a grammar that
/// stores ids (for serialization, say) sets them explicitly with
/// <see cref="Rule.As(SymbolId)">Rule.As(SymbolId)</see>.
/// </para>
/// <para>
/// SymbolId is a value type compared by <see cref="Value">SymbolId.Value</see>. It implements
/// <see cref="IEquatable{T}"/> and overrides <see cref="GetHashCode">SymbolId.GetHashCode()</see>, so
/// it works as a Dictionary or HashSet key without boxing.
/// </para>
/// </remarks>
public readonly struct SymbolId : IEquatable<SymbolId>
{
    /// <summary>
    /// The raw integer behind this id. Its range says what kind of id it is: below 0x110000 it's
    /// the Unicode code point of a single-rune leaf, and from 0x200000 up it's a rule id assigned
    /// at compile time or set explicitly with <see cref="Rule.As(SymbolId)">Rule.As(SymbolId)</see>.
    /// See <see cref="SymbolRanges"/> for the ranges.
    /// </summary>
    public int Value { get; }

    /// <summary>
    /// Wraps a raw integer as a SymbolId without validating it. Use this to build the explicit id
    /// for <see cref="Rule.As(SymbolId)">Rule.As(SymbolId)</see> (start at
    /// <see cref="SymbolRanges.CustomRangeStart">SymbolRanges.CustomRangeStart</see>), or to look up a
    /// Symbol by an id you stored earlier. For a single-rune leaf id, prefer
    /// <see cref="ForRune(int)">SymbolId.ForRune(int)</see>, which checks that the value is a valid
    /// Unicode scalar.
    /// </summary>
    /// <param name="value">The raw id value.</param>
    public SymbolId(int value) => Value = value;

    /// <summary>Two ids are equal when their <see cref="Value">SymbolId.Value</see>s are equal.</summary>
    public bool Equals(SymbolId other) => Value == other.Value;

    /// <summary>Equal when <paramref name="obj"/> is a SymbolId with the same <see cref="Value">SymbolId.Value</see>.</summary>
    public override bool Equals(object? obj) => obj is SymbolId id && Equals(id);

    /// <summary>
    /// The <see cref="Value">SymbolId.Value</see> itself, so SymbolIds hash without boxing as Dictionary
    /// or HashSet keys.
    /// </summary>
    public override int GetHashCode() => Value;

    /// <summary>
    /// The <see cref="Value">SymbolId.Value</see> as a decimal string, for debug output. For the name of
    /// the rule behind an id, use <see cref="Symbol.DisplayName">Symbol.DisplayName</see> or
    /// <see cref="Rule.NameOf(SymbolId)">Rule.NameOf(SymbolId)</see>.
    /// </summary>
    public override string ToString() => Value.ToString();

    /// <summary>True when both ids have the same <see cref="Value">SymbolId.Value</see>.</summary>
    public static bool operator ==(SymbolId a, SymbolId b) => a.Value == b.Value;

    /// <summary>True when the ids have different <see cref="Value">SymbolId.Value</see>s.</summary>
    public static bool operator !=(SymbolId a, SymbolId b) => a.Value != b.Value;

    /// <summary>
    /// Builds the id of a leaf that matched one rune: the rune's code point, checked to be a valid
    /// Unicode scalar value. This is the id an anonymous single-rune rule such as <c>Token('a')</c>
    /// stamps on its leaf, so <c>SymbolId.ForRune('a')</c> is what you compare against or search
    /// for when walking a tree of those leaves.
    /// </summary>
    /// <param name="codepoint">The rune's code point, 0 to 0x10FFFF and not a surrogate half.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="codepoint"/> isn't a valid Unicode scalar value.
    /// </exception>
    public static SymbolId ForRune(int codepoint)
    {
        // The id range 0..0x10FFFF is reserved for rune leaves (see SymbolRanges), and every id in
        // that range has to be a valid Unicode scalar value. Rune.IsValid enforces both
        // constraints: in range and not a surrogate half.
        if (!Rune.IsValid(codepoint))
            throw new ArgumentOutOfRangeException(
                nameof(codepoint),
                codepoint,
                "Must be a valid Rune (i.e. Unicode scalar value): 0..0x10FFFF, excluding surrogates 0xD800..0xDFFF.");
        return new SymbolId(codepoint);
    }
}
