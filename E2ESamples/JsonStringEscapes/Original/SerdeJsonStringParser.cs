// Faithful C# port of serde_json's JSON string-escape decoder.
//
// Upstream: serde-rs/json, src/read.rs + src/error.rs
// Commit:   5d30df60e916e9b8fc46c74794007ff271fdfbbf  (2026-05-18)
// License:  serde_json is dual-licensed MIT OR Apache-2.0 (both permissive).
//
// This file contains no upstream Rust code. It's an independent C# port
// written for an ergonomics comparison, the same approach the Pep508 and
// Newsboat samples take with their Python/C++ originals. The verbatim Rust
// section it mirrors is in upstream-serde-json-read.rs next door. The README
// quotes the driver loop and decode_hex_escape that aren't copied there.
//
// What it does: takes a complete JSON string literal (the surrounding double
// quotes included) and either decodes it to its string value or returns the
// first error, with serde_json's own ErrorCode and a character offset.
//
// Faithfulness notes:
//   * serde_json scans UTF-8 *bytes*; this port scans UTF-16 *chars*. Every
//     character in a JSON `\u`-escape is ASCII, so byte offset == char offset
//     for all the inputs the error exercise cares about. The README spells
//     this out.
//   * serde_json deserializing into a string always passes validate = true
//     (lone surrogates rejected). The byte-string path (validate = false,
//     lone surrogates kept as WTF-8) is dropped here. It isn't reachable when
//     the destination is a `String`.
//   * Positions match serde_json's: it advances its read cursor *before*
//     recording an error, so several errors land one past the offending
//     character. Kept as-is so the side-by-side test measures the real thing.

using System;
using System.Text;

namespace JsonStringEscapes.Original;

public enum SerdeErrorCode
{
    EofWhileParsingString,
    ControlCharacterWhileParsingString,
    InvalidEscape,
    UnexpectedEndOfHexEscape,
    LoneLeadingSurrogateInHexEscape,
    InvalidUnicodeCodePoint,
}

public sealed record SerdeJsonStringError(SerdeErrorCode Code, int CharIndex)
{
    // The Display strings from serde_json's src/error.rs (ErrorCode::fmt).
    public string Message => Code switch
    {
        SerdeErrorCode.EofWhileParsingString => "EOF while parsing a string",
        SerdeErrorCode.ControlCharacterWhileParsingString
            => "control character (\\u0000-\\u001F) found while parsing a string",
        SerdeErrorCode.InvalidEscape => "invalid escape",
        SerdeErrorCode.UnexpectedEndOfHexEscape => "unexpected end of hex escape",
        SerdeErrorCode.LoneLeadingSurrogateInHexEscape => "lone leading surrogate in hex escape",
        SerdeErrorCode.InvalidUnicodeCodePoint => "invalid unicode code point",
        _ => Code.ToString(),
    };

    public override string ToString()
    {
        string caret = new string(' ', Math.Max(0, CharIndex)) + "^";
        return $"{Message} (at char {CharIndex})\n{caret}";
    }
}

public sealed class SerdeJsonStringParser
{
    private readonly string _input;
    private int _index;

    private SerdeJsonStringParser(string input) => _input = input;

    /// Decode a complete JSON string literal (quotes included).
    public static bool TryParse(string literal, out string? value, out SerdeJsonStringError? error)
    {
        var parser = new SerdeJsonStringParser(literal);
        return parser.Run(out value, out error);
    }

    private SerdeJsonStringError Error(SerdeErrorCode code) => new(code, _index);

    private bool Run(out string? value, out SerdeJsonStringError? error)
    {
        value = null;
        error = null;

        // serde_json's caller eats the opening '"' before calling parse_str.
        // A complete literal is the documented input for both parsers here, so
        // a missing opening quote is out of scope for the six string errors.
        if (_index >= _input.Length || _input[_index] != '"')
        {
            error = new SerdeJsonStringError(SerdeErrorCode.EofWhileParsingString, 0);
            return false;
        }
        _index++;

        var scratch = new StringBuilder();
        while (true)
        {
            // next_or_eof
            if (_index >= _input.Length)
            {
                error = Error(SerdeErrorCode.EofWhileParsingString);
                return false;
            }
            char ch = _input[_index];
            _index++;

            if (ch != '"' && ch != '\\' && ch >= 0x20)
            {
                scratch.Append(ch);
                continue;
            }
            if (ch == '"')
            {
                value = scratch.ToString();
                return true;
            }
            if (ch == '\\')
            {
                if (!ParseEscape(scratch, out error))
                    return false;
                continue;
            }
            // ch < 0x20: a raw control character.
            error = Error(SerdeErrorCode.ControlCharacterWhileParsingString);
            return false;
        }
    }

    private bool ParseEscape(StringBuilder scratch, out SerdeJsonStringError? error)
    {
        error = null;
        if (_index >= _input.Length)
        {
            error = Error(SerdeErrorCode.EofWhileParsingString);
            return false;
        }
        char ch = _input[_index];
        _index++;

        switch (ch)
        {
            case '"': scratch.Append('"'); return true;
            case '\\': scratch.Append('\\'); return true;
            case '/': scratch.Append('/'); return true;
            case 'b': scratch.Append('\b'); return true;
            case 'f': scratch.Append('\f'); return true;
            case 'n': scratch.Append('\n'); return true;
            case 'r': scratch.Append('\r'); return true;
            case 't': scratch.Append('\t'); return true;
            case 'u': return ParseUnicodeEscape(scratch, out error);
            default:
                error = Error(SerdeErrorCode.InvalidEscape);
                return false;
        }
    }

    // serde_json's SliceRead::decode_hex_escape: grab exactly four chars (it
    // doesn't stop at a closing quote), or report EOF if fewer than four
    // remain.
    private bool DecodeHexEscape(out int value, out SerdeJsonStringError? error)
    {
        value = 0;
        error = null;
        if (_index + 4 > _input.Length)
        {
            _index = _input.Length;
            error = Error(SerdeErrorCode.EofWhileParsingString);
            return false;
        }
        int a = HexValue(_input[_index]);
        int b = HexValue(_input[_index + 1]);
        int c = HexValue(_input[_index + 2]);
        int d = HexValue(_input[_index + 3]);
        _index += 4;
        if ((a | b | c | d) < 0)
        {
            error = Error(SerdeErrorCode.InvalidEscape);
            return false;
        }
        value = (a << 12) | (b << 8) | (c << 4) | d;
        return true;
    }

    private bool ParseUnicodeEscape(StringBuilder scratch, out SerdeJsonStringError? error)
    {
        if (!DecodeHexEscape(out int n, out error))
            return false;

        // A trailing (low) surrogate with nothing before it.
        if (n >= 0xDC00 && n <= 0xDFFF)
        {
            error = Error(SerdeErrorCode.LoneLeadingSurrogateInHexEscape);
            return false;
        }

        while (true)
        {
            if (n < 0xD800 || n > 0xDBFF)
            {
                scratch.Append(char.ConvertFromUtf32(n));
                return true;
            }

            int n1 = n;

            // Expect a backslash starting the trailing-surrogate escape.
            if (_index >= _input.Length)
            {
                error = Error(SerdeErrorCode.EofWhileParsingString);
                return false;
            }
            if (_input[_index] == '\\')
            {
                _index++;
            }
            else
            {
                _index++;
                error = Error(SerdeErrorCode.UnexpectedEndOfHexEscape);
                return false;
            }

            // Expect the 'u'.
            if (_index >= _input.Length)
            {
                error = Error(SerdeErrorCode.EofWhileParsingString);
                return false;
            }
            if (_input[_index] == 'u')
            {
                _index++;
            }
            else
            {
                _index++;
                error = Error(SerdeErrorCode.UnexpectedEndOfHexEscape);
                return false;
            }

            if (!DecodeHexEscape(out int n2, out error))
                return false;

            if (n2 < 0xDC00 || n2 > 0xDFFF)
            {
                error = Error(SerdeErrorCode.LoneLeadingSurrogateInHexEscape);
                return false;
            }

            int combined = (((n1 - 0xD800) << 10) | (n2 - 0xDC00)) + 0x10000;
            scratch.Append(char.ConvertFromUtf32(combined));
            return true;
        }
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };
}
