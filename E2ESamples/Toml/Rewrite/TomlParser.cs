using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.E2ESamples.Toml.Rewrite.TomlGrammar;

namespace InductorParser.E2ESamples.Toml.Rewrite;

// Walks the parse tree TomlGrammar produces and projects it into a
// typed TomlTable. Mirrors how Tomlyn (the BSD-2 reference parser we
// compare against on NuGet) exposes its result: one root table, nested
// tables for [section] headers, arrays of tables for [[section]]
// headers, dotted keys building nested table chains.
//
// All the value-decoding (escape handling for strings, underscore
// stripping for integers, inf/nan handling for floats, datetime
// parsing) lives here, not in the grammar. The grammar produces named
// leaves; this consumer dispatches on the rule each leaf was produced
// by.
//
// Static helpers, no per-projection state: every Symbol the engine
// builds already carries a ParseContext reference, so Symbol.SourceText
// returns the verbatim original-input text without the helpers needing
// the ParseResult in scope.
public static class TomlParser
{
    // Parse a TOML document and return the typed root table.
    // Throws TomlParseException on either grammar mismatch or semantic
    // error (duplicate key, table redefined, etc.).
    public static TomlTable Parse(string input)
    {
        var result = TomlDocument.Parse(input);
        if (!result.Success)
        {
            throw new TomlParseException(
                $"TOML parse error: {result.ErrorMessage}",
                result.ErrorCharIndex,
                result.ErrorLine,
                result.ErrorColumn);
        }

        var root = new TomlTable();
        var currentTable = root;
        // Track which tables were "explicitly defined" (via a [section]
        // header) versus implicitly created by walking through a dotted
        // key. The TOML spec rejects redefining an explicitly-defined
        // table, but allows extending a table that was only created
        // implicitly.
        var explicitlyDefinedTables = new HashSet<TomlTable>();

        foreach (var symbol in result.Symbols)
        {
            if (symbol.Is(KeyValue))
            {
                ProjectKeyValue(symbol, currentTable);
            }
            else if (symbol.Is(Table))
            {
                // Table = Or(ArrayTable, StandardTable).Preserve(), so the
                // wrapper Symbol carries one child holding the concrete kind.
                var inner = symbol.Children[0];
                if (inner.Is(StandardTable))
                    currentTable = OpenStandardTable(root, inner, explicitlyDefinedTables);
                else if (inner.Is(ArrayTable))
                    currentTable = OpenArrayTable(root, inner, explicitlyDefinedTables);
            }
            // Everything else (comments, blank lines) is filtered out by
            // the grammar and never reaches here.
        }

        return root;
    }

    // ---------------------------------------------------------
    // Key navigation
    // ---------------------------------------------------------
    // Resolve the key path of a `[section]` header against the root
    // table. Walks each path segment, creating implicit tables along
    // the way and binding the leaf as an explicitly-defined table.
    private static TomlTable OpenStandardTable(TomlTable root, Symbol standardTable, HashSet<TomlTable> explicitlyDefined)
    {
        var path = ExtractKeyPath(standardTable.Children[0]);
        var parent = root;
        for (int index = 0; index < path.Count - 1; index++)
        {
            parent = StepInto(parent, path[index], explicitlyDefined);
        }

        var leafName = path[^1];
        if (parent.TryGet(leafName, out var existing))
        {
            if (existing is TomlTable existingTable && !explicitlyDefined.Contains(existingTable))
            {
                explicitlyDefined.Add(existingTable);
                return existingTable;
            }
            throw new TomlParseException($"Cannot redefine table '{string.Join('.', path)}'.");
        }

        var newTable = new TomlTable();
        parent.Add(leafName, newTable);
        explicitlyDefined.Add(newTable);
        return newTable;
    }

    // Resolve the key path of an `[[array.of.tables]]` header. The leaf
    // segment names an array; each header line appends a fresh table to
    // that array.
    private static TomlTable OpenArrayTable(TomlTable root, Symbol arrayTable, HashSet<TomlTable> explicitlyDefined)
    {
        var path = ExtractKeyPath(arrayTable.Children[0]);
        var parent = root;
        for (int index = 0; index < path.Count - 1; index++)
        {
            parent = StepInto(parent, path[index], explicitlyDefined);
        }

        var leafName = path[^1];
        var newEntry = new TomlTable();
        explicitlyDefined.Add(newEntry);

        if (parent.TryGet(leafName, out var existing))
        {
            if (existing is TomlArray existingArray)
            {
                var updated = new List<TomlValue>(existingArray.Items) { newEntry };
                parent.Replace(leafName, new TomlArray(updated));
                return newEntry;
            }
            throw new TomlParseException($"Cannot redefine '{string.Join('.', path)}' as an array of tables.");
        }

        parent.Add(leafName, new TomlArray(new List<TomlValue> { newEntry }));
        return newEntry;
    }

    // Walk into a child table by name, creating an implicit table if
    // none exists. Used by the dotted-key path on both [section]
    // headers and `a.b.c = value` keyvals.
    private static TomlTable StepInto(TomlTable table, string segment, HashSet<TomlTable> explicitlyDefined)
    {
        if (table.TryGet(segment, out var existing))
        {
            if (existing is TomlTable existingTable) return existingTable;
            if (existing is TomlArray array && array.Items.Count > 0 && array.Items[^1] is TomlTable lastEntry)
                return lastEntry;
            throw new TomlParseException($"'{segment}' is already a non-table value; cannot extend it.");
        }
        var implicitTable = new TomlTable();
        table.Add(segment, implicitTable);
        return implicitTable;
    }

    // ---------------------------------------------------------
    // Key extraction
    // ---------------------------------------------------------
    // A Key Symbol wraps either a SimpleKey or a DottedKey. Walk the
    // structure and return the path as a list of decoded segment names.
    private static List<string> ExtractKeyPath(Symbol keyNode)
    {
        var path = new List<string>();
        // Key -> SimpleKey or DottedKey
        var inner = keyNode.Children[0];
        if (inner.Is(SimpleKey))
        {
            path.Add(DecodeSimpleKey(inner));
        }
        else if (inner.Is(DottedKey))
        {
            // DottedKey's children are SimpleKey nodes (the dot-sep
            // marks were Delete-flattened away).
            foreach (var simpleKey in inner.Children)
                path.Add(DecodeSimpleKey(simpleKey));
        }
        else
        {
            throw new InvalidOperationException($"Unexpected key shape: {inner.Id.Value}");
        }
        return path;
    }

    private static string DecodeSimpleKey(Symbol simpleKey)
    {
        // SimpleKey -> QuotedKey or UnquotedKey
        var inner = simpleKey.Children[0];
        if (inner.Is(UnquotedKey)) return inner.ToString();
        if (inner.Is(QuotedKey))
        {
            // QuotedKey -> BasicString or LiteralString
            var stringNode = inner.Children[0];
            if (stringNode.Is(BasicString)) return DecodeBasicStringBody(stringNode);
            if (stringNode.Is(LiteralString)) return ExtractLiteralStringBody(stringNode);
        }
        throw new InvalidOperationException($"Unexpected simple-key shape: {inner.Id.Value}");
    }

    // ---------------------------------------------------------
    // Key/value pair projection
    // ---------------------------------------------------------
    private static void ProjectKeyValue(Symbol keyValue, TomlTable table)
    {
        // KeyValue -> [Key, Value]
        var keyNode = keyValue.Children[0];
        var valueNode = keyValue.Children[1];

        var path = ExtractKeyPath(keyNode);
        var owner = table;
        var implicits = new HashSet<TomlTable>();
        for (int index = 0; index < path.Count - 1; index++)
        {
            owner = StepInto(owner, path[index], implicits);
        }
        var leafName = path[^1];
        var value = ProjectValue(valueNode);
        owner.Add(leafName, value);
    }

    // ---------------------------------------------------------
    // Value projection
    // ---------------------------------------------------------
    private static TomlValue ProjectValue(Symbol valueNode)
    {
        // Value Or wraps the inner concrete value; the Or itself has
        // FlattenType.Flatten by default, but we marked Value as
        // Preserve so it shows up as a wrapper. Its single child is the
        // concrete value rule.
        var inner = valueNode.Children.Count == 1 ? valueNode.Children[0] : valueNode;

        if (inner.Is(BasicString))             return new TomlString(DecodeBasicStringBody(inner), TomlStringKind.Basic);
        if (inner.Is(LiteralString))           return new TomlString(ExtractLiteralStringBody(inner), TomlStringKind.Literal);
        if (inner.Is(MultiLineBasicString))    return new TomlString(DecodeMultiLineBasicStringBody(inner), TomlStringKind.MultiLineBasic);
        if (inner.Is(MultiLineLiteralString))  return new TomlString(ExtractMultiLineLiteralStringBody(inner), TomlStringKind.MultiLineLiteral);
        if (inner.Is(TomlGrammar.TomlTrue))    return new TomlBoolean(true);
        if (inner.Is(TomlGrammar.TomlFalse))   return new TomlBoolean(false);
        if (inner.Is(TomlGrammar.TomlArray))   return ProjectArray(inner);
        if (inner.Is(InlineTable))             return ProjectInlineTable(inner);
        if (inner.Is(OffsetDateTime))          return ProjectOffsetDateTime(inner);
        if (inner.Is(LocalDateTime))           return ProjectLocalDateTime(inner);
        if (inner.Is(LocalDate))               return ProjectLocalDate(inner);
        if (inner.Is(LocalTime))               return ProjectLocalTime(inner);
        if (inner.Is(TomlGrammar.TomlFloat))   return ProjectFloat(inner);
        // The hex/oct/bin prefix Literals are Delete by default, so
        // ToString() on the integer Symbol returns just the digits with
        // any underscore separators preserved by their OneOf rule.
        if (inner.Is(HexadecimalInteger)) return new TomlInteger(ParseInteger(inner.ToString().Replace("_", ""), 16), TomlIntegerBase.Hexadecimal);
        if (inner.Is(OctalInteger))       return new TomlInteger(ParseInteger(inner.ToString().Replace("_", ""), 8), TomlIntegerBase.Octal);
        if (inner.Is(BinaryInteger))      return new TomlInteger(ParseInteger(inner.ToString().Replace("_", ""), 2), TomlIntegerBase.Binary);
        if (inner.Is(DecimalInteger))     return new TomlInteger(long.Parse(inner.ToString().Replace("_", ""), CultureInfo.InvariantCulture), TomlIntegerBase.Decimal);

        throw new InvalidOperationException($"Unexpected value shape, symbol id: {inner.Id.Value}");
    }

    private static long ParseInteger(string text, int @base) =>
        Convert.ToInt64(text, @base);

    private static TomlValue ProjectFloat(Symbol floatNode)
    {
        // Float -> SpecialFloat or ordinaryFloat (a Preserve wrapper around
        // the digit-bearing And).
        var inner = floatNode.Children[0];
        if (inner.Is(SpecialFloat))
        {
            // SpecialFloat text is "[+-]?(inf|nan)". The Literal("inf") /
            // Literal("nan") are Delete by default, so the verbatim
            // mnemonic only survives through Symbol.SourceText, not
            // ToString.
            var text = inner.SourceText;
            if (text.EndsWith("inf"))
                return new TomlFloat(text.StartsWith("-") ? double.NegativeInfinity : double.PositiveInfinity);
            return new TomlFloat(double.NaN);
        }
        // OrdinaryFloat: the fraction's Token('.') and the exponent's
        // Token('e' | 'E') are Delete by default. ToString would lose
        // them; SourceText gives the verbatim text. Strip underscores
        // before handing to double.Parse.
        var floatText = inner.SourceText.Replace("_", "");
        return new TomlFloat(double.Parse(floatText, CultureInfo.InvariantCulture));
    }

    private static TomlValue ProjectArray(Symbol arrayNode)
    {
        var items = new List<TomlValue>();
        foreach (var child in arrayNode.Children)
        {
            if (child.Is(Value))
                items.Add(ProjectValue(child));
        }
        return new TomlArray(items);
    }

    private static TomlValue ProjectInlineTable(Symbol inlineTableNode)
    {
        var table = new TomlTable();
        foreach (var child in inlineTableNode.Children)
        {
            if (child.Is(KeyValue))
                ProjectKeyValue(child, table);
        }
        return table;
    }

    // ---------------------------------------------------------
    // Date-time projection
    // ---------------------------------------------------------
    // Date-time grammar uses Token('-'), Token(':'), and Token('.')
    // which are Delete by default. ToString would render
    // "19790527T073200Z" instead of "1979-05-27T07:32:00Z" and
    // DateTimeOffset.Parse would reject the compacted form, so we
    // recover the verbatim text via Symbol.SourceText.
    private static TomlValue ProjectOffsetDateTime(Symbol node)
    {
        var text = node.SourceText.Replace('t', 'T').Replace('z', 'Z');
        // ISO 8601 / RFC 3339 round-trip.
        return new TomlOffsetDateTime(DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
    }

    private static TomlValue ProjectLocalDateTime(Symbol node)
    {
        var text = node.SourceText.Replace('t', 'T');
        return new TomlLocalDateTime(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal));
    }

    private static TomlValue ProjectLocalDate(Symbol node) =>
        new TomlLocalDate(DateOnly.Parse(node.SourceText, CultureInfo.InvariantCulture));

    private static TomlValue ProjectLocalTime(Symbol node) =>
        new TomlLocalTime(TimeOnly.Parse(node.SourceText, CultureInfo.InvariantCulture));

    // ---------------------------------------------------------
    // String body decoding
    // ---------------------------------------------------------
    private static string DecodeBasicStringBody(Symbol stringNode)
    {
        // BasicString = ['"', basicStringBody, '"']. The body's
        // backslash-escape Token('\\') is Delete by default: ToString
        // would drop it and the consumer's escape decoder would misread
        // the next char ('\\Users' rendered as '\Users', then the
        // decoder treats 'U' as a Unicode-escape kickoff). Symbol.SourceText
        // returns the verbatim body text with backslashes intact.
        var bodyNode = stringNode.Children[0];
        var rawText = bodyNode.SourceText;
        return DecodeEscapeSequences(rawText, multiLine: false);
    }

    private static string ExtractLiteralStringBody(Symbol stringNode)
    {
        // LiteralString -> [literalStringBody]. The body uses
        // ScanWhile(literalChar, minimumCount: 0), which always emits
        // one leaf — possibly zero-width for the empty-string case ''
        // — so the body is always Children[0]. Literal strings have no
        // escapes and can't span newlines, so the body's ToString gives
        // the verbatim text directly.
        return stringNode.Children[0].ToString();
    }

    private static string DecodeMultiLineBasicStringBody(Symbol stringNode)
    {
        // MultiLineBasicString = ['"""', Optional(EOL), multiLineBasicStringBody, '"""']
        // The Optional(EOL) is Delete-flattened, so the only child that
        // survives is the multiLineBasicStringBody leaf — but only when
        // it has content. For an empty multi-line string """""", there
        // are no children.
        if (stringNode.Children.Count == 0) return "";
        // The body's EndOfLine leaves are Delete by default and the
        // basic-escape's leading backslash is also Delete. Recover the
        // verbatim text so newlines survive into the rendered string
        // and the escape decoder sees its backslashes.
        var rawText = stringNode.Children[0].SourceText;
        // Per spec: a newline immediately after the opening delimiter is
        // trimmed. Our grammar's Optional(EOL) ate it before the body
        // started, so rawText is already trimmed.
        return DecodeEscapeSequences(rawText, multiLine: true);
    }

    private static string ExtractMultiLineLiteralStringBody(Symbol stringNode)
    {
        if (stringNode.Children.Count == 0) return "";
        // EndOfLine inside the body is Delete by default: without
        // Symbol.SourceText, embedded newlines would be lost.
        return stringNode.Children[0].SourceText;
    }

    // Decode TOML basic-string escape sequences. Used for both basic
    // strings and multi-line basic strings.
    private static string DecodeEscapeSequences(string raw, bool multiLine)
    {
        if (raw.IndexOf('\\') < 0) return raw;

        var output = new StringBuilder(raw.Length);
        int index = 0;
        while (index < raw.Length)
        {
            char current = raw[index];
            if (current != '\\')
            {
                output.Append(current);
                index++;
                continue;
            }

            // Escape sequence.
            if (index + 1 >= raw.Length)
                throw new TomlParseException("Trailing backslash in string body.");

            char escapeChar = raw[index + 1];
            switch (escapeChar)
            {
                case '"':  output.Append('"');  index += 2; break;
                case '\\': output.Append('\\'); index += 2; break;
                case 'b':  output.Append('\b'); index += 2; break;
                case 'f':  output.Append('\f'); index += 2; break;
                case 'n':  output.Append('\n'); index += 2; break;
                case 'r':  output.Append('\r'); index += 2; break;
                case 't':  output.Append('\t'); index += 2; break;
                case 'u':
                {
                    // \uXXXX: 4 hex digits. The spec requires the
                    // result to be a valid Unicode scalar, so reject
                    // the surrogate range U+D800..U+DFFF.
                    var hex = raw.Substring(index + 2, 4);
                    int codepoint = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (codepoint >= 0xD800 && codepoint <= 0xDFFF)
                        throw new TomlParseException(
                            $"Invalid Unicode escape '\\u{hex}': U+{codepoint:X4} is in the surrogate range (U+D800..U+DFFF) and is not a valid Unicode scalar value.");
                    output.Append((char)codepoint);
                    index += 6;
                    break;
                }
                case 'U':
                {
                    // \UXXXXXXXX: 8 hex digits. Reject surrogates and
                    // values above U+10FFFF. uint.Parse so an
                    // 8-hex-digit input with the high bit set doesn't
                    // sign-extend to a negative int and slip past the
                    // > 0x10FFFF check.
                    var hex = raw.Substring(index + 2, 8);
                    uint codepoint = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (codepoint >= 0xD800 && codepoint <= 0xDFFF)
                        throw new TomlParseException(
                            $"Invalid Unicode escape '\\U{hex}': U+{codepoint:X8} is in the surrogate range (U+D800..U+DFFF) and is not a valid Unicode scalar value.");
                    if (codepoint > 0x10FFFF)
                        throw new TomlParseException(
                            $"Invalid Unicode escape '\\U{hex}': U+{codepoint:X8} is above U+10FFFF, the maximum Unicode code point.");
                    output.Append(char.ConvertFromUtf32((int)codepoint));
                    index += 10;
                    break;
                }
                default:
                    if (multiLine && (escapeChar == '\n' || escapeChar == '\r' || escapeChar == ' ' || escapeChar == '\t'))
                    {
                        // mlb-escaped-nl: a backslash at end of line eats
                        // any trailing whitespace, the newline itself, and
                        // any following whitespace-only lines.
                        index++; // skip the backslash
                        // Skip whitespace chars up to the newline.
                        while (index < raw.Length && (raw[index] == ' ' || raw[index] == '\t'))
                            index++;
                        if (index >= raw.Length || (raw[index] != '\n' && raw[index] != '\r'))
                            throw new TomlParseException("Invalid escape sequence: backslash not followed by recognized escape or end of line.");
                        // Skip the newline.
                        if (raw[index] == '\r' && index + 1 < raw.Length && raw[index + 1] == '\n') index += 2;
                        else index++;
                        // Skip following blank-or-whitespace prefix.
                        while (index < raw.Length && (raw[index] == ' ' || raw[index] == '\t' || raw[index] == '\n' || raw[index] == '\r'))
                            index++;
                        break;
                    }
                    throw new TomlParseException($"Invalid escape sequence '\\{escapeChar}'.");
            }
        }
        return output.ToString();
    }
}
