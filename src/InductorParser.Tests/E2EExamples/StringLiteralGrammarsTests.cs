using NUnit.Framework;
using InductorParser;

namespace InductorParser.Tests;

// End-to-end tests for StringLiteralGrammars. Each inner class pins
// the positive and negative behavior of one grammar. For successful
// parses the test asserts that Tree.ToString() equals the expected
// body text. The quote delimiters are FlattenType.Delete (TokenRule's
// default) so they drop out of the tree and the body is what's left.
[TestFixture]
public class StringLiteralGrammarsTests
{
    // ============================================================
    // JSON (RFC 8259)
    // ============================================================
    [TestFixture]
    public class JsonTests
    {
        private static Rule Rule => StringLiteralGrammars.Json;

        [TestCase("\"\"",                      "",                  TestName = "empty body")]
        [TestCase("\"hello\"",                 "hello",             TestName = "plain ASCII body")]
        [TestCase("\"café\"",                  "café",              TestName = "non-ASCII body (U+00E9)")]
        [TestCase("\"hi 🎸\"",                  "hi 🎸",              TestName = "supplementary-plane rune in body")]
        [TestCase("\"a\\nb\"",                 "a\\nb",             TestName = "one simple escape (\\n)")]
        [TestCase("\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"", "\\\"\\\\\\/\\b\\f\\n\\r\\t", TestName = "all eight simple escapes")]
        [TestCase("\"\\u00E9\"",               "\\u00E9",           TestName = "unicode escape")]
        [TestCase("\"\\n\\t\\r\"",             "\\n\\t\\r",         TestName = "back-to-back simple escapes")]
        [TestCase("\"\\u0041\\u0042\"",        "\\u0041\\u0042",    TestName = "back-to-back unicode escapes")]
        [TestCase("\"ends with escape\\n\"",   "ends with escape\\n", TestName = "escape at end of body")]
        [TestCase("\"\\nstarts with escape\"", "\\nstarts with escape", TestName = "escape at start of body")]
        public void Accepts(string input, string expectedBody)
        {
            var result = Rule.Parse(input);
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Tree!.ToString(), Is.EqualTo(expectedBody));
        }

        [TestCase("",              TestName = "empty input")]
        [TestCase("\"",            TestName = "opening quote only")]
        [TestCase("\"abc",         TestName = "unterminated body")]
        [TestCase("abc\"",         TestName = "missing opening quote")]
        [TestCase("\"\\q\"",       TestName = "unknown simple escape (\\q)")]
        [TestCase("\"\\u123\"",    TestName = "truncated unicode escape (3 hex digits)")]
        [TestCase("\"\\u123Z\"",   TestName = "non-hex char in unicode escape")]
        [TestCase("\"\\\"",        TestName = "backslash-quote then EOF (body ends on escape)")]
        [TestCase("\"a\tb\"",      TestName = "raw tab in body (U+0009, control char)")]
        [TestCase("\"a\nb\"",      TestName = "raw newline in body (U+000A, control char)")]
        [TestCase("\"a\0b\"",      TestName = "raw NUL in body (U+0000, control char)")]
        public void Rejects(string input)
        {
            var result = Rule.Parse(input);
            Assert.That(result.Success, Is.False);
        }
    }

    // ============================================================
    // Python single-line double-quote
    // ============================================================
    [TestFixture]
    public class PythonSingleLineTests
    {
        private static Rule Rule => StringLiteralGrammars.PythonSingleLine;

        [TestCase("\"\"",                "",                TestName = "empty body")]
        [TestCase("\"hello\"",           "hello",           TestName = "plain body")]
        [TestCase("\"it's fine\"",       "it's fine",       TestName = "single quote inside double-quote string")]
        [TestCase("\"a\\nb\"",           "a\\nb",           TestName = "simple \\n escape")]
        [TestCase("\"\\t\\r\\v\\a\\b\\f\"", "\\t\\r\\v\\a\\b\\f", TestName = "all simple C escapes")]
        [TestCase("\"\\0\"",             "\\0",             TestName = "single-digit octal (\\0)")]
        [TestCase("\"\\77\"",            "\\77",            TestName = "two-digit octal")]
        [TestCase("\"\\123\"",           "\\123",           TestName = "three-digit octal (greedy stop at non-octal)")]
        [TestCase("\"\\08\"",            "\\08",            TestName = "octal then literal digit (8 not octal)")]
        [TestCase("\"\\x7F\"",           "\\x7F",           TestName = "hex escape \\xNN")]
        [TestCase("\"\\u00e9\"",         "\\u00e9",         TestName = "4-hex unicode \\uNNNN (lowercase)")]
        [TestCase("\"\\U0001F3B8\"",     "\\U0001F3B8",     TestName = "8-hex unicode \\UNNNNNNNN")]
        [TestCase("\"\\N{LATIN SMALL LETTER E}\"", "\\N{LATIN SMALL LETTER E}", TestName = "named escape \\N{name}")]
        [TestCase("\"café\"",            "café",            TestName = "unicode content in body")]
        public void Accepts(string input, string expectedBody)
        {
            var result = Rule.Parse(input);
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Tree!.ToString(), Is.EqualTo(expectedBody));
        }

        [TestCase("\"abc\nrest\"",     TestName = "raw newline forbidden in single-line string")]
        [TestCase("\"\\x\"",           TestName = "hex escape needs 2 digits, has zero")]
        [TestCase("\"\\x1\"",          TestName = "hex escape needs 2 digits, has one")]
        [TestCase("\"\\u12\"",         TestName = "\\u needs 4 digits")]
        [TestCase("\"\\U0001F3\"",     TestName = "\\U needs 8 digits")]
        [TestCase("\"\\N{unterminated", TestName = "\\N with no closing brace")]
        [TestCase("\"unterminated",    TestName = "no closing quote")]
        public void Rejects(string input)
        {
            var result = Rule.Parse(input);
            Assert.That(result.Success, Is.False);
        }
    }

    // ============================================================
    // Python triple-quote
    // ============================================================
    [TestFixture]
    public class PythonTripleQuoteTests
    {
        private static Rule Rule => StringLiteralGrammars.PythonTripleQuote;

        [TestCase("\"\"\"\"\"\"",                     "",                 TestName = "empty body (three open + three close)")]
        [TestCase("\"\"\"hello\"\"\"",                "hello",            TestName = "plain ASCII body")]
        [TestCase("\"\"\"line1\nline2\"\"\"",         "line1\nline2",     TestName = "raw newline allowed in body")]
        [TestCase("\"\"\"a\"b\"\"\"",                 "a\"b",             TestName = "single \" in body")]
        [TestCase("\"\"\"a\"\"b\"\"\"",               "a\"\"b",           TestName = "two \"\" in body (still not the terminator)")]
        [TestCase("\"\"\"escape \\n here\"\"\"",      "escape \\n here",  TestName = "escape processing still active")]
        [TestCase("\"\"\"line1\nline2\nline3\"\"\"",  "line1\nline2\nline3", TestName = "multi-line body")]
        [TestCase("\"\"\"🎸 and \\u00e9\"\"\"",       "🎸 and \\u00e9",   TestName = "unicode and escape in body")]
        public void Accepts(string input, string expectedBody)
        {
            var result = Rule.Parse(input);
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Tree!.ToString(), Is.EqualTo(expectedBody));
        }

        [TestCase("\"\"\"unterminated",    TestName = "no closing triple-quote")]
        [TestCase("\"\"\"almost\"\"",      TestName = "only two closing quotes")]
        [TestCase("\"\"\"bad \\z escape\"\"\"", TestName = "unknown escape inside triple-quote body")]
        public void Rejects(string input)
        {
            var result = Rule.Parse(input);
            Assert.That(result.Success, Is.False);
        }

        [Test]
        public void Terminator_is_earliest_triple_quote_not_greedy()
        {
            // "abc""" is the whole literal. Any remaining text is
            // outside. Parsing as the literal ALONE should fail
            // because there's trailing content past the first """.
            var result = Rule.Parse("\"\"\"abc\"\"\"trailing");
            Assert.That(result.Success, Is.False);
        }
    }

    // ============================================================
    // Python raw single-line
    // ============================================================
    [TestFixture]
    public class PythonRawSingleLineTests
    {
        private static Rule Rule => StringLiteralGrammars.PythonRawSingleLine;

        [TestCase("r\"\"",          "",         TestName = "empty raw body")]
        [TestCase("r\"hello\"",     "hello",    TestName = "plain raw body")]
        [TestCase("r\"\\n\"",       "\\n",      TestName = "backslash-n is two literal chars")]
        [TestCase("r\"\\\\\"",      "\\\\",     TestName = "two backslashes are two literal chars")]
        [TestCase("r\"a\\qb\"",     "a\\qb",    TestName = "no escape processing (\\q is literal)")]
        [TestCase("r\"café\"",      "café",     TestName = "unicode content")]
        [TestCase("r\"🎸\"",        "🎸",       TestName = "supplementary-plane rune")]
        public void Accepts(string input, string expectedBody)
        {
            var result = Rule.Parse(input);
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Tree!.ToString(), Is.EqualTo(expectedBody));
        }

        [TestCase("",                TestName = "empty input")]
        [TestCase("\"abc\"",         TestName = "missing r prefix")]
        [TestCase("r\"unterminated", TestName = "no closing quote")]
        [TestCase("r\"",             TestName = "r and opening quote only")]
        public void Rejects(string input)
        {
            var result = Rule.Parse(input);
            Assert.That(result.Success, Is.False);
        }
    }
}
