using System;
using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Probes parser behavior on Unicode inputs that aren't well-formed or
// don't fit comfortably into UAX #29's grapheme-cluster model. Two
// goals: (1) document how the parser reacts to each category so
// callers know what to expect, and (2) lock the reactions in against
// silent regressions.
//
// The categories are grouped into seven buckets:
//
//   1. ENCODING-LEVEL MALFORMED INPUT: throws cleanly or surfaces
//      as an untyped token, never silently corrupts.
//      UTF-16 invariants broken: lone surrogates, reversed surrogate
//      pairs, the U+10FFFF maximum boundary. Only UTF-16 here because
//      the parser takes a .NET string, and .NET strings are UTF-16
//      internally. UTF-8 / UTF-32 / legacy-codepage decoding errors
//      get resolved upstream by the caller's Encoding.GetString call
//      (usually as U+FFFD replacements; see Group 4) before the
//      parser is ever invoked. Under default FormC, .NET's
//      string.Normalize rejects malformed UTF-16 by throwing
//      ArgumentException out of Parse() — the caller sees the
//      problem before any grammar runs. Under Compile(null) the
//      lexer surfaces each malformed code unit as a token with no
//      RuneValue, which OneOf and similar rules predictably reject.
//      Either path is safe; a grammar can't silently match a
//      malformed surrogate.
//
//   2. BARE ATTACHING CHARACTERS: surfaces as a normal token, so
//      grammar mismatches produce a normal error.
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
//      THe caller can
//      catch and translate, or compile with null to skip
//      normalization. U+FFFF, U+FDD0, the rest of the Private Use
//      Area, and U+FFFD all pass through as ordinary tokens.
//
//   5. NORMALIZATION EDGE CASES: caught at Compile time so the
//      grammar can't silently fail to match.
//      Cases where one logical character has more than one valid
//      Unicode encoding, and the parser has to make sure the
//      grammar's literals and the input agree. Four sub-cases:
//
//        * Multi-mark canonical reordering: combining marks of
//          different classes can appear in raw input in either order
//          and Unicode says they're equivalent. NFC normalizes the
//          input at parse time, so the parser handles this for free.
//
//        * Hangul jamo decomposition: a precomposed Korean syllable
//          like 한 (U+D55C) is canonically equivalent to its three
//          jamo (U+1112 + U+1161 + U+11AB). Under FormC the grammar
//          literal has to be written in the precomposed form (NFC
//          composes the jamo into the syllable). The grammar then
//          matches input written either way, because NFC composes
//          the input the same way before the lexer sees it.
//
//        * Canonical singletons: a few characters in Unicode exist
//          twice for historical reasons. The Angstrom sign (U+212B,
//          inherited from older scientific-notation codepages) and
//          LATIN CAPITAL LETTER A WITH RING ABOVE (U+00C5) are two
//          different code points, but they're the same character.
//          They render identically as Å. Unicode declared U+212B
//          canonically equivalent to U+00C5 and gave it a canonical
//          decomposition that maps it to U+00C5. NFC always
//          rewrites a singleton to its canonical form when it
//          normalizes a string (U+212B becomes U+00C5, and the
//          same is true for the other four singletons listed
//          below).
//
//          Without compile-time validation this would be a silent
//          trap for a grammar author: write Token('Å')
//          (the Angstrom version), the parser normalizes input to
//          FormC before the lexer runs, so any U+212B in input
//          becomes U+00C5. The lexer never sees U+212B, and your
//          rule looking for U+212B never matches anything, even
//          though every Å in your input renders the same as the
//          one you typed. The Compile validation pass catches this
//          exact case: it sees the literal U+212B differs from its
//          FormC normalization (U+00C5) and throws
//          InvalidOperationException at startup, telling the author
//          to use U+00C5 instead.
//
//          There are five canonical singletons in Unicode total:
//          the famous trio Angstrom / Ohm / Kelvin (all of which
//          duplicate Latin or Greek letters that already exist) and
//          the two angle brackets U+2329 / U+232A (which Unicode
//          declared identical to the CJK angle brackets U+3008 /
//          U+3009). All five behave the same way: NFC folds them
//          and the Compile validation rejects a literal that uses
//          the singleton form.
//
//        * Compatibility singletons: characters that are a
//          presentation variant of a plain character. The
//          mathematical bold A (𝐀, U+1D400) is the typographic-bold
//          version of plain A. The fullwidth A (Ａ, U+FF21, common
//          in CJK input) is the wide-form version of plain A. The
//          superscript two (², U+00B2) is the raised version of
//          plain 2. There are hundreds of these. Unicode considers
//          each pair to be the same content with different
//          presentation.
//
//          Compatibility singletons are NOT folded by NFC (FormC
//          leaves them alone). They ARE folded by NFKC (FormKC).
//          That gives the grammar author a deliberate choice and
//          both choices are safe:
//
//          Under default FormC (the common case), 𝐀 and A look
//          visually different and the parser treats them as
//          different. If you type Token('𝐀') the rule matches the
//          math-bold A and only the math-bold A in input, not
//          plain A. No surprises: you wrote what you meant, and
//          you'll get back what you wrote. Compile passes.
//
//          Under FormKC, the parser is matching by MEANING instead
//          of by visual form. NFKC folds 𝐀, ℂ, Ａ, ², and the
//          rest of the presentation variants down to their plain
//          ASCII / Greek / etc. equivalents before the lexer runs.
//          A grammar that wants to treat these as equivalent opts
//          into FormKC at Compile time. If the author then writes
//          Token('𝐀') they hit the same trap canonical singletons
//          have under FormC: NFKC rewrites the input's 𝐀 to A, so
//          the rule looking for 𝐀 never matches anything. The
//          Compile validation catches this exact case and throws,
//          telling the author to use the plain-A form.
//
//          So the design is: visual matching is the safe default,
//          meaning-based matching is opt-in, and either way an
//          author who picks the wrong literal form for their
//          chosen normalization gets a clear error at startup.
//
//      All four sub-cases are protected by the same validation
//      rule: at Compile time, every rule's text is
//      compared against its own normalization in the chosen form.
//      If they differ, Compile throws with a clear message. So an
//      author who writes a literal in any non-canonical form gets a 
//      startup error pointing at the offender.
//      Whichever path you take, the grammar can't silently fail to
//      match because of a mismatch between the literal's encoding
//      and the input's normalization.
//
//   6. IDENTIFIER-RELEVANT EDGE CASES: the default Identifier rule
//      has no anti-spoofing logic. Restrict via a custom script-
//      bounded TokenSet if you care about UTS #39 homoglyph attacks.
//      Mixed-script identifiers like "aа" (Latin 'a' followed by
//      Cyrillic 'а' that renders identically) are accepted because
//      both runes are XidContinue per UAX #31. The "fix" path
//      (a custom LatinLetters set rejecting Cyrillic 'а') lives
//      in UnicodeGotchasExamples. Other identifier-adjacent
//      behaviors — non-ASCII digits in TokenSet.Digits, all five
//      Letter subcategories in TokenSet.Letters — are covered in
//      TokenSetTests rather than here.
//
//   7. TRIVIAL / DEFENSIVE: parser doesn't crash, regardless of
//      shape or length.
//      Empty input, NULL byte, very long sequences. Mostly here to
//      lock in "parser doesn't crash on N consecutive zero-width
//      characters" against future regressions.
//
[TestFixture]
public class MalformedUnicodeTests
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
        Assert.Throws<ArgumentException>(() => AllOf(Literal("hello"), Eof()).Parse(input));

        // (1) Compile(null) + naive grammar fails normally:
        // Literal("hello") doesn't match a surrogate followed by 'h'.
        // Lone surrogates don't match any normal predicate (their
        // RuneValue is -1, which no TokenSet accepts), so OneOf and
        // TokenSet-based rules skip them too.
        var naiveRule = AllOf(Literal("hello"), Eof());
        naiveRule.Compile(null);
        Assert.That(naiveRule.Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the lone surrogate as
        // a wildcard.
        var anyTokenRule = AllOf(AnyToken(), Literal("hello"), Eof());
        anyTokenRule.Compile(null);
        Assert.That(anyTokenRule.Parse(input).Success, Is.True);

        // (3) Targeting the surrogate via Token(string) also works.
        var targetedRule = AllOf(Token(UnicodeExamples.HighSurrogateMinText), Literal("hello"), Eof());
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
        Assert.Throws<ArgumentException>(() => AllOf(Literal("hello"), Eof()).Parse(input));

        // (1) Compile(null) + naive grammar fails normally.
        var naiveRule = AllOf(Literal("hello"), Eof());
        naiveRule.Compile(null);
        Assert.That(naiveRule.Parse(input).Success, Is.False);

        // (2) AnyToken accommodation succeeds.
        var anyTokenRule = AllOf(AnyToken(), Literal("hello"), Eof());
        anyTokenRule.Compile(null);
        Assert.That(anyTokenRule.Parse(input).Success, Is.True);

        // (3) Token-specific accommodation succeeds.
        var targetedRule = AllOf(Token(UnicodeExamples.LowSurrogateMaxText), Literal("hello"), Eof());
        targetedRule.Compile(null);
        Assert.That(targetedRule.Parse(input).Success, Is.True);
    }

    [Test]
    public void OneOf_universe_rejects_lone_surrogate_under_null_normalization()
    {
        // A lone surrogate has no RuneValue, so OneOf rejects it even
        // when the set is the "matches anything" Universe. The
        // predicate is "is the token's RuneValue in the set," and -1
        // (the no-rune marker the lexer emits for surrogate halves)
        // is by definition not a valid Unicode scalar, so it can't
        // be a member of any TokenSet — Universe included. Using
        // Universe here (instead of a narrow set like Letters) makes
        // the failure unambiguously about RuneValue membership, not
        // about the surrogate happening to fall outside a category.
        var rule = OneOf(TokenSet.Universe);
        rule.Compile(null);
        string input = UnicodeExamples.HighSurrogateMinText;
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
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
        string input = UnicodeExamples.LowSurrogateMinText + UnicodeExamples.HighSurrogateMinText;

        // OneOrMore(AnyToken()) consumes both surrogates as wildcards
        // and confirms the lexer didn't merge or swap them.
        var anyTokenRule = OneOrMore(AnyToken());
        anyTokenRule.Compile(null);
        var anyTokenResult = anyTokenRule.Parse(input);
        Assert.That(anyTokenResult.Success, Is.True);
        Assert.That(anyTokenResult.ToString(), Is.EqualTo(input),
            "matched text equals the input character-for-character; " +
            "the parser didn't swap the surrogates or fold them");
        Assert.That(anyTokenResult.Symbols.Count, Is.EqualTo(2),
            "two distinct AnyToken matches, one per surrogate, " +
            "proving the lexer didn't merge them into a single token");

        // Targeting both surrogates explicitly via Token(string) also
        // works, in their original (low-then-high) order. Useful
        // for grammars that want to surface unpaired surrogates as
        // distinct entities (WTF-8 round-tripping, JSON unpaired-
        // surrogate handling).
        var targetedRule = AllOf(
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
        Assert.That(AllOf(Token(UnicodeExamples.MaximumCodePointRune), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphaned mark
        // as a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the combining mark specifically via
        // Token(...) also works.
        Assert.That(AllOf(Token(UnicodeExamples.CombiningAcuteText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphaned joiner
        // as a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the joiner specifically via Token(...)
        // also works.
        Assert.That(AllOf(Token(UnicodeExamples.ZeroWidthJoinerText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Bare_variation_selector_at_start_of_input_is_one_token()
    {
        // U+FE0F EMOJI VARIATION SELECTOR alone at the start of
        // input. Same start-of-text break rule as the joiner above.
        string input = UnicodeExamples.EmojiVariationSelectorText + "hello";

        // (1) Naive grammar fails: Literal("hello") doesn't match
        // a variation selector followed by 'h'.
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphan as a
        // wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the variation selector specifically via
        // Token(...) also works.
        Assert.That(AllOf(Token(UnicodeExamples.EmojiVariationSelectorText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphaned indicator
        // as a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the regional indicator specifically via
        // Token(...) also works.
        Assert.That(AllOf(Token(UnicodeExamples.RegionalIndicatorURune), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphan as a
        // wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the skin-tone modifier specifically via
        // Token(...) also works.
        Assert.That(AllOf(Token(UnicodeExamples.MediumSkinToneRune), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken after "hello" consumes the orphaned Prepend
        // as a wildcard.
        Assert.That(AllOf(Literal("hello"), AnyToken(), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the Prepend specifically via Token(...)
        // also works.
        Assert.That(AllOf(Literal("hello"), Token(UnicodeExamples.ArabicNumberSignText), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the orphaned virama
        // as a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the virama specifically via Token(...) also
        // works.
        Assert.That(AllOf(Token(UnicodeExamples.DevanagariViramaText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        var strictResult = AllOf(Literal("hello"), Eof()).Parse(input);
        Assert.That(strictResult.Success, Is.False);
        Assert.That(strictResult.ErrorCharIndex, Is.EqualTo(0));

        // (2) AnyToken at the front consumes the BOM as a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the BOM specifically via Token(...) also
        // works. The positive-match path is for grammars that want
        // to deliberately recognize and process a leading BOM.
        Assert.That(AllOf(Token(UnicodeExamples.ByteOrderMarkText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void BOM_mid_stream_is_an_ordinary_token()
    {
        // BOM in the middle of input. Behaves just like start-of-input
        // BOM: ordinary token. A grammar matching letters with no
        // BOM accommodation fails at the BOM's position.
        string input = ("a" + UnicodeExamples.ByteOrderMarkText + "b");

        // (1) Strict a-b grammar fails at offset 1 where the BOM sits.
        var strictResult = AllOf(Token('a'), Token('b'), Eof()).Parse(input);
        Assert.That(strictResult.Success, Is.False);
        Assert.That(strictResult.ErrorCharIndex, Is.EqualTo(1),
            "the BOM at position 1 isn't 'b'");

        // (2) AnyToken consumes the BOM as a wildcard between the
        // letters.
        Assert.That(AllOf(Token('a'), AnyToken(), Token('b'), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the BOM specifically via Token(...) makes
        // the same input parse successfully.
        Assert.That(AllOf(Token('a'), Token(UnicodeExamples.ByteOrderMarkText), Token('b'), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Token('a'), Token('b'), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken consumes the ZWS as a wildcard between the
        // letters.
        Assert.That(AllOf(Token('a'), AnyToken(), Token('b'), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the ZWS specifically via Token(...) makes
        // the same input parse successfully.
        Assert.That(AllOf(Token('a'), Token(UnicodeExamples.ZeroWidthSpaceText), Token('b'), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Token('a'), Token('b'), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken consumes the ("a" + ZWNJ) cluster as a
        // wildcard, then Token('b') matches the second cluster.
        Assert.That(AllOf(AnyToken(), Token('b'), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the multi-rune cluster as a single literal
        // via Token(string) also works. Token("a" + ZWNJ) matches
        // the exact two-rune cluster the lexer produces.
        Assert.That(AllOf(Token("a" + UnicodeExamples.ZeroWidthNonJoinerText), Token('b'), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("ap"), AnyToken(), Literal("ple"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the soft hyphen specifically via Token(...)
        // makes the same input parse successfully.
        Assert.That(AllOf(Literal("ap"), Token(UnicodeExamples.SoftHyphenText), Literal("ple"), Eof()).Parse(input).Success, Is.True);
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
        var identifierResult = AllOf(Identifier(), Eof()).Parse(input);
        Assert.That(identifierResult.Success, Is.False,
            "U+202E isn't a valid identifier-continue character, " +
            "so the identifier ends at the bidi control");

        // (2) AnyToken consumes the RLO as a wildcard between the
        // two halves of the word.
        Assert.That(AllOf(Literal("ab"), AnyToken(), Literal("cd"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the RLO specifically via Token(...) makes
        // the same input parse successfully. Useful if a grammar
        // wants to detect bidi controls and either flag them or
        // process around them.
        Assert.That(AllOf(Literal("ab"), Token(UnicodeExamples.RightToLeftOverrideText), Literal("cd"), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the tag character as
        // a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting the tag character specifically via
        // Token(...) also works.
        Assert.That(AllOf(Token(UnicodeExamples.LanguageTagRune), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes it as a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting it specifically via Token(...) also works.
        Assert.That(AllOf(Token(UnicodeExamples.MongolianVowelSeparatorText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        Assert.Throws<ArgumentException>(() => AllOf(Literal("hello"), Eof()).Parse(input));

        // (1) Compile(null) + naive grammar fails normally:
        // Literal("hello") doesn't match a noncharacter followed
        // by 'h'.
        var naiveRule = AllOf(Literal("hello"), Eof());
        naiveRule.Compile(null);
        Assert.That(naiveRule.Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the noncharacter as
        // a wildcard.
        var anyTokenRule = AllOf(AnyToken(), Literal("hello"), Eof());
        anyTokenRule.Compile(null);
        Assert.That(anyTokenRule.Parse(input).Success, Is.True);

        // (3) Targeting U+FFFE specifically via Token(...) also
        // works.
        var targetedRule = AllOf(Token(UnicodeExamples.NoncharacterFFFEText), Literal("hello"), Eof());
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the noncharacter as
        // a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+FFFF specifically via Token(...) also
        // works.
        Assert.That(AllOf(Token(UnicodeExamples.NoncharacterFFFFText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the noncharacter as
        // a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+FDD0 specifically via Token(...) also
        // works.
        Assert.That(AllOf(Token(UnicodeExamples.NoncharacterFDD0Text), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the PUA character as
        // a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+E000 specifically via Token(...) also
        // works. Useful for a private protocol that assigns
        // meaning to a PUA range.
        Assert.That(AllOf(Token(UnicodeExamples.PrivateUseAreaStartText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the replacement
        // character as a wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+FFFD specifically via Token(...) also
        // works. Useful for grammars that want to deliberately
        // detect and surface decoder-replacement markers in their
        // output.
        Assert.That(AllOf(Token(UnicodeExamples.ReplacementCharacterText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
    }

    // ============================================================
    // Group 5: Normalization edge cases
    //   expected: caught at Compile time so the grammar can't
    //   silently fail to match.
    // ============================================================

    [Test]
    public void Multiple_combining_marks_get_canonicalized_under_FormC()
    {
        // Vietnamese a-circumflex-dot-below (U+1EAD) decomposes to
        // a + dot-below (ccc=220) + circumflex (ccc=230). The marks
        // have different combining classes, so NFC reorders them
        // when they appear in non-canonical order. Token(U+1EAD)
        // matches input regardless of which order the author used,
        // because NFC composes both orderings to the same
        // precomposed character. (Same-class marks like acute +
        // circumflex are NOT reordered and would NOT have this
        // property.)
        var precomposedRule = AllOf(Token(UnicodeExamples.VietnameseACircumflexDotBelowRune), Eof());

        // Canonical order (ccc 220 then 230): NFC composes directly.
        Assert.That(precomposedRule.Parse(UnicodeExamples.VietnameseACircumflexDotBelowCanonicalText).Success,
            Is.True, "canonical order");

        // Reversed order (ccc 230 then 220): NFC reorders by class
        // first, then composes. Same result.
        Assert.That(precomposedRule.Parse(UnicodeExamples.VietnameseACircumflexDotBelowReorderedText).Success,
            Is.True, "NFC reorders different-class marks before composing");
    }

    [Test]
    public void Compile_throws_for_non_canonical_combining_mark_order_under_FormC()
    {
        // The flip side of the test above: a grammar literal that
        // uses non-canonical mark order would silently never match
        // any input under FormC, because every input gets canonicalized
        // before the lexer sees it. The Compile validation pass
        // catches this and throws so the author fixes the literal at
        // grammar-build time instead of debugging silent match
        // failures.
        var rule = Token(UnicodeExamples.VietnameseACircumflexDotBelowReorderedText);

        var ex = Assert.Throws<InvalidOperationException>(() => rule.Compile());
        Assert.That(ex!.Message, Does.Contain("FormC"));
    }

    [Test]
    public void Hangul_precomposed_and_decomposed_match_same_grammar_under_FormC()
    {
        // Hangul "han" U+D55C precomposed. Canonical decomposition
        // is U+1112 + U+1161 + U+11AB (three jamo). NFC composes the
        // jamo back to U+D55C, so a grammar with Token(precomposed)
        // matches both forms. UAX #15 has special-case rules for
        // Hangul composition.
        var rule = AllOf(Token(UnicodeExamples.HangulHanGrapheme), Eof());

        var precomposed = rule.Parse(UnicodeExamples.HangulHanGrapheme);
        var decomposed = rule.Parse(UnicodeExamples.HangulHanDecomposedText);

        Assert.That(precomposed.Success, Is.True, "precomposed");
        Assert.That(decomposed.Success, Is.True,
            "NFC composes the three jamo back to U+D55C");
    }

    [Test]
    public void Compile_throws_for_decomposed_Hangul_jamo_under_FormC()
    {
        // The flip side of the test above: a grammar literal in
        // decomposed-jamo form would silently never match any input
        // under FormC, because every input gets canonicalized
        // (composed back to U+D55C) before the lexer sees it. The
        // Compile validation pass catches this.
        var rule = Token(UnicodeExamples.HangulHanDecomposedText);

        var ex = Assert.Throws<InvalidOperationException>(() => rule.Compile());
        Assert.That(ex!.Message, Does.Contain("FormC"));
    }

    [Test]
    public void Compile_throws_for_canonical_singleton_Angstrom()
    {
        // U+212B ANGSTROM SIGN canonically decomposes to U+00C5 LATIN
        // CAPITAL LETTER A WITH RING ABOVE. NFC rewrites the Angstrom
        // form to U+00C5 before the lexer sees the input. So a grammar
        // with Token("Å") under FormC would silently never match.
        // The new compile-time validation pass catches this and tells
        // the author to use U+00C5 instead.
        var badRule = Token(UnicodeExamples.AngstromGrapheme);
        var ex = Assert.Throws<InvalidOperationException>(() => badRule.Compile());
        Assert.That(ex!.Message, Does.Contain("FormC"));
        Assert.That(ex.Message, Does.Contain("U+212B").Or.Contains("U+00C5"),
            "the message names either the offending literal or its " +
            "FormC-normalized replacement");

        // Positive case: a grammar with the canonical replacement
        // (U+00C5) compiles fine and matches input typed as the
        // singleton (U+212B), because NFC folds U+212B to U+00C5
        // before the lexer runs.
        var goodRule = AllOf(Token(UnicodeExamples.LatinCapitalAWithRingAboveGrapheme), Eof());
        Assert.That(goodRule.Parse(UnicodeExamples.AngstromGrapheme).Success, Is.True,
            "U+00C5 grammar matches U+212B input under FormC");
    }

    [Test]
    public void Compile_throws_for_Angstrom_singleton_under_FormD()
    {
        // Under FormD the Angstrom decomposes to A + combining ring,
        // and the literal text U+212B matches its own FormD only by
        // accident. Actually NFD of U+212B is "Å" (A + ring),
        // so the literal does NOT match its own FormD. Lock in the
        // Compile-time error here.
        var badRule = Token(UnicodeExamples.AngstromGrapheme);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            badRule.Compile(NormalizationForm.FormD));
        Assert.That(ex!.Message, Does.Contain("FormD"));

        // Positive case: a grammar with the FormD decomposed form
        // ("A" + combining ring) compiles fine and matches input
        // typed as the Angstrom singleton, because NFD decomposes
        // U+212B to that exact two-rune sequence.
        var goodRule = AllOf(Token(UnicodeExamples.LatinAWithRingAboveDecomposedText), Eof());
        goodRule.Compile(NormalizationForm.FormD);
        Assert.That(goodRule.Parse(UnicodeExamples.AngstromGrapheme).Success, Is.True,
            "decomposed grammar matches U+212B input under FormD");
    }

    [Test]
    public void Compile_throws_for_canonical_singleton_Ohm_under_FormC()
    {
        // U+2126 OHM SIGN is a canonical singleton: it canonically
        // decomposes to U+03A9 GREEK CAPITAL LETTER OMEGA. NFC
        // rewrites U+2126 to U+03A9. A grammar with the Ohm form
        // would silently never match.
        var badRule = Token(UnicodeExamples.OhmGrapheme);
        var ex = Assert.Throws<InvalidOperationException>(() => badRule.Compile());
        Assert.That(ex!.Message, Does.Contain("FormC"));

        // Positive case: a grammar with U+03A9 (Greek capital Omega)
        // matches input typed as the Ohm singleton (U+2126), because
        // NFC folds U+2126 to U+03A9 before the lexer runs.
        var goodRule = AllOf(Token(UnicodeExamples.GreekCapitalOmegaGrapheme), Eof());
        Assert.That(goodRule.Parse(UnicodeExamples.OhmGrapheme).Success, Is.True,
            "U+03A9 grammar matches U+2126 input under FormC");
    }

    [Test]
    public void Compile_throws_for_canonical_singleton_Kelvin_under_FormC()
    {
        // U+212A KELVIN SIGN is a canonical singleton: it canonically
        // decomposes to plain U+004B LATIN CAPITAL LETTER K. NFC
        // rewrites U+212A to U+004B. The third member of the
        // Angstrom / Ohm / Kelvin trio.
        var badRule = Token(UnicodeExamples.KelvinGrapheme);
        var ex = Assert.Throws<InvalidOperationException>(() => badRule.Compile());
        Assert.That(ex!.Message, Does.Contain("FormC"));

        // Positive case: a grammar with ASCII 'K' (U+004B) matches
        // input typed as the Kelvin singleton (U+212A), because NFC
        // folds U+212A to U+004B before the lexer runs.
        var goodRule = AllOf(Token(UnicodeExamples.AsciiCapitalKGrapheme), Eof());
        Assert.That(goodRule.Parse(UnicodeExamples.KelvinGrapheme).Success, Is.True,
            "ASCII K grammar matches U+212A input under FormC");
    }

    [Test]
    public void Compile_throws_for_canonical_singleton_angle_bracket_under_FormC()
    {
        // U+2329 LEFT-POINTING ANGLE BRACKET is a canonical singleton:
        // canonically decomposes to U+3008 LEFT ANGLE BRACKET (the
        // CJK angle bracket). NFC rewrites U+2329 to U+3008. The
        // less-famous canonical singleton; not a Latin/Greek
        // duplicate but the same mechanism.
        var badRule = Token(UnicodeExamples.LeftPointingAngleBracketGrapheme);
        var ex = Assert.Throws<InvalidOperationException>(() => badRule.Compile());
        Assert.That(ex!.Message, Does.Contain("FormC"));

        // Positive case: a grammar with U+3008 (CJK angle bracket)
        // matches input typed as U+2329, because NFC folds U+2329
        // to U+3008 before the lexer runs.
        var goodRule = AllOf(Token(UnicodeExamples.CjkLeftAngleBracketGrapheme), Eof());
        Assert.That(goodRule.Parse(UnicodeExamples.LeftPointingAngleBracketGrapheme).Success, Is.True,
            "U+3008 grammar matches U+2329 input under FormC");
    }

    [Test]
    public void Compile_accepts_compatibility_singleton_under_FormC()
    {
        // U+2102 DOUBLE-STRUCK CAPITAL C is a compatibility singleton:
        // its compatibility decomposition is U+0043 plain C, but its
        // canonical decomposition is itself. FormC only does canonical
        // decompositions, so U+2102 passes through NFC unchanged.
        // The grammar literal in the source character matches its own
        // FormC, so Compile validation accepts it.
        var rule = Token(UnicodeExamples.DoubleStruckCGrapheme);

        Assert.DoesNotThrow(() => rule.Compile());
    }

    [Test]
    public void Compile_throws_for_compatibility_singleton_under_FormKC()
    {
        // Same character (U+2102 DOUBLE-STRUCK CAPITAL C). Under
        // FormKC, NFKC also applies compatibility decompositions,
        // so U+2102 normalizes to U+0043 plain C. A grammar literal
        // in the source character would silently never match. The
        // Compile validation pass catches this exactly the same way
        // it catches canonical singletons under FormC.
        var badRule = Token(UnicodeExamples.DoubleStruckCGrapheme);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            badRule.Compile(NormalizationForm.FormKC));
        Assert.That(ex!.Message, Does.Contain("FormKC"));

        // Positive case: a grammar with ASCII 'C' (U+0043) compiled
        // for FormKC matches input typed as the double-struck C
        // singleton (U+2102), because NFKC folds U+2102 to U+0043
        // before the lexer runs. This is the matching-by-meaning
        // path: the author opts into FormKC to treat presentation
        // variants as equivalent to their plain forms.
        var goodRule = AllOf(Token(UnicodeExamples.AsciiCapitalCGrapheme), Eof());
        goodRule.Compile(NormalizationForm.FormKC);
        Assert.That(goodRule.Parse(UnicodeExamples.DoubleStruckCGrapheme).Success, Is.True,
            "ASCII C grammar matches U+2102 input under FormKC");
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
        var rule = AllOf(Identifier(), Eof());
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
        Assert.That(AllOf(Literal("hello"), Eof()).Parse(input).Success, Is.False);

        // (2) AnyToken at the front consumes the NULL byte as a
        // wildcard.
        Assert.That(AllOf(AnyToken(), Literal("hello"), Eof()).Parse(input).Success, Is.True);

        // (3) Targeting U+0000 specifically via Token(...) also
        // works. Useful for a binary-friendly format that uses
        // NUL as a separator.
        Assert.That(AllOf(Token(UnicodeExamples.NullText), Literal("hello"), Eof()).Parse(input).Success, Is.True);
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
        var rule = AllOf(AnyToken(), Eof());
        string input = UnicodeExamples.ManEmojiGrapheme + UnicodeExamples.ZeroWidthJoinerText;
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.True);
    }
}
