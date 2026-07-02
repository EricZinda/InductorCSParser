// Thin tree walker that drives BibTexGrammar.Document and converts
// the parsed tree to a list of records the test corpus can compare
// against the Original/ parser's output. Mirrors the upstream
// bibtex-parser / pybtex public API: parse(text) returns a list of
// entries, each with a type, citation key, and field/value pairs.
//
// Three entry shapes show up in the tree:
//   - regularEntry:  @article{key, ...}     -> EntryType, CitationKey, Fields
//   - preambleEntry: @preamble{ value }     -> EntryType="preamble",
//                                              CitationKey="",
//                                              Fields=[("", value)]
//   - stringEntry:   @string{ name = ... }  -> EntryType="string",
//                                              CitationKey="",
//                                              Fields=[(name, value)]
//
// All three appear as children of an "entry" Or-node, so a single
// FindAll(Entry) walks them in document order and the dispatch picks
// the matching shape's specific children.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using InductorParser;
using InductorParser.SyntaxTree;

namespace BibTexSample.Rewrite;

public record BibTexEntry(
    string EntryType,
    string CitationKey,
    IReadOnlyList<BibTexField> Fields,
    int Position);

public record BibTexField(string Name, string Value);

public static class BibTexReader
{
    public static IReadOnlyList<BibTexEntry> Parse(string input)
    {
        // This sample renders the failure position itself (see the error type's
        // formatting), so the message text stays position-less.
        var options = new ParseOptions
        {
            WithErrorTemplate = "{message}",
            PositionalErrorTemplate = "unexpected '{character}'.",
            EndOfInputErrorTemplate = "unexpected end of input.",
        };
        var result = BibTexGrammar.Document.Parse(input, options);
        if (!result.Success)
        {
            int line = result.ErrorLine + 1;
            int column = result.ErrorCharColumn + 1;
            throw new FormatException($"line {line}, column {column}: {result.ErrorMessage}");
        }
        var entries = new List<BibTexEntry>();
        foreach (var top in result.Symbols)
        {
            foreach (var entrySymbol in top.FindAll(BibTexGrammar.Entry))
            {
                entries.Add(BuildEntry(entrySymbol));
            }
        }
        return entries;
    }

    private static BibTexEntry BuildEntry(Symbol entrySymbol)
    {
        if (entrySymbol.Find(BibTexGrammar.PreambleEntry) is { } preamble)
            return BuildPreambleEntry(preamble);
        if (entrySymbol.Find(BibTexGrammar.StringEntry) is { } stringMacro)
            return BuildStringEntry(stringMacro);
        if (entrySymbol.Find(BibTexGrammar.RegularEntry) is { } regular)
            return BuildRegularEntry(regular);
        throw new InvalidOperationException("entry node carries no recognized inner shape");
    }

    private static BibTexEntry BuildRegularEntry(Symbol entrySymbol)
    {
        var entryType = entrySymbol.Find(BibTexGrammar.EntryType)!.ToString().ToLowerInvariant();
        var citationKey = entrySymbol.Find(BibTexGrammar.CitationKey)!.ToString();
        var fields = entrySymbol.FindAll(BibTexGrammar.Field)
            .Select(BuildField)
            .ToList();
        var position = entrySymbol.SourceRange?.Start.CharIndex ?? 0;
        return new BibTexEntry(entryType, citationKey, fields, position);
    }

    private static BibTexEntry BuildPreambleEntry(Symbol entrySymbol)
    {
        var value = ConcatenateValueParts(entrySymbol.Find(BibTexGrammar.FieldValue)!);
        var position = entrySymbol.SourceRange?.Start.CharIndex ?? 0;
        return new BibTexEntry("preamble", "", new[] { new BibTexField("", value) }, position);
    }

    private static BibTexEntry BuildStringEntry(Symbol entrySymbol)
    {
        var name = entrySymbol.Find(BibTexGrammar.MacroName)!.ToString().ToLowerInvariant();
        var value = ConcatenateValueParts(entrySymbol.Find(BibTexGrammar.FieldValue)!);
        var position = entrySymbol.SourceRange?.Start.CharIndex ?? 0;
        return new BibTexEntry("string", "", new[] { new BibTexField(name, value) }, position);
    }

    private static BibTexField BuildField(Symbol fieldSymbol)
    {
        var name = fieldSymbol.Find(BibTexGrammar.FieldName)!.ToString().ToLowerInvariant();
        var value = ConcatenateValueParts(fieldSymbol.Find(BibTexGrammar.FieldValue)!);
        return new BibTexField(name, value);
    }

    // FieldValue is one-or-more value-part children joined by '#' at
    // parse time. The '#' is Delete'd so only the parts survive in the
    // tree. This walk concatenates their contents in tree order.
    private static string ConcatenateValueParts(Symbol fieldValue)
    {
        var buffer = new StringBuilder();
        foreach (var child in fieldValue.Children)
        {
            buffer.Append(ExtractValuePart(child));
        }
        return buffer.ToString();
    }

    private static string ExtractValuePart(Symbol part)
    {
        if (part.Is(BibTexGrammar.QuotedValue)) return StripDelimiters(part);
        if (part.Is(BibTexGrammar.BracedValue)) return StripDelimiters(part);
        if (part.Is(BibTexGrammar.IntegerValue)) return part.ToString();
        if (part.Is(BibTexGrammar.BareWord)) return part.ToString();
        throw new InvalidOperationException($"unknown value-part shape: {part.DisplayName}");
    }

    // Returns the value content with the outer delimiters removed. We
    // use SourceText, not ToString, because the inner Token('{') /
    // Token('}') inside a nested braced value default to
    // FlattenType.Delete and would drop from ToString. SourceText is
    // verbatim source bytes, so nested braces and escape characters
    // survive. The only adjustment is to strip the surrounding pair.
    private static string StripDelimiters(Symbol delimitedValue)
    {
        var raw = delimitedValue.SourceText;
        return raw.Substring(1, raw.Length - 2);
    }
}
