using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TraceTestHelpers;

namespace InductorParser.Tests;

// Do .As(string) and .WithError(string) carry non-ASCII text through
// every path they touch? The other Unicode fixtures cover literals,
// TokenSets, and the lexer; this one covers the two fluent modifiers
// whose argument is a free-form string the grammar author types.
//
// .As(name) is used in three places: the FNV-1a name hash that assigns
// the rule its SymbolId, the per-grammar name <-> id indexes behind
// NameOf / IdOf / Symbol.Is / Symbol.DisplayName, and the trace label.
// .WithError stores a message that surfaces through
// ParseResult.ErrorMessage and gets appended to failure trace lines.
// None of these decode the string
// into Unicode scalars: the hash walks UTF-16 code units, the indexes
// compare ordinally, the message is opaque. So none should care what the
// string holds, but "shouldn't" isn't "doesn't".
//
// Most tests here take a single string and assert something uniform
// about it: it round-trips through NameOf / IdOf, two rules sharing it
// fail to compile, the set-once exception quotes it, and so on. Those
// run the whole UnicodeExamples corpus through a [TestCaseSource].
// UnicodeExamples is the project's shared set of named Unicode constants
// (ordinary BMP text, supplementary-plane runes, multi-rune graphemes,
// lone surrogates, combining marks, joiners, BOM, bidi controls,
// noncharacters, Private Use, replacement, null, ...), and its
// AllStringConstants reflects over itself, so a constant added there for
// any other fixture automatically extends the coverage here too.
//
// UnexpectedUnicodeTests can't share this matrix shape: it probes parse
// behavior, where each category has a different expected outcome (a lone
// surrogate throws under FormC, a combining mark doesn't), so its tests
// are hand-written per category. .As / .WithError treat every string
// identically, so one uniform assertion covers them all.
//
// The rest stay discrete because the catalog adds nothing to them. The
// hash-determinism test needs a hardcoded id per name. The distinctness
// tests need a specific pair of genuinely different strings (two unequal
// names, or a precomposed/decomposed pair) that one catalog entry can't
// supply. The error-resolution tests turn on failure depth and the
// forced flag: resolution compares positions and a bool and never reads
// the message text, so every catalog string behaves identically there,
// and WithError_carries already covers a message surviving verbatim. The
// trace tests verbatim-lock a whole trace format around the string. The
// end-to-end test gives one grammar several string roles at once.
//
// Catalog entries are carried as int[] UTF-16 code units and rebuilt
// into a string inside the test. NUnit puts test-case arguments into the
// generated test name, and a lone surrogate in a test name is invalid
// XML for the result file; an int[] sidesteps that. The constant's field
// name is a separate, ASCII-only argument used as the readable label.
[TestFixture]
public class UnicodeAsAndWithErrorTests
{
    // Unicode strings used by the discrete behavior tests below. Built
    // from canary-protected UnicodeExamples constants so an editor
    // silently rewriting a literal trips Canary instead of quietly
    // weakening the test.
    private static readonly string GreekName = UnicodeExamples.GreekKalimeraIdentifier;        // BMP, all single-rune letters
    private static readonly string DevanagariName = UnicodeExamples.DevanagariHindiIdentifier; // BMP, multi-rune graphemes
    private static readonly string EmojiName = UnicodeExamples.GuitarGrapheme;                 // supplementary plane (surrogate pair)

    // The whole UnicodeExamples corpus turned into test cases: the field
    // name is the readable label, the value is carried as int[] code
    // units (see CodeUnits). UnicodeExamples.AllStringConstants does the
    // reflection, so a Unicode example added to that file for any reason
    // automatically becomes an .As name and a .WithError message case.
    private static IEnumerable<TestCaseData> UnicodeStringCatalog() =>
        UnicodeExamples.AllStringConstants()
            .Select(constant => new TestCaseData(constant.Name, CodeUnits(constant.Value)));

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void As_carries_a_unicode_name_through_every_lookup(string label, int[] codeUnits)
    {
        // One catalog entry, used as a whole rule name, probed through
        // all four name paths: the NameOf / IdOf reverse-and-forward
        // indexes built at Compile, and the Symbol.DisplayName / Symbol.Is
        // lookups a tree walker uses after a parse. .As never normalizes a
        // name and the hash walks code units, so even an ill-formed name
        // round-trips byte-for-byte.
        string name = BuildString(codeUnits);
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As(name);
        rule.Compile();

        Assert.That(rule.NameOf(rule.Id), Is.EqualTo(name), $"NameOf [{label}]");
        Assert.That(rule.IdOf(name)!.Value, Is.EqualTo(rule.Id), $"IdOf [{label}]");

        var result = rule.Parse("hello");
        Assert.That(result.Success, Is.True, $"parse [{label}]");
        Assert.That(result.Tree!.DisplayName, Is.EqualTo(name), $"Symbol.DisplayName [{label}]");
        Assert.That(result.Tree!.Is(name), Is.True, $"Symbol.Is [{label}]");
    }

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void WithError_carries_a_unicode_message_to_ParseResult(string label, int[] codeUnits)
    {
        // The same catalog, used as a whole error message. The message is
        // an opaque string the parser hands to ParseResult.ErrorMessage,
        // never normalized or decoded, so every category surfaces verbatim
        // when the rule it is set on becomes the deepest failure.
        string message = BuildString(codeUnits);
        var rule = OneOrMore(OneOf(TokenSet.Letters)).WithError(message);

        var result = rule.Parse("123");

        Assert.That(result.Success, Is.False, $"parse fails [{label}]");
        Assert.That(result.ErrorMessage, Is.EqualTo(message), $"ErrorMessage [{label}]");
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0), $"ErrorCharIndex [{label}]");
    }

    [Test]
    public void Unicode_name_ids_are_stable_across_processes()
    {
        // A named rule's SymbolId is the FNV-1a hash of its name mapped
        // into the custom range. The hash feeds the low byte then the high
        // byte of every UTF-16 code unit, so a supplementary-plane name (a
        // surrogate pair) contributes four bytes and a BMP letter two.
        // These ids get persisted in serialized parse trees and match
        // traces, so the hash has to stay byte-for-byte stable.
        //
        // The expected values are hardcoded rather than recomputed via
        // HashNameToCustomRange: recomputing would pass even if the hash
        // silently changed, because both sides would move together. A
        // hardcoded number fails loudly and forces a conscious decision
        // about breaking persisted ids, for instance if someone reworked
        // the per-char loop into a per-rune one, which would shift every
        // supplementary-plane name's id.
        //
        // This test doesn't run the UnicodeStringCatalog like the
        // round-trip tests above. Those assert one uniform property, so
        // every catalog entry takes the same assertion. This asserts a
        // different specific id per name, which would need a hand-kept
        // table of one precomputed hash per catalog entry, and that would
        // break the catalog's whole point: a constant added to
        // UnicodeExamples would then fail this test until someone computed
        // its hash by hand. Three names cover the byte layouts the hash
        // has, BMP (two bytes per char), supplementary (a surrogate pair),
        // and an unpaired surrogate, so more names test nothing new.
        var bmp = OneOrMore(OneOf(TokenSet.Letters)).As(GreekName);
        bmp.Compile();
        Assert.That(bmp.Id.Value, Is.EqualTo(1309885215), "BMP Greek name");

        var supplementary = OneOrMore(OneOf(TokenSet.Letters)).As(EmojiName);
        supplementary.Compile();
        Assert.That(supplementary.Id.Value, Is.EqualTo(1818025668), "supplementary-plane emoji name");

        // A name that is a single unpaired surrogate. The hash walks code
        // units and never decodes a scalar, so it produces a stable id
        // here too. This case is the reason the hardcoded-value form
        // matters most: string.EnumerateRunes turns every lone surrogate
        // into U+FFFD, so a hash reworked to walk runes wouldn't just
        // shift this id, it would give every distinct lone surrogate the
        // same one. The hardcoded value fails loudly if that happens.
        var loneSurrogate = OneOrMore(OneOf(TokenSet.Letters)).As(UnicodeExamples.HighSurrogateMinText);
        loneSurrogate.Compile();
        Assert.That(loneSurrogate.Id.Value, Is.EqualTo(966238277), "lone high surrogate name");

        // Every id must still land in the custom range reserved for
        // user-defined rules, not the Unicode-rune or built-in ranges.
        Assert.That(bmp.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart));
        Assert.That(supplementary.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart));
        Assert.That(loneSurrogate.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart));
    }

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void Same_unicode_name_produces_the_same_id(string label, int[] codeUnits)
    {
        // Two independently built rules with the same Unicode name hash to
        // the same id, the property that makes serialized ids portable
        // across runs and across grammar rebuilds. A uniform property (no
        // per-name expected value, just first.Id == second.Id), so unlike
        // the hardcoded-id determinism test it runs the whole catalog.
        string name = BuildString(codeUnits);
        var first = OneOrMore(OneOf(TokenSet.Letters)).As(name);
        first.Compile();

        var second = OneOrMore(OneOf(TokenSet.Letters)).As(name);
        second.Compile();

        Assert.That(second.Id.Value, Is.EqualTo(first.Id.Value), label);
    }

    [Test]
    public void Distinct_unicode_names_get_distinct_ids()
    {
        // Greek and Devanagari names in one grammar resolve to two
        // different rules with two different ids, and each name round-trips.
        var greek = OneOrMore(OneOf(TokenSet.Letters)).As(GreekName);
        var devanagari = OneOrMore(OneOf(TokenSet.Letters)).As(DevanagariName);
        var root = And(greek, devanagari);
        root.Compile();

        Assert.That(greek.Id.Value, Is.Not.EqualTo(devanagari.Id.Value));
        Assert.That(root.NameOf(greek.Id), Is.EqualTo(GreekName));
        Assert.That(root.NameOf(devanagari.Id), Is.EqualTo(DevanagariName));
    }

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void Two_reachable_rules_with_the_same_unicode_name_fail_to_compile(string label, int[] codeUnits)
    {
        // The duplicate-name check keys a Dictionary<string, Rule> on the
        // name, so it rejects two rules that collide on any Unicode name
        // just as it rejects ASCII ones, and the offending name shows up
        // in the error so the author can find it.
        string name = BuildString(codeUnits);
        var first = OneOrMore(OneOf(TokenSet.Letters)).As(name);
        var second = OneOrMore(OneOf(TokenSet.Digits)).As(name);
        var root = And(first, second);

        var exception = Assert.Throws<InvalidOperationException>(() => root.Compile());
        AssertContainsOrdinal(exception!.Message, name, label);
    }

    [Test]
    public void Canonically_equivalent_unicode_names_are_treated_as_distinct()
    {
        // "café" precomposed (U+00E9) and "café" decomposed (e + U+0301)
        // render identically and are canonically equivalent, but they are
        // different UTF-16 strings. Names are compared ordinally, never by
        // Unicode canonical equivalence, so the two rules coexist in one
        // grammar with two distinct ids. A grammar author who wants them
        // treated as one name has to normalize the string before passing
        // it to .As(). This locks that behavior: ordinal, predictable, no
        // surprise normalization.
        string precomposed = UnicodeExamples.CafePrecomposedGrapheme;
        string decomposed = UnicodeExamples.CafeDecomposedText;
        Assert.That(precomposed, Is.Not.EqualTo(decomposed), "test premise: the two forms differ as strings");

        var precomposedRule = OneOrMore(OneOf(TokenSet.Letters)).As(precomposed);
        var decomposedRule = OneOrMore(OneOf(TokenSet.Digits)).As(decomposed);
        var root = And(precomposedRule, decomposedRule);
        Assert.DoesNotThrow(() => root.Compile());

        Assert.That(root.IdOf(precomposed)!.Value, Is.Not.EqualTo(root.IdOf(decomposed)!.Value));
        Assert.That(root.NameOf(precomposedRule.Id), Is.EqualTo(precomposed));
        Assert.That(root.NameOf(decomposedRule.Id), Is.EqualTo(decomposed));
    }

    [Test]
    public void Distinct_lone_surrogate_names_stay_distinct()
    {
        // Two rules named with two different lone high surrogates (U+D800
        // and U+D83D). They differ only in the surrogate code unit. The
        // duplicate-name check compares the raw strings ordinally, so it
        // sees two distinct names and the grammar compiles. If any path
        // decoded a lone surrogate (string.EnumerateRunes turns every one
        // into U+FFFD), the two names would collapse into one and Compile
        // would reject the grammar as a duplicate.
        var first = OneOrMore(OneOf(TokenSet.Letters)).As(UnicodeExamples.HighSurrogateMinText);
        var second = OneOrMore(OneOf(TokenSet.Digits)).As(UnicodeExamples.EmojiStartHighSurrogateText);
        var root = And(first, second);
        Assert.DoesNotThrow(() => root.Compile());

        Assert.That(first.Id.Value, Is.Not.EqualTo(second.Id.Value));
        Assert.That(root.NameOf(first.Id), Is.EqualTo(UnicodeExamples.HighSurrogateMinText));
        Assert.That(root.NameOf(second.Id), Is.EqualTo(UnicodeExamples.EmojiStartHighSurrogateText));
    }

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void Named_single_rune_Token_with_a_unicode_name_returns_that_name(string label, int[] codeUnits)
    {
        // Token('a') carries its rune code point (0x61) as its id by
        // construction. .As(name) clears that auto-assigned id so Compile
        // hands the rule a fresh hash-derived custom-range id, and NameOf
        // returns the user name rather than the rune text. Any Unicode
        // name on a single-rune Token behaves the same.
        string name = BuildString(codeUnits);
        var aChar = Token('a').As(name);
        aChar.Compile();

        Assert.That(aChar.NameOf(aChar.Id), Is.EqualTo(name), label);
        Assert.That(aChar.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart), label);
    }

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void Unicode_name_composes_with_an_explicit_SymbolId(string label, int[] codeUnits)
    {
        // .As(string) writes Name; .As(SymbolId) writes Id. They compose:
        // a rule can carry both a Unicode display name and an explicit id.
        // After compile the explicit id stands and NameOf still resolves
        // it to the Unicode name.
        string name = BuildString(codeUnits);
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 4242);
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As(name).As(explicitId);
        rule.Compile();

        Assert.That(rule.Id, Is.EqualTo(explicitId), label);
        Assert.That(rule.NameOf(explicitId), Is.EqualTo(name), label);
        Assert.That(rule.IdOf(name)!.Value, Is.EqualTo(explicitId), label);
    }

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void As_set_once_exception_quotes_the_unicode_name(string label, int[] codeUnits)
    {
        // .As(string) is set-once: a second call throws. The exception
        // quotes the name so the author can spot the clash, so the
        // Unicode text has to survive into that interpolated message.
        string name = BuildString(codeUnits);
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As(name);

        var exception = Assert.Throws<InvalidOperationException>(() => rule.As(name));
        AssertContainsOrdinal(exception!.Message, name, label);
        AssertContainsOrdinal(exception.Message, "set-once", label);
    }

    [Test]
    [RecursiveEngineOnly]
    public void Unicode_rule_name_appears_in_the_trace_label()
    {
        // The trace label is "{Name}:{ruleClassName}". The name flows
        // through the TraceInterpolatedStringHandler into the sink; a
        // Unicode name has to land in the label byte-for-byte. Input is
        // ASCII so only the final label line carries the Unicode text.
        var sink = NewSink();
        var rule = OneOrMore(OneOf(TokenSet.Ascii.Letters)).As(GreekName);
        rule.Parse("foo", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "      Lexer.Read: 'f', Consumed: 1",
            "      SUCC | OneOf: found 'f', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'o', Consumed: 2",
            "      SUCC | OneOf: found 'o', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: 'o', Consumed: 3",
            "      SUCC | OneOf: found 'o', wanted one of '[A-Z,a-z]'",
            "      Lexer.Read: '<EOF>', Consumed: 3",
            "      FAIL | OneOf: found '<EOF>', wanted one of '[A-Z,a-z]'",
            "      Lexer.RecordFailure: new deepest failure at char 3",
            "   SUCC | " + GreekName + ":OneOrMore: count= 3"
        );
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Deeper_unicode_WithError_message_wins_over_a_shallower_one()
    {
        // Depth-primary error resolution with two Unicode messages. The Or
        // runs both branches: the letters branch fails at offset 0, the
        // digits branch consumes '#' and fails at offset 1. The deeper
        // failure surfaces, and its Unicode message has to come through
        // intact. See docs/ErrorArchitecture.md.
        string shallow = "need " + GreekName;
        string deep = "need " + DevanagariName;
        var letters = OneOrMore(OneOf(TokenSet.Letters)).WithError(shallow);
        var digits = And(Token('#'), OneOrMore(OneOf(TokenSet.Digits)).WithError(deep));
        var rule = Or(letters, digits);

        var result = rule.Parse("#x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo(deep));
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
    }

    [Test]
    public void Forced_unicode_WithError_message_overrides_a_deeper_named_failure()
    {
        // A forced WithError is a hard override: it wins over every
        // non-forced failure at any depth. With Unicode messages on both
        // the inner Token and the outer forced Or, the outer message has
        // to surface unchanged.
        string innerMessage = "unterminated " + GreekName;
        string outerMessage = "expected " + DevanagariName;
        var quoted = And(
            Token('"'),
            ZeroOrMore(NoneOf(TokenSet.Runes("\""))),
            Token('"').WithError(innerMessage));
        var rule = Or(quoted, Token('x')).WithError(outerMessage, forced: true);

        var result = rule.Parse("\"hello");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo(outerMessage));
    }

    [TestCaseSource(nameof(UnicodeStringCatalog))]
    public void WithError_set_once_exception_quotes_the_unicode_message(string label, int[] codeUnits)
    {
        // .WithError is set-once: a second call throws. The exception
        // quotes the message, so the Unicode text has to survive into
        // that interpolated message.
        string message = BuildString(codeUnits);
        var rule = OneOrMore(OneOf(TokenSet.Letters)).WithError(message);

        var exception = Assert.Throws<InvalidOperationException>(() => rule.WithError(message));
        AssertContainsOrdinal(exception!.Message, message, label);
        AssertContainsOrdinal(exception.Message, "set-once", label);
    }

    [Test]
    [RecursiveEngineOnly]
    public void WithError_unicode_message_appears_in_trace_output()
    {
        // On a failure line the WithError message is appended in quotes
        // after the trace body. The message flows through the
        // TraceInterpolatedStringHandler; its Unicode text has to reach
        // the sink unchanged.
        string message = "expected " + GreekName;
        var sink = NewSink();
        var rule = Token('a').WithError(message);
        rule.Parse("x", new ParseOptions { TraceSink = sink });

        string expected = Lines(
            "   Lexer.Read: 'x', Consumed: 1",
            "   FAIL | Token: found 'x', wanted 'a' \"" + message + "\"");
        Assert.That(sink.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void Unicode_named_grammar_with_a_unicode_error_handles_unicode_input()
    {
        // One grammar carrying a Unicode name and a Unicode error message,
        // exercised on both the success and failure paths with Unicode
        // input. The root is named with an emoji and its WithError message
        // names a Devanagari rule; the success input is Greek letters.
        string errorMessage = "expected " + DevanagariName;
        var word = OneOrMore(OneOf(TokenSet.Letters))
            .As(EmojiName)
            .WithError(errorMessage);

        // Greek "καλημέρα" is eight letters: the grammar matches all of
        // them and the parsed root carries the emoji display name.
        var success = word.Parse(GreekName);
        Assert.That(success.Success, Is.True);
        Assert.That(success.Tree!.DisplayName, Is.EqualTo(EmojiName));
        Assert.That(success.ToString(), Is.EqualTo(GreekName));

        // No letters at the start: the rule fails and surfaces its
        // Unicode error message.
        var failure = word.Parse("123");
        Assert.That(failure.Success, Is.False);
        Assert.That(failure.ErrorMessage, Is.EqualTo(errorMessage));
        Assert.That(failure.ErrorCharIndex, Is.EqualTo(0));
    }

    private static int[] CodeUnits(string text)
    {
        var units = new int[text.Length];
        for (int index = 0; index < text.Length; index++)
            units[index] = text[index];
        return units;
    }

    private static string BuildString(int[] codeUnits)
    {
        var chars = new char[codeUnits.Length];
        for (int index = 0; index < codeUnits.Length; index++)
            chars[index] = (char)codeUnits[index];
        return new string(chars);
    }

    // Ordinal substring assertion. NUnit's Does.Contain searches a string
    // culture-sensitively, and a culture-sensitive search treats the
    // default-ignorable code points in the catalog (skin-tone modifiers,
    // ZWJ, combining marks, BOM, bidi controls, ...) as collation-
    // invisible, so it fails to find a message that is one even when the
    // code units are right there. Ordinal compares code units literally.
    private static void AssertContainsOrdinal(string actual, string expected, string label)
    {
        Assert.That(actual.Contains(expected, StringComparison.Ordinal), Is.True,
            $"[{label}] message should contain the expected text. Message: {actual}");
    }
}
