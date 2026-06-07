// Corpus test against xampl.bib (Oren Patashnik, 1988/2010, public
// domain, see Tests/Fixtures/xampl.bib for the upstream attribution
// note). This is the standard BibTeX example file, bundled with every
// TeX distribution since 1985 and the file used to test BibTeX itself.
// It exercises every entry type the standard BibTeX styles accept
// (@ARTICLE, @BOOK, @INBOOK, @BOOKLET, @INCOLLECTION, @MANUAL,
// @MASTERSTHESIS, @MISC, @INPROCEEDINGS, @PROCEEDINGS, @PHDTHESIS,
// @TECHREPORT, @UNPUBLISHED) plus the @preamble and @string macro
// declarations, plus the field-value features that aren't in the
// minimal-shape tests: bareword values like 'month = jul', string
// concatenation with '#', cross-reference fields, TeX-escape
// sequences in titles and author names, and stray prose between
// entries.
//
// Two asserts here:
//   1. Both parsers return the same entries from the same file. If
//      one accepts something the other doesn't, we want to know.
//   2. The entry shape lines up with what xampl.bib's structure
//      promises: 40 entries, distributed across the 13 entry types
//      plus preamble and string.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OriginalParser = BibTexSample.Original.BibTexParser;
using RewriteReader = BibTexSample.Rewrite.BibTexReader;

namespace BibTexSample.Tests;

[TestFixture]
public class XamplCorpusTests
{
    private static string LoadCorpus()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Tests", "Fixtures", "xampl.bib");
        return File.ReadAllText(path);
    }

    [Test]
    public void Both_parsers_accept_the_full_xampl_corpus()
    {
        var input = LoadCorpus();
        var originalEntries = OriginalParser.Parse(input);
        var rewriteEntries = RewriteReader.Parse(input);

        Assert.That(rewriteEntries, Has.Count.EqualTo(originalEntries.Count),
            "Original and Rewrite should accept the same entries");

        for (int i = 0; i < originalEntries.Count; i++)
        {
            var originalEntry = originalEntries[i];
            var rewriteEntry = rewriteEntries[i];
            Assert.That(rewriteEntry.EntryType, Is.EqualTo(originalEntry.EntryType),
                $"entry {i}: type mismatch");
            Assert.That(rewriteEntry.CitationKey, Is.EqualTo(originalEntry.CitationKey),
                $"entry {i} ({originalEntry.EntryType}): citation key mismatch");
            Assert.That(rewriteEntry.Fields.Select(field => field.Name),
                Is.EqualTo(originalEntry.Fields.Select(field => field.Name)),
                $"entry {i} ({originalEntry.CitationKey}): field names mismatch");
            Assert.That(rewriteEntry.Fields.Select(field => field.Value),
                Is.EqualTo(originalEntry.Fields.Select(field => field.Value)),
                $"entry {i} ({originalEntry.CitationKey}): field values mismatch");
        }
    }

    [Test]
    public void Xampl_carries_one_preamble_three_string_macros_and_thirty_six_regular_entries()
    {
        var input = LoadCorpus();
        var entries = RewriteReader.Parse(input);

        var byType = entries.GroupBy(e => e.EntryType).ToDictionary(g => g.Key, g => g.Count());

        Assert.That(byType.GetValueOrDefault("preamble"), Is.EqualTo(1));
        Assert.That(byType.GetValueOrDefault("string"), Is.EqualTo(3));
        Assert.That(entries.Count, Is.EqualTo(40), "xampl.bib defines 40 entries total");

        // Every standard BibTeX entry type shows up at least once.
        var regularTypes = new[]
        {
            "article", "inbook", "book", "booklet", "incollection",
            "manual", "mastersthesis", "misc", "inproceedings",
            "proceedings", "phdthesis", "techreport", "unpublished",
        };
        foreach (var entryType in regularTypes)
        {
            Assert.That(byType.GetValueOrDefault(entryType), Is.GreaterThanOrEqualTo(1),
                $"xampl.bib should contain at least one @{entryType} entry");
        }
    }

    [Test]
    public void Xampl_exercises_bareword_field_values_string_concatenation_and_tex_escapes()
    {
        var input = LoadCorpus();
        var entries = RewriteReader.Parse(input);

        // 'month = jul' is the canonical bareword value, used heavily
        // in xampl.bib. Find one entry that has it.
        var bareWordMonth = entries
            .SelectMany(e => e.Fields, (e, f) => (entry: e, field: f))
            .First(both => both.field.Name == "month" && both.field.Value == "jul");
        Assert.That(bareWordMonth.entry.EntryType, Is.Not.Empty);

        // String concatenation in 'booktitle = "..." # STOC' fields
        // shows up across the @INPROCEEDINGS entries. The macro name
        // STOC stays as the literal text 'STOC' since neither parser
        // resolves macros.
        var concatBooktitle = entries
            .SelectMany(e => e.Fields, (e, f) => (entry: e, field: f))
            .First(both => both.field.Name == "booktitle" && both.field.Value.Contains("STOC"));
        Assert.That(concatBooktitle.field.Value, Does.Contain("Proc. Fifteenth Annual ACM"));

        // TeX-escape sequences like '{\"{U}}nderwood' stay verbatim
        // in field values. The parser captures the source form and
        // leaves expansion to the renderer.
        var texEscapedAuthor = entries
            .SelectMany(e => e.Fields)
            .FirstOrDefault(f => f.Value.Contains(@"{\""{U}}nderwood"));
        Assert.That(texEscapedAuthor, Is.Not.Null,
            "xampl.bib's UNPUBLISHED entry has Ulrich {\\\"{U}}nderwood as an author");
    }
}
