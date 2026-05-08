using System;
using System.Collections.Generic;

namespace InductorParser.E2ESamples.Toml.Rewrite;

// Typed AST for a parsed TOML document. Mirrors how Tomlyn (the BSD-2
// reference parser we compare against) exposes its result: an abstract
// base TomlValue with sealed records for each of the eight value kinds
// TOML 1.0 defines, plus a TomlTable that holds the named children of a
// table (or an inline table). The top-level document is a TomlTable.
//
// Indexer ergonomics modeled after Newtonsoft's JToken / JObject so a
// caller can chain root["servers"]["main"]["port"] without explicit
// casts at each step.
public abstract record TomlValue
{
    public virtual TomlValue this[string key] =>
        throw new InvalidOperationException($"{GetType().Name} isn't a TOML table; can't index by key.");

    public virtual TomlValue this[int index] =>
        throw new InvalidOperationException($"{GetType().Name} isn't a TOML array; can't index by int.");

    public static explicit operator string(TomlValue value) => value switch
    {
        TomlString s => s.Value,
        _ => throw new InvalidCastException($"Can't cast {value.GetType().Name} to string.")
    };

    public static explicit operator long(TomlValue value) => value switch
    {
        TomlInteger n => n.Value,
        _ => throw new InvalidCastException($"Can't cast {value.GetType().Name} to long.")
    };

    public static explicit operator double(TomlValue value) => value switch
    {
        TomlFloat f => f.Value,
        TomlInteger n => n.Value,
        _ => throw new InvalidCastException($"Can't cast {value.GetType().Name} to double.")
    };

    public static explicit operator bool(TomlValue value) => value switch
    {
        TomlBoolean b => b.Value,
        _ => throw new InvalidCastException($"Can't cast {value.GetType().Name} to bool.")
    };
}

public sealed record TomlString(string Value, TomlStringKind Kind) : TomlValue;

public enum TomlStringKind { Basic, Literal, MultiLineBasic, MultiLineLiteral }

public sealed record TomlInteger(long Value, TomlIntegerBase Base) : TomlValue;

public enum TomlIntegerBase { Decimal, Hexadecimal, Octal, Binary }

public sealed record TomlFloat(double Value) : TomlValue;

public sealed record TomlBoolean(bool Value) : TomlValue;

public sealed record TomlOffsetDateTime(DateTimeOffset Value) : TomlValue;

public sealed record TomlLocalDateTime(DateTime Value) : TomlValue;

public sealed record TomlLocalDate(DateOnly Value) : TomlValue;

public sealed record TomlLocalTime(TimeOnly Value) : TomlValue;

public sealed record TomlArray(IReadOnlyList<TomlValue> Items) : TomlValue
{
    public override TomlValue this[int index] => Items[index];
}

// Both top-level (with [section] headers turning into nested tables) and
// inline-table values land here. Members preserve insertion order so a
// round-trip can reproduce the source order.
public sealed record TomlTable : TomlValue
{
    private readonly Dictionary<string, TomlValue> _members = new(StringComparer.Ordinal);
    private readonly List<string> _orderedKeys = new();

    public IReadOnlyList<string> OrderedKeys => _orderedKeys;
    public IReadOnlyDictionary<string, TomlValue> Members => _members;

    public override TomlValue this[string key] => _members[key];

    internal void Add(string key, TomlValue value)
    {
        if (_members.ContainsKey(key))
            throw new TomlParseException($"Duplicate key '{key}'.");
        _members[key] = value;
        _orderedKeys.Add(key);
    }

    internal bool TryGet(string key, out TomlValue value) => _members.TryGetValue(key, out value!);

    internal void Replace(string key, TomlValue value)
    {
        // Used only by the dotted-key / table machinery when we're upgrading
        // a placeholder table to a fully-populated one.
        _members[key] = value;
    }

    // Records' auto-generated equality compares declared properties; we
    // override here so two TomlTables with the same members compare
    // equal regardless of insertion order.
    public bool Equals(TomlTable? other)
    {
        if (other is null) return false;
        if (_members.Count != other._members.Count) return false;
        foreach (var pair in _members)
            if (!other._members.TryGetValue(pair.Key, out var otherValue) || !Equals(pair.Value, otherValue))
                return false;
        return true;
    }

    public override int GetHashCode() => _members.Count;
}

public sealed class TomlParseException : Exception
{
    public int? CharIndex { get; }
    public int? Line { get; }
    public int? Column { get; }

    public TomlParseException(string message) : base(message) { }

    public TomlParseException(string message, int charIndex, int line, int column) : base(message)
    {
        CharIndex = charIndex;
        Line = line;
        Column = column;
    }
}
