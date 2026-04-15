using System;
using System.Text;

namespace InductorParser.SyntaxTree;

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
