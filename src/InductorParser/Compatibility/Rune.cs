// Polyfill for System.Text.Rune.
//
// netstandard2.1 doesn't ship this type, so we polyfill. The #if gate keeps
// the polyfill out of the netcoreapp3.0+ and net8.0 builds where the BCL
// provides the real one. The class is defined in the BCL namespace
// System.Text so caller code (`using System.Text; ... new Rune(c)`) resolves
// to whichever Rune is available without changing imports.
//
// Unlike the other polyfills in this folder, this one is `public` because
// Rune appears in the parser's public API (Token(Rune), TokenSet.Single(Rune),
// and so on). Callers on netstandard2.1 hosts (Unity, including WebGL and
// iOS) have to be able to construct one. The library targets netstandard2.1
// because that's what Unity's IL2CPP scripting backend supports (see
// docs/CodeArchitecture.md).
//
// This implementation is the minimum we need: a 21-bit Unicode scalar with
// code-point validation, surrogate-pair encoding/decoding, equality,
// comparison, and a Unicode-category lookup that delegates to
// System.Globalization. It doesn't include the BCL's UTF-8 encoding helpers,
// OperationStatus-returning decoders, or the IsLetter/IsDigit family. Add
// those when something inside the parser actually needs them.

#if !NETCOREAPP3_0_OR_GREATER

using System.Globalization;

namespace System.Text;

/// <summary>
/// A Unicode scalar value: any code point from U+0000 through U+10FFFF
/// except the surrogate range U+D800..U+DFFF.
/// </summary>
public readonly struct Rune : IEquatable<Rune>, IComparable<Rune>
{
    private const int MaxCodepoint = 0x10FFFF;
    private const int HighSurrogateStart = 0xD800;
    private const int LowSurrogateEnd = 0xDFFF;
    private const int BmpEnd = 0xFFFF;

    private readonly int _value;
    public int Value => _value;

    public bool IsAscii => _value < 0x80;
    public bool IsBmp => _value <= BmpEnd;

    public static Rune ReplacementChar => new Rune(0xFFFD);

    public Rune(char ch)
    {
        if (char.IsSurrogate(ch))
            throw new ArgumentOutOfRangeException(nameof(ch), "Surrogate halves aren't valid Runes (i.e. Unicode scalar values).");
        _value = ch;
    }

    public Rune(char highSurrogate, char lowSurrogate)
    {
        if (!char.IsHighSurrogate(highSurrogate))
            throw new ArgumentOutOfRangeException(nameof(highSurrogate));
        if (!char.IsLowSurrogate(lowSurrogate))
            throw new ArgumentOutOfRangeException(nameof(lowSurrogate));
        _value = char.ConvertToUtf32(highSurrogate, lowSurrogate);
    }

    public Rune(int value)
    {
        if (!IsValid(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Not a valid Rune (i.e. Unicode scalar value).");
        _value = value;
    }

    public Rune(uint value) : this((int)value) { }

    public static bool IsValid(int value) =>
        value >= 0 && value <= MaxCodepoint && (value < HighSurrogateStart || value > LowSurrogateEnd);

    public static bool IsValid(uint value) => IsValid((int)value);

    public static bool TryCreate(int value, out Rune result)
    {
        if (IsValid(value))
        {
            result = new Rune(value);
            return true;
        }
        result = default;
        return false;
    }

    public static bool TryCreate(char ch, out Rune result)
    {
        if (!char.IsSurrogate(ch))
        {
            result = new Rune(ch);
            return true;
        }
        result = default;
        return false;
    }

    public static bool TryCreate(char highSurrogate, char lowSurrogate, out Rune result)
    {
        if (char.IsHighSurrogate(highSurrogate) && char.IsLowSurrogate(lowSurrogate))
        {
            result = new Rune(highSurrogate, lowSurrogate);
            return true;
        }
        result = default;
        return false;
    }

    public static UnicodeCategory GetUnicodeCategory(Rune value)
    {
        if (value.IsBmp)
            return CharUnicodeInfo.GetUnicodeCategory((char)value._value);
        return CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(value._value), 0);
    }

    public bool Equals(Rune other) => _value == other._value;
    public override bool Equals(object? obj) => obj is Rune other && Equals(other);
    public override int GetHashCode() => _value;
    public int CompareTo(Rune other) => _value.CompareTo(other._value);

    public override string ToString() =>
        IsBmp ? new string((char)_value, 1) : char.ConvertFromUtf32(_value);

    public static bool operator ==(Rune a, Rune b) => a._value == b._value;
    public static bool operator !=(Rune a, Rune b) => a._value != b._value;
    public static bool operator <(Rune a, Rune b) => a._value < b._value;
    public static bool operator <=(Rune a, Rune b) => a._value <= b._value;
    public static bool operator >(Rune a, Rune b) => a._value > b._value;
    public static bool operator >=(Rune a, Rune b) => a._value >= b._value;
}

#endif
