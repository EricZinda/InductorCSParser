// Polyfill for System.Text.Rune.
//
// Background: System.Text.Rune ships with .NET Core 3.0 and .NET 5+, but is
// NOT in the netstandard2.1 reference assemblies. The library targets
// netstandard2.1 because that's what Unity's IL2CPP scripting backend
// supports (see docs/CodeArchitecture.md). Without this polyfill, callers
// on netstandard2.1 hosts (Unity, including WebGL and iOS) can't use the
// Rune-typed overloads of Token(), TokenSet.Single(), etc.
//
// The class is defined in the BCL namespace System.Text so that caller code
// (`using System.Text; ... new Rune(c)`) resolves to whichever Rune is
// available without changing imports. When this assembly is built against
// net5.0 or later, the BCL ships its own Rune in System.Text and this file
// compiles to nothing. References resolve to the BCL type instead.
//
// This implementation is the minimum we need: a validated 21-bit code point
// wrapper with surrogate-pair encoding/decoding, equality, comparison, and
// a Unicode-category lookup that delegates to System.Globalization. It
// doesn't include the BCL's UTF-8 encoding helpers, OperationStatus-returning
// decoders, or the IsLetter/IsDigit family. Add those when something
// inside the parser actually needs them.

#if !NET5_0_OR_GREATER

using System.Globalization;

namespace System.Text;

public readonly struct Rune : IEquatable<Rune>, IComparable<Rune>
{
    private const int MaxCodepoint = 0x10FFFF;
    private const int HighSurrogateStart = 0xD800;
    private const int LowSurrogateEnd = 0xDFFF;
    private const int BmpEnd = 0xFFFF;

    private readonly int _value;

    public Rune(char ch)
    {
        if (char.IsSurrogate(ch))
            throw new ArgumentOutOfRangeException(nameof(ch), "Surrogate halves aren't valid scalar values.");
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
            throw new ArgumentOutOfRangeException(nameof(value), "Not a valid Unicode scalar value.");
        _value = value;
    }

    public Rune(uint value) : this((int)value) { }

    public int Value => _value;

    public bool IsAscii => _value < 0x80;
    public bool IsBmp => _value <= BmpEnd;

    public static Rune ReplacementChar => new Rune(0xFFFD);

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
