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
//   1. ENCODING-LEVEL ILL-FORMED INPUT: throws cleanly or surfaces
//      as an untyped token, never silently corrupts.
//      These are (the only) cases truly defined as "ill formed" 
//      by the Unicode standard and are all examples where 
//      UTF-16 invariants are broken: lone surrogates, reversed surrogate
//      pairs, the U+10FFFF maximum boundary. Only UTF-16 here because
//      the parser takes a .NET string, and .NET strings are UTF-16
//      internally. UTF-8 / UTF-32 / legacy-codepage decoding errors
//      get resolved upstream by the caller's Encoding.GetString call
//      (usually as U+FFFD replacements; see Group 4) before the
//      parser is ever invoked. Under default FormC, .NET's
//      string.Normalize rejects ill-formed UTF-16 by throwing
//      ArgumentException out of Parse() — the caller sees the
//      problem before any grammar runs. Under Compile(null) the
//      lexer surfaces each ill-formed code unit as a token with no
//      RuneValue, which OneOf and similar rules predictably reject.
//      Either path is safe; a grammar can't silently match a
//      ill-formed surrogate.
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
//      (one .NET-specific exception throws cleanly).
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
//      containing U+FFFE throws ArgumentException out of Parse().
//      Why U+FFFE and not the other noncharacters: U+FFFE is the
//      byte-swapped form of U+FEFF (BOM), so its presence in a
//      string is a signal that upstream byte-order detection
//      failed. Windows' NormalizeString refuses to process such
//      strings on that theory. U+FFFF and U+FDD0..U+FDEF carry
//      no such signal and pass through.
//      The caller can catch and translate, or compile with null to skip
//      normalization. U+FFFF, U+FDD0, the rest of the Private Use
//      Area, and U+FFFD all pass through as ordinary tokens.
//
//   5. NORMALIZATION EDGE CASES: compile-time normalization checks
//      ensure literals match the chosen form; see NormalizationTests.cs
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
        // ever runs, so any grammar throws ArgumentException.
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

        // (0) Default FormC throws out of Parse before the lexer
        // runs.
        Assert.Throws<ArgumentException>(() => And(Literal("hello"), Eof()).Parse(input));

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

        // (0) Default FormC throws.
        Assert.Throws<ArgumentException>(() => And(Literal("hello"), Eof()).Parse(input));

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
        // Universe = ~default(TokenSet) is [0, 0xD7FF] union [0xE000,
        // 0x10FFFF]: the complement operator splits around the surrogate
        // block, so Universe doesn't include surrogates by construction.
        // A lone surrogate token therefore doesn't match Universe —
        // not because of the membership logic, but because the surrogate
        // isn't in the set. Compare with the Range(0, 0x10FFFF) test
        // below which explicitly does include the surrogate block and
        // does match a stray surrogate.
        var rule = OneOf(TokenSet.Universe);
        rule.Compile(null);
        string input = UnicodeExamples.HighSurrogateMinText;
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void OneOf_with_range_including_surrogates_matches_lone_high_surrogate()
    {
        // Range(0, 0x10FFFF) is documented to include the surrogate gap
        // as legal-but-rarely-useful interior of the interval (see
        // TokenSet.Range docs around TokenSet.cs:363-371). Under
        // Compile(null) a stray surrogate is a valid one-char token. The
        // OneOf membership check disambiguates the three RuneValue == -1
        // cases (EOF / multi-rune / stray surrogate) and queries the
        // rune intervals using the surrogate's UTF-16 code unit value
        // for the surrogate case, so a user-typed Range that includes
        // surrogate code points correctly matches them. The motivating
        // use case is WTF-8 / unpaired-surrogate round-tripping.
        var rule = OneOf(TokenSet.Range(0, 0x10FFFF));
        rule.Compile(null);

        Assert.That(rule.Parse(UnicodeExamples.HighSurrogateMinText).Success, Is.True,
            "Range(0, 0x10FFFF) includes the high surrogate min; should match it.");
        Assert.That(rule.Parse(UnicodeExamples.LowSurrogateMaxText).Success, Is.True,
            "Range(0, 0x10FFFF) includes the low surrogate max; should match it.");
        Assert.That(rule.Parse("a").Success, Is.True,
            "Range(0, 0x10FFFF) still matches ordinary scalars.");
    }

    [Test]
    public void NoneOf_with_range_including_surrogates_rejects_lone_surrogate()
    {
        // The mirror case for NoneOf. Without the surrogate-aware
        // membership check, NoneOf wrongly accepted lone surrogates
        // even when the user's set explicitly negated them: the
        // membership probe returned false (because RuneValue was -1
        // and the rune intervals weren't queried), NoneOf inverted
        // false to true, and the false-positive match shipped. The
        // surrogate-aware check fixes both directions in lockstep.
        var rule = And(NoneOf(TokenSet.Range(0, 0x10FFFF)), Eof());
        rule.Compile(null);

        Assert.That(rule.Parse(UnicodeExamples.HighSurrogateMinText).Success, Is.False,
            "NoneOf(range that includes surrogates) should reject a high surrogate.");
        Assert.That(rule.Parse(UnicodeExamples.LowSurrogateMaxText).Success, Is.False,
            "NoneOf(range that includes surrogates) should reject a low surrogate.");
        Assert.That(rule.Parse("a").Success, Is.False,
            "NoneOf(Range(0, 0x10FFFF)) covers everything; rejects ASCII too.");
    }

    [Test]
    public void OneOf_with_range_excluding_surrogates_still_rejects_lone_surrogate()
    {
        // Sanity counter-test. A user-typed Range that doesn't include
        // surrogates (the typical case — Range('a','z'), Letters, etc.)
        // should still reject lone-surrogate input. The fix only adds a
        // new path; it doesn't change how non-surrogate-bearing sets
        // behave.
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
        // Lock in that the parser does NOT silently reorder the two
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
        // U+10FFFF fails normally at the orphan; the positive-match
        // path is just for grammars that want to deliberately do
        // something with this exact code point.
        Assert.That(And(Token(UnicodeExamples.MaximumCodePointRune), Eof()).Parse(input).Success, Is.True);
    }

    // Coverage matrix: every non-null normalization form routes
    // input through String.Normalize before the lexer runs, and
    // Normalize rejects ill-formed UTF-16 by throwing
    // ArgumentException. So all four non-null forms produce the
    // same Parse-time throw on lone surrogates and reversed pairs.
    // The earlier per-input tests (Lone_high_surrogate_handling
    // etc.) cover only FormC; this parameterized test fills the
    // FormD / FormKC / FormKD gap so a future runtime change that
    // diverged any of them from FormC would surface here.
    //
    // Inputs are passed as int[] code points and built into the
    // string inside the test, because NUnit's [TestCase] attribute
    // serializes parameters in a way that drops or reinterprets
    // lone surrogates. Building from code points side-steps that.
    [TestCase(NormalizationForm.FormC, new[] { 0xD800 }, TestName = "FormC + lone high surrogate U+D800")]
    [TestCase(NormalizationForm.FormC, new[] { 0xDFFF }, TestName = "FormC + lone low surrogate U+DFFF")]
    [TestCase(NormalizationForm.FormC, new[] { 0xDC00, 0xD800 }, TestName = "FormC + reversed surrogate pair (low,high)")]
    [TestCase(NormalizationForm.FormD, new[] { 0xD800 }, TestName = "FormD + lone high surrogate U+D800")]
    [TestCase(NormalizationForm.FormD, new[] { 0xDFFF }, TestName = "FormD + lone low surrogate U+DFFF")]
    [TestCase(NormalizationForm.FormD, new[] { 0xDC00, 0xD800 }, TestName = "FormD + reversed surrogate pair (low,high)")]
    [TestCase(NormalizationForm.FormKC, new[] { 0xD800 }, TestName = "FormKC + lone high surrogate U+D800")]
    [TestCase(NormalizationForm.FormKC, new[] { 0xDFFF }, TestName = "FormKC + lone low surrogate U+DFFF")]
    [TestCase(NormalizationForm.FormKC, new[] { 0xDC00, 0xD800 }, TestName = "FormKC + reversed surrogate pair (low,high)")]
    [TestCase(NormalizationForm.FormKD, new[] { 0xD800 }, TestName = "FormKD + lone high surrogate U+D800")]
    [TestCase(NormalizationForm.FormKD, new[] { 0xDFFF }, TestName = "FormKD + lone low surrogate U+DFFF")]
    [TestCase(NormalizationForm.FormKD, new[] { 0xDC00, 0xD800 }, TestName = "FormKD + reversed surrogate pair (low,high)")]
    public void Ill_formed_input_throws_under_every_non_null_normalization_form(
        NormalizationForm form, int[] illFormedCodeUnits)
    {
        string illFormedInput = BuildStringFromCodeUnits(illFormedCodeUnits);
        var rule = And(Literal("hello"), Eof());
        rule.Compile(form);

        Assert.Throws<ArgumentException>(() => rule.Parse(illFormedInput),
            $"{form} should route input through String.Normalize, " +
            $"which rejects ill-formed UTF-16 with ArgumentException");
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
        // BEFORE the orphan (adding text after would pair them up).
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

    // ============================================================
    // Group 3: Default-ignorable / format / bidi characters
    //   expected: surfaces as a normal token, so grammar mismatches
    //   produce a normal error.
    // ============================================================

    [Test]
    public void BOM_at_start_of_input_is_consumed_as_one_token()
    {
        // U+FEFF BOM. NFC keeps it. Lexer reads as one token. The
        // parser doesn't strip BOMs; the caller does, or the grammar
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
        // both sides; .NET's Unicode category is Zs (Space_Separator),
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
        // match — it expects a token whose RuneValue is 'a' alone.
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
        // parser is NOT susceptible to Trojan-Source-style visual
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

    // ============================================================
    // Group 4: Noncharacters, Private Use, Replacement
    //   expected: surfaces as a normal token, so grammar mismatches
    //   produce a normal error (one .NET-specific exception throws
    //   cleanly).
    // ============================================================

    [Test]
    public void Noncharacter_FFFE_handling()
    {
        // U+FFFE is a Unicode noncharacter. .NET's string.Normalize
        // rejects FFFE specifically as "invalid Unicode code points".
        // Under default FormC the parser calls Normalize before the
        // lexer ever runs, so any grammar throws ArgumentException.
        // Compile(null) skips normalization, the noncharacter survives
        // as one token, and grammars that need to handle it follow
        // the same 4-part pattern as the other noncharacter tests.
        string input = UnicodeExamples.NoncharacterFFFEText + "hello";

        // (0) Default FormC throws out of Parse before the lexer
        // runs. The exception propagates to the caller; callers
        // whose input might contain U+FFFE either strip it upstream
        // or compile with null.
        Assert.Throws<ArgumentException>(() => And(Literal("hello"), Eof()).Parse(input));

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
        // the parser it's a perfectly valid scalar; the parser treats
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
    //   (Other identifier behaviors — non-ASCII digits in
    //   TokenSet.Digits, all five Letter subcategories in
    //   TokenSet.Letters — are covered in TokenSetTests rather
    //   than here.)
    // ============================================================

    [Test]
    public void Mixed_script_identifier_is_accepted_by_default_Identifier()
    {
        // Latin 'a' (U+0061) followed by Cyrillic small letter a
        // (U+0430). Both are XidContinue characters per UAX #31, so
        // Identifier() accepts the mixed-script string. Documents
        // that the default has NO mixed-script restriction; if you
        // care about homoglyph spoofing (UTS #39), build a script-
        // restricted TokenSet manually. The "fix" side (a custom
        // LatinLetters set rejecting Cyrillic 'а') is shown in
        // UnicodeGotchasExamples.Homoglyph_LatinLetters_set_rejects_Cyrillic_a.
        var rule = And(Identifier(), Eof());
        string input = "a" + UnicodeExamples.CyrillicSmallAGrapheme;
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True);
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
}
