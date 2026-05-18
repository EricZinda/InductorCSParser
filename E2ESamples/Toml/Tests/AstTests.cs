using System;
using System.Linq;
using NUnit.Framework;
using InductorParser.E2ESamples.Toml.Rewrite;
using static InductorParser.E2ESamples.Toml.Tests.TestSupport;

namespace InductorParser.E2ESamples.Toml.Tests;

// AST-level tests: parse TOML input and verify the projected typed
// tree matches expectations. The test inputs are drawn directly from
// the worked examples in https://toml.io/en/v1.0.0 so each test name
// names the spec section it covers.
//
// Multi-line inputs go through Lines(lineBreak, ...) rather than a raw
// string literal: a raw string's embedded newlines are real source
// bytes that git autocrlf rewrites per platform, so the fixture would
// silently differ between a Linux and a Windows checkout. Building the
// input from single-line literals plus an explicit LineBreak keeps it
// identical everywhere, and the [TestCase(LineBreak.Lf/Crlf)] pair
// runs every multi-line fixture under both line endings from one body.
[TestFixture]
public class AstTests
{
    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void KeyValue_BasicTypes_ProjectToTypedValues(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "title = \"TOML Example\"",
            "enabled = true",
            "port = 8080",
            "pi = 3.14159");
        var root = TomlParser.Parse(input);

        Assert.That((string)root["title"], Is.EqualTo("TOML Example"));
        Assert.That((bool)root["enabled"], Is.True);
        Assert.That((long)root["port"], Is.EqualTo(8080));
        Assert.That((double)root["pi"], Is.EqualTo(3.14159).Within(1e-9));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void BasicString_DecodesEscapeSequences(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "quote = \"She said \\\"hi\\\"\"",
            "backslash = \"C:\\\\Users\"",
            "newline = \"line1\\nline2\"",
            "unicode = \"smile ☺\"");
        var root = TomlParser.Parse(input);

        Assert.That((string)root["quote"], Is.EqualTo("She said \"hi\""));
        Assert.That((string)root["backslash"], Is.EqualTo("C:\\Users"));
        Assert.That((string)root["newline"], Is.EqualTo("line1\nline2"));
        Assert.That((string)root["unicode"], Is.EqualTo("smile ☺"));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void LiteralString_KeepsBackslashesLiteral(LineBreak lineBreak)
    {
        // From the spec: literal strings have NO escape processing.
        var input = Lines(lineBreak,
            "winpath = 'C:\\Users\\nodejs\\templates'",
            "regex = '<\\i\\c*\\s*>'");
        var root = TomlParser.Parse(input);

        Assert.That((string)root["winpath"], Is.EqualTo(@"C:\Users\nodejs\templates"));
        Assert.That((string)root["regex"], Is.EqualTo(@"<\i\c*\s*>"));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void EmptyString_BothQuoteStyles_Parse(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "a = \"\"",
            "b = ''");
        var root = TomlParser.Parse(input);

        Assert.That((string)root["a"], Is.EqualTo(""));
        Assert.That((string)root["b"], Is.EqualTo(""));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void Integer_AllBases_DecodeToLong(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "dec = 1_234_567",
            "hex = 0xDEAD_BEEF",
            "oct = 0o755",
            "bin = 0b1010_1010",
            "negative = -42");
        var root = TomlParser.Parse(input);

        Assert.That((long)root["dec"], Is.EqualTo(1234567));
        Assert.That((long)root["hex"], Is.EqualTo(0xDEADBEEF));
        Assert.That((long)root["oct"], Is.EqualTo(493));
        Assert.That((long)root["bin"], Is.EqualTo(0b10101010));
        Assert.That((long)root["negative"], Is.EqualTo(-42));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void Float_RegularAndSpecial_Parse(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "pi = 3.14_15",
            "sci = 1e10",
            "neg_sci = -2.5E-3",
            "inf = inf",
            "neg_inf = -inf",
            "nan_value = nan");
        var root = TomlParser.Parse(input);

        Assert.That((double)root["pi"], Is.EqualTo(3.1415).Within(1e-9));
        Assert.That((double)root["sci"], Is.EqualTo(1e10));
        Assert.That((double)root["neg_sci"], Is.EqualTo(-2.5e-3).Within(1e-12));
        Assert.That((double)root["inf"], Is.EqualTo(double.PositiveInfinity));
        Assert.That((double)root["neg_inf"], Is.EqualTo(double.NegativeInfinity));
        Assert.That(double.IsNaN((double)root["nan_value"]), Is.True);
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void DottedKey_BuildsNestedTables(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "physical.color = \"orange\"",
            "physical.shape = \"round\"",
            "site.\"google.com\" = true");
        var root = TomlParser.Parse(input);

        Assert.That((string)root["physical"]["color"], Is.EqualTo("orange"));
        Assert.That((string)root["physical"]["shape"], Is.EqualTo("round"));
        Assert.That((bool)root["site"]["google.com"], Is.True);
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void StdTable_GroupsKeysUnderHeader(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "title = \"TOML Example\"",
            "",
            "[database]",
            "server = \"192.168.1.1\"",
            "ports = [ 8001, 8001, 8002 ]",
            "enabled = true",
            "",
            "[servers.alpha]",
            "ip = \"10.0.0.1\"",
            "",
            "[servers.beta]",
            "ip = \"10.0.0.2\"");
        var root = TomlParser.Parse(input);

        Assert.That((string)root["title"], Is.EqualTo("TOML Example"));
        Assert.That((string)root["database"]["server"], Is.EqualTo("192.168.1.1"));
        Assert.That((bool)root["database"]["enabled"], Is.True);
        Assert.That((string)root["servers"]["alpha"]["ip"], Is.EqualTo("10.0.0.1"));
        Assert.That((string)root["servers"]["beta"]["ip"], Is.EqualTo("10.0.0.2"));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void Array_HeterogeneousNumbersAndStrings(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "integers = [ 1, 2, 3 ]",
            "colors = [ \"red\", \"yellow\", \"green\" ]",
            "mixed_with_newlines = [",
            "    \"alpha\",",
            "    \"beta\",  # trailing comment",
            "    \"gamma\",",
            "]");
        var root = TomlParser.Parse(input);

        var integers = (TomlArray)root["integers"];
        Assert.That(integers.Items.Count, Is.EqualTo(3));
        Assert.That((long)integers.Items[2], Is.EqualTo(3));

        var colors = (TomlArray)root["colors"];
        Assert.That(colors.Items.Select(item => (string)item), Is.EqualTo(new[] { "red", "yellow", "green" }));

        var mixed = (TomlArray)root["mixed_with_newlines"];
        Assert.That(mixed.Items.Count, Is.EqualTo(3));
        Assert.That((string)mixed.Items[1], Is.EqualTo("beta"));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void InlineTable_ParsesFlatKeyVals(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "name = { first = \"Tom\", last = \"Preston-Werner\" }",
            "point = { x = 1, y = 2 }");
        var root = TomlParser.Parse(input);

        Assert.That((string)root["name"]["first"], Is.EqualTo("Tom"));
        Assert.That((string)root["name"]["last"], Is.EqualTo("Preston-Werner"));
        Assert.That((long)root["point"]["x"], Is.EqualTo(1));
        Assert.That((long)root["point"]["y"], Is.EqualTo(2));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void ArrayOfTables_AppendsEntries(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "[[products]]",
            "name = \"Hammer\"",
            "sku = 738594937",
            "",
            "[[products]]  # empty entry",
            "",
            "[[products]]",
            "name = \"Nail\"",
            "sku = 284758393",
            "color = \"gray\"");
        var root = TomlParser.Parse(input);

        var products = (TomlArray)root["products"];
        Assert.That(products.Items.Count, Is.EqualTo(3));
        Assert.That((string)products.Items[0]["name"], Is.EqualTo("Hammer"));
        Assert.That((long)products.Items[0]["sku"], Is.EqualTo(738594937));

        // Middle entry is the empty table.
        var middle = (TomlTable)products.Items[1];
        Assert.That(middle.Members.Count, Is.EqualTo(0));

        Assert.That((string)products.Items[2]["color"], Is.EqualTo("gray"));
    }

    [Test]
    public void OffsetDateTime_Parses()
    {
        var input = "when = 1979-05-27T07:32:00Z\n";
        var root = TomlParser.Parse(input);

        var when = (TomlOffsetDateTime)root["when"];
        Assert.That(when.Value, Is.EqualTo(new DateTimeOffset(1979, 5, 27, 7, 32, 0, TimeSpan.Zero)));
    }

    [Test]
    public void LocalDate_Parses()
    {
        var root = TomlParser.Parse("d = 1979-05-27\n");
        var d = (TomlLocalDate)root["d"];
        Assert.That(d.Value, Is.EqualTo(new DateOnly(1979, 5, 27)));
    }

    [Test]
    public void LocalTime_Parses()
    {
        var root = TomlParser.Parse("t = 07:32:00\n");
        var t = (TomlLocalTime)root["t"];
        Assert.That(t.Value, Is.EqualTo(new TimeOnly(7, 32, 0)));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void Comments_AreIgnored(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "# This is a full-line comment",
            "key = \"value\"  # this is a same-line comment",
            "",
            "[section]  # comment after a table header",
            "inner = 1");
        var root = TomlParser.Parse(input);

        Assert.That((string)root["key"], Is.EqualTo("value"));
        Assert.That((long)root["section"]["inner"], Is.EqualTo(1));
    }

    [Test]
    public void MlBasicString_TrimsLeadingNewlineAndDecodesEscapes()
    {
        // From the spec example.
        var input = "str1 = \"\"\"\nRoses are red\nViolets are blue\"\"\"\n";
        var root = TomlParser.Parse(input);

        Assert.That((string)root["str1"], Is.EqualTo("Roses are red\nViolets are blue"));
    }

    [Test]
    public void MlLiteralString_PreservesEverythingLiterally()
    {
        var input = "regex = '''I [dw]on't need \\d{2} apples'''\n";
        var root = TomlParser.Parse(input);

        Assert.That((string)root["regex"], Is.EqualTo("I [dw]on't need \\d{2} apples"));
    }

    [TestCase(LineBreak.Lf)]
    [TestCase(LineBreak.Crlf)]
    public void DuplicateKey_AtSameTableLevel_Throws(LineBreak lineBreak)
    {
        var input = Lines(lineBreak,
            "name = \"Tom\"",
            "name = \"Pradyun\"");
        var ex = Assert.Throws<TomlParseException>(() => TomlParser.Parse(input));
        Assert.That(ex!.Message, Does.Contain("Duplicate key 'name'"));
    }
}
