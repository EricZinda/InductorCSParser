// Tests for both the Original C# port of a BibTeX parser and the
// InductorParser-based Rewrite. The corpus is drawn from real
// academic .bib snippets: BibTeX's own examples, the bibtex-parser
// (JS) and pybtex (Python) test suites, and a handful of real-world
// entries with Unicode author names and titles.
//
// The Unicode citation-key cases are the headline difference: the
// Original rejects them at the regex, the Rewrite parses them as
// UAX #31 identifiers and rebuilds the same entry shape.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OriginalParser = BibTexSample.Original.BibTexParser;
using OriginalException = BibTexSample.Original.BibTexParseException;
using OriginalEntry = BibTexSample.Original.BibTexEntry;
using RewriteReader = BibTexSample.Rewrite.BibTexReader;
using RewriteEntry = BibTexSample.Rewrite.BibTexEntry;

namespace BibTexSample.Tests;

[TestFixture]
public class BibTexTests
{
    [Test]
    public void Both_parsers_read_a_minimal_article_entry()
    {
        const string input = @"@article{einstein1905,
  author = ""Albert Einstein"",
  title = ""On the electrodynamics of moving bodies"",
  journal = ""Annalen der Physik"",
  year = 1905
}";
        var originalEntries = OriginalParser.Parse(input);
        var rewriteEntries = RewriteReader.Parse(input);

        Assert.That(originalEntries, Has.Count.EqualTo(1));
        Assert.That(rewriteEntries, Has.Count.EqualTo(1));

        Assert.That(originalEntries[0].EntryType, Is.EqualTo("article"));
        Assert.That(rewriteEntries[0].EntryType, Is.EqualTo("article"));

        Assert.That(originalEntries[0].CitationKey, Is.EqualTo("einstein1905"));
        Assert.That(rewriteEntries[0].CitationKey, Is.EqualTo("einstein1905"));

        AssertSameFields(originalEntries[0], rewriteEntries[0]);
    }

    [Test]
    public void Both_parsers_handle_braced_values_with_nested_braces()
    {
        const string input = @"@book{knuth1984,
  author = {Donald E. Knuth},
  title = {The {\TeX}book},
  year = 1984
}";
        var originalEntries = OriginalParser.Parse(input);
        var rewriteEntries = RewriteReader.Parse(input);

        Assert.That(originalEntries, Has.Count.EqualTo(1));
        Assert.That(rewriteEntries, Has.Count.EqualTo(1));

        var originalTitle = originalEntries[0].Fields.Single(field => field.Name == "title").Value;
        var rewriteTitle = rewriteEntries[0].Fields.Single(field => field.Name == "title").Value;

        Assert.That(originalTitle, Is.EqualTo(@"The {\TeX}book"));
        Assert.That(rewriteTitle, Is.EqualTo(@"The {\TeX}book"));
    }

    [Test]
    public void Both_parsers_handle_deeply_nested_braces()
    {
        const string input = @"@misc{nested,
  note = {a {b {c {d}}} e}
}";
        var originalEntries = OriginalParser.Parse(input);
        var rewriteEntries = RewriteReader.Parse(input);

        Assert.That(originalEntries[0].Fields[0].Value, Is.EqualTo("a {b {c {d}}} e"));
        Assert.That(rewriteEntries[0].Fields[0].Value, Is.EqualTo("a {b {c {d}}} e"));
    }

    [Test]
    public void Both_parsers_handle_unicode_in_field_values()
    {
        const string input = @"@article{schrodinger1926,
  author = ""Erwin Schrödinger"",
  title = ""Quantisierung als Eigenwertproblem"",
  journal = ""Annalen der Physik"",
  year = 1926
}";
        var originalEntries = OriginalParser.Parse(input);
        var rewriteEntries = RewriteReader.Parse(input);

        Assert.That(originalEntries[0].Fields.Single(field => field.Name == "author").Value,
            Is.EqualTo("Erwin Schrödinger"));
        Assert.That(rewriteEntries[0].Fields.Single(field => field.Name == "author").Value,
            Is.EqualTo("Erwin Schrödinger"));
    }

    [Test]
    public void Both_parsers_handle_chinese_japanese_korean_in_field_values()
    {
        const string input = @"@book{tao2011,
  author = {陶 哲軒},
  title = {解析入门},
  year = 2011
}";
        var originalEntries = OriginalParser.Parse(input);
        var rewriteEntries = RewriteReader.Parse(input);

        Assert.That(originalEntries[0].Fields.Single(field => field.Name == "title").Value,
            Is.EqualTo("解析入门"));
        Assert.That(rewriteEntries[0].Fields.Single(field => field.Name == "title").Value,
            Is.EqualTo("解析入门"));
    }

    [TestCase("gärtner2020")]
    [TestCase("张2019")]
    [TestCase("καραμβάρης1998")]
    [TestCase("müller_2024")]
    [TestCase("горбачёв1985")]
    public void Rewrite_accepts_Unicode_citation_keys_the_Original_rejects(string citationKey)
    {
        var input = $"@article{{{citationKey}, author = \"Test\", year = 2020}}";

        var rewriteEntries = RewriteReader.Parse(input);
        Assert.That(rewriteEntries, Has.Count.EqualTo(1));
        Assert.That(rewriteEntries[0].CitationKey, Is.EqualTo(citationKey));

        Assert.That(() => OriginalParser.Parse(input),
            Throws.InstanceOf<OriginalException>(),
            "Original parser should reject Unicode citation key '" + citationKey + "'");
    }

    [Test]
    public void Both_parsers_handle_multiple_entries_with_stray_text_between()
    {
        const string input = @"This is a paper bibliography.

@article{first,
  author = ""A"",
  year = 2020
}

(This stray comment is allowed.)

@book{second,
  author = ""B"",
  year = 2021
}";
        var originalEntries = OriginalParser.Parse(input);
        var rewriteEntries = RewriteReader.Parse(input);

        Assert.That(originalEntries, Has.Count.EqualTo(2));
        Assert.That(rewriteEntries, Has.Count.EqualTo(2));

        Assert.That(originalEntries.Select(e => e.CitationKey),
            Is.EquivalentTo(new[] { "first", "second" }));
        Assert.That(rewriteEntries.Select(e => e.CitationKey),
            Is.EquivalentTo(new[] { "first", "second" }));
    }

    [Test]
    public void Both_parsers_accept_a_trailing_comma_after_the_last_field()
    {
        const string input = @"@misc{trailing,
  author = ""X"",
  year = 2024,
}";
        var originalEntries = OriginalParser.Parse(input);
        var rewriteEntries = RewriteReader.Parse(input);

        Assert.That(originalEntries[0].Fields, Has.Count.EqualTo(2));
        Assert.That(rewriteEntries[0].Fields, Has.Count.EqualTo(2));
    }

    [Test]
    public void Both_parsers_treat_entry_types_and_field_names_as_case_insensitive()
    {
        const string input = @"@Article{caseTest,
  Author = ""X"",
  YEAR = 2024
}";
        var originalEntries = OriginalParser.Parse(input);
        var rewriteEntries = RewriteReader.Parse(input);

        Assert.That(originalEntries[0].EntryType, Is.EqualTo("article"));
        Assert.That(rewriteEntries[0].EntryType, Is.EqualTo("article"));

        Assert.That(originalEntries[0].Fields.Select(f => f.Name),
            Is.EquivalentTo(new[] { "author", "year" }));
        Assert.That(rewriteEntries[0].Fields.Select(f => f.Name),
            Is.EquivalentTo(new[] { "author", "year" }));
    }

    [Test]
    public void Rewrite_reports_line_and_column_on_a_missing_closing_brace()
    {
        const string input = @"@article{broken, author = ""A""";
        var exception = Assert.Throws<FormatException>(() => RewriteReader.Parse(input));
        Assert.That(exception!.Message, Does.StartWith("line 1, column"));
        Assert.That(exception.Message, Does.Contain("expected '}' to close entry"));
    }

    [Test]
    public void Rewrite_reports_position_on_a_missing_equals_sign()
    {
        const string input = @"@article{x, author ""A""}";
        var exception = Assert.Throws<FormatException>(() => RewriteReader.Parse(input));
        Assert.That(exception!.Message, Does.Contain("expected '=' after field name"));
    }

    [Test]
    public void Rewrite_reports_position_on_an_unterminated_quoted_value()
    {
        const string input = "@article{x, author = \"missing closing quote, year = 2020}";
        var exception = Assert.Throws<FormatException>(() => RewriteReader.Parse(input));
        Assert.That(exception!.Message, Does.StartWith("line 1, column"));
    }

    [Test]
    public void Rewrite_tree_carries_source_text_on_every_named_node()
    {
        const string input = @"@article{einstein1905,
  author = ""Albert Einstein"",
  year = 1905
}";
        var result = BibTexSample.Rewrite.BibTexGrammar.Document.Parse(input);
        Assert.That(result.Success, Is.True);
        var entry = result.Symbols
            .SelectMany(symbol => symbol.FindAll(BibTexSample.Rewrite.BibTexGrammar.Entry))
            .First();

        Assert.That(entry.Find(BibTexSample.Rewrite.BibTexGrammar.EntryType)!.SourceText, Is.EqualTo("article"));
        Assert.That(entry.Find(BibTexSample.Rewrite.BibTexGrammar.CitationKey)!.SourceText, Is.EqualTo("einstein1905"));

        var fields = entry.FindAll(BibTexSample.Rewrite.BibTexGrammar.Field).ToList();
        Assert.That(fields, Has.Count.EqualTo(2));
        Assert.That(fields[0].Find(BibTexSample.Rewrite.BibTexGrammar.FieldName)!.SourceText, Is.EqualTo("author"));
    }

    private static void AssertSameFields(OriginalEntry original, RewriteEntry rewrite)
    {
        Assert.That(rewrite.Fields.Count, Is.EqualTo(original.Fields.Count));
        for (int i = 0; i < original.Fields.Count; i++)
        {
            Assert.That(rewrite.Fields[i].Name, Is.EqualTo(original.Fields[i].Name));
            Assert.That(rewrite.Fields[i].Value, Is.EqualTo(original.Fields[i].Value));
        }
    }
}
