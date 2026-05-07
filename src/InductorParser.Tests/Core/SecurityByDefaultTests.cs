using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Demonstrates that grammars written naturally are protected
// against the classic Unicode-related security issues WITHOUT the
// author having to know about the attacks. Each test names a real
// attack class, shows what an unaware author's grammar looks like,
// and proves the parser handles the attack correctly.
//
// The last section ("Issues NOT handled by default") is the 
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
//   6. (Counterexample)        homoglyphs — NOT defended by default
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
        // identifier; the compiler / parser sees the original
        // logical sequence and acts on something different. This
        // is the published "Trojan Source" attack family.
        //
        // Default safety: the parser doesn't apply Unicode's
        // Bidirectional Algorithm. It sees the actual character
        // sequence in the input. U+202E isn't in XID_Continue per
        // UAX #31, so a naive Identifier() grammar rejects any
        // input that contains it. No attacker-aware logic in the
        // grammar — the rule just doesn't match.
        var grammar = And(Identifier(), Eof()).Compile();

        string trojanInput = "ab‮cd";  // logical order: a, b, RLO, c, d
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
        Assert.That(grammar.Parse("𝐀").Success, Is.False,
            "math-bold 𝐀 (U+1D400) is distinct from A under FormC");
        Assert.That(grammar.Parse("Ａ").Success, Is.False,
            "fullwidth Ａ (U+FF21) is distinct from A under FormC");
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
        Assert.That(defaultRule.Parse("ｓｅｌｅｃｔ").Success, Is.False,
            "fullwidth 'ｓｅｌｅｃｔ' SLIPS PAST the blocker under default " +
            "FormC — this is the bypass to fix");

        // FormKC: the blocker catches the lookalike.
        var formKCRule = And(Literal("select"), Eof()).Compile(NormalizationForm.FormKC);
        Assert.That(formKCRule.Parse("select").Success, Is.True,
            "plain 'select' still matches under FormKC");
        Assert.That(formKCRule.Parse("ｓｅｌｅｃｔ").Success, Is.True,
            "fullwidth 'ｓｅｌｅｃｔ' now matches under FormKC: NFKC converts " +
            "it to plain ASCII before the lexer runs, so the blocker fires");
    }

    // ============================================================
    // 3. Invisible content hiding
    // ============================================================

    [Test]
    public void Invisible_character_inside_a_keyword_breaks_a_strict_literal()
    {
        // Threat: an attacker hides characters that don't render
        // — zero-width space, soft hyphen, byte-order mark —
        // inside text that looks like a regular word. Anyone
        // reading the result sees "apple"; the actual character
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
        //     input gets through. UNLIKE the lookalike case,
        //     no normalization form strips invisibles — they're
        //     real characters with their own purpose, just non-
        //     rendering ones. You have to strip them yourself
        //     before parsing. See the next test for the shape.
        var grammar = And(Literal("apple"), Eof()).Compile();

        Assert.That(grammar.Parse("apple").Success, Is.True,
            "clean 'apple' matches");
        Assert.That(grammar.Parse("ap​ple").Success, Is.False,
            "ZWS (U+200B) hidden between p and p breaks the literal match");
        Assert.That(grammar.Parse("ap­ple").Success, Is.False,
            "soft hyphen (U+00AD) breaks the match the same way");
        Assert.That(grammar.Parse("﻿apple").Success, Is.False,
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
        // UNLIKE the lookalike case, FormKC doesn't fix this.
        // No normalization form strips invisibles. The fix is
        // to strip them yourself before calling Parse. The
        // recipe is in UnicodeGotchasExamples.cs at
        // Invisible_format_character_strip_recipe.

        var blocker = And(Literal("apple"), Eof()).Compile();

        // Default: the blocker misses the invisible-laden
        // version.
        Assert.That(blocker.Parse("apple").Success, Is.True,
            "plain 'apple' matches as expected");
        Assert.That(blocker.Parse("ap​ple").Success, Is.False,
            "invisible-laden 'ap<ZWS>ple' SLIPS PAST the blocker " +
            "— no normalization form strips invisibles");

        // Pre-strip the invisibles, then the blocker catches
        // it. This is what the recipe in UnicodeGotchasExamples
        // does.
        var invisibles = new HashSet<int>
        {
            0x200B,  // zero-width space
            0x200C,  // zero-width non-joiner
            0x200D,  // zero-width joiner
            0x00AD,  // soft hyphen
            0xFEFF,  // BOM
        };
        string smuggled = "ap​ple";
        string stripped = string.Concat(smuggled.EnumerateRunes()
            .Where(r => !invisibles.Contains(r.Value)));
        Assert.That(blocker.Parse(stripped).Success, Is.True,
            "after pre-parse stripping, the blocker catches the bypass");
    }

    // ============================================================
    // 4. Encoding injection (ill-formed UTF-16)
    // ============================================================

    [Test]
    public void Ill_formed_UTF16_throws_at_normalization_before_any_grammar_runs()
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
        // throws ArgumentException on ill-formed UTF-16, and the
        // exception propagates out of Parse. The caller learns
        // the input was corrupt; no grammar rule ever sees the
        // ill-formed input and never silently matches against it.
        var grammar = And(Literal("hello"), Eof()).Compile();  // default FormC

        string loneHigh = BuildString(0xD800);
        Assert.Throws<ArgumentException>(
            () => grammar.Parse(loneHigh),
            "lone high surrogate throws at normalization, before the " +
            "lexer runs");

        string reversedPair = BuildString(0xDC00, 0xD800);  // low followed by high
        Assert.Throws<ArgumentException>(
            () => grammar.Parse(reversedPair),
            "reversed surrogate pair throws the same way");
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
        // U+FFFD; it surfaces each one as an ordinary token. A
        // grammar that wants to refuse tampered input adds
        // NoneOf(TokenSet.Replacement) to its character classes
        // (or checks post-parse). This is opt-in: most grammars
        // are happy to accept input with U+FFFD if the rest of
        // the structure is fine. The defense is one TokenSet away.
        byte[] tamperedBytes = [0x68, 0xFF, 0x69]; // 'h', invalid lead 0xFF, 'i'
        string tampered = Encoding.UTF8.GetString(tamperedBytes);
        Assert.That(tampered, Does.Contain("�"),
            "the .NET UTF-8 decoder substituted U+FFFD for the invalid byte");

        // A grammar that refuses ANY input that's been through a
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
    // 6. Issues NOT handled by default (things to watch for counterexamples)
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
        // The default Identifier() rule does NOT defend against
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
        // NOT every
        // Unicode-related security issue is handled automatically.
        // The homoglyph case is the main one where the grammar
        // author has to think about it.
        var grammar = And(Identifier(), Eof()).Compile();

        string homoglyphInput = "aаmin";  // Latin a + Cyrillic а + 'min'
        Assert.That(grammar.Parse(homoglyphInput).Success, Is.True,
            "default Identifier() accepts mixed scripts. This is the main " +
            "classic Unicode-security issue the parser does NOT defend " +
            "against by default. Use a script-restricted TokenSet " +
            "(see UnicodeGotchasExamples) when this matters.");
    }

    // Helper: build a string from raw UTF-16 code units, preserving
    // ill-formed sequences. Used for tests that need to feed lone
    // surrogates or reversed pairs through the parser.
    private static string BuildString(params int[] codeUnits)
    {
        var chars = new char[codeUnits.Length];
        for (int i = 0; i < codeUnits.Length; i++) chars[i] = (char)codeUnits[i];
        return new string(chars);
    }
}
