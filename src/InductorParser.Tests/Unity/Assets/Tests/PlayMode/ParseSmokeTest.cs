using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.PlayModeTests
{
    // Minimal smoke test that verifies the library loads and parses under
    // IL2CPP. This runs against the netstandard2.1 DLL built from
    // src/InductorParser and copied into Assets/Plugins/. It's the "does
    // the whole thing work under IL2CPP at all" tripwire, not a
    // comprehensive test pass. Comprehensive coverage is in
    // src/InductorParser.Tests (net8.0 CoreCLR).
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
            var settingName = OneOrMore(OneOf(RuneSet.Letters));
            var settingValue = FirstOf(
                Float().Flatten(FlattenType.Flatten),
                Integer().Flatten(FlattenType.Flatten),
                OneOrMore(OneOf(RuneSet.Letters))
            ).Flatten(FlattenType.Preserve);
            var document = AllOf(
                settingName,
                OptionalWhitespace(),
                Token('='),
                OptionalWhitespace(),
                settingValue,
                OptionalWhitespace(),
                Token(';')
            );

            var result = document.Parse("setting = 5;");

            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Tree!.Find(settingName)!.ToString(), Is.EqualTo("setting"));
            Assert.That(result.Tree!.Find(settingValue)!.ToString(), Is.EqualTo("5"));
        }

        [Test]
        public void StringChars_parses_under_il2cpp()
        {
            // Tripwire for the StringBody primitive under IL2CPP.
            // Exercises the inline rune-decode helper
            // (Lexer.TryPeekRune), the RuneSet-stopper fast path,
            // and the single-rune escape start plus escape-end
            // dispatch. Grammar mirrors a minimal JSON string body.
            var escapeEnd = OneOf(RuneSet.Runes("\"\\/bfnrt"));
            var body = StringBody(RuneSet.Runes("\""), new Rune('\\'), escapeEnd);
            var rule = AllOf(Token('"'), body, Token('"'));

            var result = rule.Parse("\"hello\\n\"");

            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Tree!.ToString(), Is.EqualTo("hello\\n"));
        }

        [Test]
        public void Reports_failure_position_under_il2cpp()
        {
            var rule = AllOf(
                OneOrMore(OneOf(RuneSet.Letters)),
                Token(';').WithError("expected ';'")
            );

            var result = rule.Parse("abc1");

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
            Assert.That(result.ErrorMessage, Is.EqualTo("expected ';'"));
        }
    }
}
