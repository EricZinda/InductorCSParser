using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.StateMachine;
using InductorParser.Tracing;

namespace InductorParser.Tests.StateMachine;

// Direct-call tests for the scanner-skip extension methods that live
// next to the StateMachine engine in
// ExperimentalSrc/InductorParser/StateMachine/LexerScanningExtensions.cs.
// These exercise the extensions against a freshly-constructed Lexer
// rather than going through a Parse call, which lets the test pin
// behaviors that would otherwise be invisible at the rule level.
[TestFixture]
public class LexerScanningExtensionsTests
{
    [Test]
    public void Scanner_skip_in_one_rune_sublexer_does_not_skip_mid_cluster_runes()
    {
        // The lexer normally reads one grapheme per token
        // (a character can be several code points: an accented letter, an
        // emoji). WithinToken switches to a mode that reads one code point
        // per token, so an inner rule can look inside a character.
        //
        // The scanner-skip speed trick (Lexer.AdvanceUntilRuneIn and
        // friends) jumps ahead with a fast text search, then asks
        // IsAtMidToken "did I land on a real token start?" before stopping.
        // IsAtMidToken answers by the active token unit: in the one-code-
        // point mode a combining mark is its own token, and
        // only the trailing half of a surrogate pair counts as token
        // interior. So the fast search has to stop on the combining mark,
        // the same place the slow per-token scan stops. This test holds the
        // two scans to that agreement.
        //
        // Fixture: 'e' + combining accent (U+0065 then U+0301) is one
        // character made of two code points. Reading one code point at a
        // time, a scan for the accent has to stop on it (offset 1), not run
        // off the end (offset 2). Built from explicit code points so the
        // source file stays ASCII-only and the encoding can't drift between
        // editors. 0x65 = 'e', 0x301 = combining acute.
        string subInput = new string(new[] { (char)0x0065, (char)0x0301 });
        var set = TokenSet.Single(0x0301);
        Assert.That(set.TryGetBmpChars(1, out var bmpCandidates), Is.True,
            "U+0301 is a single BMP char, so the vectorized fast path applies.");

        var fastPath = new Lexer(subInput, startPosition: 0, endPosition: subInput.Length,
            traceSink: null, traceLevel: TraceLevel.Normal, oneRunePerToken: true);
        fastPath.AdvanceUntilRuneIn(set, bmpCandidates);

        var slowPath = new Lexer(subInput, startPosition: 0, endPosition: subInput.Length,
            traceSink: null, traceLevel: TraceLevel.Normal, oneRunePerToken: true);
        slowPath.AdvanceUntilRuneIn(set, bmpCandidates: null);

        Assert.That(fastPath.Position, Is.EqualTo(1),
            "Vectorized fast path must stop on the combining mark, the one-rune token at offset 1.");
        Assert.That(fastPath.Position, Is.EqualTo(slowPath.Position),
            "The IndexOfAny fast path and the per-token slow path must agree in one-rune mode.");
    }
}
