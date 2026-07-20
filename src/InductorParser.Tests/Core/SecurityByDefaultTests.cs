using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
namespace InductorParser.Tests;

// Demonstrates that grammars written naturally are protected
// against the classic Unicode-related security issues WITHOUT the
// author having to know about the attacks. Each test names a real
// attack class, shows what an unaware author's grammar looks like,
// and proves the parser handles the attack correctly.
//
// The last section ("Issues not handled by default") is the
// counterpart: a small number of attacks do require the grammar
// author to opt into a defense. Those are called out explicitly
// with a pointer to the recipe in UnicodeGotchasExamples.cs.
//
// Threat classes covered:
//   1. Trojan Source           (bidi-direction overrides, CVE-2021-42574)
//   2. Compatibility spoofing  (math-bold, fullwidth, and other lookalikes)
//   3. Invisible content       (zero-width space, soft hyphen, BOM)
//   4. Encoding injection      (ill-formed UTF-16 in .NET strings)
//   5. Upstream tampering      (U+FFFD as a permissive-decoder signal)
//   6. (Counterexample)        homoglyphs, not defended by default
//
// Each test is small on purpose. The comment block above each test
// is the reason the test exists, and is meant to read on its own
// without the reader having to chase the assertions to understand
// the threat model.
[TestFixture]
public class SecurityByDefaultTests
{
    // ============================================================
    // 1. Trojan Source (CVE-2021-42574)
    // ============================================================

    [Test]
    public void Trojan_Source_bidi_override_breaks_an_identifier_match()
    {
        // Threat: an attacker drops a bidi-direction control
        // (U+202E RIGHT-TO-LEFT OVERRIDE, or related LRO / PDF /
        // RLI / LRI / FSI) into source code or input. An editor
        // reorders the visual display so a reviewer sees one
        // identifier. The compiler / parser sees the original
        // logical sequence and acts on something different. This
        // is the published "Trojan Source" attack family.
        //
        // Default safety: the parser doesn't apply Unicode's
        // Bidirectional Algorithm. It sees the actual character
        // sequence in the input. U+202E isn't in XID_Continue per
        // UAX #31, so a naive Identifier() grammar rejects any
        // input that contains it. No attacker-aware logic in the
        // grammar, the rule just doesn't match.
        var grammar = And(Identifier(), Eof()).Compile();

        string trojanInput = $"ab{UnicodeExamples.RightToLeftOverrideText}cd";  // logical order: a, b, RLO, c, d
        Assert.That(grammar.Parse(trojanInput).Success, Is.False,
            "Identifier() rejects bidi controls: U+202E breaks the " +
            "identifier match because it's not an XID_Continue character " +
            "per UAX #31");
    }

    // ============================================================
    // 2. Compatibility-character spoofing
    // ============================================================

    [Test]
    public void Compatibility_lookalike_doesnt_match_plain_letter_under_FormC()
    {
        // Threat: some characters look almost identical to
        // common letters but are different code points. 𝐀
        // (math-bold A), Ａ (fullwidth A, common on Asian
        // keyboards), and a few hundred others all look like A
        // but aren't.
        //
        // Attackers use this in two opposite ways:
        //
        //   * To slip past a check. A site blocks requests
        //     containing "select" to stop SQL injection. An
        //     attacker submits "ｓｅｌｅｃｔ" (fullwidth). The
        //     check doesn't see "select" and lets it through.
        //     The database then treats the fullwidth letters
        //     as ASCII and runs the bad query.
        //
        //   * To impersonate a real name. A site reserves
        //     "admin". An attacker registers "𝐚dmin" (math-
        //     bold a). It's a different string so the reserved-
        //     name check passes, but anyone looking at it sees
        //     "admin".
        //
        // What this test shows: by default, the parser keeps
        // these lookalikes separate. Token('A') matches the
        // real letter A, not 𝐀 or Ａ.
        //
        // Whether that default is the safer choice depends on
        // what your rule is for:
        //
        //   * If your rule defines what's ALLOWED in (e.g. an
        //     identifier match, a DSL keyword list), the
        //     default is good. An attacker's lookalike doesn't
        //     satisfy the rule, so it gets rejected.
        //
        //   * If your rule defines what's BLOCKED (e.g. a
        //     filter looking for SQL keywords), the default is
        //     bad. An attacker's lookalike doesn't satisfy the
        //     rule either, so the block doesn't fire and the
        //     input gets through. Compile with FormKC instead.
        //     That converts lookalikes to plain letters before the
        //     rule runs, so "ｓｅｌｅｃｔ" becomes "select" and
        //     your blocker catches it.
        var grammar = And(Token('A'), Eof()).Compile();

        Assert.That(grammar.Parse("A").Success, Is.True,
            "plain A matches plain A");
        Assert.That(grammar.Parse(UnicodeExamples.MathematicalBoldCapitalAGrapheme).Success, Is.False,
            $"math-bold {UnicodeExamples.MathematicalBoldCapitalAGrapheme} (U+1D400) is distinct from A under FormC");
        Assert.That(grammar.Parse(UnicodeExamples.FullwidthAGrapheme).Success, Is.False,
            $"fullwidth {UnicodeExamples.FullwidthAGrapheme} (U+FF21) is distinct from A under FormC");
    }

    [Test]
    public void Blocking_compat_lookalikes_works_when_compiled_with_FormKC()
    {
        // The flip side of the test above. If your rule's job
        // is to BLOCK input that contains "select" (because a
        // downstream layer will treat fullwidth letters as ASCII
        // and run the bad query), the FormC default doesn't
        // help. Literal("select") doesn't match "ｓｅｌｅｃｔ"
        // because they're different code points, so the block
        // never fires and the bypass works.
        //
        // FormKC fixes this. It converts the fullwidth letters to
        // plain ASCII before the lexer runs, so Literal("select")
        // matches "ｓｅｌｅｃｔ" and the block catches the bypass.

        // Default FormC: the blocker misses the lookalike.
        var defaultRule = And(Literal("select"), Eof()).Compile();
        Assert.That(defaultRule.Parse("select").Success, Is.True,
            "plain 'select' matches as expected");
        Assert.That(defaultRule.Parse(UnicodeExamples.FullwidthSelectIdentifier).Success, Is.False,
            $"fullwidth '{UnicodeExamples.FullwidthSelectIdentifier}' SLIPS PAST the blocker under default " +
            $"FormC {UnicodeExamples.EmDashGrapheme} this is the bypass to fix");

        // FormKC: the blocker catches the lookalike.
        var formKCRule = And(Literal("select"), Eof()).Compile(NormalizationForm.FormKC);
        Assert.That(formKCRule.Parse("select").Success, Is.True,
            "plain 'select' still matches under FormKC");
        Assert.That(formKCRule.Parse(UnicodeExamples.FullwidthSelectIdentifier).Success, Is.True,
            $"fullwidth '{UnicodeExamples.FullwidthSelectIdentifier}' now matches under FormKC: NFKC converts " +
            "it to plain ASCII before the lexer runs, so the blocker fires");
    }

    // ============================================================
    // 3. Invisible content hiding
    // ============================================================

    [Test]
    public void Invisible_character_inside_a_keyword_breaks_a_strict_literal()
    {
        // Threat: an attacker hides characters that don't render
        // (zero-width space, soft hyphen, byte-order mark)
        // inside text that looks like a regular word. Anyone
        // reading the result sees "apple". The actual character
        // sequence is "ap<invisible>ple". Same kinds of attacks
        // as the lookalike-character case above:
        //
        //   * Slip past filters. A profanity filter blocks
        //     "kill". The attacker writes "ki" + ZWS + "ll".
        //     The filter does an exact-match check, doesn't see
        //     "kill", lets the post through. Readers see "kill"
        //     because the ZWS doesn't render.
        //
        //   * Impersonate a real name. A username that displays
        //     as "admin" but compares as a different string to
        //     the reserved-name check.
        //
        //   * Hide content from search. A banned term in a
        //     document with invisibles sprinkled in doesn't show
        //     up when someone searches for the plain term.
        //
        // What this test shows: by default, the parser keeps
        // invisibles as their own tokens. Literal("apple")
        // matches the literal "apple" only, not "ap<ZWS>ple" or
        // "ap<soft-hyphen>ple".
        //
        // Whether that default is the safer choice depends on
        // what your rule is for (same allow/block split as the
        // lookalike test above):
        //
        //   * If your rule defines what's ALLOWED (e.g. an
        //     exact match on a valid keyword), the default is
        //     good. The attacker's invisible-laden version
        //     doesn't match, so it gets rejected.
        //
        //   * If your rule defines what's BLOCKED (e.g. a
        //     filter looking for a banned word), the default
        //     is bad. The attacker's version doesn't match the
        //     rule either, so the block doesn't fire and the
        //     input gets through. Unlike the lookalike case,
        //     no normalization form strips invisibles. They're
        //     real characters with their own purpose, just non-
        //     rendering ones. You have to strip them yourself
        //     before parsing. See the next test for the shape.
        var grammar = And(Literal("apple"), Eof()).Compile();

        Assert.That(grammar.Parse("apple").Success, Is.True,
            "clean 'apple' matches");
        Assert.That(grammar.Parse($"ap{UnicodeExamples.ZeroWidthSpaceText}ple").Success, Is.False,
            "ZWS (U+200B) hidden between p and p breaks the literal match");
        Assert.That(grammar.Parse($"ap{UnicodeExamples.SoftHyphenText}ple").Success, Is.False,
            "soft hyphen (U+00AD) breaks the match the same way");
        Assert.That(grammar.Parse($"{UnicodeExamples.ByteOrderMarkText}apple").Success, Is.False,
            "BOM (U+FEFF) at the start is also a real token, not a no-op");
    }

    [Test]
    public void Blocking_invisible_smuggling_requires_pre_parse_stripping()
    {
        // The flip side of the test above. If your rule's job
        // is to BLOCK input containing "apple" (because some
        // downstream layer renders the invisibles away and
        // treats it as plain "apple"), the default doesn't
        // help. Literal("apple") doesn't match "ap<ZWS>ple",
        // so the block never fires and the bypass works.
        //
        // Unlike the lookalike case, FormKC doesn't fix this.
        // No normalization form strips invisibles. Strip them
        // yourself before calling Parse. The recipe is in
        // UnicodeGotchasExamples.cs at
        // Invisible_format_character_strip_recipe.

        var blocker = And(Literal("apple"), Eof()).Compile();

        // Default: the blocker misses the invisible-laden
        // version.
        Assert.That(blocker.Parse("apple").Success, Is.True,
            "plain 'apple' matches as expected");
        Assert.That(blocker.Parse($"ap{UnicodeExamples.ZeroWidthSpaceText}ple").Success, Is.False,
            "invisible-laden 'ap<ZWS>ple' SLIPS PAST the blocker " +
            $"{UnicodeExamples.EmDashGrapheme} no normalization form strips invisibles");

        // Pre-strip the invisibles, then the blocker catches
        // it. Same idea as the recipe in UnicodeGotchasExamples,
        // which strips by rune. A char-level strip does the same
        // job here because every invisible in the set is a single
        // BMP char. (It also has to be char-level: this file syncs
        // into the IL2CPP pass, whose netstandard2.1 surface has
        // no string.EnumerateRunes.)
        var invisibles = new HashSet<int>
        {
            0x200B,  // zero-width space
            0x200C,  // zero-width non-joiner
            0x200D,  // zero-width joiner
            0x00AD,  // soft hyphen
            0xFEFF,  // BOM
        };
        string smuggled = $"ap{UnicodeExamples.ZeroWidthSpaceText}ple";
        string stripped = string.Concat(smuggled.Where(c => !invisibles.Contains(c)));
        Assert.That(blocker.Parse(stripped).Success, Is.True,
            "after pre-parse stripping, the blocker catches the bypass");
    }

    // ============================================================
    // 4. Encoding injection (ill-formed UTF-16)
    // ============================================================

    [Test]
    public void Ill_formed_UTF16_reported_as_MalformedInput_before_any_grammar_runs()
    {
        // Threat: an attacker constructs a .NET string with
        // ill-formed UTF-16 (lone surrogate, reversed pair, and
        // similar). .NET strings are sequences of UTF-16 code
        // units with NO well-formedness validation, so a caller
        // building strings from raw bytes (or via WTF-8 round-
        // tripping) can produce ill-formed input that reaches
        // the parser.
        //
        // Default safety: every non-null normalization form
        // (FormC, FormD, FormKC, FormKD) routes input through
        // String.Normalize before the lexer runs. Normalize
        // rejects ill-formed UTF-16, and Parse reports that as a
        // MalformedInput result rather than running any grammar
        // rule against the input. No rule ever sees the ill-formed
        // input, so it can never silently match. The caller learns
        // the input was corrupt by checking Outcome.
        var grammar = And(Literal("hello"), Eof()).Compile();  // default FormC

        string loneHigh = UnicodeExamples.HighSurrogateMinText;
        Assert.That(grammar.Parse(loneHigh).Outcome, Is.EqualTo(ParseOutcome.MalformedInput),
            "lone high surrogate is rejected at normalization, before the lexer runs");

        string reversedPair = UnicodeExamples.ReversedSurrogatePairText;  // low followed by high
        Assert.That(grammar.Parse(reversedPair).Outcome, Is.EqualTo(ParseOutcome.MalformedInput),
            "reversed surrogate pair is rejected the same way");
    }

    // ============================================================
    // 5. Upstream decoder tampering (U+FFFD as a signal)
    // ============================================================

    [Test]
    public void Upstream_decoder_tampering_can_be_detected_via_TokenSet_Replacement()
    {
        // Threat: an attacker feeds malformed bytes through a
        // permissive decoder (the default behavior of every .NET
        // Unicode encoding: UTF-8, UTF-16, UTF-32). The decoder
        // substitutes U+FFFD for the malformed bytes and produces
        // a string that LOOKS valid but had data dropped. Depending
        // on what was dropped, the resulting string might match a
        // grammar that would have rejected the original bytes.
        //
        // Default safety: the parser doesn't strip or rewrite
        // U+FFFD. It surfaces each one as an ordinary token. A
        // grammar that wants to refuse tampered input adds
        // NoneOf(TokenSet.Replacement) to its character classes
        // (or checks post-parse). This is opt-in: most grammars
        // are happy to accept input with U+FFFD if the rest of
        // the structure is fine. The defense is one TokenSet away.
        byte[] tamperedBytes = { 0x68, 0xFF, 0x69 }; // 'h', invalid lead 0xFF, 'i'
        string tampered = Encoding.UTF8.GetString(tamperedBytes);
        Assert.That(tampered, Does.Contain(UnicodeExamples.ReplacementCharacterText),
            "the .NET UTF-8 decoder substituted U+FFFD for the invalid byte");

        // A grammar that refuses any input that's been through a
        // permissive decoder. The author opts in by writing
        // NoneOf(TokenSet.Replacement) where they would otherwise
        // have written AnyToken or NoneOf(...).
        var grammar = And(
            OneOrMore(NoneOf(TokenSet.Replacement)),
            Eof()).Compile();

        Assert.That(grammar.Parse("hi").Success, Is.True,
            "clean input has no U+FFFD, the rule passes");
        Assert.That(grammar.Parse(tampered).Success, Is.False,
            "tampered input has a U+FFFD from .NET's decoder, the rule rejects it");
    }

    // ============================================================
    // 6. Issues not handled by default (things to watch for counterexamples)
    // ============================================================

    [Test]
    public void Counterexample_mixed_script_homoglyph_IS_accepted_by_default_Identifier()
    {
        // UTS #39 confusable characters: Latin 'a' (U+0061) and
        // Cyrillic 'а' (U+0430) render identically in most fonts
        // but are different code points. An attacker creates an
        // identifier "admin" with one letter swapped for the
        // Cyrillic homoglyph. It looks identical to the legitimate
        // "admin" to a human but is a different string to the
        // parser, and a string comparison against an authoritative
        // list of reserved names misses it.
        //
        // The default Identifier() rule doesn't defend against
        // this attack. Both Latin a and Cyrillic а are
        // XID_Continue per UAX #31, and UAX #31 places no script
        // restriction on identifiers. So Identifier() accepts the
        // mixed-script version.
        //
        // The fix: build a script-restricted TokenSet that only
        // accepts characters from the script(s) the grammar
        // intends to support, and use it in place of Identifier().
        // See UnicodeGotchasExamples.Homoglyph_LatinLetters_set_rejects_Cyrillic_a
        // for the recipe.
        //
        // Not every Unicode-related security issue is handled
        // automatically.
        // The homoglyph case is the main one where the grammar
        // author has to think about it.
        var grammar = And(Identifier(), Eof()).Compile();

        string homoglyphInput = $"a{UnicodeExamples.CyrillicSmallAGrapheme}min";  // Latin a + Cyrillic а + 'min'
        Assert.That(grammar.Parse(homoglyphInput).Success, Is.True,
            "default Identifier() accepts mixed scripts. This is the main " +
            "classic Unicode-security issue the parser does NOT defend " +
            "against by default. Use a script-restricted TokenSet " +
            "(see UnicodeGotchasExamples) when this matters.");
    }

    // Verifies the Primer4 "Invisible characters" widened-set recipe:
    // UnicodeCategory.Format isn't the full set of invisible
    // characters, so a Format-only filter has an exploitable hole. The
    // clearest example is U+3164 HANGUL FILLER, which renders as blank
    // width but is category Letter (Lo), so it passes both a
    // Format-only filter and a "letters only" rule. The variation
    // selectors and COMBINING GRAPHEME JOINER are category Mark (Mn),
    // also outside Format. The widened set unions Format with those
    // strays and closes the hole.
    [Test]
    public void Invisible_HangulFiller_escapes_a_Format_only_filter_but_the_widened_set_catches_it()
    {
        var formatOnly = TokenSet.Category(UnicodeCategory.Format);

        var invisibles =
            TokenSet.Category(UnicodeCategory.Format)
            | TokenSet.FromRanges(new (int Low, int High)[]
              {
                  (0x034F, 0x034F),    // COMBINING GRAPHEME JOINER
                  (0x115F, 0x1160),    // Hangul choseong / jungseong fillers
                  (0x3164, 0x3164),    // HANGUL FILLER
                  (0xFFA0, 0xFFA0),    // halfwidth Hangul filler
                  (0xFE00, 0xFE0F),    // variation selectors 1-16
                  (0xE0100, 0xE01EF),  // variation selectors 17-256
              });

        // The hole: a Format-only filter doesn't contain these invisibles.
        Assert.That(formatOnly.ContainsRune(0x3164), Is.False,
            "HANGUL FILLER is category Letter (Lo), not Format, so a Format-only filter misses it");
        Assert.That(formatOnly.ContainsRune(0x034F), Is.False,
            "COMBINING GRAPHEME JOINER is category Mark (Mn), not Format");
        Assert.That(formatOnly.ContainsRune(0xFE0F), Is.False,
            "VARIATION SELECTOR-16 is category Mark (Mn), not Format");

        // The widened set catches every one of them, including the
        // supplementary variation selectors above the BMP.
        Assert.That(invisibles.ContainsRune(0x3164), Is.True);
        Assert.That(invisibles.ContainsRune(0x034F), Is.True);
        Assert.That(invisibles.ContainsRune(0xFE0F), Is.True);
        Assert.That(invisibles.ContainsRune(0xE0100), Is.True);

        // Why HANGUL FILLER is especially dangerous: it's a Letter, so
        // a "letters only" rule accepts it too.
        Assert.That(TokenSet.Letters.ContainsRune(0x3164), Is.True,
            "HANGUL FILLER is a Letter, so OneOf(TokenSet.Letters) would accept it");

        // End to end: a username rule that rejects invisibles with the
        // Format-only set lets the blank-width filler through, while the
        // widened set rejects it.
        var withFormatOnly = And(OneOrMore(NoneOf(formatOnly)), Eof()).Compile();
        var withWidened = And(OneOrMore(NoneOf(invisibles)), Eof()).Compile();

        string smuggled = "ad" + (char)0x3164 + "min";   // U+3164 HANGUL FILLER: displays close to "admin"
        Assert.That(withFormatOnly.Parse(smuggled).Success, Is.True,
            "the Format-only filter is the hole: it accepts the HANGUL FILLER");
        Assert.That(withWidened.Parse(smuggled).Success, Is.False,
            "the widened Invisibles set rejects the HANGUL FILLER");
    }

    // Verifies the Primer4 "Trojan Source" narrower filter: for
    // human-language text, reject only the twelve bidi control
    // characters instead of all of Format (which would also remove
    // ZWNJ / ZWJ that Persian, Indic scripts, and emoji need). This
    // asserts the set is exactly the twelve controls with nothing
    // adjacent swept in.
    [Test]
    public void BidiControls_set_covers_the_twelve_bidi_format_characters()
    {
        var bidiControls = TokenSet.FromRanges(new (int Low, int High)[]
        {
            (0x202A, 0x202E),   // LRE, RLE, PDF, LRO, RLO
            (0x2066, 0x2069),   // LRI, RLI, FSI, PDI
            (0x200E, 0x200F),   // LRM, RLM
            (0x061C, 0x061C),   // ALM
        });

        int[] theTwelve =
        {
            0x202A, 0x202B, 0x202C, 0x202D, 0x202E,   // embeddings, PDF, overrides
            0x2066, 0x2067, 0x2068, 0x2069,           // isolates + PDI
            0x200E, 0x200F,                           // LRM, RLM
            0x061C,                                   // ALM
        };
        foreach (int codepoint in theTwelve)
            Assert.That(bidiControls.ContainsRune(codepoint), Is.True,
                $"U+{codepoint:X4} is a bidi control and must be in the set");

        // Boundaries: characters just outside each range aren't swept in.
        Assert.That(bidiControls.ContainsRune(0x2029), Is.False, "PARAGRAPH SEPARATOR is not a bidi control");
        Assert.That(bidiControls.ContainsRune(0x2065), Is.False, "just below the isolate range");
        Assert.That(bidiControls.ContainsRune(0x206A), Is.False, "just above the isolate range");
        Assert.That(bidiControls.ContainsRune('a'), Is.False, "an ordinary letter is not a bidi control");

        // End to end: a rule that rejects bidi controls stops the
        // RIGHT-TO-LEFT OVERRIDE while accepting clean text.
        var noBidi = And(OneOrMore(NoneOf(bidiControls)), Eof()).Compile();
        Assert.That(noBidi.Parse("grant").Success, Is.True);
        Assert.That(noBidi.Parse("gr" + (char)0x202E + "ant").Success, Is.False,
            "the RLO (U+202E) is rejected by the bidi-control filter");
    }
}
