using System;
using NUnit.Framework;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

// Parallel to JsonParserTests. Covers the same grammar behaviors via
// the strongly-typed JsonParserTyped entry point, with one extra dose
// of pattern-matching so the typed discriminated-union story is
// exercised end-to-end.
[TestFixture]
public class JsonParserTypedTests
{
    [Test]
    public void Projects_null_literal()
    {
        Assert.That(JsonParserTyped.Project("null"), Is.SameAs(JsonNullValue.Instance));
    }

    [Test]
    public void Projects_boolean_literals()
    {
        Assert.That(JsonParserTyped.Project("true"), Is.EqualTo(new JsonBoolValue(true)));
        Assert.That(JsonParserTyped.Project("false"), Is.EqualTo(new JsonBoolValue(false)));
    }

    [TestCase("0", 0.0)]
    [TestCase("42", 42.0)]
    [TestCase("-7", -7.0)]
    [TestCase("3.14", 3.14)]
    [TestCase("1e3", 1000.0)]
    public void Projects_numbers(string input, double expected)
    {
        Assert.That(JsonParserTyped.Project(input), Is.EqualTo(new JsonNumberValue(expected)));
    }

    [Test]
    public void Projects_strings()
    {
        Assert.That(JsonParserTyped.Project("\"hello\""), Is.EqualTo(new JsonStringValue("hello")));
    }

    [Test]
    public void Projects_empty_object()
    {
        var result = (JsonObjectValue)JsonParserTyped.Project("{}");
        Assert.That(result.Members, Is.Empty);
    }

    [Test]
    public void Projects_flat_object()
    {
        var result = (JsonObjectValue)JsonParserTyped.Project(
            "{\"name\":\"Alice\",\"age\":30,\"active\":true,\"partner\":null}");

        Assert.That(result.Members["name"], Is.EqualTo(new JsonStringValue("Alice")));
        Assert.That(result.Members["age"], Is.EqualTo(new JsonNumberValue(30.0)));
        Assert.That(result.Members["active"], Is.EqualTo(new JsonBoolValue(true)));
        Assert.That(result.Members["partner"], Is.SameAs(JsonNullValue.Instance));
    }

    [Test]
    public void Projects_array()
    {
        var result = (JsonArrayValue)JsonParserTyped.Project("[1, \"two\", true, null]");

        Assert.That(result.Items, Is.EqualTo(new JsonValue[]
        {
            new JsonNumberValue(1.0),
            new JsonStringValue("two"),
            new JsonBoolValue(true),
            JsonNullValue.Instance
        }));
    }

    [Test]
    public void Decodes_string_escapes()
    {
        Assert.That(JsonParserTyped.Project("\"say \\\"hi\\\"\""),
            Is.EqualTo(new JsonStringValue("say \"hi\"")));
        Assert.That(JsonParserTyped.Project("\"\\u00e9\""),
            Is.EqualTo(new JsonStringValue(UnicodeExamples.LatinEAcutePrecomposedGrapheme)));
    }

    [Test]
    public void Rejects_malformed_input()
    {
        Assert.Throws<FormatException>(() => JsonParserTyped.Project("{\"missing\":"));
        Assert.Throws<FormatException>(() => JsonParserTyped.Project("truthy"));
    }

    [Test]
    public void Supports_pattern_matching_dispatch()
    {
        // Real-world use case: exhaustive pattern match over JsonValue
        // instead of casting an object? / dictionary lookup chain. The
        // sealed-subtype hierarchy lets the C# compiler tell you when
        // a switch is missing a case.
        static string DescribeFirst(JsonValue value) => value switch
        {
            JsonNullValue => "null",
            JsonBoolValue b => "bool:" + b.Value,
            JsonNumberValue n => "number:" + n.Value,
            JsonStringValue s => "string:" + s.Value,
            JsonArrayValue a when a.Items.Count > 0 => "array first=" + DescribeFirst(a.Items[0]),
            JsonArrayValue => "array empty",
            JsonObjectValue o when o.Members.Count > 0 => "object has " + o.Members.Count,
            JsonObjectValue => "object empty",
            _ => "unknown"
        };

        Assert.That(DescribeFirst(JsonParserTyped.Project("null")), Is.EqualTo("null"));
        Assert.That(DescribeFirst(JsonParserTyped.Project("42")), Is.EqualTo("number:42"));
        Assert.That(DescribeFirst(JsonParserTyped.Project("[\"a\", \"b\"]")), Is.EqualTo("array first=string:a"));
        Assert.That(DescribeFirst(JsonParserTyped.Project("{\"x\":1}")), Is.EqualTo("object has 1"));
    }

    [Test]
    public void Supports_Newtonsoft_style_ergonomic_access()
    {
        // With the explicit conversion operators and virtual indexers
        // on JsonValue, reading fields collapses from the verbose
        // ((JsonStringValue)root.Members["name"]).Value form to the
        // same shape Newtonsoft exposes on JToken.
        var root = JsonParserTyped.Project(
            "{\"name\":\"Alice\",\"age\":30,\"active\":true,\"email\":null," +
            "\"roles\":[\"admin\",\"user\"],\"address\":{\"city\":\"Seattle\"}}");

        Assert.That((string?)root["name"], Is.EqualTo("Alice"));
        Assert.That((double)root["age"], Is.EqualTo(30.0));
        Assert.That((bool)root["active"], Is.True);
        Assert.That((string?)root["email"], Is.Null);
        Assert.That((string?)root["roles"][0], Is.EqualTo("admin"));
        Assert.That((string?)root["address"]["city"], Is.EqualTo("Seattle"));
    }

    [Test]
    public void Ergonomic_cast_throws_on_type_mismatch()
    {
        var root = JsonParserTyped.Project("{\"age\":30}");

        // Asking for a string when the value is a number fails loudly.
        Assert.Throws<InvalidCastException>(() => { var _ = (string?)root["age"]; });
        // Indexing a non-object by string surfaces through the indexer
        // on the base JsonValue.
        Assert.Throws<InvalidOperationException>(() => { var _ = root["age"]["nope"]; });
    }
}
