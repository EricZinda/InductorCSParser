using System;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TestHelpers;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Tests for Rule.Compile(NormalizationForm?). Two promises the feature has to keep:
//
//   1. The input is rewritten into the form NormalizeInput names (NFC by
//      default) before the lexer sees a single character. So a grammar
//      whose literals are already in that form matches input in either
//      composition style. Precomposed "café" (U+00E9) and decomposed
//      "cafe\u0301" both normalize to the same NFC string, so a grammar
//      literal written as the precomposed "café" accepts both. The
//      default works for almost everyone because string literals in source
//      files saved by any modern editor are already NFC, so a literal
//      "é" typed in code is exactly the precomposed form the normalizer
//      produces. A grammar that hand-builds decomposed literals out of
//      explicit escapes (Token("e\u0301")) under the default NFC
//      normalization will not match, since the input gets composed out
//      from under it.
//
//   2. Positions reported in ParseResult (ErrorCharIndex and its derived
//      properties) index into the CALLER'S ORIGINAL input string, never
//      into the normalized form. An editor forwarding the error straight
//      into an LSP diagnostic sees offsets that line up with the document
//      it knows about.
[TestFixture]
public class NormalizationTests
{
    // "café" with precomposed é (U+00E9). Each char is one grapheme.
    private const string CafePrecomposed = "caf\u00E9";
    // "cafe\u0301": same rendered text, decomposed. Five chars, four graphemes
    // (the fourth is "e" + combining acute).
    private const string CafeDecomposed = "cafe" + CombiningAcuteText;

    // Grammar for "café" spelled in the precomposed form that most grammar
    // authors write. Four Token rules in sequence.
    private static Rule CafeRule() =>
        And(Token('c'), Token('a'), Token('f'), Token("\u00E9"));

    [Test]
    public void Default_NFC_matches_precomposed_input_against_precomposed_grammar()
    {
        // Baseline: when input and grammar literals are both already in NFC,
        // default normalization is effectively a no-op and the parse just
        // works. The interesting case is the next test, where the input is
        // decomposed and only matches because the normalizer composes it.
        var result = And(CafeRule(), Eof()).Parse(CafePrecomposed);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Default_NFC_matches_decomposed_input_against_precomposed_grammar()
    {
        // The headline promise: the same precomposed grammar matches the
        // decomposed form of the same word. Without normalization the
        // comparison at Token("\u00E9") would see "e" and fail.
        var result = And(CafeRule(), Eof()).Parse(CafeDecomposed);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void NormalizeInput_null_fails_decomposed_input_against_precomposed_grammar()
    {
        // Opt out of normalization and the precomposed vs decomposed
        // distinction appears.
        var rule = And(CafeRule(), Eof());
        rule.Compile(null);
        var result = rule.Parse(CafeDecomposed);
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void NormalizeInput_null_passes_precomposed_input_against_precomposed_grammar()
    {
        // Turning off normalization only matters when the input would have
        // been rewritten. Precomposed "café" is already in NFC, so opting
        // out changes nothing here and the parse still succeeds.
        var rule = And(CafeRule(), Eof());
        rule.Compile(null);
        var result = rule.Parse(CafePrecomposed);
        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void Failure_position_indexes_into_original_not_normalized_for_decomposed_input()
    {
        // Input is eight chars: 'c','a','f','e','\u0301','x','y','z'. After
        // NFC the internal form is seven chars: 'c','a','f','\u00E9','x','y','z'.
        // Grammar matches café, then expects '1', sees 'x', fails.
        //
        // In NORMALIZED coordinates the failure is at index 4 (the 'x'
        // sitting right after the composed 'é'). If the parser leaked that
        // index out, callers would scratch their heads: input[4] is
        // '\u0301', not 'x'. The contract is original coordinates, so the
        // reported index has to be 5 (the 'x' in the caller's input).
        string input = CafeDecomposed + "xyz";
        var rule = And(CafeRule(), Token('1'), Eof());
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
        // Original chars: c(0) a(1) f(2) e(3) acute(4) x(5) y(6) z(7).
        // Graphemes: c=0, a=1, f=2, é=3 (e+acute is one grapheme), x=4,
        // y=5, z=6. The 'x' is char 5, rune 5, grapheme 4.
        AssertErrorPosition(result,
            charIndex: 5, line: 0, column: 5,
            TokenIndex: 4);
        Assert.That(input[result.ErrorCharIndex], Is.EqualTo('x'),
            "the char at ErrorCharIndex should be the one the grammar rejected");
    }

    [Test]
    public void Failure_inside_composed_sequence_snaps_to_grapheme_start_in_original()
    {
        // Grammar matches "caf" then expects '1' and sees 'é'. In NORMALIZED
        // space the failing position is 3 (the precomposed 'é'). The failing
        // grapheme in the decomposed original is "é" at indexes 3..4,
        // and ErrorCharIndex reports the START of that grapheme (3). That's
        // what an editor needs to highlight the whole bad grapheme rather
        // than landing in the middle of a combining sequence.
        string input = CafeDecomposed;
        var rule = And(Token('c'), Token('a'), Token('f'), Token('1'), Eof());
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
        // "café" graphemes: c=0, a=1, f=2, é=3. Char 3 is the 'e'
        // that starts the failing 'é' grapheme.
        AssertErrorPosition(result,
            charIndex: 3, line: 0, column: 3,
            TokenIndex: 3);
        Assert.That(input[result.ErrorCharIndex], Is.EqualTo('e'));
    }

    [Test]
    public void Already_normalized_input_reports_positions_identical_to_null_option()
    {
        // The common case: input is already in FormC, so normalization
        // shouldn't change the text. Failure positions must come out
        // identical to the no-normalization path whether the runtime
        // returns the same string reference or an equivalent one.
        string input = CafePrecomposed + "X";
        var rule = And(CafeRule(), Token('1'), Eof());

        var ruleWithNullForm = And(CafeRule(), Token('1'), Eof());
        ruleWithNullForm.Compile(null);

        var withNfc = rule.Parse(input);
        var withoutNormalization = ruleWithNullForm.Parse(input);

        Assert.That(withNfc.Success, Is.False);
        Assert.That(withoutNormalization.Success, Is.False);
        AssertErrorPositionsEqual(expected: withoutNormalization, actual: withNfc);
    }

    [Test]
    public void NormalizeInput_null_reports_positions_exactly_into_decomposed_input()
    {
        // With normalization off, the lexer sees the original input string and
        // positions are trivially into the original. This test locks in
        // that baseline so a future refactor can't silently regress it.
        string input = CafeDecomposed + "X";
        var rule = And(Token('c'), Token('a'), Token('f'), Token('X'), Eof());
        rule.Compile(null);
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
        // Grammar consumed "caf" then wanted 'X' but got 'e'. The failing
        // grapheme is 'e' + combining acute starting at char 3.
        // Graphemes: c=0, a=1, f=2, é=3.
        AssertErrorPosition(result,
            charIndex: 3, line: 0, column: 3,
            TokenIndex: 3);
        Assert.That(input[result.ErrorCharIndex], Is.EqualTo('e'));
    }

    [Test]
    public void NFC_default_is_FormC()
    {
        // Lock in the default so a careless refactor that flipped it to
        // FormD or null failse. The form lives on the compiled rule
        // (set by Compile) and the default is FormC, matching the
        // historical ParseOptions.NormalizeInput default.
        var rule = And(Token('a'), Eof()).Compile();
        Assert.That(rule.NormalizationForm, Is.EqualTo(NormalizationForm.FormC));
    }

    [Test]
    public void Abort_via_work_limit_reports_position_into_original_not_normalized()
    {
        // Budget-abort path shares the same translation code as
        // grammar-mismatch failure. Prove it by tripping the work limit on
        // decomposed input and asserting the index lands inside the
        // caller's original string, not past its end.
        //
        // The decomposed form is 2 chars per grapheme. NFC squashes it to
        // 1 char. If the parser leaked the normalized-space lexer position
        // out unchanged, the assertion that ErrorCharIndex is within the
        // *original* input length would catch it. Input has to be long
        // enough for OneOrMore to cross BudgetCheckInterval (1024) and
        // trigger the periodic rule-count check.
        string chunk = "e" + CombiningAcuteText;
        string input = string.Concat(Enumerable.Repeat(chunk, 5000));
        var rule = OneOrMore(AnyToken());
        var result = rule.Parse(input,
            new ParseOptions { RuleCountLimit = 10 });

        Assert.That(result.Outcome, Is.EqualTo(ParseOutcome.RuleCountLimitExceeded));
        Assert.That(result.ErrorCharIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(result.ErrorCharIndex, Is.LessThanOrEqualTo(input.Length),
            "abort position must be a valid index into the caller's original input");
        Assert.That(result.ErrorLine, Is.EqualTo(0), "input has no newlines");
        Assert.That(result.ErrorColumn, Is.EqualTo(result.ErrorCharIndex),
            "single-line input means column equals char index");
        var position = result.ErrorPosition;
        Assert.That(position, Is.Not.Null);
        Assert.That(position!.Value.CharIndex, Is.EqualTo(result.ErrorCharIndex),
            "ErrorPosition bundle must agree with ErrorCharIndex");
    }

    // Compatibility-form tests. FormKC and FormKD convert ligatures, circled
    // digits, fullwidth forms, superscripts, and similar cosmetic
    // variations into their plain-text equivalents. The conversion can expand
    // one grapheme into several ("\uFB01" → "fi", two graphemes), which
    // is why the translator uses a per-grapheme normalize walker for these
    // forms rather than the lockstep walker.
    //
    // U+FB01 is LATIN SMALL LIGATURE FI, the textbook compatibility-
    // conversion example: one rune, one grapheme in the original.
    // Normalizes to "fi" (two runes, two graphemes).
    private const string FiLigature = "\uFB01";

    [Test]
    public void FormKC_converts_ligature_so_plain_grammar_matches_ligature_input()
    {
        // Grammar spells "fish" in the plain ASCII form. Input uses the fi
        // ligature. Without normalization the Token('f') would see "\uFB01"
        // and fail. FormKC converts the ligature to "fi" before the lexer
        // runs, so the grammar matches through.
        var rule = And(Token('f'), Token('i'), Token('s'), Token('h'), Eof());
        rule.Compile(NormalizationForm.FormKC);
        var result = rule.Parse(FiLigature + "sh");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void FormKC_failure_after_ligature_reports_original_position()
    {
        // Input is the ligature (1 char) followed by "sh" (2 chars),
        // 3 chars total. Normalized is "fish" (4 chars). Grammar matches
        // f, i, then expects 'X' and sees 's'. Failure in NORMALIZED
        // coordinates is at index 2 (the 's'). In ORIGINAL coordinates
        // 's' sits at index 1, right after the 1-char ligature.
        string input = FiLigature + "sh";
        var rule = And(Token('f'), Token('i'), Token('X'));
        rule.Compile(NormalizationForm.FormKC);
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
        // Original chars: ﬁ(0) s(1) h(2). Each is its own grapheme
        // and rune in the original (the ligature is one rune, one grapheme),
        // so char/rune/grapheme indices line up at position 1.
        AssertErrorPosition(result,
            charIndex: 1, line: 0, column: 1,
            TokenIndex: 1);
        Assert.That(input[result.ErrorCharIndex], Is.EqualTo('s'));
    }

    [Test]
    public void FormKC_failure_inside_ligature_expansion_snaps_to_ligature_start()
    {
        // Input is just the ligature (1 char). Normalized is "fi" (2
        // chars). Grammar matches 'f' then expects 'X' and sees 'i'.
        // Failure in NORMALIZED coordinates is at index 1, which sits
        // INSIDE the ligature's expansion (the original ligature has no
        // index 1). The translator has to snap back to the start of the
        // ligature grapheme at index 0.
        string input = FiLigature;
        var rule = And(Token('f'), Token('X'));
        rule.Compile(NormalizationForm.FormKC);
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
        // Original input is just the ligature: char 0, rune 0, grapheme 0,
        // line 0, column 0. The snap lands on the ligature start in
        // every unit.
        AssertErrorPosition(result,
            charIndex: 0, line: 0, column: 0,
            TokenIndex: 0);
    }

    [Test]
    public void FormKD_converts_ligature_and_ends_decomposed()
    {
        // FormKD does the same compatibility conversion as FormKC but the
        // output is decomposed. For a pure-ASCII conversion target ("fi") there's
        // no canonical decomposition, so FormKD output matches FormKC
        // output here. This test verifies that the FormKD path through
        // the translator works end-to-end, not that the decomposed
        // endpoint differs for this particular input.
        var rule = And(Token('f'), Token('i'), Token('s'), Token('h'), Eof());
        rule.Compile(NormalizationForm.FormKD);
        var result = rule.Parse(FiLigature + "sh");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void FormKD_failure_position_translates_through_per_grapheme_walker()
    {
        // Same position-snap case as the FormKC test above, but through
        // FormKD to prove the compatibility-form dispatch catches both.
        string input = FiLigature;
        var rule = And(Token('f'), Token('X'));
        rule.Compile(NormalizationForm.FormKD);
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
        AssertErrorPosition(result,
            charIndex: 0, line: 0, column: 0,
            TokenIndex: 0);
    }

    // Compile-time validation pass tests. The form chosen at Compile is
    // checked against every literal-bearing rule's expected text. A rule
    // whose text isn't already in that form would silently never match
    // (the lexer normalizes input, so the literal would be looking for
    // bytes the lexer can't produce). Compile catches that at startup.

    [Test]
    public void Compile_auto_converts_literal_to_FormC()
    {
        // Build a rule whose Token literal is decomposed (e + combining
        // acute, two runes that render as one user-visible character).
        // Default Compile uses FormC, which composes the two runes into
        // U+00E9. The lexer would never produce a two-rune "e+acute" for
        // this rule to match. Compile catches that at grammar-build time.
        var rule = Token(CafeDecomposed[3..]);  // a one-grapheme decomposed form
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse(CafePrecomposed[3..]).Success, Is.True);
        Assert.That(rule.Parse(CafeDecomposed[3..]).Success, Is.True);
    }
    [Test]
    public void Compile_null_skips_validation()
    {
        // Same decomposed-Token rule. With null normalization the
        // validation pass is skipped entirely (the author opted out of
        // normalization, so any literal form is acceptable).
        var rule = Token(CafeDecomposed[3..]);
        Assert.DoesNotThrow(() => rule.Compile(null));
        Assert.That(rule.NormalizationForm, Is.Null);
    }

    [Test]
    public void Compile_FormD_accepts_decomposed()
    {
        // The decomposed literal IS in FormD already, so compiling
        // against FormD passes validation.
        var rule = Token(CafeDecomposed[3..]);
        Assert.DoesNotThrow(() => rule.Compile(NormalizationForm.FormD));
        Assert.That(rule.NormalizationForm, Is.EqualTo(NormalizationForm.FormD));
    }

    [Test]
    public void Compile_auto_converts_two_bad_literals_in_one_grammar()
    {
        // Both literals get auto-converted at Compile. The grammar
        // compiles cleanly and parses correctly.
        var firstLiteral = Literal("e" + CombiningAcuteText).As("first");
        var secondLiteral = Token(CafeDecomposed[3..]).As("second");
        var rule = And(firstLiteral, secondLiteral);

        Assert.DoesNotThrow(() => rule.Compile());
        // Parse a precomposed-form input — the auto-converted literals match.
        Assert.That(rule.Parse(CafePrecomposed[3..] + CafePrecomposed[3..]).Success, Is.True);
    }

    [Test]
    public void Compile_LiteralIgnoreAsciiCase_rejects_non_ASCII_pattern_at_construction()
    {
        // Pure-ASCII LiteralIgnoreAsciiCase compiles fine under any
        // form (ASCII is invariant under all four NFC/NFD/NFKC/NFKD).
        Assert.DoesNotThrow(() =>
            LiteralIgnoreAsciiCase("HELLO").Compile());

        // Mixed ASCII + non-ASCII patterns throw at construction.
        // ASCII case-folding doesn't apply to non-ASCII code points,
        // so a non-ASCII char in a LiteralIgnoreAsciiCase pattern would
        // silently behave as a bit-exact compare and mislead the reader.
        // Grammars that want a non-ASCII keyword should use Literal(...).
        Assert.Throws<ArgumentException>(() =>
            LiteralIgnoreAsciiCase("caf" + "e" + CombiningAcuteText));
        // Literal accepts non-ASCII content, including decomposed
        // combining-mark sequences. Compile then auto-converts under
        // the chosen form.
        var literalRule = Literal("caf" + "e" + CombiningAcuteText);
        Assert.DoesNotThrow(() => literalRule.Compile());
        Assert.That(literalRule.Parse(CafePrecomposed).Success, Is.True);
        Assert.That(literalRule.Parse(CafeDecomposed).Success, Is.True);
    }

    [Test]
    public void Compile_with_different_form_after_first_throws()
    {
        // The form is committed at first compile. A subsequent Compile
        // with a different form throws because the grammar's identity
        // (and the validation result) is tied to the first chosen form.
        var rule = And(Token('a'), Token('b'));
        rule.Compile();  // FormC default

        var ex = Assert.Throws<InvalidOperationException>(() =>
            rule.Compile(NormalizationForm.FormD));
        Assert.That(ex!.Message, Does.Contain("already been compiled"));
        Assert.That(ex.Message, Does.Contain("FormC"));
        Assert.That(ex.Message, Does.Contain("FormD"));
    }

    [Test]
    public void Compile_with_same_form_is_idempotent()
    {
        // Re-Compile with the same form is a no-op (matching the
        // existing _sealed early-return contract). Important because
        // Parse triggers an auto-Compile that should never throw on
        // an already-compiled grammar.
        var rule = And(Token('a'), Token('b'));
        rule.Compile();
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.DoesNotThrow(() => rule.Compile(NormalizationForm.FormC));
    }

    [Test]
    public void NormalizationForm_property_returns_compiled_form()
    {
        // The public read-only NormalizationForm property reflects what
        // was passed to Compile. Callers and tests can introspect a
        // compiled grammar's form without parsing.
        var ruleC = And(Token('a'), Token('b'));
        ruleC.Compile();
        Assert.That(ruleC.NormalizationForm, Is.EqualTo(NormalizationForm.FormC));

        var ruleNull = And(Token('a'), Token('b'));
        ruleNull.Compile(null);
        Assert.That(ruleNull.NormalizationForm, Is.Null);

        var ruleKC = And(Token('a'), Token('b'));
        ruleKC.Compile(NormalizationForm.FormKC);
        Assert.That(ruleKC.NormalizationForm, Is.EqualTo(NormalizationForm.FormKC));
    }

    // The same grammar source, compiled four ways, all matching their
    // respective normalized inputs. This is the security-scanning use case
    // for Compile's auto-conversion: instead of writing four separate
    // grammars (one per form) to detect homoglyph and presentation-variant
    // attacks across normalization boundaries, the author writes one
    // identifier grammar and runs it under each form. A string that parses
    // under FormC but fails under FormKC (or vice versa) is the attack
    // signature.
    [Test]
    public void Same_identifier_grammar_compiles_under_all_four_forms_and_matches_appropriate_input()
    {
        // Grammar source: one identifier with EOF to anchor the match.
        // Built fresh for each form so each rule has its own
        // _set / _expected mutated by Compile.
        static Rule BuildIdentifierGrammar(NormalizationForm? form) =>
            And(Identifier(form), Eof());

        var ruleC = BuildIdentifierGrammar(NormalizationForm.FormC);
        ruleC.Compile(NormalizationForm.FormC);
        var ruleD = BuildIdentifierGrammar(NormalizationForm.FormD);
        ruleD.Compile(NormalizationForm.FormD);
        var ruleKC = BuildIdentifierGrammar(NormalizationForm.FormKC);
        ruleKC.Compile(NormalizationForm.FormKC);
        var ruleKD = BuildIdentifierGrammar(NormalizationForm.FormKD);
        ruleKD.Compile(NormalizationForm.FormKD);

        // Plain ASCII identifier matches under every form.
        Assert.That(ruleC.Parse("foo").Success, Is.True, "FormC accepts plain ASCII");
        Assert.That(ruleD.Parse("foo").Success, Is.True, "FormD accepts plain ASCII");
        Assert.That(ruleKC.Parse("foo").Success, Is.True, "FormKC accepts plain ASCII");
        Assert.That(ruleKD.Parse("foo").Success, Is.True, "FormKD accepts plain ASCII");

        // "café" with precomposed é matches under both canonical forms.
        // FormC sees U+00E9, FormD sees "e + combining acute" — both are
        // canonically equivalent and both are valid identifiers.
        Assert.That(ruleC.Parse(CafePrecomposed).Success, Is.True);
        Assert.That(ruleD.Parse(CafePrecomposed).Success, Is.True);
        Assert.That(ruleC.Parse(CafeDecomposed).Success, Is.True);
        Assert.That(ruleD.Parse(CafeDecomposed).Success, Is.True);

        // The homoglyph-detection pattern in practice: take a single
        // input, parse it under each form's compiled grammar, and diff
        // the resulting trees. If they match, the input has no
        // presentation variants the chosen forms disagree on. If they
        // diverge, the input is a candidate for review. The scanner
        // doesn't need to know the input ahead of time; the divergence
        // is the signal.
        var asciiViaC = ruleC.Parse("foo");
        var asciiViaKC = ruleKC.Parse("foo");
        Assert.That(asciiViaC.Success && asciiViaKC.Success, Is.True);
        Assert.That(asciiViaC.Tree!.ToString(), Is.EqualTo(asciiViaKC.Tree!.ToString()),
            "plain ASCII input: same tree under FormC and FormKC, no signal");

        // Same grammar, but the input has a presentation variant
        // (fullwidth letters that NFKC collapses to ASCII). Both forms
        // accept it as a valid identifier, but the tree text differs:
        // FormC preserves the original code points, FormKC collapses
        // them. That divergence is what a multi-form scanner flags.
        var presentationVariantViaC = ruleC.Parse("ｆｏｏ");
        var presentationVariantViaKC = ruleKC.Parse("ｆｏｏ");
        Assert.That(presentationVariantViaC.Success && presentationVariantViaKC.Success, Is.True);
        Assert.That(presentationVariantViaC.Tree!.ToString(),
            Is.Not.EqualTo(presentationVariantViaKC.Tree!.ToString()),
            "presentation-variant input: tree diverges between FormC and FormKC, " +
            "which is the signal a homoglyph scanner watches for");
    }

    // ============================================================
    // Set / token / literal auto-conversion across FormC, FormD,
    // and FormKC. Compile rewrites the rule's literal into the
    // chosen form so a grammar author who typed the "wrong" side
    // (precomposed under FormD, decomposed under FormC, ligature
    // under FormKC, etc.) gets a working grammar instead of a
    // silent miss.
    // ============================================================

    [Test]
    public void OneOf_with_precomposed_entry_matches_decomposed_input_under_FormD()
    {
        // OneOf with the precomposed U+00E9 stores it as a single-rune
        // entry. Under FormD the lexer decomposes input to "e + combining
        // acute" (multi-rune cluster), which a rune-only set can't
        // represent. Compile-time set normalization (adding canonical
        // equivalents to the set) adds the decomposed form as a multi-
        // rune entry, so the rule matches both forms regardless of
        // which canonical form the lexer produces.
        var rule = OneOf(TokenSet.Graphemes(LatinEAcutePrecomposedGrapheme));
        rule.Compile(System.Text.NormalizationForm.FormD);

        var precomposed = rule.Parse(LatinEAcutePrecomposedGrapheme);
        var decomposed = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(precomposed.Success, Is.True, precomposed.ErrorMessage);
        Assert.That(decomposed.Success, Is.True, decomposed.ErrorMessage);
    }

    [Test]
    public void OneOf_with_decomposed_entry_matches_precomposed_input_under_FormC()
    {
        // Symmetric direction: user types decomposed in source, FormC
        // (the default) recomposes the input. Set normalization adds
        // the composed single-rune form to the set so both inputs match.
        var rule = OneOf(TokenSet.Graphemes(LatinEAcuteGrapheme));
        rule.Compile();  // default FormC

        var precomposed = rule.Parse(LatinEAcutePrecomposedGrapheme);
        var decomposed = rule.Parse(LatinEAcuteGrapheme);

        Assert.That(precomposed.Success, Is.True, precomposed.ErrorMessage);
        Assert.That(decomposed.Success, Is.True, decomposed.ErrorMessage);
    }

    [Test]
    public void NoneOf_with_precomposed_entry_rejects_decomposed_input_under_FormD()
    {
        // Without set normalization, NoneOf(precomposed-é).Parse(decomposed)
        // under FormD would wrongly succeed: the decomposed multi-rune
        // token isn't in the rune-only set, the membership test returns
        // false, NoneOf inverts that into a false-positive match. Set
        // normalization adds the decomposed cluster to the set so NoneOf
        // correctly rejects both forms.
        var rule = And(NoneOf(TokenSet.Graphemes(LatinEAcutePrecomposedGrapheme)), Eof());
        rule.Compile(System.Text.NormalizationForm.FormD);

        Assert.That(rule.Parse(LatinEAcutePrecomposedGrapheme).Success, Is.False);
        Assert.That(rule.Parse(LatinEAcuteGrapheme).Success, Is.False);
        // Sanity: a different character still passes through.
        Assert.That(rule.Parse("a").Success, Is.True);
    }

    [Test]
    public void OneOf_with_singleton_decomposing_rune_matches_normalized_form()
    {
        // U+2126 OHM SIGN canonically decomposes to U+03A9 GREEK CAPITAL
        // LETTER OMEGA — both single runes. Under FormC the lexer
        // produces U+03A9 from input U+2126. Without set normalization,
        // OneOf(Ohm) wouldn't match because its set has only U+2126.
        // Compile-time set normalization covers this case (entries
        // where the decomposed form differs from the rune but is still
        // single-rune) by adding U+03A9 to the rune intervals.
        var rule = OneOf(OhmGrapheme);
        rule.Compile();  // default FormC

        Assert.That(rule.Parse(OhmGrapheme).Success, Is.True);
        Assert.That(rule.Parse(GreekCapitalOmegaGrapheme).Success, Is.True);
    }

    [Test]
    public void Token_with_decomposed_source_compiles_under_FormC_and_matches_input()
    {
        // User typed "e" + combining acute (decomposed) in the grammar
        // source. Compile under FormC auto-converts the _expected text
        // to the precomposed U+00E9 form. The rule then matches FormC-
        // normalized input (also U+00E9).
        var rule = Token(LatinEAcuteGrapheme);
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse(LatinEAcutePrecomposedGrapheme).Success, Is.True);
        Assert.That(rule.Parse(LatinEAcuteGrapheme).Success, Is.True);
    }

    [Test]
    public void Token_with_precomposed_source_compiles_under_FormD_and_matches_input()
    {
        // Symmetric: user typed precomposed U+00E9. Compile under FormD
        // auto-converts _expected to the decomposed two-rune form so the
        // rule matches FormD-normalized input.
        var rule = Token(LatinEAcutePrecomposedGrapheme);
        Assert.DoesNotThrow(() => rule.Compile(NormalizationForm.FormD));
        Assert.That(rule.Parse(LatinEAcutePrecomposedGrapheme).Success, Is.True);
        Assert.That(rule.Parse(LatinEAcuteGrapheme).Success, Is.True);
    }

    [Test]
    public void Literal_with_mixed_form_source_compiles_under_FormC_and_matches_input()
    {
        // "caf" + decomposed e-acute in source. Compile under FormC
        // auto-converts _expected so it matches FormC-normalized input
        // (precomposed U+00E9 at the end).
        var rule = Literal("caf" + LatinEAcuteGrapheme);
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse("caf" + LatinEAcutePrecomposedGrapheme).Success, Is.True);
        Assert.That(rule.Parse("caf" + LatinEAcuteGrapheme).Success, Is.True);
    }

    [Test]
    public void OneOf_letters_matches_tibetan_composite_letter_under_default_Compile()
    {
        // U+0F43 TIBETAN LETTER GHA is in TokenSet.Letters. It
        // decomposes to U+0F42 + U+0FB7 (a multi-rune cluster) under
        // both composed and decomposed forms. Under default FormC the
        // lexer hands the rule that two-rune cluster.
        // OneOf(TokenSet.Letters).Compile() must set-normalize the set
        // (add the multi-rune composed equivalent) so the rule still matches.
        var rule = And(OneOf(TokenSet.Letters), Eof());
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse("གྷ").Success, Is.True,
            "single-rune tibetan letter still matches");
        Assert.That(rule.Parse("གྷ").Success, Is.True,
            "decomposed tibetan letter matches via set normalization");
    }

    [Test]
    public void OneOf_TokenSet_Single_with_decomposable_rune_matches_under_FormD()
    {
        // OneOf(TokenSet.Single(0xE9)) goes through the TokenSet path,
        // not the string path. Compile-time set normalization must
        // still add the decomposed cluster so the rule matches both
        // forms of input under FormD.
        var rule = OneOf(TokenSet.Single(0x00E9));
        Assert.DoesNotThrow(() => rule.Compile(NormalizationForm.FormD));
        Assert.That(rule.Parse(LatinEAcutePrecomposedGrapheme).Success, Is.True);
        Assert.That(rule.Parse(LatinEAcuteGrapheme).Success, Is.True);
    }

    [Test]
    public void Token_with_singleton_decomposable_rune_repins_id()
    {
        // Token U+2126 OHM SIGN compiled under FormC auto-converts
        // _expected to U+03A9 GREEK CAPITAL OMEGA. The rule's Id (which
        // reflects the single-rune token value) should be re-pinned to
        // 0x03A9, not stay as 0x2126.
        var rule = Token(OhmGrapheme);
        rule.Compile();
        Assert.That(rule.Id.Value, Is.EqualTo(0x03A9),
            "Token Id repins to the converted rune value");
    }

    [Test]
    public void Token_with_singleton_decomposable_rune_preserves_user_pinned_id()
    {
        // Sibling of Token_with_singleton_decomposable_rune_repins_id.
        // The unnamed case re-pins Id to the post-normalization rune,
        // which is desired (leaf-id consistency between Token('Ω') and
        // Token('Ω').Compile(FormC)). But when the user pinned an
        // explicit SymbolId via .As(new SymbolId(...)), Compile must NOT
        // overwrite it. .As(SymbolId) is documented as the
        // stable-numbering hook, useful for serialized parse trees, and
        // a silent re-pin under normalization defeats that promise.
        int pinned = SymbolRanges.CustomRangeStart + 0x100;
        var rule = Token(OhmGrapheme).As(new SymbolId(pinned));
        rule.Compile();
        Assert.That(rule.Id.Value, Is.EqualTo(pinned),
            "User-pinned SymbolId survives canonical-singleton normalization");
    }

    [Test]
    public void Compile_same_grammar_under_FormC_and_FormD_both_succeed()
    {
        // Locks in the security-scanning use case: write the grammar
        // once, compile under multiple forms. Each Compile must succeed
        // independently and match its form's normalized input.
        var ruleC = Token(LatinEAcutePrecomposedGrapheme);
        ruleC.Compile(NormalizationForm.FormC);
        Assert.That(ruleC.Parse(LatinEAcutePrecomposedGrapheme).Success, Is.True);

        var ruleD = Token(LatinEAcutePrecomposedGrapheme);
        ruleD.Compile(NormalizationForm.FormD);
        Assert.That(ruleD.Parse(LatinEAcuteGrapheme).Success, Is.True);
    }

    [Test]
    public void Token_with_compatibility_conversion_to_multiple_graphemes_throws_clear_compile_error()
    {
        // U+FB01 LATIN SMALL LIGATURE FI converts under FormKC to "fi",
        // two separate graphemes. Token matches exactly one grapheme,
        // so no auto-conversion is possible. Compile must throw a clear
        // error naming the multi-grapheme conversion and pointing the
        // user at Literal or And.
        var rule = Token("ﬁ");

        var exception = Assert.Throws<InvalidOperationException>(
            () => rule.Compile(NormalizationForm.FormKC));
        Assert.That(exception!.Message, Does.Contain("fi"),
            "error message shows the multi-grapheme conversion result");
        Assert.That(exception.Message, Does.Contain("Literal").Or.Contain("And"),
            "error message suggests Literal or And as the fix");
    }

    [Test]
    public void OneOf_with_compatibility_conversion_to_multiple_graphemes_throws_clear_compile_error()
    {
        // Same character (U+FB01), same compatibility conversion ("fi").
        // For OneOf the projected set drops U+FB01 because its conversion
        // is multi-grapheme, and the offender mechanism reports it.
        // Compile throws an aggregated InvalidOperationException.
        var rule = OneOf("ﬁ");

        var exception = Assert.Throws<InvalidOperationException>(
            () => rule.Compile(NormalizationForm.FormKC));
        Assert.That(exception!.Message, Does.Contain("fi"),
            "error message shows the multi-grapheme conversion result");
    }

    [Test]
    public void Identifier_default_under_FormKC_throws_clear_compile_error()
    {
        // TokenSet.XidStart contains U+FB01 (LATIN SMALL LIGATURE FI)
        // among many other compatibility-converting letters. Under
        // FormKC U+FB01 converts to "fi" (multi-grapheme), which a
        // OneOf rule can't match as a single token. Strict policy:
        // throw, don't silently miss. The error names the
        // WithCompatibilityEquivalents helper (and the form-aware
        // Identifier overload, by extension) as the fix.
        var rule = Identifier();

        var exception = Assert.Throws<InvalidOperationException>(
            () => rule.Compile(NormalizationForm.FormKC));
        Assert.That(exception!.Message, Does.Contain("WithCompatibilityEquivalents"),
            "error message points users at the helper that resolves multi-grapheme conversions");
    }

    [Test]
    public void OneOf_compatibility_singleton_matches_under_FormKC()
    {
        // U+2102 DOUBLE-STRUCK CAPITAL C converts to plain 'C' (U+0043)
        // under NFKC. That's a single-grapheme conversion, so
        // NormalizedFor handles it automatically: U+2102 in the set
        // gets replaced by 'C'. Lexer under FormKC produces 'C' from
        // input U+2102, matches.
        var rule = OneOf("ℂ");
        rule.Compile(NormalizationForm.FormKC);
        Assert.That(rule.Parse("ℂ").Success, Is.True,
            "U+2102 input converts to 'C' which is now in the set");
        Assert.That(rule.Parse("C").Success, Is.True,
            "plain 'C' input matches directly");
    }

    [Test]
    public void OneOf_with_partially_composed_multi_rune_matches_under_FormD()
    {
        // A multi-rune entry where NFC == entry but NFD differs:
        // "é + combining macron" (precomposed é followed by another
        // combining mark). NFC keeps it the same (no further composition).
        // NFD decomposes the precomposed é: "e + combining acute +
        // combining macron". Under FormD the lexer produces the 3-rune
        // form, so the set must contain that. Form-projection replaces
        // the 2-rune entry with the 3-rune form.
        string partiallyComposed = "é̄";              // é + macron
        string fullyDecomposed = "é̄";               // e + acute + macron
        var rule = OneOf(TokenSet.Graphemes(partiallyComposed));
        rule.Compile(NormalizationForm.FormD);
        Assert.That(rule.Parse(fullyDecomposed).Success, Is.True,
            "input gets decomposed to the 3-rune form; set was projected to match");
    }

    [Test]
    public void OneOf_with_WithCompatibilityEquivalents_matches_ligature_pieces_under_FormKC()
    {
        // OneOf("ﬁ").Compile(FormKC) throws by default. With explicit
        // opt-in via WithCompatibilityEquivalents the entry expands to
        // 'f' and 'i' as separate set members, so the rule matches each
        // grapheme the lexer produces from input 'ﬁ' as "fi".
        var set = TokenSet.Runes("ﬁ").WithCompatibilityEquivalents(NormalizationForm.FormKC);
        var singleGraphemeRule = And(OneOf(set), Eof());
        singleGraphemeRule.Compile(NormalizationForm.FormKC);
        Assert.That(singleGraphemeRule.Parse("f").Success, Is.True);
        Assert.That(singleGraphemeRule.Parse("i").Success, Is.True);

        // Input 'ﬁ' converts to two tokens ('f' then 'i'), so a
        // structurally-larger rule that consumes both graphemes now matches.
        var bothGraphemesRule = And(OneOf(set), OneOf(set), Eof());
        bothGraphemesRule.Compile(NormalizationForm.FormKC);
        Assert.That(bothGraphemesRule.Parse("ﬁ").Success, Is.True,
            "input 'ﬁ' converts to 'f' + 'i', both graphemes match");
        Assert.That(bothGraphemesRule.Parse("fi").Success, Is.True,
            "plain 'fi' input matches the same way");
    }

    [Test]
    public void Identifier_form_aware_overload_compiles_under_FormKC()
    {
        // The form-aware overload pre-applies WithCompatibilityEquivalents
        // to XidStart and XidContinue, so multi-grapheme compatibility
        // conversions get expanded into their grapheme pieces (which
        // are already in the category sets anyway). Compile under FormKC
        // succeeds, and matching works on fullwidth / ligature input.
        var rule = Identifier(NormalizationForm.FormKC);

        Assert.DoesNotThrow(() => rule.Compile(NormalizationForm.FormKC));
        Assert.That(rule.Parse("ﬁoo").Success, Is.True,
            "input 'ﬁ' converts to 'f' + 'i' under FormKC; rule matches as 'fioo'");
    }

    // ============================================================
    // Form-aware Compile: auto-conversion of grammar literals.
    // Combining-mark reordering, Hangul jamo composition, and the
    // canonical / compatibility singletons (Angstrom, Ohm, Kelvin,
    // angle bracket, double-struck C). The Compile validation pass
    // rewrites a literal that doesn't match its own normalized form
    // so the grammar still matches lexer-normalized input.
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
        var precomposedRule = And(Token(UnicodeExamples.VietnameseACircumflexDotBelowRune), Eof());

        // Canonical order (ccc 220 then 230): NFC composes directly.
        Assert.That(precomposedRule.Parse(UnicodeExamples.VietnameseACircumflexDotBelowCanonicalText).Success,
            Is.True, "canonical order");

        // Reversed order (ccc 230 then 220): NFC reorders by class
        // first, then composes. Same result.
        Assert.That(precomposedRule.Parse(UnicodeExamples.VietnameseACircumflexDotBelowReorderedText).Success,
            Is.True, "NFC reorders different-class marks before composing");
    }

    [Test]
    public void Compile_auto_converts_non_canonical_combining_mark_order_under_FormC()
    {
        // The flip side of the test above: a grammar literal that
        // uses non-canonical mark order would silently never match
        // any input under FormC, because every input gets canonicalized
        // before the lexer sees it. The Compile validation pass
        // catches this and throws so the author fixes the literal at
        // grammar-build time instead of debugging silent match
        // failures.
        var rule = Token(UnicodeExamples.VietnameseACircumflexDotBelowReorderedText);

        // Compile auto-converts the non-canonical mark order to canonical order.
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse(UnicodeExamples.VietnameseACircumflexDotBelowCanonicalText).Success, Is.True);
        Assert.That(rule.Parse(UnicodeExamples.VietnameseACircumflexDotBelowReorderedText).Success, Is.True);
    }

    [Test]
    public void Hangul_precomposed_and_decomposed_match_same_grammar_under_FormC()
    {
        // Hangul "han" U+D55C precomposed. Canonical decomposition
        // is U+1112 + U+1161 + U+11AB (three jamo). NFC composes the
        // jamo back to U+D55C, so a grammar with Token(precomposed)
        // matches both forms. UAX #15 has special-case rules for
        // Hangul composition.
        var rule = And(Token(UnicodeExamples.HangulHanGrapheme), Eof());

        var precomposed = rule.Parse(UnicodeExamples.HangulHanGrapheme);
        var decomposed = rule.Parse(UnicodeExamples.HangulHanDecomposedText);

        Assert.That(precomposed.Success, Is.True, "precomposed");
        Assert.That(decomposed.Success, Is.True,
            "NFC composes the three jamo back to U+D55C");
    }

    [Test]
    public void Compile_auto_converts_decomposed_Hangul_jamo_under_FormC()
    {
        // The flip side of the test above: a grammar literal in
        // decomposed-jamo form would silently never match any input
        // under FormC, because every input gets canonicalized
        // (composed back to U+D55C) before the lexer sees it. The
        // Compile validation pass catches this.
        var rule = Token(UnicodeExamples.HangulHanDecomposedText);

        // Compile auto-converts the decomposed jamo to its precomposed form.
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse(UnicodeExamples.HangulHanGrapheme).Success, Is.True);
        Assert.That(rule.Parse(UnicodeExamples.HangulHanDecomposedText).Success, Is.True);
    }

    [Test]
    public void Compile_auto_converts_canonical_singleton_Angstrom()
    {
        // U+212B ANGSTROM SIGN canonically decomposes to U+00C5 LATIN
        // CAPITAL LETTER A WITH RING ABOVE. NFC rewrites the Angstrom
        // form to U+00C5 before the lexer sees the input. So a grammar
        // with Token("Å") under FormC would silently never match.
        // The new compile-time validation pass catches this and tells
        // the author to use U+00C5 instead.
        var rule = Token(UnicodeExamples.AngstromGrapheme);
        // Compile auto-converts U+212B to U+00C5 at Compile.
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse(UnicodeExamples.AngstromGrapheme).Success, Is.True);
        Assert.That(rule.Parse(UnicodeExamples.LatinCapitalAWithRingAboveGrapheme).Success, Is.True);

        // Positive case: a grammar with the canonical replacement
        // (U+00C5) compiles fine and matches input typed as the
        // singleton (U+212B), because NFC converts U+212B to U+00C5
        // before the lexer runs.
        var goodRule = And(Token(UnicodeExamples.LatinCapitalAWithRingAboveGrapheme), Eof());
        Assert.That(goodRule.Parse(UnicodeExamples.AngstromGrapheme).Success, Is.True,
            "U+00C5 grammar matches U+212B input under FormC");
    }

    [Test]
    public void Compile_auto_converts_Angstrom_singleton_under_FormD()
    {
        // Under FormD the Angstrom decomposes to A + combining ring,
        // and the literal text U+212B matches its own FormD only by
        // accident. Actually NFD of U+212B is "Å" (A + ring),
        // so the literal does NOT match its own FormD. Lock in the
        // Compile-time error here.
        var rule = Token(UnicodeExamples.AngstromGrapheme);
        // Compile auto-converts U+212B to its NFD form ("A" + combining ring).
        Assert.DoesNotThrow(() => rule.Compile(NormalizationForm.FormD));
        Assert.That(rule.Parse(UnicodeExamples.AngstromGrapheme).Success, Is.True);

        // Positive case: a grammar with the FormD decomposed form
        // ("A" + combining ring) compiles fine and matches input
        // typed as the Angstrom singleton, because NFD decomposes
        // U+212B to that exact two-rune sequence.
        var goodRule = And(Token(UnicodeExamples.LatinAWithRingAboveDecomposedText), Eof());
        goodRule.Compile(NormalizationForm.FormD);
        Assert.That(goodRule.Parse(UnicodeExamples.AngstromGrapheme).Success, Is.True,
            "decomposed grammar matches U+212B input under FormD");
    }

    [Test]
    public void Compile_auto_converts_canonical_singleton_Ohm_under_FormC()
    {
        // U+2126 OHM SIGN is a canonical singleton: it canonically
        // decomposes to U+03A9 GREEK CAPITAL LETTER OMEGA. NFC
        // rewrites U+2126 to U+03A9. A grammar with the Ohm form
        // would silently never match.
        var rule = Token(UnicodeExamples.OhmGrapheme);
        // Compile auto-converts U+2126 to U+03A9 at Compile.
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse(UnicodeExamples.OhmGrapheme).Success, Is.True);
        Assert.That(rule.Parse(UnicodeExamples.GreekCapitalOmegaGrapheme).Success, Is.True);

        // Positive case: a grammar with U+03A9 (Greek capital Omega)
        // matches input typed as the Ohm singleton (U+2126), because
        // NFC converts U+2126 to U+03A9 before the lexer runs.
        var goodRule = And(Token(UnicodeExamples.GreekCapitalOmegaGrapheme), Eof());
        Assert.That(goodRule.Parse(UnicodeExamples.OhmGrapheme).Success, Is.True,
            "U+03A9 grammar matches U+2126 input under FormC");
    }

    [Test]
    public void Compile_auto_converts_canonical_singleton_Kelvin_under_FormC()
    {
        // U+212A KELVIN SIGN is a canonical singleton: it canonically
        // decomposes to plain U+004B LATIN CAPITAL LETTER K. NFC
        // rewrites U+212A to U+004B. The third member of the
        // Angstrom / Ohm / Kelvin trio.
        var rule = Token(UnicodeExamples.KelvinGrapheme);
        // Compile auto-converts U+212A to U+004B at Compile.
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse(UnicodeExamples.KelvinGrapheme).Success, Is.True);
        Assert.That(rule.Parse(UnicodeExamples.AsciiCapitalKGrapheme).Success, Is.True);

        // Positive case: a grammar with ASCII 'K' (U+004B) matches
        // input typed as the Kelvin singleton (U+212A), because NFC
        // converts U+212A to U+004B before the lexer runs.
        var goodRule = And(Token(UnicodeExamples.AsciiCapitalKGrapheme), Eof());
        Assert.That(goodRule.Parse(UnicodeExamples.KelvinGrapheme).Success, Is.True,
            "ASCII K grammar matches U+212A input under FormC");
    }

    [Test]
    public void Compile_auto_converts_canonical_singleton_angle_bracket_under_FormC()
    {
        // U+2329 LEFT-POINTING ANGLE BRACKET is a canonical singleton:
        // canonically decomposes to U+3008 LEFT ANGLE BRACKET (the
        // CJK angle bracket). NFC rewrites U+2329 to U+3008. The
        // less-famous canonical singleton; not a Latin/Greek
        // duplicate but the same mechanism.
        var rule = Token(UnicodeExamples.LeftPointingAngleBracketGrapheme);
        // Compile auto-converts U+2329 to U+3008 at Compile.
        Assert.DoesNotThrow(() => rule.Compile());
        Assert.That(rule.Parse(UnicodeExamples.LeftPointingAngleBracketGrapheme).Success, Is.True);
        Assert.That(rule.Parse(UnicodeExamples.CjkLeftAngleBracketGrapheme).Success, Is.True);

        // Positive case: a grammar with U+3008 (CJK angle bracket)
        // matches input typed as U+2329, because NFC converts U+2329
        // to U+3008 before the lexer runs.
        var goodRule = And(Token(UnicodeExamples.CjkLeftAngleBracketGrapheme), Eof());
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
    public void Compile_auto_converts_compatibility_singleton_under_FormKC()
    {
        // Same character (U+2102 DOUBLE-STRUCK CAPITAL C). Under
        // FormKC, NFKC also applies compatibility decompositions,
        // so U+2102 normalizes to U+0043 plain C. A grammar literal
        // in the source character would silently never match. The
        // Compile validation pass catches this exactly the same way
        // it catches canonical singletons under FormC.
        var rule = Token(UnicodeExamples.DoubleStruckCGrapheme);
        // Compile auto-converts U+2102 to U+0043 (the NFKC conversion) at Compile.
        Assert.DoesNotThrow(() => rule.Compile(NormalizationForm.FormKC));
        Assert.That(rule.Parse(UnicodeExamples.DoubleStruckCGrapheme).Success, Is.True);

        // Positive case: a grammar with ASCII 'C' (U+0043) compiled
        // for FormKC matches input typed as the double-struck C
        // singleton (U+2102), because NFKC converts U+2102 to U+0043
        // before the lexer runs. This is the matching-by-meaning
        // path: the author opts into FormKC to treat presentation
        // variants as equivalent to their plain forms.
        var goodRule = And(Token(UnicodeExamples.AsciiCapitalCGrapheme), Eof());
        goodRule.Compile(NormalizationForm.FormKC);
        Assert.That(goodRule.Parse(UnicodeExamples.DoubleStruckCGrapheme).Success, Is.True,
            "ASCII C grammar matches U+2102 input under FormKC");
    }

    // ============================================================
    // Position-translation tests for compatibility forms. Verify
    // TranslateToOriginal returns the same answer as a whole-string
    // prefix walk over every position in the normalized string, for
    // the inputs the doc identifies as interesting:
    //
    //   - defective combining sequences (lone mark at start, lone
    //     mark after a control character, expanding mark, two
    //     marks at start)
    //   - Korean compatibility jamo (the canonical case where two
    //     original graphemes collapse to one normalized character
    //     under FormKC)
    //
    // See docs/MappingPositionsAfterNormalization.md for the algorithm and
    // UAX #29 citations.
    // ============================================================

    [Test]
    public void Translator_agrees_with_whole_string_for_leading_defective_combining_mark()
    {
        // Lone combining acute (no base) at the very start, then Z.
        AssertTranslatorAgreesWithWholeString("́Z", NormalizationForm.FormKC);
        AssertTranslatorAgreesWithWholeString("́Z", NormalizationForm.FormKD);
    }

    [Test]
    public void Translator_agrees_with_whole_string_for_defective_combining_mark_after_control()
    {
        // X, newline, defective acute (alone because UAX #29 GB5
        // breaks after Control), then Y.
        AssertTranslatorAgreesWithWholeString("X\ńY", NormalizationForm.FormKC);
        AssertTranslatorAgreesWithWholeString("X\ńY", NormalizationForm.FormKD);
    }

    [Test]
    public void Translator_agrees_with_whole_string_for_leading_expanding_defective_mark()
    {
        // U+0344 COMBINING GREEK DIALYTIKA TONOS canonically
        // decomposes to two combining marks (U+0308 + U+0301), so
        // normalization actually changes string length here. The
        // defective mark sits at index 0, so this is the exact
        // case the comment is worried about.
        AssertTranslatorAgreesWithWholeString("̈́Z", NormalizationForm.FormKC);
        AssertTranslatorAgreesWithWholeString("̈́Z", NormalizationForm.FormKD);
    }

    [Test]
    public void Translator_agrees_with_whole_string_for_two_defective_marks_at_start()
    {
        // Two combining marks at the start (no base). UAX #29 GB9
        // keeps them in ONE grapheme cluster (no break before
        // Extend), so per-grapheme normalization runs on the
        // whole "̣́" sequence at once. Whole-string
        // normalization reorders by combining class (ccc 230 then
        // 220 becomes 220 then 230).
        AssertTranslatorAgreesWithWholeString("̣́Z", NormalizationForm.FormKC);
        AssertTranslatorAgreesWithWholeString("̣́Z", NormalizationForm.FormKD);
    }

    [Test]
    public void ErrorCharIndex_for_parse_failure_after_expanding_defective_mark_FormKC()
    {
        // End-to-end check on a real parse: the U+0344 dialytika
        // tonos sits alone at index 0, expanding under FormKC to
        // two combining marks (U+0308 + U+0301). The grammar
        // matches that single grapheme via Token, then expects Y
        // and sees Z. ErrorCharIndex must point at the Z in the
        // ORIGINAL input, which is index 1 (the two-char expansion
        // lives only in the normalized form).
        string original = "̈́Z";
        var rule = And(Token("̈́"), Token('Y'));
        rule.Compile(NormalizationForm.FormKC);
        var result = rule.Parse(original);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1),
            "Z sits at char 1 in the original, even though the failure " +
            "is at normalized-index 2 (after the two-char expansion of U+0344)");
        Assert.That(original[result.ErrorCharIndex], Is.EqualTo('Z'));
    }

    [Test]
    public void Translator_agrees_with_whole_string_for_Korean_compatibility_jamo()
    {
        // U+3131 + U+314F (Korean compatibility jamo "ㄱㅏ") compose
        // to one syllable U+AC00 (가) under FormKC. Compatibility
        // decomposition produces conjoining jamo (U+1100 L + U+1161
        // V) which then canonically compose to one syllable. Two
        // original graphemes (both gcb=Other) collapse to one
        // normalized character. This is the canonical example the
        // doc walks through. Under FormKD the conjoining jamo don't
        // compose, so the normalized form has two chars (L + V).
        AssertTranslatorAgreesWithWholeString("ㄱㅏ", NormalizationForm.FormKC);
        AssertTranslatorAgreesWithWholeString("ㄱㅏ", NormalizationForm.FormKD);
    }

    [Test]
    public void Translator_agrees_with_whole_string_for_Korean_compatibility_jamo_with_surrounding_chars()
    {
        // Same compatibility jamo with chars on either side, so the
        // walker has more positions to map. Exercises the lookup at
        // multiple normalized indices including the boundaries
        // between the surrounding chars and the multi-grapheme
        // region.
        AssertTranslatorAgreesWithWholeString("AㄱㅏZ", NormalizationForm.FormKC);
        AssertTranslatorAgreesWithWholeString("AㄱㅏZ", NormalizationForm.FormKD);
    }

    [Test]
    public void ErrorCharIndex_for_parse_failure_after_Korean_compatibility_jamo_FormKC()
    {
        // End-to-end check on a real parse: U+3131 + U+314F (two
        // Korean compatibility-jamo chars) compose to U+AC00 (one
        // syllable) under FormKC. Original has 2 chars for the
        // jamo, normalized has 1 char for the syllable. The grammar
        // matches the syllable then expects 'Y' and sees 'Z'.
        // ErrorCharIndex must point at the 'Z' in the ORIGINAL
        // (index 2, after both compatibility-jamo chars), not at
        // the failure position in normalized space (index 1, right
        // after the single syllable).
        string original = "ㄱㅏZ";
        var rule = And(Token("가"), Token('Y'));
        rule.Compile(NormalizationForm.FormKC);
        var result = rule.Parse(original);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(2),
            "Z sits at char 2 in the original (after the two compatibility-jamo chars), " +
            "even though the failure is at normalized-index 1 (after the single syllable)");
        Assert.That(original[result.ErrorCharIndex], Is.EqualTo('Z'));
    }

    // Iterate every position in the normalized string and compare
    // TranslateToOriginal's per-grapheme answer against the spec-
    // blessed whole-string prefix walk. The normalized form is
    // re-allocated with new string(...) so the ReferenceEquals
    // fast path inside TranslateToOriginal doesn't short-circuit
    // before the per-grapheme walker runs (which would happen for
    // inputs whose normalized form is content-identical to the
    // original).
    private static void AssertTranslatorAgreesWithWholeString(
        string original, NormalizationForm form)
    {
        string normalized = new string(original.Normalize(form).ToCharArray());
        for (int i = 0; i <= normalized.Length; i++)
        {
            int translatorAnswer = NormalizedPositionMap.TranslateToOriginal(
                original, normalized, i, form);
            int wholeStringAnswer = WholeStringPositionMap(original, i, form);
            Assert.That(translatorAnswer, Is.EqualTo(wholeStringAnswer),
                $"position translation drifted at normalizedIndex={i}, form={form}");
        }
    }

    // Brute-force reference: walk the original grapheme by grapheme
    // and explicitly check at each boundary whether normalizing the
    // PREFIX of the original up to that boundary produces a prefix
    // of the full normalized string. That's the safe-boundary
    // condition the doc spells out. Return the largest safe
    // boundary at or before normalizedIndex.
    //
    // The brute force re-normalizes the full prefix every iteration
    // (no chunk-since-last-verified optimization), which makes the
    // implementation obviously correct at the cost of being O(N²).
    // It's only used as a reference in tests; the walker in
    // NormalizedPositionMap has the linear-amortized version.
    private static int WholeStringPositionMap(string original, int normalizedIndex, NormalizationForm form)
    {
        string normalized = original.Normalize(form);
        if (normalizedIndex <= 0) return 0;
        if (normalizedIndex >= normalized.Length) return original.Length;

        int bestSafeOrigPos = 0;
        int origPos = 0;
        while (origPos < original.Length)
        {
            int step = StringInfo.GetNextTextElement(original, origPos).Length;
            if (step <= 0) step = 1;
            origPos += step;

            string prefixNormalized = original[..origPos].Normalize(form);

            // Safe boundary check: prefixNormalized must be an actual
            // prefix of the full normalized string (not just length-
            // compatible).
            bool isSafe = prefixNormalized.Length <= normalized.Length
                && string.CompareOrdinal(normalized, 0, prefixNormalized, 0, prefixNormalized.Length) == 0;

            if (isSafe)
            {
                if (prefixNormalized.Length > normalizedIndex)
                    return bestSafeOrigPos;
                if (prefixNormalized.Length == normalizedIndex)
                    return origPos;
                bestSafeOrigPos = origPos;
            }
        }

        return original.Length;
    }

}
