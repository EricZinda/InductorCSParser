using System;
using NUnit.Framework;
using InductorParser.E2ESamples.Toml.Rewrite;

namespace InductorParser.E2ESamples.Toml.Tests;

// The README's headline claim about this port is "errors come back with
// usable positions". These tests pick a handful of malformed inputs,
// assert that the InductorParser-based TOML rewrite rejects them, and
// verify the line/column the error points at. Tomlyn rejects the same
// inputs (verified in TomlynComparisonTests) so the rewrite's behavior
// is consistent with the reference implementation, even if the exact
// message wording differs.
//
// LSP convention: line and column are zero-based.
[TestFixture]
public class ErrorPositionTests
{
    [Test]
    public void UnterminatedBasicString_PointsToEndOfBody()
    {
        // The closing '"' is missing. The parser walks the body and
        // hits EOF. The deepest failure should be at the EOF position
        // (or at the newline if there is one).
        var input = "key = \"unclosed";
        var result = TomlGrammar.TomlDocument.Parse(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        // ErrorCharColumn is at end-of-input (column == length of the only line)
        // because that's where the closing quote was expected.
        Assert.That(result.ErrorCharColumn, Is.EqualTo(input.Length));
    }

    [Test]
    public void MissingEqualsAfterKey_PointsAtValuePosition()
    {
        // The `=` is missing between key and value.
        var input = "key value\n";
        var result = TomlGrammar.TomlDocument.Parse(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        // The grammar expected '=' after `key `. Deepest failure is at the 'v'
        // of "value" (column 4: 'k'(0) 'e'(1) 'y'(2) ' '(3) 'v'(4)).
        Assert.That(result.ErrorCharColumn, Is.EqualTo(4));
        Assert.That(result.ErrorMessage, Does.Contain("Expected '='").IgnoreCase);
    }

    [Test]
    public void MissingClosingBracketOnTableHeader_PointsToBracketPosition()
    {
        // The closing ']' is missing.
        var input = "[server\nport = 8080\n";
        var result = TomlGrammar.TomlDocument.Parse(input);

        Assert.That(result.Success, Is.False);
        // Deepest failure is at the newline after "[server"
        // (line 0, column 7: '['(0) 's'(1) 'e'(2) 'r'(3) 'v'(4) 'e'(5) 'r'(6)).
        Assert.That(result.ErrorLine, Is.EqualTo(0));
        Assert.That(result.ErrorCharColumn, Is.EqualTo(7));
        Assert.That(result.ErrorMessage, Does.Contain("']'"));
    }

    [Test]
    public void UnclosedArray_PointsToMissingBracket()
    {
        // The closing ']' is missing.
        var input = "ports = [ 8000, 8001, 8002\n";
        var result = TomlGrammar.TomlDocument.Parse(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("']'"));
    }

    [Test]
    public void TomlParser_DuplicateKey_ReportsAtSemanticLayer()
    {
        // Duplicate key isn't a grammar error (the input matches the
        // grammar fine). It's caught by the TomlParser consumer when
        // it tries to insert the second one. The exception type and
        // message tell the user which key was duplicated.
        var input = "name = \"Tom\"\nname = \"Pradyun\"\n";
        var ex = Assert.Throws<TomlParseException>(() => TomlParser.Parse(input));
        Assert.That(ex!.Message, Does.Contain("Duplicate key 'name'"));
    }
}
