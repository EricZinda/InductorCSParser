using System.Linq;
using NUnit.Framework;
using InductorParser.E2ESamples.Toml.Rewrite;
using Tomlyn;

namespace InductorParser.E2ESamples.Toml.Tests;

// Unicode-handling tests covering three rules in TOML 1.0:
//
//   1. Literal control characters are rejected in basic and literal
//      strings. The grammar's basic-unescaped / literal-char TokenSets
//      carve out tab, LF, CR and exclude the rest of U+0000..U+001F
//      and U+007F.
//   2. \uXXXX and \UXXXXXXXX escapes must resolve to a valid Unicode
//      scalar value. The grammar accepts any 4 or 8 hex digits; the
//      decoder rejects surrogates (U+D800..U+DFFF) and values above
//      U+10FFFF.
//   3. Keys are normalized to NFC by the scanner before the consumer
//      sees them. Same-looking keys in NFC and NFD form therefore
//      collide as duplicates, which diverges from Tomlyn.
//
// Non-ASCII code points are constructed from numeric values
// (char.ConvertFromUtf32) rather than written as glyphs in the source,
// so the source bytes are unambiguous regardless of editor or compiler
// normalization.
[TestFixture]
public class UnicodeTests
{
    // ---------------------------------------------------------
    // Angle 1: control characters in strings (grammar level)
    // ---------------------------------------------------------

    [TestCase((char)0x00, TestName = "BasicString_RawNul_Rejected")]
    [TestCase((char)0x01, TestName = "BasicString_RawSoh_Rejected")]
    [TestCase((char)0x08, TestName = "BasicString_RawBackspace_Rejected")]
    [TestCase((char)0x0B, TestName = "BasicString_RawVerticalTab_Rejected")]
    [TestCase((char)0x0C, TestName = "BasicString_RawFormFeed_Rejected")]
    [TestCase((char)0x1F, TestName = "BasicString_RawUnitSeparator_Rejected")]
    [TestCase((char)0x7F, TestName = "BasicString_RawDelete_Rejected")]
    public void BasicString_RawControlChar_Rejected(char controlChar)
    {
        var input = $"key = \"a{controlChar}b\"\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.False,
            $"U+{(int)controlChar:X4} should not be allowed raw in a basic string body.");
    }

    [Test]
    public void BasicString_RawTab_Allowed()
    {
        // Tab is the one control character TOML's basic-unescaped class
        // explicitly lets through. Same carve-out applies in literal
        // strings.
        var input = "key = \"a\tb\"\n";
        var root = TomlParser.Parse(input);
        Assert.That((string)root["key"], Is.EqualTo("a\tb"));
    }

    [TestCase((char)0x00, TestName = "LiteralString_RawNul_Rejected")]
    [TestCase((char)0x1F, TestName = "LiteralString_RawUnitSeparator_Rejected")]
    [TestCase((char)0x7F, TestName = "LiteralString_RawDelete_Rejected")]
    public void LiteralString_RawControlChar_Rejected(char controlChar)
    {
        var input = $"key = 'a{controlChar}b'\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.False,
            $"U+{(int)controlChar:X4} should not be allowed raw in a literal string body.");
    }

    // ---------------------------------------------------------
    // Angle 2: \u and \U surrogate / out-of-range rejection
    // ---------------------------------------------------------

    [Test]
    public void ShortEscape_HighSurrogate_Rejected()
    {
        var input = "key = \"\\uD800\"\n";
        var ex = Assert.Throws<TomlParseException>(() => TomlParser.Parse(input));
        Assert.That(ex!.Message, Does.Contain("surrogate"));
        Assert.That(ex.Message, Does.Contain("D800"));
    }

    [Test]
    public void ShortEscape_LowSurrogate_Rejected()
    {
        var input = "key = \"\\uDFFF\"\n";
        var ex = Assert.Throws<TomlParseException>(() => TomlParser.Parse(input));
        Assert.That(ex!.Message, Does.Contain("surrogate"));
    }

    [Test]
    public void ShortEscape_BoundaryBelowSurrogateRange_Allowed()
    {
        // U+D7FF is the last code point before the surrogate range, so
        // the escape has to round-trip cleanly.
        var input = "key = \"\\uD7FF\"\n";
        var root = TomlParser.Parse(input);
        Assert.That((string)root["key"], Is.EqualTo(((char)0xD7FF).ToString()));
    }

    [Test]
    public void ShortEscape_BoundaryAboveSurrogateRange_Allowed()
    {
        // U+E000 is the first code point in the Private Use Area, just
        // past the surrogate range.
        var input = "key = \"\\uE000\"\n";
        var root = TomlParser.Parse(input);
        Assert.That((string)root["key"], Is.EqualTo(((char)0xE000).ToString()));
    }

    [Test]
    public void LongEscape_SurrogateInLowEightDigits_Rejected()
    {
        // \U accepts 8 hex digits, but a value in the surrogate range
        // dressed up as \U0000DXXX is still invalid.
        var input = "key = \"\\U0000D800\"\n";
        var ex = Assert.Throws<TomlParseException>(() => TomlParser.Parse(input));
        Assert.That(ex!.Message, Does.Contain("surrogate"));
    }

    [Test]
    public void LongEscape_OneAboveMaxScalar_Rejected()
    {
        var input = "key = \"\\U00110000\"\n";
        var ex = Assert.Throws<TomlParseException>(() => TomlParser.Parse(input));
        Assert.That(ex!.Message, Does.Contain("10FFFF"));
    }

    [Test]
    public void LongEscape_HighBitSet_Rejected_NotSilentlyTreatedAsNegative()
    {
        // A signed 8-hex-digit parse with the high bit set turns into
        // a negative int, which would slip past a > 0x10FFFF range
        // check. The decoder uses uint.Parse so the range check sees
        // the real magnitude.
        var input = "key = \"\\U80000000\"\n";
        var ex = Assert.Throws<TomlParseException>(() => TomlParser.Parse(input));
        Assert.That(ex!.Message, Does.Contain("10FFFF"));
    }

    [Test]
    public void LongEscape_MaxScalar_Allowed()
    {
        // U+10FFFF is the highest legal scalar: the upper bound of the
        // four-byte UTF-8 plane. Worth a positive test so the off-by-one
        // boundary stays correct.
        var input = "key = \"\\U0010FFFF\"\n";
        var root = TomlParser.Parse(input);
        Assert.That((string)root["key"], Is.EqualTo(char.ConvertFromUtf32(0x10FFFF)));
    }

    [Test]
    public void LongEscape_AstralCharacter_RoundTrips()
    {
        // A real four-byte UTF-8 character: a smiley face from the
        // emoticons block. Confirms the "valid scalar above the BMP"
        // path produces a proper surrogate pair in the output.
        var input = "smile = \"\\U0001F600\"\n";
        var root = TomlParser.Parse(input);
        var decoded = (string)root["smile"];
        Assert.That(decoded.Length, Is.EqualTo(2),
            "U+1F600 lives outside the BMP, so .NET stores it as a UTF-16 surrogate pair.");
        Assert.That(char.IsHighSurrogate(decoded[0]), Is.True);
        Assert.That(char.IsLowSurrogate(decoded[1]), Is.True);
        Assert.That(char.ConvertToUtf32(decoded, 0), Is.EqualTo(0x1F600));
    }

    // ---------------------------------------------------------
    // Line endings: only LF and CRLF, per ABNF "newline = LF / CRLF"
    // ---------------------------------------------------------

    [Test]
    public void BareCr_BetweenLines_IsRejected()
    {
        // TOML's ABNF allows only LF and CRLF as line terminators. A
        // bare CR between key/value pairs has to fail.
        var input = "a = 1\rb = 2\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void BareCr_AtEndOfComment_IsRejected()
    {
        // Same reasoning for a CR ending a comment.
        var input = "# comment\ra = 1\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void BareCr_InsideMultilineBasicString_IsRejected()
    {
        // Bare CR inside an ml-basic-string body would otherwise sneak
        // through the body's newline alternative.
        var input = "s = \"\"\"line1\rline2\"\"\"\n";
        var result = TomlGrammar.TomlDocument.Parse(input);
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Crlf_BetweenLines_IsAccepted()
    {
        // Positive control: CRLF is a legal TOML line terminator and
        // should round-trip.
        var input = "a = 1\r\nb = 2\r\n";
        var root = TomlParser.Parse(input);
        Assert.That((long)root["a"], Is.EqualTo(1));
        Assert.That((long)root["b"], Is.EqualTo(2));
    }

    // ---------------------------------------------------------
    // Angle 3: key normalization is not implicit
    // ---------------------------------------------------------

    [Test]
    public void NfdInput_IsNormalizedToNfc_BeforeConsumerSeesIt()
    {
        // The scanner normalizes input to NFC per grapheme, so a key
        // written as "a" + U+0300 (combining grave) arrives at the
        // consumer as U+00E0. The two forms produce the same key.
        var nfc = char.ConvertFromUtf32(0x00E0);
        var nfd = "a" + char.ConvertFromUtf32(0x0300);
        var inputNfc = $"\"{nfc}\" = 1\n";
        var inputNfd = $"\"{nfd}\" = 2\n";

        var rootNfc = TomlParser.Parse(inputNfc);
        var rootNfd = TomlParser.Parse(inputNfd);

        Assert.That(rootNfc.OrderedKeys.Single(), Is.EqualTo(nfc));
        Assert.That(rootNfd.OrderedKeys.Single(), Is.EqualTo(nfc));

        // A document with both forms in succession is therefore a
        // duplicate-key document.
        var bothForms = inputNfc + inputNfd;
        var ex = Assert.Throws<TomlParseException>(() => TomlParser.Parse(bothForms));
        Assert.That(ex!.Message, Does.Contain("Duplicate key"));
    }

    [Test]
    public void NfdInput_DivergesFromTomlyn_OnSameDocument()
    {
        // Tomlyn keeps NFC and NFD as separate keys. The
        // InductorParser-based parser collapses them.
        var nfc = char.ConvertFromUtf32(0x00E0);
        var nfd = "a" + char.ConvertFromUtf32(0x0300);
        var input = $"\"{nfc}\" = 1\n\"{nfd}\" = 2\n";

        Assert.Throws<TomlParseException>(() => TomlParser.Parse(input));

        var theirs = Tomlyn.Toml.ToModel(input);
        Assert.That(theirs.Count, Is.EqualTo(2));
    }

    [Test]
    public void HiddenZeroWidthSpace_InQuotedKey_ProducesDistinctKey()
    {
        // The Pinheiro example reduced to TOML: a quoted key with an
        // invisible Zero Width Space (U+200B) embedded in it parses,
        // and the resulting key is not the same as the visually
        // identical one without the ZWSP.
        var hidden = "a" + char.ConvertFromUtf32(0x200B) + "b";
        var visible = "ab";
        var input = $"\"{hidden}\" = 1\n\"{visible}\" = 2\n";

        var root = TomlParser.Parse(input);

        Assert.That(root.OrderedKeys.Count, Is.EqualTo(2));
        Assert.That(root.OrderedKeys.Contains(hidden), Is.True);
        Assert.That(root.OrderedKeys.Contains(visible), Is.True);
    }
}
