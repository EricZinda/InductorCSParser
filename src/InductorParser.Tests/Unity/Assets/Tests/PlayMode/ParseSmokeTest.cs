using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.PlayModeTests
{
    // Minimal smoke test that verifies the library loads and parses under
    // IL2CPP. This runs against the netstandard2.1 DLL built from
    // src/InductorParser and copied into Assets/Plugins/. It's the "does
    // the whole thing work under IL2CPP at all" tripwire, not a
    // comprehensive test pass. Comprehensive coverage is in
    // src/InductorParser.Tests (.NET 8 and .NET 10 CoreCLR).
    //
    // The grammar mirrors E2EExamples/SettingExampleTests.cs because it
    // exercises the pieces most likely to trip IL2CPP: generics in the
    // rule tree, the Rune polyfill on netstandard2.1, Span-based lexing,
    // and the interpolated-string-handler trace hooks.
    [TestFixture]
    public class ParseSmokeTest
    {
        [Test]
        public void Parses_integer_setting_under_il2cpp()
        {
            // Preserve on the name, value, and document so the parse
            // tree exists for the Find calls below, matching the
            // grammar shape in E2EExamples/SettingExampleTests.cs.
            var settingName = Identifier();
            var settingValue = Or(
                Float(),
                Integer(),
                OneOrMore(OneOf(TokenSet.Letters))
            ).Flatten(FlattenType.Preserve);
            var document = And(
                settingName,
                Optional(AnyWhitespace()),
                Token('='),
                Optional(AnyWhitespace()),
                settingValue,
                Optional(AnyWhitespace()),
                Token(';')
            ).Flatten(FlattenType.Preserve);

            var result = document.Parse("setting = 5;");

            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Tree!.Find(settingName)!.ToString(), Is.EqualTo("setting"));
            Assert.That(result.Tree!.Find(settingValue)!.ToString(), Is.EqualTo("5"));
        }

        [Test]
        public void StringChars_parses_under_il2cpp()
        {
            // Tripwire for the ScanUntil primitive under IL2CPP.
            // Exercises the inline rune-decode helper
            // (Lexer.TryPeekRune), the TokenSet-stopper fast path,
            // and the single-rune escape start plus escape-end
            // dispatch. Grammar mirrors a minimal JSON string body.
            var escapeEnd = OneOf(TokenSet.Runes("\"\\/bfnrt"));
            var body = ScanUntil(TokenSet.Runes("\""), new Rune('\\'), escapeEnd);
            var rule = And(Token('"'), body, Token('"'));

            var result = rule.Parse("\"hello\\n\"");

            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Tree!.ToString(), Is.EqualTo("hello\\n"));
        }

        [Test]
        public void Bundled_unicode_implementations_are_active_under_il2cpp()
        {
            // In the assembly Unity loads, UnicodeImplementation.Automatic
            // must mean the built-in segmenter and normalizer, because
            // Unity's runtimes ship a legacy StringInfo and a
            // string.Normalize that misses mappings. Each behavior
            // assertion is its own tripwire: the legacy StringInfo
            // returns 1 for CRLF, and Mono leaves the fi ligature
            // (U+FB01) unexpanded under FormKC, so if Automatic wrongly
            // picked Runtime here, those lines fail before the enum
            // check below. The ligature is built from its code point so
            // no tooling can renormalize the source literal.
            Assert.That(GraphemeHelpers.FirstClusterLength("\r\nx".AsSpan()), Is.EqualTo(2));
            string ligature = ((char)0xFB01).ToString();
            Assert.That(
                NormalizationHelpers.Normalize(ligature, NormalizationForm.FormKC),
                Is.EqualTo("fi"));
            Assert.That(UnicodeEnvironment.ActiveImplementation,
                Is.EqualTo(UnicodeImplementation.Bundled));
        }

        [Test]
        public void Reports_failure_position_under_il2cpp()
        {
            var rule = And(
                OneOrMore(OneOf(TokenSet.Letters)),
                Token(';').WithError("expected ';'")
            );

            var result = rule.Parse("abc1");

            // The default template appends the position to a WithError
            // message, so the full rendered form is asserted here.
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
            Assert.That(result.ErrorMessage, Is.EqualTo("expected ';' at line 1, column 4."));
        }
    }
}
