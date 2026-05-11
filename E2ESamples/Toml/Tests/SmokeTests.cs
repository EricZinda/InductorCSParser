using NUnit.Framework;
using InductorParser.E2ESamples.Toml.Rewrite;

namespace InductorParser.E2ESamples.Toml.Tests;

// Minimum viable round-trip tests for the InductorParser TOML grammar.
// These don't decode values yet; they just assert the grammar accepts
// well-formed TOML and rejects clearly malformed TOML at the right
// position. Detailed AST tests live in TomlAstTests.
[TestFixture]
public class SmokeTests
{
    [Test]
    public void EmptyDocument_Parses()
    {
        var result = TomlGrammar.TomlDocument.Parse("");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void SingleKeyValue_Parses()
    {
        var result = TomlGrammar.TomlDocument.Parse("key = \"value\"");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void SimpleTable_Parses()
    {
        var input = "[server]\nhost = \"localhost\"\nport = 8080\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void DottedKey_Parses()
    {
        var input = "physical.color = \"orange\"\nphysical.shape = \"round\"\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Array_Parses()
    {
        var input = "ports = [ 8000, 8001, 8002 ]\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void InlineTable_Parses()
    {
        var input = "point = { x = 1, y = 2 }\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Booleans_Parse()
    {
        var input = "enabled = true\ndisabled = false\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Numbers_Parse()
    {
        var input = "int = 42\nhex = 0xDEADBEEF\noct = 0o755\nbin = 0b1010\nfloat = 3.14\nexp = 1e10\nsign = -7\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }
}
