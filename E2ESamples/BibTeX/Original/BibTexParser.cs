// Hand-written C# port of a typical BibTeX parser, modeled on
// bibtex-parser (https://github.com/digitalheir/bibtex-js-parser,
// MIT) and pybtex (https://pybtex.org, MIT). Both upstreams take the
// same general shape: a line / character scanner, regex-driven entry
// recognition, and a recursive function for balanced braces. This
// port keeps that shape so the side-by-side comparison with the
// grammar rewrite stays apples to apples.
//
// What this covers:
//   - @type{citekey, field = value, field = value, ...}
//   - Entry types: article, book, inproceedings, misc, etc.
//   - @preamble{ value } (bibliography-scope TeX macros)
//   - @string{ name = value } (macro definitions)
//   - Citation keys (ASCII identifier-shaped, per upstream regex)
//   - Field names (case-insensitive)
//   - Field values: "..." quoted, {...} braced (with balanced
//     nested braces), bare integer, or bare identifier (a macro
//     reference like 'jul' or 'STOC' that biber would expand at
//     bibliography-rendering time. This parser keeps the literal
//     text rather than expanding)
//   - String concatenation across value parts with '#'
//   - Trailing commas (BibTeX is permissive)
//   - Inter-entry whitespace and stray comments outside @entries
//
// What's intentionally cut for this sample:
//   - Macro expansion / cross-reference resolution (the parser
//     records the literal source. Downstream is the renderer's job)
//
// The citation-key shape is the part that matters for the Unicode
// angle: the typical regex (and most published BibTeX parser ports)
// uses /[A-Za-z_][A-Za-z0-9_:.\-+/]*/, which silently rejects every
// non-ASCII letter. Modern BibLaTeX (the biber backend) accepts
// Unicode citation keys per its spec, so academic papers with
// Greek, Chinese, or Cyrillic author names benefit from Unicode
// citekeys. The rewrite uses Rules.Identifier(UAX #31) so a key
// like `gärtner2020` or `张2019` parses, matching biber.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BibTexSample.Original;

public class BibTexParseException : Exception
{
    public int Position { get; }
    public BibTexParseException(string message, int position) : base(message)
    {
        Position = position;
    }
    public override string ToString() => $"position {Position}: {Message}";
}

public record BibTexEntry(
    string EntryType,
    string CitationKey,
    IReadOnlyList<BibTexField> Fields,
    int Position);

public record BibTexField(string Name, string Value);

public class BibTexParser
{
    private static readonly Regex EntryTypeRegex = new(@"\G@([A-Za-z]+)\s*[\{(]", RegexOptions.Compiled);
    // The naive ASCII-only citation-key regex: a leading letter or
    // underscore followed by letters, digits, and a handful of common
    // punctuation. Every published JS / Python BibTeX parser uses some
    // close variant of this. None accepts non-ASCII letters by default.
    private static readonly Regex CitationKeyRegex = new(@"\G[A-Za-z_][A-Za-z0-9_:.\-+/]*", RegexOptions.Compiled);
    private static readonly Regex FieldNameRegex = new(@"\G[A-Za-z][A-Za-z0-9_-]*", RegexOptions.Compiled);

    private readonly string _input;
    private int _position;

    private BibTexParser(string input)
    {
        _input = input;
        _position = 0;
    }

    public static IReadOnlyList<BibTexEntry> Parse(string input)
    {
        var parser = new BibTexParser(input);
        var entries = new List<BibTexEntry>();
        parser.SkipWhitespaceAndStrayText();
        while (parser._position < parser._input.Length)
        {
            entries.Add(parser.ParseEntry());
            parser.SkipWhitespaceAndStrayText();
        }
        return entries;
    }

    private BibTexEntry ParseEntry()
    {
        int entryStart = _position;
        var typeMatch = EntryTypeRegex.Match(_input, _position);
        if (!typeMatch.Success)
            throw new BibTexParseException("expected '@type{' to start an entry", _position);
        var entryType = typeMatch.Groups[1].Value.ToLowerInvariant();
        _position = typeMatch.Index + typeMatch.Length;

        if (entryType == "preamble") return ParsePreambleBody(entryStart);
        if (entryType == "string") return ParseStringBody(entryStart);

        SkipInlineWhitespace();
        var keyMatch = CitationKeyRegex.Match(_input, _position);
        if (!keyMatch.Success)
            throw new BibTexParseException("expected a citation key", _position);
        var citationKey = keyMatch.Value;
        _position = keyMatch.Index + keyMatch.Length;

        SkipInlineWhitespace();
        ExpectChar(',', "expected ',' after citation key");

        var fields = new List<BibTexField>();
        while (true)
        {
            SkipWhitespace();
            if (_position < _input.Length && _input[_position] == '}')
            {
                _position++;
                break;
            }
            if (_position >= _input.Length)
                throw new BibTexParseException("unexpected end of input inside entry", _position);

            var nameMatch = FieldNameRegex.Match(_input, _position);
            if (!nameMatch.Success)
                throw new BibTexParseException("expected a field name", _position);
            var fieldName = nameMatch.Value.ToLowerInvariant();
            _position = nameMatch.Index + nameMatch.Length;

            SkipWhitespace();
            ExpectChar('=', "expected '=' after field name");
            SkipWhitespace();

            var fieldValue = ParseFieldValueExpression();
            fields.Add(new BibTexField(fieldName, fieldValue));

            SkipWhitespace();
            if (_position < _input.Length && _input[_position] == ',')
            {
                _position++;
                continue;
            }
            SkipWhitespace();
            if (_position < _input.Length && _input[_position] == '}')
            {
                _position++;
                break;
            }
            throw new BibTexParseException("expected ',' or '}' after field value", _position);
        }
        return new BibTexEntry(entryType, citationKey, fields, entryStart);
    }

    // @preamble{ value-expression }: a single value at the top level
    // with no citation key. The entry header already consumed '@preamble'
    // plus the opening '{'.
    private BibTexEntry ParsePreambleBody(int entryStart)
    {
        SkipWhitespace();
        var value = ParseFieldValueExpression();
        SkipWhitespace();
        ExpectChar('}', "expected '}' to close @preamble");
        return new BibTexEntry("preamble", "", new[] { new BibTexField("", value) }, entryStart);
    }

    // @string{ name = value-expression }: a macro definition, with the
    // same shape as a single field but at the top level. The entry
    // header already consumed '@string' plus the opening '{'.
    private BibTexEntry ParseStringBody(int entryStart)
    {
        SkipWhitespace();
        // The macro name shares the same identifier shape as a citation
        // key (e.g. 'STOC-key', 'ACM').
        var nameMatch = CitationKeyRegex.Match(_input, _position);
        if (!nameMatch.Success)
            throw new BibTexParseException("expected a macro name", _position);
        var name = nameMatch.Value.ToLowerInvariant();
        _position = nameMatch.Index + nameMatch.Length;

        SkipWhitespace();
        ExpectChar('=', "expected '=' after macro name");
        SkipWhitespace();
        var value = ParseFieldValueExpression();
        SkipWhitespace();
        ExpectChar('}', "expected '}' to close @string");
        return new BibTexEntry("string", "", new[] { new BibTexField(name, value) }, entryStart);
    }

    // A field value is one or more value parts joined by '#'. The
    // concatenation glues the parts' contents together verbatim.
    // Macro references stay as their literal name (no expansion).
    private string ParseFieldValueExpression()
    {
        var buffer = new StringBuilder(ParseFieldValuePart());
        while (true)
        {
            SkipWhitespace();
            if (_position >= _input.Length || _input[_position] != '#')
                return buffer.ToString();
            _position++;
            SkipWhitespace();
            buffer.Append(ParseFieldValuePart());
        }
    }

    private string ParseFieldValuePart()
    {
        if (_position >= _input.Length)
            throw new BibTexParseException("expected a field value", _position);
        char c = _input[_position];
        if (c == '"') return ParseQuotedValue();
        if (c == '{') return ParseBracedValue();
        if (char.IsDigit(c)) return ParseIntegerValue();
        var wordMatch = CitationKeyRegex.Match(_input, _position);
        if (wordMatch.Success)
        {
            _position = wordMatch.Index + wordMatch.Length;
            return wordMatch.Value;
        }
        throw new BibTexParseException("expected '\"', '{', a digit, or a macro name at start of field value", _position);
    }

    private string ParseQuotedValue()
    {
        int start = _position;
        _position++;
        var buffer = new StringBuilder();
        while (_position < _input.Length && _input[_position] != '"')
        {
            if (_input[_position] == '\\' && _position + 1 < _input.Length)
            {
                buffer.Append(_input[_position]);
                buffer.Append(_input[_position + 1]);
                _position += 2;
                continue;
            }
            buffer.Append(_input[_position]);
            _position++;
        }
        if (_position >= _input.Length)
            throw new BibTexParseException("unterminated quoted string", start);
        _position++;
        return buffer.ToString();
    }

    private string ParseBracedValue()
    {
        int start = _position;
        _position++;
        var buffer = new StringBuilder();
        int depth = 1;
        while (_position < _input.Length)
        {
            char c = _input[_position];
            if (c == '\\' && _position + 1 < _input.Length)
            {
                buffer.Append(c);
                buffer.Append(_input[_position + 1]);
                _position += 2;
                continue;
            }
            if (c == '{')
            {
                depth++;
                buffer.Append(c);
                _position++;
                continue;
            }
            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    _position++;
                    return buffer.ToString();
                }
                buffer.Append(c);
                _position++;
                continue;
            }
            buffer.Append(c);
            _position++;
        }
        throw new BibTexParseException("unterminated braced value", start);
    }

    private string ParseIntegerValue()
    {
        int start = _position;
        while (_position < _input.Length && char.IsDigit(_input[_position]))
            _position++;
        return _input.Substring(start, _position - start);
    }

    private void SkipWhitespace()
    {
        while (_position < _input.Length && char.IsWhiteSpace(_input[_position]))
            _position++;
    }

    private void SkipInlineWhitespace()
    {
        while (_position < _input.Length &&
               (_input[_position] == ' ' || _input[_position] == '\t'))
            _position++;
    }

    private void SkipWhitespaceAndStrayText()
    {
        // Real BibTeX files often have prose between entries (the
        // .bib format treats anything outside @entries as a comment).
        while (_position < _input.Length && _input[_position] != '@')
            _position++;
    }

    private void ExpectChar(char expected, string message)
    {
        if (_position >= _input.Length || _input[_position] != expected)
            throw new BibTexParseException(message, _position);
        _position++;
    }
}
