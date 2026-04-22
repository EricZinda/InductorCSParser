using System;
using System.Text;

namespace InductorParser.SyntaxTree;

// Identifies a rule (or a character leaf) in a parse tree. Every Symbol
// carries one. The integer value's range tells you what kind of id it is:
//
//   * 0..0x10FFFF        — a character leaf. The id IS the Unicode code
//                          point. `new SymbolId('a').Value == 0x61`. Use
//                          ForRune to construct one with validation.
//   * 0x110000..0x1FFFFF — built-in rule ids (see SymbolRanges).
//   * 0x200000+          — user-defined or anonymous rule ids, assigned
//                          during Compile.
//
// Stored inline on every Symbol and compared by value, so lookups like
// Tree.Find(rule) are cheap integer compares rather than reference
// checks. Implementing IEquatable<SymbolId> + overriding GetHashCode
// lets SymbolIds be Dictionary / HashSet keys with no boxing.
public readonly struct SymbolId : IEquatable<SymbolId>
{
    public int Value { get; }

    public SymbolId(int value) => Value = value;

    public bool Equals(SymbolId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is SymbolId id && Equals(id);
    public override int GetHashCode() => Value;
    public override string ToString() => Value.ToString();

    public static bool operator ==(SymbolId a, SymbolId b) => a.Value == b.Value;
    public static bool operator !=(SymbolId a, SymbolId b) => a.Value != b.Value;

    // Build a SymbolId that represents a character leaf in the parse tree.
    // The id range 0..0x10FFFF is reserved for character symbols (see
    // SymbolRanges), and every id in that range has to be a valid Unicode
    // scalar value. Rune.IsValid enforces both constraints: in range and
    // not a surrogate half.
    public static SymbolId ForRune(int codepoint)
    {
        if (!Rune.IsValid(codepoint))
            throw new ArgumentOutOfRangeException(
                nameof(codepoint),
                codepoint,
                "Must be a valid Unicode scalar value (0..0x10FFFF, excluding surrogates 0xD800..0xDFFF).");
        return new SymbolId(codepoint);
    }
}
