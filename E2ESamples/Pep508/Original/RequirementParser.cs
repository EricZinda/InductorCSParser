// A clean, hand-written C# port of the PEP 508 dependency-specifier
// parser from the `packaging` library (packaging/_tokenizer.py and the
// requirement half of packaging/_parser.py).
//
// Upstream: https://github.com/pypa/packaging  (Apache-2.0 / BSD-2)
// Ported from src/packaging/_parser.py + src/packaging/_tokenizer.py
// as of the main branch, fetched 2026-05-15.
//
// This sample contains no upstream code: it is an independent C#
// implementation of the same grammar and the same error messages,
// written for an ergonomics comparison against the InductorParser
// rewrite in ../Rewrite/. `packaging` is the reason this format was
// chosen. Its maintainers explicitly replaced an earlier
// pyparsing-based parser with this hand-written tokenizer specifically
// to produce better, position-anchored error messages. The
// `ParserSyntaxError` below renders the same message / source / caret
// shape that `packaging` shows a user.
//
// Scope: the requirement core (name, extras, version specifier, URL).
// Environment markers (`; python_version < "3.8"`) are out of scope.
// See README.md.

using System;
using System.Collections.Generic;

namespace Pep508Sample.Original;

public sealed record OriginalVersionConstraint(string Operator, string Version);

public sealed record OriginalRequirement(
    string Name,
    IReadOnlyList<string> Extras,
    IReadOnlyList<OriginalVersionConstraint> Specifiers,
    string? Url);

// The C# stand-in for packaging.ParserSyntaxError. Its ToString renders
// the message, the source line, and a tilde+caret marker under the
// span, byte-for-byte the same shape as packaging's __str__.
public sealed class ParserSyntaxError : Exception
{
    public string RawMessage { get; }
    public string SourceText { get; }
    public int SpanStart { get; }
    public int SpanEnd { get; }

    public ParserSyntaxError(string message, string source, int spanStart, int spanEnd)
        : base(message)
    {
        RawMessage = message;
        SourceText = source;
        SpanStart = spanStart;
        SpanEnd = spanEnd;
    }

    public override string ToString()
    {
        string marker = new string(' ', SpanStart)
            + new string('~', Math.Max(0, SpanEnd - SpanStart))
            + "^";
        return $"{RawMessage}\n    {SourceText}\n    {marker}";
    }
}

public static class OriginalRequirementParser
{
    public static OriginalRequirement Parse(string input)
    {
        if (!TryParse(input, out var requirement, out var error))
            throw error!;
        return requirement!;
    }

    public static bool TryParse(string input, out OriginalRequirement? requirement, out ParserSyntaxError? error)
    {
        requirement = null;
        error = null;
        try
        {
            requirement = new Cursor(input).ParseRequirement();
            return true;
        }
        catch (ParserSyntaxError syntaxError)
        {
            error = syntaxError;
            return false;
        }
    }

    // A position cursor over the source, mirroring packaging's
    // Tokenizer: check / expect / consume / read against named token
    // rules, raise_syntax_error for diagnostics.
    private sealed class Cursor
    {
        private readonly string _source;
        private int _position;

        public Cursor(string source) => _source = source;

        private bool Eof => _position >= _source.Length;
        private char Current => _source[_position];

        private bool IsAt(char c) => !Eof && Current == c;

        private void SkipWhitespace()
        {
            while (!Eof && (Current == ' ' || Current == '\t'))
                _position++;
        }

        private static bool IsNameStart(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        private static bool IsNameBody(char c) =>
            IsNameStart(c) || c == '-' || c == '_' || c == '.';

        private static bool IsVersionChar(char c) =>
            IsNameStart(c) || "-_.*+!".IndexOf(c) >= 0;

        private Exception RaiseSyntaxError(string message, int? spanStart = null, int? spanEnd = null) =>
            throw new ParserSyntaxError(
                message, _source, spanStart ?? _position, spanEnd ?? _position);

        // requirement = WS? IDENTIFIER WS? extras WS? requirement_details END
        public OriginalRequirement ParseRequirement()
        {
            SkipWhitespace();

            string name = ReadIdentifier();
            if (name.Length == 0)
                throw RaiseSyntaxError("Expected package name at the start of dependency specifier");
            SkipWhitespace();

            var extras = ParseExtras();
            SkipWhitespace();

            var (url, specifiers) = ParseRequirementDetails();

            SkipWhitespace();
            if (!Eof)
                throw RaiseSyntaxError("Expected end of dependency specifier");

            return new OriginalRequirement(name, extras, specifiers, url);
        }

        // extras = ('[' WS? extras_list? WS? ']')?
        private List<string> ParseExtras()
        {
            if (!IsAt('['))
                return new List<string>();

            int openPosition = _position;
            _position++; // consume '['
            SkipWhitespace();

            var extras = ParseExtrasList();
            SkipWhitespace();

            if (!IsAt(']'))
                throw RaiseSyntaxError(
                    "Expected matching RIGHT_BRACKET for LEFT_BRACKET, after extras",
                    spanStart: openPosition);
            _position++; // consume ']'
            return extras;
        }

        // extras_list = identifier (WS? ',' WS? identifier)*
        private List<string> ParseExtrasList()
        {
            var extras = new List<string>();

            string first = ReadIdentifier();
            if (first.Length == 0)
                return extras;
            extras.Add(first);

            while (true)
            {
                SkipWhitespace();
                if (!Eof && IsNameStart(Current))
                    throw RaiseSyntaxError("Expected comma between extra names");
                if (!IsAt(','))
                    break;

                _position++; // consume ','
                SkipWhitespace();

                string next = ReadIdentifier();
                if (next.Length == 0)
                    throw RaiseSyntaxError("Expected extra name after comma");
                extras.Add(next);
            }

            return extras;
        }

        // requirement_details = ('@' WS? URL) | version_specifier | <empty>
        private (string? url, List<OriginalVersionConstraint> specifiers) ParseRequirementDetails()
        {
            if (IsAt('@'))
            {
                _position++; // consume '@'
                SkipWhitespace();
                string url = ReadUrl();
                if (url.Length == 0)
                    throw RaiseSyntaxError("Expected URL after @");
                return (url, new List<OriginalVersionConstraint>());
            }

            return (null, ParseSpecifier());
        }

        // specifier = '(' WS? version_many WS? ')' | WS? version_many WS?
        private List<OriginalVersionConstraint> ParseSpecifier()
        {
            if (!IsAt('('))
            {
                SkipWhitespace();
                var bare = ParseVersionMany();
                SkipWhitespace();
                return bare;
            }

            int openPosition = _position;
            _position++; // consume '('
            SkipWhitespace();
            var specifiers = ParseVersionMany();
            SkipWhitespace();
            if (!IsAt(')'))
                throw RaiseSyntaxError(
                    "Expected matching RIGHT_PARENTHESIS for LEFT_PARENTHESIS, after version specifier",
                    spanStart: openPosition);
            _position++; // consume ')'
            return specifiers;
        }

        // version_many = (version_one (WS? ',' WS? version_one)*)?
        private List<OriginalVersionConstraint> ParseVersionMany()
        {
            var specifiers = new List<OriginalVersionConstraint>();

            while (true)
            {
                // packaging tokenizes operator + version as one SPECIFIER
                // token, so an operator with no version is simply not a
                // SPECIFIER: the loop ends and the leftover trips the
                // final END check. Replicate by rolling back.
                int rollback = _position;
                string op = ReadOperator();
                if (op.Length == 0)
                    break;
                SkipWhitespace();
                string version = ReadVersion();
                if (version.Length == 0)
                {
                    _position = rollback;
                    break;
                }
                specifiers.Add(new OriginalVersionConstraint(op, version));

                SkipWhitespace();
                if (!IsAt(','))
                    break;
                _position++; // consume ','
                SkipWhitespace();
            }

            return specifiers;
        }

        private string ReadIdentifier()
        {
            if (Eof || !IsNameStart(Current))
                return string.Empty;
            int start = _position;
            _position++;
            while (!Eof && IsNameBody(Current))
                _position++;
            return _source.Substring(start, _position - start);
        }

        private string ReadVersion()
        {
            int start = _position;
            while (!Eof && IsVersionChar(Current))
                _position++;
            return _source.Substring(start, _position - start);
        }

        private string ReadUrl()
        {
            int start = _position;
            while (!Eof && Current != ' ' && Current != '\t')
                _position++;
            return _source.Substring(start, _position - start);
        }

        private static readonly string[] Operators =
            { "===", "==", "~=", "!=", "<=", ">=", "<", ">" };

        private string ReadOperator()
        {
            foreach (var op in Operators)
            {
                if (_position + op.Length <= _source.Length
                    && _source.AsSpan(_position, op.Length).SequenceEqual(op))
                {
                    _position += op.Length;
                    return op;
                }
            }
            return string.Empty;
        }
    }
}
