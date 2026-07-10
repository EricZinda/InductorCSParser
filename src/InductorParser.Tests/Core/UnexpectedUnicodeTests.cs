using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

// Probes parser behavior on Unicode inputs that are either technically
// ill-formed or end up being unexpected gotchas or edge cases. Two
// goals: (1) document how the parser reacts to each category so
// callers know what to expect, and (2) lock the reactions in against
// silent regressions.
//
// The categories are grouped into seven buckets:
//
//   1. ENCODING-LEVEL ILL-FORMED INPUT: reported as MalformedInput
//      or surfaces as an untyped token, never silently corrupts.
//      These are (the only) cases truly defined as "ill formed"
//      by the Unicode standard and are all examples where
//      UTF-16 invariants are broken: lone surrogates, reversed surrogate
//      pairs, the U+10FFFF maximum boundary. Only UTF-16 here because
//      the parser takes a .NET string, and .NET strings are UTF-16
//      internally. UTF-8 / UTF-32 / legacy-codepage decoding errors
//      get resolved upstream by the caller's Encoding.GetString call
//      (usually as U+FFFD replacements, see Group 4) before the
//      parser is ever invoked. Under default FormC, .NET's
//      string.Normalize rejects ill-formed UTF-16. Parse catches that
//      and returns a ParseResult whose Outcome is MalformedInput,
//      positioned at the offending character with a message the
//      grammar author can localize, so the caller handles it the same
//      way as every other failure instead of catching a BCL exception.
//      Under Compile(null) the lexer surfaces each ill-formed code unit
//      as a token with no RuneValue, which OneOf and similar rules
//      predictably reject. Either path is safe. A grammar can't
//      silently match a ill-formed surrogate.
//
//   2. BARE ATTACHING CHARACTERS: surfaces as a normal token, so
//      grammar mismatches produce a normal error.
//      Not technically "ill formed" but can be unexpected and should
//      be handled consistently and transparently. These are:
//      UAX #29 attaching characters (combining marks, Zero Width
//      Joiner, variation selectors, regional indicators, skin-tone
//      modifiers, prepend, Indic linker) appearing without their
//      normal base. UAX #29 always breaks at the start and end of
//      text, so each one ends up as its own one-character cluster.
//      The grammar writer doesn't need any special-case logic: the
//      stray attaching character shows up as a token that the
//      grammar's rules (OneOf, Token, Literal, etc.) won't match,
//      and the parse fails normally. Same handling as any other
//      unexpected character.
//
//   3. INVISIBLE FORMATTING CHARACTERS: surfaces as a normal token,
//      so grammar mismatches produce a normal error.
//      Not technically "ill formed" but can be unexpected and should
//      be handled consistently and transparently. These are:
//      Characters that don't render as a glyph: zero-width spaces
//      and joiners, soft hyphens, BOMs, variation selectors, the
//      bidi-direction controls (the same ones that power Trojan-
//      Source attacks in source-code editors), tag characters. The
//      parser is faithful to the actual character sequence in the
//      input string. It doesn't reorder anything based on the bidi-
//      direction controls (so a Trojan-Source-style attack that
//      tricks an editor into displaying the text differently from
//      its logical order can't fool the parser), and it doesn't
//      strip the no-render characters out (so a zero-width space
//      hidden between two letters becomes a real token between
//      them, not invisible to the grammar). Same safety story as
//      Group 2: an invisible formatting character that doesn't fit
//      the grammar produces a normal error.
//
//   4. NONCHARACTERS, PRIVATE USE, REPLACEMENT: surfaces as a
//      normal token, so grammar mismatches produce a normal error
//      (one .NET-specific case, U+FFFE, surfaces as MalformedInput).
//      Not technically "ill formed" but can be unexpected and should
//      be handled consistently and transparently. These are:
//      Code points designated never-a-character (FFFE/FFFF,
//      FDD0..FDEF), Private Use Area (E000..F8FF), and the
//      replacement character U+FFFD that decoders write for
//      malformed input. The parser treats almost all of these as
//      ordinary tokens (it doesn't filter or substitute) with the
//      same safety story as Group 2: a code point your grammar
//      doesn't recognize produces a normal error. The exception: .NET's
//      string.Normalize rejects U+FFFE specifically as "invalid
//      Unicode code points," so default FormC parsing of input
//      containing U+FFFE returns a MalformedInput result out of Parse().
//      Why U+FFFE and not the other noncharacters: U+FFFE is the
//      byte-swapped form of U+FEFF (BOM), so its presence in a
//      string is a signal that upstream byte-order detection
//      failed. Windows' NormalizeString refuses to process such
//      strings on that theory. U+FFFF and U+FDD0..U+FDEF have
//      no such signal and pass through.
//      The caller reads the MalformedInput outcome (or compiles with
//      null to skip normalization). U+FFFF, U+FDD0, the rest of the Private Use
//      Area, and U+FFFD all pass through as ordinary tokens.
//
//   5. NORMALIZATION EDGE CASES: compile-time normalization checks
//      ensure literals match the chosen form. See NormalizationTests.cs
//      for the full coverage.
//
//   6. IDENTIFIER-RELEVANT EDGE CASES: the default Identifier rule
//      doesn't try to detect lookalike-character attacks. If the
//      grammar accepts identifiers and the input might be hostile,
//      build a TokenSet that only allows one script (Latin only,
//      Cyrillic only, etc.). UTS #39 is the Unicode security spec
//      that covers this attack.
//
//      Example: "aа" looks like one word but is actually Latin 'a'
//      (U+0061) followed by Cyrillic 'а' (U+0430). Both render
//      identically. Identifier() accepts this string because both
//      characters are identifier-valid per UAX #31 (the XID_Continue
//      property). The fix (a Latin-only TokenSet that rejects the
//      Cyrillic version) is in UnicodeGotchasExamples.
//
//      Other identifier-related behavior (non-ASCII digits in
//      TokenSet.Digits, the five Letter categories in
//      TokenSet.Letters) is covered in TokenSetTests, not here.
//
//   7. TRIVIAL / DEFENSIVE: parser doesn't crash, regardless of
//      shape or length.
//      Empty input, NULL byte, very long sequences. Mostly here to
//      lock in "parser doesn't crash on N consecutive zero-width
//      characters" against future regressions.
//
[TestFixture]
public class UnexpectedUnicodeTests
{
    // ============================================================
    // Group 1: Encoding-level malformed input (broken UTF-16)
    //   expected: throws cleanly or surfaces as an untyped token,
    //   never silently corrupts.
    // ============================================================

    [Test]
    public void Lone_high_surrogate_handling()
    {
        // .NET's string.Normalize rejects malformed UTF-16. Under
        // default FormC the parser calls Normalize before the lexer
        // ever runs. Rather than let that ArgumentException escape,
        // Parse returns a MalformedInput result positioned at the
        // offending surrogate, with a message the grammar author can
        // localize (see Ill_formed_input_returns_localizable_MalformedInput_result).
        // Compile(null) skips normalization, the lexer's TryPeekRune
        // treats a surrogate half as "no rune here" and Read() emits
        // a one-char token with no RuneValue. Grammars that need to
        // handle a lone surrogate follow the same 4-part pattern as
        // the other malformed tests.
        //
        // Note on Token(...) for surrogates: Token(char), Token(int),
        // and Token(Rune) all reject surrogate code points at
        // construction (they aren't valid Unicode scalars). Token(string)
        // goes through the grapheme-cluster path and compares input
        // chars byte-for-byte against the literal, so it matches
        // surrogate halves under Compile(null). Useful for grammars
        // that want to deliberately accept or process one (e.g. WTF-8
        // round-tripping or unpaired-surrogate handling in JSON).
        string input = UnicodeExamples.HighSurrogateMinText + "hello";

        // (0) Default FormC reports a MalformedInput result at the lone
        // surrogate (index 0) instead of throwing.
        var malformed = And(Literal("hello"), Eof()).Parse(input);
        Assert.That(malformed.Outcome, Is.EqualTo(ParseOutcome.MalformedInput));
        Assert.That(malformed.ErrorCharIndex, Is.EqualTo(0));

        // (1) Compile(null) + naive grammar fails normally:
        // Literal("hello") doesn't match a surrogate followed by 'h'.
        // Lone surrogates don't match any normal predicate (their
        // RuneValue is -1, which no TokenSet accepts), so OneOf and
        // TokenSet-based rules skip them too.
        var naiveRule = And(Literal("hello"), Eof());
        naiveRule.Compile(null);
        Assert.That(naiveRule.Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the lone surrogate as
        // a wildcard.
        var anyTokenRule = And(AnyToken(), Literal("hello"), Eof());
        anyTokenRule.Compile(null);
        Assert.That(anyTokenRule.Parse(input).Success, Is.True);

        // (3) Targeting the surrogate via Token(string) also works.
        var targetedRule = And(Token(UnicodeExamples.HighSurrogateMinText), Literal("hello"), Eof());
        targetedRule.Compile(null);
        Assert.That(targetedRule.Parse(input).Success, Is.True);
    }

    [Test]
    public void Lone_low_surrogate_handling()
    {
        // Same shape as the high-surrogate test using the maximum
        // low-surrogate code unit (U+DFFF). Locks in that the lone-
        // surrogate path treats high and low halves symmetrically.
        string input = UnicodeExamples.LowSurrogateMaxText + "hello";

        // (0) Default FormC reports a MalformedInput result at the lone
        // surrogate (index 0) instead of throwing.
        var malformed = And(Literal("hello"), Eof()).Parse(input);
        Assert.That(malformed.Outcome, Is.EqualTo(ParseOutcome.MalformedInput));
        Assert.That(malformed.ErrorCharIndex, Is.EqualTo(0));

        // (1) Compile(null) + naive grammar fails normally.
        var naiveRule = And(Literal("hello"), Eof());
        naiveRule.Compile(null);
        Assert.That(naiveRule.Parse(input).Success, Is.False);

        // (2) AnyToken accommodation succeeds.
        var anyTokenRule = And(AnyToken(), Literal("hello"), Eof());
        anyTokenRule.Compile(null);
        Assert.That(anyTokenRule.Parse(input).Success, Is.True);

        // (3) Token-specific accommodation succeeds.
        var targetedRule = And(Token(UnicodeExamples.LowSurrogateMaxText), Literal("hello"), Eof());
        targetedRule.Compile(null);
        Assert.That(targetedRule.Parse(input).Success, Is.True);
    }

    [Test]
    public void Ill_formed_input_returns_localizable_MalformedInput_result()
    {
        // The whole point of MalformedInput over a thrown ArgumentException:
        // a non-English app can replace the wording through
        // ParseOptions.MalformedInputTemplate, the same way it localizes
        // every other parse failure. The position placeholders work, and so
        // does {character}, which renders the offending element (a lone
        // surrogate comes out as U+D800, routed through DisplayEscape).
        string input = "ok" + UnicodeExamples.HighSurrogateMinText;
        var rule = And(Literal("ok"), Eof()).Compile();

        var options = new ParseOptions
        {
            MalformedInputTemplate = "entrada no valida en {charIndex}: {character}",
        };
        var result = rule.Parse(input, options);

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.MalformedInput));
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2), "the lone surrogate sits at index 2");
        Assert.That(result.ErrorMessage, Is.EqualTo("entrada no valida en 2: U+D800"));
    }

    [Test]
    public void Malformed_input_position_points_at_the_first_offending_char_not_just_zero()
    {
        // Checks the position-finder doesn't degenerate to "always report
        // index 0". It has to walk past valid content and land on the first
        // genuinely ill-formed code unit, including correctly skipping a valid
        // surrogate PAIR (two chars that can't be mistaken for two lone
        // surrogates).
        var grammar = And(Literal("ignored"), Eof()).Compile();  // default FormC, never runs

        // "a" (1 char) + waving hand (U+1F44B, a valid surrogate pair = 2
        // chars) + a lone surrogate. The pair is skipped, so the lone
        // surrogate is the first ill-formed unit, at index 3.
        string afterValidPair = "a" + UnicodeExamples.WavingHandGrapheme + UnicodeExamples.HighSurrogateMinText;
        var afterPairResult = grammar.Parse(afterValidPair);
        Assert.That(afterPairResult.Outcome, Is.EqualTo(ParseOutcome.MalformedInput));
        Assert.That(afterPairResult.ErrorCharIndex, Is.EqualTo(3),
            "the valid surrogate pair is skipped; the lone surrogate is at index 3");

        // A lone surrogate in the MIDDLE, with valid text after it, still
        // reports the surrogate's position (index 2), proving the finder stops
        // at the first offender rather than running to the end.
        string surrogateInMiddle = "ab" + UnicodeExamples.HighSurrogateMinText + "cd";
        var middleResult = grammar.Parse(surrogateInMiddle);
        Assert.That(middleResult.Outcome, Is.EqualTo(ParseOutcome.MalformedInput));
        Assert.That(middleResult.ErrorCharIndex, Is.EqualTo(2),
            "the lone surrogate between 'ab' and 'cd' is at index 2");

        // The non-surrogate branch (U+FFFE) is found at a non-zero position
        // too, mirroring the index-0 coverage in Noncharacter_FFFE_handling.
        string fffeAfterPrefix = "abc" + UnicodeExamples.NoncharacterFFFEText;
        var fffeResult = grammar.Parse(fffeAfterPrefix);
        Assert.That(fffeResult.Outcome, Is.EqualTo(ParseOutcome.MalformedInput));
        Assert.That(fffeResult.ErrorCharIndex, Is.EqualTo(3),
            "U+FFFE is found at index 3, after the ASCII prefix");
    }

    [Test]
    public void Token_with_stray_surrogate_under_default_Compile_throws_clear_error()
    {
        // A surrogate-bearing literal can never be normalized to FormC (or any
        // other form). The user has to use Compile(null) for these, per the
        // documented WTF-8 / unpaired-surrogate round-tripping case. Compile()
        // should surface that in a clear InvalidOperationException with the
        // same helpful shape as other normalization mismatches, not let .NET's
        // generic ArgumentException leak out. The original ArgumentException
        // is preserved as InnerException (wrapped in AggregateException so the
        // shape stays uniform when multiple literals each trip Normalize) so
        // a programmatic caller can still drill in to the runtime cause.
        var rule = Token(UnicodeExamples.HighSurrogateMinText);

        var exception = Assert.Throws<InvalidOperationException>(() => rule.Compile());
        Assert.That(exception!.Message, Does.Contain("surrogate"),
            "Compile error should mention surrogates so the author knows what to fix.");
        Assert.That(exception.Message, Does.Contain("Compile(null)"),
            "Compile error should suggest Compile(null) as the documented path.");
        Assert.That(exception.InnerException, Is.InstanceOf<AggregateException>(),
            "InnerException should expose the runtime cause.");
        var aggregate = (AggregateException)exception.InnerException!;
        Assert.That(aggregate.InnerExceptions, Has.Count.EqualTo(1));
        Assert.That(aggregate.InnerExceptions[0], Is.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Literal_with_stray_surrogate_under_default_Compile_throws_clear_error()
    {
        // Same shape as Token, applied to LiteralRule. The form-validation
        // pass walks every literal-bearing rule, so the same fix has to cover
        // GraphemeRule, LiteralRule, and LiteralIgnoreAsciiCaseRule alike.
        var rule = Literal(UnicodeExamples.HighSurrogateMinText + "X");

        var exception = Assert.Throws<InvalidOperationException>(() => rule.Compile());
        Assert.That(exception!.Message, Does.Contain("surrogate"));
        Assert.That(exception.InnerException, Is.InstanceOf<AggregateException>());
    }

    [Test]
    public void LiteralIgnoreAsciiCase_with_stray_surrogate_throws_at_construction()
    {
        // Surrogate halves are outside the ASCII range (0xD800..0xDFFF
        // > 0x7F), so LiteralIgnoreAsciiCase rejects them at construction
        // before the form-validation pass ever runs. Token and Literal
        // still defer to the form-validation pass, which is the right
        // shape for them: they accept any string content, and the
        // string.Normalize call inside Compile is what trips on the
        // surrogate.
        var exception = Assert.Throws<ArgumentException>(() =>
            LiteralIgnoreAsciiCase(UnicodeExamples.HighSurrogateMinText + "x"));
        Assert.That(exception!.Message, Does.Contain("ASCII-only"));
    }

    [Test]
    public void Multiple_surrogate_literals_under_default_Compile_aggregate_inner_exceptions()
    {
        // Every literal-bearing rule that trips string.Normalize contributes
        // one ArgumentException to the AggregateException. The grammar author
        // sees a single multi-rule message in InvalidOperationException.Message
        // and can walk InnerExceptions for the per-rule runtime cause.
        // LiteralIgnoreAsciiCase isn't part of this aggregate because it
        // rejects non-ASCII at construction, before Compile runs.
        var rule = And(
            Token(UnicodeExamples.HighSurrogateMinText),
            Literal(UnicodeExamples.HighSurrogateMinText + "X"));

        var exception = Assert.Throws<InvalidOperationException>(() => rule.Compile());
        Assert.That(exception!.InnerException, Is.InstanceOf<AggregateException>());
        var aggregate = (AggregateException)exception.InnerException!;
        Assert.That(aggregate.InnerExceptions, Has.Count.EqualTo(2));
        Assert.That(aggregate.InnerExceptions, Has.All.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Literal_with_lone_surrogate_first_char_compiles_under_null_normalization()
    {
        // Mirrors the Token(string) round-tripping case at the top of this
        // file: a grammar that wants to deliberately accept WTF-8 / unpaired-
        // surrogate input under Compile(null) should be able to write the
        // surrogate as the first char of a Literal too, not just as a
        // single-token Token(string). Regression: ComputeRuleStart used to
        // pass TryPeekRune's -1 marker straight to TokenSet.Single, which
        // blew up with "Actual value was -1." out of Compile.
        string input = UnicodeExamples.HighSurrogateMinText + "X";
        var rule = Literal(UnicodeExamples.HighSurrogateMinText + "X");

        Assert.DoesNotThrow(() => rule.Compile(null));
        Assert.That(rule.Parse(input).Success, Is.True);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_with_lone_surrogate_first_char_throws_at_construction()
    {
        // The pattern restriction is on the pattern itself, regardless
        // of which normalization form Compile would later use. Surrogate
        // halves are outside the ASCII range, so LiteralIgnoreAsciiCase
        // rejects them up front even when the caller would have compiled
        // with Compile(null) to skip the form-validation pass entirely.
        // Grammars that want surrogate-prefixed match-as-written
        // behavior should use Literal(...) (still works under
        // Compile(null), as the Literal test above shows).
        Assert.Throws<ArgumentException>(() =>
            LiteralIgnoreAsciiCase(UnicodeExamples.HighSurrogateMinText + "X"));
    }

    [Test]
    public void Literal_with_lone_low_surrogate_first_char_compiles_under_null_normalization()
    {
        // Lone LOW surrogate at the start of the literal exercises the
        // char.IsSurrogate-but-not-high branch of TryPeekRune. The fix
        // covers both halves the same way.
        string input = UnicodeExamples.LowSurrogateMaxText + "X";
        var rule = Literal(UnicodeExamples.LowSurrogateMaxText + "X");

        Assert.DoesNotThrow(() => rule.Compile(null));
        Assert.That(rule.Parse(input).Success, Is.True);
    }

    [Test]
    public void Or_with_AnyToken_fallback_matches_lone_surrogate_under_null_normalization()
    {
        // Or(specific, AnyToken()) on lone-surrogate input under
        // Compile(null): AnyToken() matches the surrogate as
        // a one-char token, the Or succeeds, and the matched text
        // round-trips the surrogate verbatim. Same shape grammars use
        // for "specific case, otherwise pass through anything," now
        // exercised on input where the next position is a surrogate
        // half (no valid rune) rather than a real rune.
        string input = UnicodeExamples.HighSurrogateMinText;
        var rule = Or(Literal("X"), AnyToken());
        rule.Compile(null);

        var result = rule.Parse(input);
        Assert.That(result.Success, Is.True,
            "AnyToken fallback should match the lone surrogate as a one-char token");
        Assert.That(result.ToString(), Is.EqualTo(input),
            "matched text round-trips the surrogate verbatim");
    }

    [Test]
    public void Or_with_AnyToken_fallback_matches_lone_low_surrogate_under_null_normalization()
    {
        // Mirror of the high-surrogate test using a lone LOW surrogate so
        // both halves of the surrogate range get exercised through the
        // Or + AnyToken fallback.
        string input = UnicodeExamples.LowSurrogateMaxText;
        var rule = Or(Literal("X"), AnyToken());
        rule.Compile(null);

        Assert.That(rule.Parse(input).Success, Is.True);
    }

    [Test]
    public void Or_eof_shortcut_lets_zero_width_children_match()
    {
        // Or(specific, Eof()) on empty input: every Always child
        // (Literal here) is correctly skipped at EOF since there's no
        // rune to consume, and Eof (Advance.Never) is still tried and
        // matches. Asserts the EOF fast-fail behavior the lookahead
        // shortcut delivers, separate from the surrogate-handling
        // tests above.
        var rule = Or(Literal("X"), Eof()).Compile();
        Assert.That(rule.Parse("").Success, Is.True);
    }

    [Test]
    public void OneOf_universe_rejects_lone_surrogate_under_null_normalization()
    {
        // Universe is the scalar-value universe (no surrogates), because
        // operator ~ complements over scalar values only. Under
        // Compile(null) a lone surrogate token therefore doesn't match
        // Universe, not because of the membership logic, but because
        // the surrogate isn't in the set. Grammars that want surrogates
        // opt in with `| Surrogates`.
        var rule = OneOf(TokenSet.Universe);
        rule.Compile(null);
        string input = UnicodeExamples.HighSurrogateMinText;
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void OneOf_with_surrogate_bearing_set_matches_lone_high_surrogate()
    {
        // The opt-in surrogate set: every scalar value, plus the
        // surrogate block. Under Compile(null) a stray surrogate is a
        // valid one-char token, and OneOf membership through
        // TokenSet.ContainsToken disambiguates the three RuneValue == -1
        // cases (EOF / multi-rune / stray surrogate) and queries the
        // rune intervals using the surrogate's UTF-16 code unit value
        // for the surrogate case. The motivating use case is WTF-8 /
        // unpaired-surrogate round-tripping.
        var rule = OneOf(TokenSet.Range(0, 0x10FFFF) | TokenSet.Surrogates);
        rule.Compile(null);

        Assert.That(rule.Parse(UnicodeExamples.HighSurrogateMinText).Success, Is.True,
            "The set includes the high surrogate min; should match it.");
        Assert.That(rule.Parse(UnicodeExamples.LowSurrogateMaxText).Success, Is.True,
            "The set includes the low surrogate max; should match it.");
        Assert.That(rule.Parse("a").Success, Is.True,
            "The set still matches ordinary scalars.");
    }

    [Test]
    public void NoneOf_with_surrogate_bearing_set_rejects_lone_surrogate()
    {
        // The mirror case for NoneOf. Without the surrogate-aware
        // membership check, NoneOf wrongly accepted lone surrogates
        // even when the user's set explicitly negated them: the
        // membership probe returned false (because RuneValue was -1
        // and the rune intervals weren't queried), NoneOf inverted
        // false to true, and the false-positive match shipped. The
        // surrogate-aware check fixes both directions in lockstep.
        var everything = TokenSet.Range(0, 0x10FFFF) | TokenSet.Surrogates;
        var rule = And(NoneOf(everything), Eof());
        rule.Compile(null);

        Assert.That(rule.Parse(UnicodeExamples.HighSurrogateMinText).Success, Is.False,
            "NoneOf(everything) should reject a high surrogate.");
        Assert.That(rule.Parse(UnicodeExamples.LowSurrogateMaxText).Success, Is.False,
            "NoneOf(everything) should reject a low surrogate.");
        Assert.That(rule.Parse("a").Success, Is.False,
            "NoneOf(everything) covers ASCII too; rejects it.");
    }

    [Test]
    public void NoneOf_scalar_range_accepts_lone_surrogate_input()
    {
        // Range(0, 0x10FFFF) splits around the surrogate block, so a
        // lone surrogate isn't in the set. NoneOf(scalars) therefore
        // ACCEPTS a lone surrogate under Compile(null) (the surrogate
        // is "not in the set of scalar values"). The grammar adds
        // `| Surrogates` to the inner set when it wants surrogates
        // rejected as well.
        var rule = And(NoneOf(TokenSet.Range(0, 0x10FFFF)), Eof());
        rule.Compile(null);

        Assert.That(rule.Parse(UnicodeExamples.HighSurrogateMinText).Success, Is.True);
        Assert.That(rule.Parse(UnicodeExamples.LowSurrogateMaxText).Success, Is.True);
    }

    [Test]
    public void OneOf_with_range_excluding_surrogates_rejects_lone_surrogate()
    {
        // A user-typed Range that doesn't touch the surrogate block (the
        // typical case, Range('a','z'), Letters, etc.) rejects lone-
        // surrogate input.
        var rule = OneOf(TokenSet.Range('a', 'z'));
        rule.Compile(null);

        Assert.That(rule.Parse(UnicodeExamples.HighSurrogateMinText).Success, Is.False);
        Assert.That(rule.Parse("a").Success, Is.True);
    }

    [Test]
    public void Reversed_surrogate_pair_under_null_normalization_lexer_handles_gracefully()
    {
        // Low surrogate followed by high surrogate is two lone surrogates
        // back to back, not a valid pair (UTF-16 pairs are HIGH then
        // LOW). The lexer treats each as a one-char token with no
        // RuneValue. WTF-8 (Simon Sapin) discusses this exact pattern.
        // Lock in that the parser doesn't silently reorder the two
        // halves into a valid pair: each surrogate stays in its
        // original position as its own token, so OneOrMore sees two
        // tokens, not one merged scalar.
        string input = UnicodeExamples.ReversedSurrogatePairText;

        // OneOrMore(AnyToken()) consumes both surrogates as wildcards
        // and confirms the lexer didn't merge or swap them.
        var anyTokenRule = OneOrMore(AnyToken());
        anyTokenRule.Compile(null);
        var anyTokenResult = anyTokenRule.Parse(input);
        Assert.That(anyTokenResult.Success, Is.True);
        Assert.That(anyTokenResult.ToString(), Is.EqualTo(input),
            "matched text equals the input character-for-character; " +
            "the parser didn't swap the surrogates or convert them");
        Assert.That(anyTokenResult.Symbols.Count, Is.EqualTo(2),
            "two distinct AnyToken matches, one per surrogate, " +
            "proving the lexer didn't merge them into a single token");

        // Targeting both surrogates explicitly via Token(string) also
        // works, in their original (low-then-high) order. Useful
        // for grammars that want to surface unpaired surrogates as
        // distinct entities (WTF-8 round-tripping, JSON unpaired-
        // surrogate handling).
        var targetedRule = And(
            Token(UnicodeExamples.LowSurrogateMinText),
            Token(UnicodeExamples.HighSurrogateMinText),
            Eof());
        targetedRule.Compile(null);
        Assert.That(targetedRule.Parse(input).Success, Is.True);
    }

    [Test]
    public void Maximum_code_point_10FFFF_is_one_token()
    {
        // U+10FFFF is the largest valid Unicode scalar. Encoded as a
        // surrogate pair (U+DBFF U+DFFF). NFC keeps it. Lexer reads
        // as one token. Boundary case for the rune-decoding paths.
        string input = UnicodeExamples.MaximumCodePointGrapheme;

        // AnyToken consumes whatever's there (the catch-all path
        // most grammars use).
        Assert.That(AnyToken().Parse(input).Success, Is.True);

        // You can also write a rule that targets U+10FFFF
        // specifically via Token(...). An unrecognized character
        // doesn't match any normal rule, so a grammar without
        // U+10FFFF fails normally at the orphan. The positive-match
        // path is just for grammars that want to deliberately do
        // something with this exact code point.
        Assert.That(And(Token(UnicodeExamples.MaximumCodePointRune), Eof()).Parse(input).Success, Is.True);
    }

    // Coverage matrix: every non-null normalization form routes
    // input through String.Normalize before the lexer runs, and
    // Normalize rejects ill-formed UTF-16 by throwing
    // ArgumentException. Parse catches that and returns a
    // MalformedInput result, so all four non-null forms produce the
    // same MalformedInput outcome on lone surrogates and reversed pairs.
    // The earlier per-input tests (Lone_high_surrogate_handling
    // etc.) cover only FormC. This parameterized test fills the
    // FormD / FormKC / FormKD gap so a future runtime change that
    // diverged any of them from FormC would surface here.
    //
    // Inputs are passed as int[] code points and built into the
    // string inside the test, because NUnit's [TestCase] attribute
    // serializes parameters in a way that drops or reinterprets
    // lone surrogates. Building from code points side-steps that.
    [TestCase(NormalizationForm.FormC, new[] { UnicodeExamples.HighSurrogateMinRune }, TestName = "FormC + lone high surrogate U+D800")]
    [TestCase(NormalizationForm.FormC, new[] { UnicodeExamples.LowSurrogateMaxRune }, TestName = "FormC + lone low surrogate U+DFFF")]
    [TestCase(NormalizationForm.FormC, new[] { UnicodeExamples.LowSurrogateMinRune, UnicodeExamples.HighSurrogateMinRune }, TestName = "FormC + reversed surrogate pair (low,high)")]
    [TestCase(NormalizationForm.FormD, new[] { UnicodeExamples.HighSurrogateMinRune }, TestName = "FormD + lone high surrogate U+D800")]
    [TestCase(NormalizationForm.FormD, new[] { UnicodeExamples.LowSurrogateMaxRune }, TestName = "FormD + lone low surrogate U+DFFF")]
    [TestCase(NormalizationForm.FormD, new[] { UnicodeExamples.LowSurrogateMinRune, UnicodeExamples.HighSurrogateMinRune }, TestName = "FormD + reversed surrogate pair (low,high)")]
    [TestCase(NormalizationForm.FormKC, new[] { UnicodeExamples.HighSurrogateMinRune }, TestName = "FormKC + lone high surrogate U+D800")]
    [TestCase(NormalizationForm.FormKC, new[] { UnicodeExamples.LowSurrogateMaxRune }, TestName = "FormKC + lone low surrogate U+DFFF")]
    [TestCase(NormalizationForm.FormKC, new[] { UnicodeExamples.LowSurrogateMinRune, UnicodeExamples.HighSurrogateMinRune }, TestName = "FormKC + reversed surrogate pair (low,high)")]
    [TestCase(NormalizationForm.FormKD, new[] { UnicodeExamples.HighSurrogateMinRune }, TestName = "FormKD + lone high surrogate U+D800")]
    [TestCase(NormalizationForm.FormKD, new[] { UnicodeExamples.LowSurrogateMaxRune }, TestName = "FormKD + lone low surrogate U+DFFF")]
    [TestCase(NormalizationForm.FormKD, new[] { UnicodeExamples.LowSurrogateMinRune, UnicodeExamples.HighSurrogateMinRune }, TestName = "FormKD + reversed surrogate pair (low,high)")]
    public void Ill_formed_input_returns_malformed_input_under_every_non_null_normalization_form(
        NormalizationForm form, int[] illFormedCodeUnits)
    {
        string illFormedInput = BuildStringFromCodeUnits(illFormedCodeUnits);
        var rule = And(Literal("hello"), Eof());
        rule.Compile(form);

        var result = rule.Parse(illFormedInput);
        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.MalformedInput),
            $"{form} should route input through String.Normalize, which rejects " +
            $"ill-formed UTF-16, and Parse should report that as MalformedInput");
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0),
            "the first ill-formed code unit is at index 0 in every case");
    }

    [TestCase(new[] { 0xD800 }, TestName = "Compile(null) accepts lone high surrogate U+D800")]
    [TestCase(new[] { 0xDFFF }, TestName = "Compile(null) accepts lone low surrogate U+DFFF")]
    [TestCase(new[] { 0xDC00, 0xD800 }, TestName = "Compile(null) accepts reversed surrogate pair")]
    public void Ill_formed_input_does_not_throw_under_null_normalization(int[] illFormedCodeUnits)
    {
        // Compile(null) skips normalization entirely. The lexer
        // surfaces each ill-formed code unit as a token with no
        // RuneValue, and the grammar fails normally rather than
        // throwing. This is the safety valve for callers that need
        // to handle bytes-as-tokens (WTF-8 round-tripping, JSON
        // unpaired-surrogate handling) and don't want the
        // Normalize-time throw.
        string illFormedInput = BuildStringFromCodeUnits(illFormedCodeUnits);
        var rule = And(Literal("hello"), Eof());
        rule.Compile(null);

        Assert.DoesNotThrow(() => rule.Parse(illFormedInput),
            "Compile(null) should skip String.Normalize, so ill-formed " +
            "input passes through to the lexer instead of throwing");
    }

    private static string BuildStringFromCodeUnits(int[] codeUnits)
    {
        var chars = new char[codeUnits.Length];
        for (int i = 0; i < codeUnits.Length; i++) chars[i] = (char)codeUnits[i];
        return new string(chars);
    }

    // ============================================================
    // Group 2: Bare attaching characters (no base to attach to)
    //   expected: surfaces as a normal token, so grammar mismatches
    //   produce a normal error.
    // ============================================================

    [Test]
    public void Bare_combining_acute_at_start_of_input_is_one_token()
    {
        // U+0301 is a non-spacing combining mark. UAX #29 always
        // breaks at the start of text, so a bare combining mark
        // with no preceding base ends up as its own one-character
        // cluster. NFC doesn't introduce a base.
        string input = UnicodeExamples.CombiningAcuteText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a combining mark followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphaned mark
        // as a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the combining mark specifically via
        // Token(...) also works.
        Assert.That(And(Token(UnicodeExamples.CombiningAcuteText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Bare_combining_acute_doesnt_match_OneOf_letters()
    {
        // A combining mark isn't a Letter (it's Mn). OneOf(Letters)
        // rejects it.
        var rule = OneOf(TokenSet.Letters);
        var result = rule.Parse(UnicodeExamples.CombiningAcuteText);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Bare_zero_width_joiner_at_start_of_input_is_one_token()
    {
        // U+200D ZERO WIDTH JOINER (the character that glues emoji
        // together into composed sequences like the family emoji).
        // With no preceding base, the mandatory start-of-text break
        // wraps it as one cluster on its own.
        string input = UnicodeExamples.ZeroWidthJoinerText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a joiner followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphaned joiner
        // as a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the joiner specifically via Token(...)
        // also works.
        Assert.That(And(Token(UnicodeExamples.ZeroWidthJoinerText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Bare_variation_selector_at_start_of_input_is_one_token()
    {
        // U+FE0F EMOJI VARIATION SELECTOR alone at the start of
        // input. Same start-of-text break rule as the joiner above.
        string input = UnicodeExamples.EmojiVariationSelectorText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a variation selector followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphan as a
        // wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the variation selector specifically via
        // Token(...) also works.
        Assert.That(And(Token(UnicodeExamples.EmojiVariationSelectorText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Single_regional_indicator_alone_is_one_token()
    {
        // U+1F1FA REGIONAL INDICATOR SYMBOL LETTER U alone (no pair
        // partner). UAX #29 has a special rule that pairs two
        // regional indicators in a row into a flag cluster, but a
        // single one with nothing to pair with is just one cluster
        // on its own.
        string input = UnicodeExamples.RegionalIndicatorUText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // an unpaired regional indicator followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphaned indicator
        // as a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the regional indicator specifically via
        // Token(...) also works.
        Assert.That(And(Token(UnicodeExamples.RegionalIndicatorURune), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Bare_skin_tone_modifier_alone_is_one_token()
    {
        // U+1F3FD MEDIUM SKIN TONE without a base emoji. UAX #29
        // GCB=Extend, but at start of text the orphan ends up as
        // its own one-character cluster.
        string input = UnicodeExamples.MediumSkinToneText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a skin-tone modifier followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphan as a
        // wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the skin-tone modifier specifically via
        // Token(...) also works.
        Assert.That(And(Token(UnicodeExamples.MediumSkinToneRune), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Prepend_character_at_end_of_input_is_one_token()
    {
        // U+0600 ARABIC NUMBER SIGN is in UAX #29's Prepend category:
        // it normally attaches to the FOLLOWING character. With no
        // following character (end of input) it has nothing to
        // prepend to, so the mandatory end-of-text break wraps it
        // as its own one-character cluster. Normal text comes
        // before the orphan (adding text after would pair them up).
        string input = "hello" + UnicodeExamples.ArabicNumberSignText;

        // (1) Naive grammar fails: Literal("hello") + Eof doesn't
        // match because the trailing Prepend is in the way of Eof.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken after "hello" consumes the orphaned Prepend
        // as a wildcard.
        Assert.That(And(Literal("hello"), AnyToken(), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the Prepend specifically via Token(...)
        // also works.
        Assert.That(And(Literal("hello"), Token(UnicodeExamples.ArabicNumberSignText), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Devanagari_virama_at_start_of_input_is_one_token()
    {
        // U+094D DEVANAGARI SIGN VIRAMA. UAX #29 classifies it as
        // an extending character (it normally joins to the previous
        // consonant in Indic conjuncts). At the start of input there
        // is no previous character to join to, so the mandatory
        // start-of-text break wraps it as its own one-character
        // cluster.
        string input = UnicodeExamples.DevanagariViramaText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a virama followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphaned virama
        // as a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the virama specifically via Token(...) also
        // works.
        Assert.That(And(Token(UnicodeExamples.DevanagariViramaText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Bare_combining_grapheme_joiner_at_start_of_input_is_one_token()
    {
        // U+034F COMBINING GRAPHEME JOINER (CGJ). Combining mark
        // whose only role in NFC is to block canonical reordering
        // of the combining marks around it. Used in Hebrew niqqud
        // and Yiddish typography to keep mark sequences in
        // visually correct (but canonically non-canonical) order
        // through normalization. UAX #29 GCB=Extend, so a bare
        // CGJ with no preceding base ends up as its own one-
        // character cluster, same shape as the other bare
        // attaching characters above.
        string input = UnicodeExamples.CombiningGraphemeJoinerText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a CGJ followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the CGJ as a
        // wildcard. Literal() and Eof() default to FlattenType.Delete
        // so only the AnyToken's match surfaces in the symbol list.
        // Asserting its content verifies that the leading AnyToken
        // really is the CGJ alone (one cluster) and the lexer
        // didn't accidentally merge the CGJ into the following 'h'.
        var anyTokenResult = And(AnyToken(), Literal("hello"), Eof()).Parse(input);
        Assert.That(anyTokenResult.Success, Is.True);
        Assert.That(anyTokenResult.Symbols.Count, Is.EqualTo(1),
            "only the AnyToken surfaces; Literal('hello') deletes");
        Assert.That(anyTokenResult.Symbols[0].SourceText, Is.EqualTo(UnicodeExamples.CombiningGraphemeJoinerText),
            "the leading AnyToken matched exactly the CGJ, not CGJ+'h'");

        // (3) Targeting the CGJ specifically via Token(...) also
        // works.
        Assert.That(And(Token(UnicodeExamples.CombiningGraphemeJoinerText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    // ============================================================
    // Group 3: Default-ignorable / format / bidi characters
    //   expected: surfaces as a normal token, so grammar mismatches
    //   produce a normal error.
    // ============================================================

    [Test]
    public void BOM_at_start_of_input_is_consumed_as_one_token()
    {
        // U+FEFF BOM. NFC keeps it. Lexer reads as one token. The
        // parser doesn't strip BOMs. The caller does, or the grammar
        // accommodates with AnyToken or Token(BOM). Textbook BOM
        // gotcha: a strict grammar fails at offset 0.
        string input = (UnicodeExamples.ByteOrderMarkText + "hello");

        // (1) Strict grammar fails: Literal("hello") doesn't match a
        // BOM followed by 'h'.
        var strictResult = And(Literal("hello"), Eof()).Parse(input);
        Assert.That(strictResult.Success, Is.False);
        Assert.That(strictResult.ErrorCharIndex, Is.EqualTo(0));

        // (2) AnyToken at the front consumes the BOM as a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the BOM specifically via Token(...) also
        // works. The positive-match path is for grammars that want
        // to deliberately recognize and process a leading BOM.
        Assert.That(And(Token(UnicodeExamples.ByteOrderMarkText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void BOM_mid_stream_is_an_ordinary_token()
    {
        // BOM in the middle of input. Behaves just like start-of-input
        // BOM: ordinary token. A grammar matching letters with no
        // BOM accommodation fails at the BOM's position.
        string input = ("a" + UnicodeExamples.ByteOrderMarkText + "b");

        // (1) Strict a-b grammar fails at offset 1 where the BOM sits.
        var strictResult = And(Token('a'), Token('b'), Eof()).Parse(input);
        Assert.That(strictResult.Success, Is.False);
        Assert.That(strictResult.ErrorCharIndex, Is.EqualTo(1),
            "the BOM at position 1 isn't 'b'");

        // (2) AnyToken consumes the BOM as a wildcard between the
        // letters.
        Assert.That(And(Token('a'), AnyToken(), Token('b'), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the BOM specifically via Token(...) makes
        // the same input parse successfully.
        Assert.That(And(Token('a'), Token(UnicodeExamples.ByteOrderMarkText), Token('b'), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void No_break_space_between_letters_is_separate_token()
    {
        // U+00A0 NO-BREAK SPACE renders as a space but is its own code
        // point. UAX #29 classifies it as GCB=Other so it breaks on
        // both sides. .NET's Unicode category is Zs (Space_Separator),
        // the same as U+0020. The lexer sees three tokens
        // (a, NBSP, b), so a grammar matching "ab" with no NBSP
        // accommodation fails at the NBSP. Common gotcha when input is
        // pasted from a word processor or scraped from HTML where
        // ordinary spaces have been replaced with NBSP for layout
        // reasons.
        string input = "a" + UnicodeExamples.NoBreakSpaceText + "b";

        // (1) Strict a-b grammar fails at the NBSP.
        Assert.That(And(Token('a'), Token('b'), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken consumes the NBSP as a wildcard.
        Assert.That(And(Token('a'), AnyToken(), Token('b'), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the NBSP via Token(...) makes the same input
        // parse successfully. OneOf(TokenSet.Whitespace) would also
        // match it: NBSP is in the Zs category, which the .NET-derived
        // whitespace classification covers.
        Assert.That(And(Token('a'), Token(UnicodeExamples.NoBreakSpaceText), Token('b'), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Zero_width_space_between_letters_is_separate_token()
    {
        // U+200B ZERO WIDTH SPACE is classified by UAX #29 as a
        // character that breaks on both sides. The input is three
        // tokens (a, U+200B, b). A grammar matching "ab" without a
        // tolerance for U+200B fails.
        string input = ("a" + UnicodeExamples.ZeroWidthSpaceText + "b");

        // (1) Strict a-b grammar fails at the ZWS.
        Assert.That(And(Token('a'), Token('b'), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken consumes the ZWS as a wildcard between the
        // letters.
        Assert.That(And(Token('a'), AnyToken(), Token('b'), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the ZWS specifically via Token(...) makes
        // the same input parse successfully.
        Assert.That(And(Token('a'), Token(UnicodeExamples.ZeroWidthSpaceText), Token('b'), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Zero_width_non_joiner_between_letters_attaches_to_first_letter()
    {
        // U+200C ZERO WIDTH NON-JOINER is classified as extending
        // the previous character in UAX #29, so it joins to the
        // previous base. The input is two clusters: ("a" + ZWNJ)
        // is one cluster, and 'b' is the second.
        string input = ("a" + UnicodeExamples.ZeroWidthNonJoinerText + "b");

        // (1) Naive a-b grammar fails. The lexer hands back the
        // ("a" + ZWNJ) cluster as one token, so Token('a') doesn't
        // match, since it expects a token whose RuneValue is 'a' alone.
        Assert.That(And(Token('a'), Token('b'), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken consumes the ("a" + ZWNJ) cluster as a
        // wildcard, then Token('b') matches the second cluster.
        Assert.That(And(AnyToken(), Token('b'), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the multi-rune cluster as a single literal
        // via Token(string) also works. Token("a" + ZWNJ) matches
        // the exact two-rune cluster the lexer produces.
        Assert.That(And(Token("a" + UnicodeExamples.ZeroWidthNonJoinerText), Token('b'), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Soft_hyphen_between_letters_is_separate_token()
    {
        // U+00AD SOFT HYPHEN is classified by UAX #29 as a character
        // that breaks on both sides. The classic "looks like apple
        // but isn't apple" gotcha.
        string input = ("ap" + UnicodeExamples.SoftHyphenText + "ple");

        // (1) Naive grammar fails: Literal("apple") doesn't match
        // because the soft hyphen is between p and p.
        var naiveResult = Literal("apple").Parse(input);
        Assert.That(naiveResult.Success, Is.False,
            "the SOFT HYPHEN at offset 2 sits between p and p");

        // (2) AnyToken consumes the soft hyphen as a wildcard
        // between the two halves of the word.
        Assert.That(And(Literal("ap"), AnyToken(), Literal("ple"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the soft hyphen specifically via Token(...)
        // makes the same input parse successfully.
        Assert.That(And(Literal("ap"), Token(UnicodeExamples.SoftHyphenText), Literal("ple"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Bidi_rlo_inside_identifier_is_consumed_as_token()
    {
        // U+202E RIGHT-TO-LEFT OVERRIDE inside an identifier. The
        // parser doesn't apply Unicode's Bidirectional Algorithm,
        // so it just sees U+202E as one token of its own.
        // Identifier() rejects it because U+202E isn't an
        // identifier-continue character. This documents that the
        // parser isn't susceptible to Trojan-Source-style visual
        // reordering tricks: what the parser sees is what's in the
        // input characters, not what a renderer might display.
        string input = ("ab" + UnicodeExamples.RightToLeftOverrideText + "cd");

        // (1) Identifier() fails: U+202E ends the identifier at
        // offset 2.
        var identifierResult = And(Identifier(), Eof()).Parse(input);
        Assert.That(identifierResult.Success, Is.False,
            "U+202E isn't a valid identifier-continue character, " +
            "so the identifier ends at the bidi control");

        // (2) AnyToken consumes the RLO as a wildcard between the
        // two halves of the word.
        Assert.That(And(Literal("ab"), AnyToken(), Literal("cd"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the RLO specifically via Token(...) makes
        // the same input parse successfully. Useful if a grammar
        // wants to detect bidi controls and either flag them or
        // process around them.
        Assert.That(And(Literal("ab"), Token(UnicodeExamples.RightToLeftOverrideText), Literal("cd"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Tag_character_outside_emoji_is_one_token()
    {
        // U+E0001 LANGUAGE TAG. Used inside emoji tag sequences for
        // subdivision flags. UAX #29 GCB=Extend, but at start of
        // text the orphan ends up as its own one-character cluster.
        string input = UnicodeExamples.LanguageTagText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a tag character followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the tag character as
        // a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the tag character specifically via
        // Token(...) also works.
        Assert.That(And(Token(UnicodeExamples.LanguageTagRune), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Mongolian_vowel_separator_documented()
    {
        // U+180E MONGOLIAN VOWEL SEPARATOR. Property has changed
        // across Unicode versions (was Cf, became Whitespace, now
        // back to Cf depending on the runtime's Unicode data). The
        // parser doesn't crash, but exact behavior of OneOf(Letters)
        // and similar is runtime-dependent.
        string input = UnicodeExamples.MongolianVowelSeparatorText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a Mongolian vowel separator followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes it as a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting it specifically via Token(...) also works.
        Assert.That(And(Token(UnicodeExamples.MongolianVowelSeparatorText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Line_separator_U_2028_is_a_token_not_matched_by_Token_LF()
    {
        // U+2028 LINE SEPARATOR. UTS #18 line terminator, but a
        // distinct rune from LF. The famous ECMAScript / JSON
        // mismatch bug: pre-ES2019 JavaScript string literals
        // disallowed U+2028 and U+2029 as unescaped characters
        // while JSON allowed them, so JSONP responses containing
        // these characters in user-generated content produced
        // "Unexpected token ILLEGAL" errors in the browser. ES2019
        // aligned the JS string grammar with JSON. The parser
        // treats U+2028 as one ordinary token. A line-based
        // grammar matching Token('\n') doesn't catch it, but
        // EndOfLine() (which uses TokenSet.LineTerminators) does.
        string input = "a" + UnicodeExamples.LineSeparatorText + "b";

        // (1) Naive Token('\n') doesn't catch the line separator
        // (different rune from U+000A).
        Assert.That(And(Token('a'), Token('\n'), Token('b'), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken consumes U+2028 as a wildcard between the
        // letters. Token('a') / Token('b') / Eof() default to
        // FlattenType.Delete so only the AnyToken match surfaces
        // in the symbol list. Asserting its content verifies that
        // the middle cluster really is U+2028 (not silently dropped
        // or remapped).
        var anyTokenResult = And(Token('a'), AnyToken(), Token('b'), Eof()).Parse(input);
        Assert.That(anyTokenResult.Success, Is.True);
        Assert.That(anyTokenResult.Symbols.Count, Is.EqualTo(1),
            "only the AnyToken surfaces; the surrounding Tokens delete");
        Assert.That(anyTokenResult.Symbols[0].SourceText, Is.EqualTo(UnicodeExamples.LineSeparatorText),
            "the AnyToken between 'a' and 'b' matched exactly U+2028");

        // (3) EndOfLine() recognizes U+2028 as a line terminator.
        // EndOfLine defaults to FlattenType.Delete so it doesn't
        // surface a symbol, but the structural shape of the
        // grammar (Token('a') ... Token('b'), Eof()) only succeeds
        // if EndOfLine consumed exactly the U+2028 cluster: any
        // other consumption would leave 'b' / Eof mismatching.
        Assert.That(And(Token('a'), EndOfLine(), Token('b'), Eof()).Parse(input).Success, Is.True,
            "EndOfLine matched the U+2028 char specifically; " +
            "any other consumption would fail Token('b') or Eof");
    }

    [Test]
    public void Paragraph_separator_U_2029_is_a_token_not_matched_by_Token_LF()
    {
        // U+2029 PARAGRAPH SEPARATOR. Sibling of U+2028 with the
        // same JSON / JavaScript history. Same parser behavior:
        // one ordinary token, Token('\n') doesn't catch it,
        // EndOfLine() does.
        string input = "a" + UnicodeExamples.ParagraphSeparatorText + "b";

        Assert.That(And(Token('a'), Token('\n'), Token('b'), Eof()).Parse(input).Success, Is.False);

        var anyTokenResult = And(Token('a'), AnyToken(), Token('b'), Eof()).Parse(input);
        Assert.That(anyTokenResult.Success, Is.True);
        Assert.That(anyTokenResult.Symbols[0].SourceText, Is.EqualTo(UnicodeExamples.ParagraphSeparatorText),
            "the AnyToken between 'a' and 'b' matched exactly U+2029");

        Assert.That(And(Token('a'), EndOfLine(), Token('b'), Eof()).Parse(input).Success, Is.True,
            "EndOfLine matched the U+2029 char specifically");
    }

    [Test]
    public void Next_line_U_0085_is_a_token_not_matched_by_Token_LF()
    {
        // U+0085 NEXT LINE (NEL). C1 control imported from EBCDIC
        // for round-tripping with IBM mainframe text. UTS #18
        // line terminator. Real-world quirk: Java's BufferedReader
        // treats NEL as a line terminator on some JVMs but not
        // others, and XML 1.1 added it to the newline list while
        // XML 1.0 omitted it. The parser treats NEL as one
        // ordinary token. Token('\n') doesn't catch it,
        // EndOfLine() does.
        string input = "a" + UnicodeExamples.NextLineText + "b";

        Assert.That(And(Token('a'), Token('\n'), Token('b'), Eof()).Parse(input).Success, Is.False);

        var anyTokenResult = And(Token('a'), AnyToken(), Token('b'), Eof()).Parse(input);
        Assert.That(anyTokenResult.Success, Is.True);
        Assert.That(anyTokenResult.Symbols[0].SourceText, Is.EqualTo(UnicodeExamples.NextLineText),
            "the AnyToken between 'a' and 'b' matched exactly U+0085");

        Assert.That(And(Token('a'), EndOfLine(), Token('b'), Eof()).Parse(input).Success, Is.True,
            "EndOfLine matched the NEL char specifically");
    }

    [Test]
    public void Bidi_isolate_LRI_inside_identifier_is_consumed_as_token()
    {
        // U+2066 LEFT-TO-RIGHT ISOLATE (LRI). One of the Unicode
        // 6.3 bidi isolate controls (LRI, RLI U+2067, FSI U+2068,
        // PDI U+2069). The Trojan Source paper (CVE-2021-42574)
        // demonstrated source-code attacks using the directional
        // formatting characters, including the newer isolates
        // alongside the older RLO covered above. Same safety
        // story as RLO: the parser doesn't apply Unicode's
        // Bidirectional Algorithm, so what the grammar sees is
        // the actual character sequence in the input, not what a
        // bidi-aware renderer might display.
        string input = "ab" + UnicodeExamples.LeftToRightIsolateText + "cd";

        // (1) Identifier() fails: U+2066 isn't an identifier-
        // continue character, so the identifier ends at the bidi
        // control.
        var identifierResult = And(Identifier(), Eof()).Parse(input);
        Assert.That(identifierResult.Success, Is.False,
            "U+2066 isn't a valid identifier-continue character, " +
            "so the identifier ends at the bidi isolate");

        // (2) AnyToken consumes the LRI as a wildcard between the
        // two halves of the word. Literal() defaults to
        // FlattenType.Delete so the surrounding "ab" / "cd"
        // matches don't surface. The only top-level symbol is
        // the AnyToken match. Asserting its content verifies that
        // the consumed cluster really is U+2066 and not some
        // bidi-aware reorder.
        var anyTokenResult = And(Literal("ab"), AnyToken(), Literal("cd"), Eof()).Parse(input);
        Assert.That(anyTokenResult.Success, Is.True);
        Assert.That(anyTokenResult.Symbols.Count, Is.EqualTo(1),
            "only the AnyToken surfaces; the surrounding Literals delete");
        Assert.That(anyTokenResult.Symbols[0].SourceText, Is.EqualTo(UnicodeExamples.LeftToRightIsolateText),
            "the AnyToken between 'ab' and 'cd' matched exactly U+2066");

        // (3) Targeting the LRI specifically via Token(...) makes
        // the same input parse successfully. Useful for grammars
        // that want to detect bidi controls explicitly and either
        // flag them or process around them.
        Assert.That(And(Literal("ab"), Token(UnicodeExamples.LeftToRightIsolateText), Literal("cd"), Eof()).Parse(input).Success, Is.True);
    }

    // ============================================================
    // Group 4: Noncharacters, Private Use, Replacement
    //   expected: surfaces as a normal token, so grammar mismatches
    //   produce a normal error (one .NET-specific case, U+FFFE,
    //   surfaces as a MalformedInput result instead).
    // ============================================================

    [Test]
    public void Noncharacter_FFFE_handling()
    {
        // U+FFFE is a Unicode noncharacter. .NET's string.Normalize
        // rejects FFFE specifically as "invalid Unicode code points".
        // Under default FormC the parser calls Normalize before the
        // lexer ever runs. Rather than let that ArgumentException
        // escape, Parse returns a MalformedInput result. Compile(null)
        // skips normalization, the noncharacter survives as one token,
        // and grammars that need to handle it follow the same 4-part
        // pattern as the other noncharacter tests.
        string input = UnicodeExamples.NoncharacterFFFEText + "hello";

        // (0) Default FormC reports MalformedInput at the U+FFFE
        // (index 0) instead of throwing. Callers whose input might
        // contain U+FFFE either strip it upstream or compile with null.
        var malformed = And(Literal("hello"), Eof()).Parse(input);
        Assert.That(malformed.Outcome, Is.EqualTo(ParseOutcome.MalformedInput));
        Assert.That(malformed.ErrorCharIndex, Is.EqualTo(0));

        // (1) Compile(null) + naive grammar fails normally:
        // Literal("hello") doesn't match a noncharacter followed
        // by 'h'.
        var naiveRule = And(Literal("hello"), Eof());
        naiveRule.Compile(null);
        Assert.That(naiveRule.Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the noncharacter as
        // a wildcard.
        var anyTokenRule = And(AnyToken(), Literal("hello"), Eof());
        anyTokenRule.Compile(null);
        Assert.That(anyTokenRule.Parse(input).Success, Is.True);

        // (3) Targeting U+FFFE specifically via Token(...) also
        // works.
        var targetedRule = And(Token(UnicodeExamples.NoncharacterFFFEText), Literal("hello"), Eof());
        targetedRule.Compile(null);
        Assert.That(targetedRule.Parse(input).Success, Is.True);
    }

    [Test]
    public void Noncharacter_FFFF_is_treated_as_an_ordinary_token()
    {
        // U+FFFF is also a Unicode noncharacter. Surprisingly, .NET's
        // Normalize accepts it (only U+FFFE specifically gets the
        // strict "invalid Unicode code points" rejection). So FFFF
        // reaches the lexer as a one-token grapheme. Locks in the
        // FFFE-vs-FFFF asymmetry so a future .NET runtime change
        // would surface here.
        string input = UnicodeExamples.NoncharacterFFFFText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a noncharacter followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the noncharacter as
        // a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+FFFF specifically via Token(...) also
        // works.
        Assert.That(And(Token(UnicodeExamples.NoncharacterFFFFText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Noncharacter_FDD0_is_treated_as_an_ordinary_token()
    {
        // U+FDD0..U+FDEF is the second block of Unicode noncharacters.
        // Unlike FFFE / FFFF, .NET's Normalize accepts these (the
        // strict check only rejects the FFFE / FFFF pattern in each
        // plane). So FDD0 reaches the lexer under default FormC.
        // Asymmetry is .NET's, not ours.
        string input = UnicodeExamples.NoncharacterFDD0Text + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a noncharacter followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the noncharacter as
        // a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+FDD0 specifically via Token(...) also
        // works.
        Assert.That(And(Token(UnicodeExamples.NoncharacterFDD0Text), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Private_use_area_character_is_an_ordinary_token()
    {
        // U+E000 starts the BMP Private Use Area. Valid scalar, no
        // assigned character. NFC keeps it. Lexer reads as one token.
        // OneOf(Letters) won't match because PUA characters have no
        // category data.
        string input = UnicodeExamples.PrivateUseAreaStartText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a PUA character followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the PUA character as
        // a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+E000 specifically via Token(...) also
        // works. Useful for a private protocol that assigns
        // meaning to a PUA range.
        Assert.That(And(Token(UnicodeExamples.PrivateUseAreaStartText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Replacement_character_FFFD_is_an_ordinary_token()
    {
        // U+FFFD REPLACEMENT CHARACTER is what permissive decoders
        // emit when they see invalid bytes. By the time it reaches
        // the parser it's a perfectly valid scalar, and the parser treats
        // it as one token. If you see U+FFFD in input, that's a
        // signal an upstream decoder swallowed something malformed.
        string input = UnicodeExamples.ReplacementCharacterText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a U+FFFD followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the replacement
        // character as a wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+FFFD specifically via Token(...) also
        // works. Useful for grammars that want to deliberately
        // detect and surface decoder-replacement markers in their
        // output.
        Assert.That(And(Token(UnicodeExamples.ReplacementCharacterText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Replacement_character_from_decoder_is_detected_by_TokenSet_Replacement()
    {
        // End-to-end test of the .NET decoder -> U+FFFD -> grammar
        // chain. When .NET's Unicode-encoding decoders hit an
        // ill-formed byte sequence, the default
        // DecoderReplacementFallback substitutes U+FFFD into the
        // output string. A grammar that wants to surface or reject
        // those substitutions can use OneOf(TokenSet.Replacement).

        // Ill-formed UTF-8: 0xFF is never a valid UTF-8 lead byte,
        // so the decoder substitutes U+FFFD for it.
        byte[] illFormedUtf8 = [0x68, 0x65, 0xFF, 0x6C, 0x6C, 0x6F];
        string fromUtf8 = Encoding.UTF8.GetString(illFormedUtf8);
        Assert.That(fromUtf8, Does.Contain(UnicodeExamples.ReplacementCharacterText),
            "UTF-8 decoder should substitute U+FFFD for the ill-formed 0xFF byte");

        // Ill-formed UTF-16 LE: an odd byte count leaves a
        // dangling single byte that can't be paired into a code
        // unit. The decoder substitutes U+FFFD for the orphan.
        byte[] illFormedUtf16 = [0x68, 0x00, 0x65, 0x00, 0xFF];
        string fromUtf16 = Encoding.Unicode.GetString(illFormedUtf16);
        Assert.That(fromUtf16, Does.Contain(UnicodeExamples.ReplacementCharacterText),
            "UTF-16 decoder should substitute U+FFFD for the dangling byte");

        // Grammar: scan past any non-replacement tokens, match
        // exactly one U+FFFD via TokenSet.Replacement, then consume
        // the rest. Useful for "reject any input that's been
        // through a permissive decoder" patterns.
        var rule = And(
            ZeroOrMore(NoneOf(TokenSet.Replacement)),
            OneOf(TokenSet.Replacement),
            ZeroOrMore(AnyToken()),
            Eof());

        Assert.That(rule.Parse(fromUtf8).Success, Is.True,
            "TokenSet.Replacement matches the U+FFFD from the UTF-8 decoder");
        Assert.That(rule.Parse(fromUtf16).Success, Is.True,
            "TokenSet.Replacement matches the U+FFFD from the UTF-16 decoder");

        // Negative case: clean input has no U+FFFD, so the OneOf
        // step has nothing to match and the grammar fails.
        Assert.That(rule.Parse("hello").Success, Is.False,
            "TokenSet.Replacement has nothing to match in clean input");
    }

    // ============================================================
    // Group 6: Identifier-relevant edge cases
    //   expected: the default Identifier rule has no mixed-script
    //   anti-spoofing logic. Restrict via a custom script-bounded
    //   TokenSet if you care about UTS #39 homoglyph attacks.
    //   (Other identifier behaviors, like non-ASCII digits in
    //   TokenSet.Digits and all five Letter subcategories in
    //   TokenSet.Letters, are covered in TokenSetTests rather
    //   than here.)
    // ============================================================

    [Test]
    public void Mixed_script_identifier_is_accepted_by_default_Identifier()
    {
        // Latin 'a' (U+0061) followed by Cyrillic small letter a
        // (U+0430). Both are XidContinue characters per UAX #31, so
        // Identifier() accepts the mixed-script string. Documents
        // that the default has no mixed-script restriction. If you
        // care about homoglyph spoofing (UTS #39), build a script-
        // restricted TokenSet manually. The "fix" side (a custom
        // LatinLetters set rejecting Cyrillic 'а') is shown in
        // UnicodeGotchasExamples.Homoglyph_LatinLetters_set_rejects_Cyrillic_a.
        var rule = And(Identifier(), Eof());
        string input = "a" + UnicodeExamples.CyrillicSmallAGrapheme;
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void LiteralIgnoreAsciiCase_doesnt_fold_Turkish_dotted_or_dotless_I()
    {
        // The famous internationalization bug. Under Turkish
        // locale rules, ToUpper("i") is "İ" (U+0130, capital I
        // with dot above) and ToLower("I") is "ı" (U+0131,
        // dotless small i), not the ASCII forms. Locale-aware
        // case-insensitive comparisons therefore disagree
        // depending on the user's system locale. It hurt Spotify
        // in 2013 (Turkish iOS users couldn't log in if their
        // email had 'I' in it), and it affects .NET Framework's
        // String.Compare without an explicit culture, Win32
        // CompareString, Java's String.toLowerCase, and many
        // others.
        //
        // The parser's LiteralIgnoreAsciiCase is ASCII-only by
        // design specifically to avoid this. ASCII 'I' matches only
        // ASCII 'i'. U+0130 and U+0131 are their own runes that
        // don't participate in the case-insensitive match in
        // either direction.
        // A grammar matching LiteralIgnoreAsciiCase("size") on
        // input containing Turkish dotted I or dotless i fails
        // normally, same as any other unrecognized rune.
        var rule = LiteralIgnoreAsciiCase("size").Compile();

        // ASCII case-insensitive matching works as expected.
        Assert.That(rule.Parse("size").Success, Is.True);
        Assert.That(rule.Parse("SIZE").Success, Is.True);
        Assert.That(rule.Parse("Size").Success, Is.True);

        // Turkish dotted I (U+0130) doesn't match ASCII 'i' under the
        // ASCII-only case-insensitive rule.
        string turkishCapitalI = "s" + UnicodeExamples.TurkishCapitalIWithDotGrapheme + "ze";
        Assert.That(rule.Parse(turkishCapitalI).Success, Is.False,
            $"{UnicodeExamples.TurkishCapitalIWithDotGrapheme} (U+0130) is its own rune; " +
            "the ASCII-only case-insensitive compare doesn't treat it as 'i'");

        // Turkish dotless i (U+0131) doesn't match ASCII 'I' under the
        // ASCII-only case-insensitive rule.
        string turkishSmallDotlessI = "s" + UnicodeExamples.TurkishSmallDotlessIGrapheme + "ze";
        Assert.That(rule.Parse(turkishSmallDotlessI).Success, Is.False,
            $"{UnicodeExamples.TurkishSmallDotlessIGrapheme} (U+0131) is its own rune; " +
            "the ASCII-only case-insensitive compare doesn't treat it as 'i' either");
    }

    [Test]
    public void Cherokee_letter_passes_Identifier_but_Latin_only_TokenSet_rejects_it()
    {
        // U+13A0 CHEROKEE LETTER A renders as a glyph that
        // resembles Latin capitals in common fonts. Microsoft's
        // 2018 phishing analysis documented hostnames mixing
        // Cherokee letters with Latin to spoof legitimate names
        // (the Cherokee letter has category Lo, in XID_Start, so
        // identifier rules let it through). Same shape as the
        // Cyrillic 'а' homoglyph case above: the default
        // Identifier() accepts the mixed-script input, and the
        // fix is a script-restricted TokenSet that rejects
        // runes outside the desired script. The Latin-only set
        // built from Ascii.Letters + Latin-1 Supplement is the
        // same one shown in
        // UnicodeGotchasExamples.Homoglyph_LatinLetters_set_rejects_Cyrillic_a.
        string input = "Apple" + UnicodeExamples.CherokeeLetterAGrapheme + "Sauce";

        // (1) Default Identifier() accepts the mixed-script input
        // because Cherokee letters are XID_Continue.
        Assert.That(And(Identifier(), Eof()).Parse(input).Success, Is.True,
            $"default Identifier accepts {UnicodeExamples.CherokeeLetterAGrapheme} (Cherokee letter A)");

        // (2) Latin-only TokenSet rejects the Cherokee letter.
        var latinLetters =
            (TokenSet.Ascii.Letters | TokenSet.Range(new System.Text.Rune(0x00C0), new System.Text.Rune(0x00FF)))
            & TokenSet.Letters;
        var latinOnly = And(OneOrMore(OneOf(latinLetters)), Eof()).Compile();
        Assert.That(latinOnly.Parse(input).Success, Is.False,
            $"Latin-only set rejects {UnicodeExamples.CherokeeLetterAGrapheme} (Cherokee A)");
        Assert.That(latinOnly.Parse("AppleSauce").Success, Is.True,
            "Latin-only set accepts plain ASCII unchanged");
    }

    [Test]
    public void Greek_final_sigma_and_regular_sigma_are_different_tokens()
    {
        // U+03C2 GREEK SMALL LETTER FINAL SIGMA and U+03C3 GREEK
        // SMALL LETTER SIGMA are different runes, but to a Greek
        // reader they're the same letter: the final form only
        // appears at the end of a word, and Unicode treats them
        // as case-equivalent to the same uppercase Σ. Real-world
        // Greek search bug: software that compares text rune-by-
        // rune misses "πῶς" (with final sigma) for a query of
        // "πῶσ" (with regular sigma). The parser treats the two
        // as distinct: a grammar matching one doesn't accept the
        // other. Grammars that want either-sigma matching build a
        // TokenSet that includes both runes explicitly.
        var sigmaRule = And(Token(UnicodeExamples.GreekSmallSigmaGrapheme), Eof());
        var finalSigmaRule = And(Token(UnicodeExamples.GreekSmallFinalSigmaGrapheme), Eof());

        // Each Token matches only its own rune.
        Assert.That(sigmaRule.Parse(UnicodeExamples.GreekSmallSigmaGrapheme).Success, Is.True);
        Assert.That(sigmaRule.Parse(UnicodeExamples.GreekSmallFinalSigmaGrapheme).Success, Is.False,
            $"Token({UnicodeExamples.GreekSmallSigmaGrapheme}) doesn't match the final-sigma form " +
            $"{UnicodeExamples.GreekSmallFinalSigmaGrapheme} (different rune)");

        Assert.That(finalSigmaRule.Parse(UnicodeExamples.GreekSmallFinalSigmaGrapheme).Success, Is.True);
        Assert.That(finalSigmaRule.Parse(UnicodeExamples.GreekSmallSigmaGrapheme).Success, Is.False,
            $"Token({UnicodeExamples.GreekSmallFinalSigmaGrapheme}) doesn't match the regular-sigma form " +
            $"{UnicodeExamples.GreekSmallSigmaGrapheme} (different rune)");

        // Either-sigma matching: a TokenSet that includes both
        // runes is the documented fix shape.
        var eitherSigma = And(OneOf(TokenSet.Single(0x03C2) | TokenSet.Single(0x03C3)), Eof());
        Assert.That(eitherSigma.Parse(UnicodeExamples.GreekSmallSigmaGrapheme).Success, Is.True);
        Assert.That(eitherSigma.Parse(UnicodeExamples.GreekSmallFinalSigmaGrapheme).Success, Is.True);
    }

    // ============================================================
    // Group 7: Trivial / defensive
    //   expected: parser doesn't crash, regardless of shape or
    //   length.
    // ============================================================

    [Test]
    public void Empty_input_against_AnyToken_fails_with_EOF()
    {
        var rule = AnyToken();
        var result = rule.Parse("");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }

    [Test]
    public void Empty_input_against_Eof_succeeds()
    {
        var rule = Eof();
        var result = rule.Parse("");

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void Null_byte_is_an_ordinary_token()
    {
        // U+0000 is a valid scalar. Lexer treats like any other rune.
        string input = UnicodeExamples.NullText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a NULL byte followed by 'h'.
        Assert.That(And(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the NULL byte as a
        // wildcard.
        Assert.That(And(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+0000 specifically via Token(...) also
        // works. Useful for a binary-friendly format that uses
        // NUL as a separator.
        Assert.That(And(Token(UnicodeExamples.NullText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Null_byte_doesnt_match_letters_or_digits()
    {
        // U+0000 is Cc (Control), not Letter or Digit.
        var rule = OneOf(TokenSet.Letters | TokenSet.Digits);
        var result = rule.Parse(UnicodeExamples.NullText);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Parser_doesnt_crash_on_long_run_of_combining_marks()
    {
        // 100 combining acutes in a row. UAX #29 bundles them into
        // one cluster (no base, all Extend). One big token.
        var rule = AnyToken();
        string input = new string(UnicodeExamples.CombiningAcuteText[0], 100);
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void Parser_doesnt_crash_on_alternating_zero_width_joiner_and_emoji()
    {
        // Three man emoji separated by Zero Width Joiners. Forms one
        // joined-emoji cluster under UAX #29.
        var rule = AnyToken();
        string input = UnicodeExamples.ManEmojiGrapheme + UnicodeExamples.ZeroWidthJoinerText
            + UnicodeExamples.ManEmojiGrapheme + UnicodeExamples.ZeroWidthJoinerText
            + UnicodeExamples.ManEmojiGrapheme;
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void Parser_doesnt_crash_on_dangling_zero_width_joiner_at_end_of_input()
    {
        // Man emoji + Zero Width Joiner at end of input, no following
        // base. UAX #29 includes the trailing joiner in the man's
        // cluster (extending rule).
        var rule = And(AnyToken(), Eof());
        string input = UnicodeExamples.ManEmojiGrapheme + UnicodeExamples.ZeroWidthJoinerText;
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public void England_flag_tag_sequence_is_one_token()
    {
        // The England flag emoji is encoded as a UAX #29 emoji
        // tag sequence (GB10): WAVING BLACK FLAG U+1F3F4,
        // followed by tag characters for the ISO 3166-2
        // subdivision code "gbeng", terminated by CANCEL TAG
        // U+E007F. Seven runes total, 14 UTF-16 chars. UAX #29
        // keeps the whole sequence as a single grapheme cluster:
        // each tag character is GCB=Extend and the cluster
        // extends from the base black flag through the cancel
        // tag. Common parser bug: software that doesn't know
        // about tag sequences treats the base flag and each tag
        // rune as separate clusters, then strips or rejects the
        // tag chars because they have no rendering on their own,
        // which loses the subdivision information.
        string input = UnicodeExamples.EnglandFlagGrapheme;

        // (1) The whole sequence is exactly one cluster: one
        // AnyToken consumes it and Eof follows immediately. The
        // Symbols / ToString assertions verify that the matched
        // cluster really is the full 14-UTF-16-char sequence,
        // not just that some prefix matched.
        var anyTokenResult = And(AnyToken(), Eof()).Parse(input);
        Assert.That(anyTokenResult.Success, Is.True,
            "the full England flag tag sequence is a single cluster");
        Assert.That(anyTokenResult.ToString(), Is.EqualTo(input),
            "matched text covers every UTF-16 char of the input");
        Assert.That(anyTokenResult.Symbols.Count, Is.EqualTo(1),
            "exactly one cluster, not seven runes or two halves");
        Assert.That(anyTokenResult.Symbols[0].SourceText, Is.EqualTo(UnicodeExamples.EnglandFlagGrapheme),
            "the single cluster's content is the entire tag sequence");

        // (2) Two AnyTokens would need two clusters, but there's
        // only one. The second AnyToken sees Eof and fails.
        Assert.That(And(AnyToken(), AnyToken(), Eof()).Parse(input).Success, Is.False,
            "the sequence is one cluster, not two");

        // (3) Targeting the whole sequence via Token(string)
        // matches the multi-rune cluster the lexer produces.
        Assert.That(And(Token(UnicodeExamples.EnglandFlagGrapheme), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Three_consecutive_regional_indicators_form_a_pair_plus_lone_third()
    {
        // UAX #29 GB12 / GB13 group regional indicators into
        // pairs from the left: every (RI RI) pair is glued
        // together, and the next RI after a closed pair starts a
        // fresh cluster. "U + S + F" therefore splits into the
        // US flag (U + S) and a lone trailing F, not into one
        // garbled three-letter cluster. Common parser bug:
        // assuming any sequence of regional indicators is one cluster
        // and rendering them as a single (invalid) flag, or
        // attempting to interpret the third indicator as part of
        // the country code instead of the start of something
        // new.
        string input = UnicodeExamples.RegionalIndicatorUText
            + UnicodeExamples.RegionalIndicatorSText
            + UnicodeExamples.RegionalIndicatorFText;

        // (1) Exactly two clusters: the US flag pair, then the
        // lone F. Two AnyToken matches consume the whole input.
        // Verifying the matched text per-symbol pins which two
        // clusters the lexer produced, not just that there were two
        // of something.
        var twoTokens = And(AnyToken(), AnyToken(), Eof()).Parse(input);
        Assert.That(twoTokens.Success, Is.True,
            "three RIs split as pair + lone third per UAX #29");
        Assert.That(twoTokens.ToString(), Is.EqualTo(input),
            "matched text equals the input character-for-character");
        Assert.That(twoTokens.Symbols.Count, Is.EqualTo(2),
            "exactly two clusters per UAX #29 GB12/GB13");
        Assert.That(twoTokens.Symbols[0].SourceText, Is.EqualTo(UnicodeExamples.USFlagGrapheme),
            "first cluster is the U+S regional-indicator pair (US flag)");
        Assert.That(twoTokens.Symbols[1].SourceText, Is.EqualTo(UnicodeExamples.RegionalIndicatorFText),
            "second cluster is the lone F regional indicator");

        // (2) Three AnyToken positions need three clusters, but
        // there are only two, so the third AnyToken sees Eof.
        Assert.That(And(AnyToken(), AnyToken(), AnyToken(), Eof()).Parse(input).Success, Is.False,
            "three AnyToken positions need three clusters; the pair counts as one");

        // (3) Targeting the pair as a single cluster via
        // Token(string) matches the US flag, then the lone F is
        // its own one-rune token.
        Assert.That(And(Token(UnicodeExamples.USFlagGrapheme),
                        Token(UnicodeExamples.RegionalIndicatorFText),
                        Eof()).Parse(input).Success, Is.True);
    }
}
