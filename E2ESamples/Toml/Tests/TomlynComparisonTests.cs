using System;
using System.Linq;
using NUnit.Framework;
using TomlynModel = Tomlyn.Model;
using InductorParser.E2ESamples.Toml.Rewrite;

namespace InductorParser.E2ESamples.Toml.Tests;

// Side-by-side parser comparison against Tomlyn
// (https://www.nuget.org/packages/Tomlyn, BSD-2-Clause), the reference
// C# TOML parser. For each input we project both parsers' output to a
// common shape (string for strings, long for ints, etc.) and assert
// they agree. Catches projection drift between the rewrite and the
// reference.
[TestFixture]
public class TomlynComparisonTests
{
    private static (TomlTable ours, TomlynModel.TomlTable theirs) ParseBoth(string input)
    {
        var ours = TomlParser.Parse(input);
        var theirs = Tomlyn.Toml.ToModel(input);
        return (ours, theirs);
    }

    [Test]
    public void StringsAndScalars_BothPartiesAgree()
    {
        var input = """
                    title = "TOML Example"
                    enabled = true
                    port = 8080
                    pi = 3.14159
                    sci = 1.5e3
                    """;
        var (ours, theirs) = ParseBoth(input);

        Assert.That((string)ours["title"], Is.EqualTo((string)theirs["title"]!));
        Assert.That((bool)ours["enabled"], Is.EqualTo((bool)theirs["enabled"]!));
        Assert.That((long)ours["port"], Is.EqualTo((long)theirs["port"]!));
        Assert.That((double)ours["pi"], Is.EqualTo((double)theirs["pi"]!).Within(1e-9));
        Assert.That((double)ours["sci"], Is.EqualTo((double)theirs["sci"]!).Within(1e-9));
    }

    [Test]
    public void NestedTables_BothPartiesAgree()
    {
        var input = """
                    [database]
                    server = "192.168.1.1"
                    enabled = true

                    [servers.alpha]
                    ip = "10.0.0.1"

                    [servers.beta]
                    ip = "10.0.0.2"
                    """;
        var (ours, theirs) = ParseBoth(input);

        Assert.That((string)ours["database"]["server"],
            Is.EqualTo((string)((TomlynModel.TomlTable)theirs["database"]!)["server"]!));

        var ourAlpha = ours["servers"]["alpha"];
        var theirAlpha = (TomlynModel.TomlTable)((TomlynModel.TomlTable)theirs["servers"]!)["alpha"]!;
        Assert.That((string)ourAlpha["ip"], Is.EqualTo((string)theirAlpha["ip"]!));
    }

    [Test]
    public void Arrays_BothPartiesAgree()
    {
        var input = """
                    integers = [ 1, 2, 3 ]
                    colors = [ "red", "yellow", "green" ]
                    """;
        var (ours, theirs) = ParseBoth(input);

        var ourIntegers = (TomlArray)ours["integers"];
        var theirIntegers = (TomlynModel.TomlArray)theirs["integers"]!;
        Assert.That(ourIntegers.Items.Count, Is.EqualTo(theirIntegers.Count));
        for (int index = 0; index < ourIntegers.Items.Count; index++)
            Assert.That((long)ourIntegers.Items[index], Is.EqualTo((long)theirIntegers[index]!));

        var ourColors = (TomlArray)ours["colors"];
        var theirColors = (TomlynModel.TomlArray)theirs["colors"]!;
        var ourStrings = ourColors.Items.Select(item => (string)item).ToArray();
        var theirStrings = Enumerable.Range(0, theirColors.Count).Select(index => (string)theirColors[index]!).ToArray();
        Assert.That(ourStrings, Is.EqualTo(theirStrings));
    }

    [Test]
    public void ArrayOfTables_BothPartiesAgree()
    {
        var input = """
                    [[products]]
                    name = "Hammer"
                    sku = 738594937

                    [[products]]
                    name = "Nail"
                    sku = 284758393
                    color = "gray"
                    """;
        var (ours, theirs) = ParseBoth(input);

        var ourProducts = (TomlArray)ours["products"];
        var theirProducts = (TomlynModel.TomlTableArray)theirs["products"]!;
        Assert.That(ourProducts.Items.Count, Is.EqualTo(theirProducts.Count));
        for (int index = 0; index < ourProducts.Items.Count; index++)
        {
            var ourItem = (TomlTable)ourProducts.Items[index];
            var theirItem = theirProducts[index];
            Assert.That((string)ourItem["name"], Is.EqualTo((string)theirItem["name"]!));
            Assert.That((long)ourItem["sku"], Is.EqualTo((long)theirItem["sku"]!));
        }
    }

    [TestCase("key = \"unclosed")]
    [TestCase("key value\n")]
    [TestCase("[server\nport = 8080\n")]
    [TestCase("ports = [ 8000, 8001, 8002\n")]
    public void BadInputs_BothPartiesReject(string input)
    {
        // Both parsers must reject. The exact exception type and
        // message wording differ; what matters is that nothing parses
        // successfully.
        Assert.Throws<TomlParseException>(() => TomlParser.Parse(input));
        Assert.Throws<Tomlyn.TomlException>(() => Tomlyn.Toml.ToModel(input));
    }
}
