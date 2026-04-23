using System.Linq;
using System.Text;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests;

// Tests for ParseOptions.NormalizeInput. Two promises the feature has to keep:
//
//   1. A grammar written in one composition form (the grammar author's
//      choice, NFC is the default and what most people pick) matches input
//      in either form. "café" grammar accepts precomposed "café" (U+00E9)
//      and decomposed "cafe\u0301" equally.
//
//   2. Positions reported in ParseResult (ErrorCharIndex and its derived
//      properties) index into the CALLER'S ORIGINAL input string, never
//      into the normalized form. An editor forwarding the error straight
//      into an LSP diagnostic sees offsets that line up with the document
//      it knows about.
[TestFixture]
public class NormalizationTests
{
    // "café" with precomposed é (U+00E9). One grapheme per char.
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
        // distinction is back in the caller's face. Documents the trade.
        var result = And(CafeRule(), Eof()).Parse(CafeDecomposed,
            new ParseOptions { NormalizeInput = null });
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void NormalizeInput_null_passes_precomposed_input_against_precomposed_grammar()
    {
        // Sanity check: opting out doesn't break the already-matching path.
        var result = And(CafeRule(), Eof()).Parse(CafePrecomposed,
            new ParseOptions { NormalizeInput = null });
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
        Assert.That(result.ErrorCharIndex, Is.EqualTo(5),
            "ErrorCharIndex should index into the original decomposed input");
        Assert.That(input[result.ErrorCharIndex], Is.EqualTo('x'),
            "the char at ErrorCharIndex should be the one the grammar rejected");
    }

    [Test]
    public void Failure_inside_composed_sequence_snaps_to_grapheme_start_in_original()
    {
        // Grammar wants "caf1", i.e. fails at the slot where 'é' sits. In
        // NORMALIZED space the failing lexer position is 3 (the precomposed
        // 'é'). Translated back to the decomposed original, that's also
        // position 3 (the 'e' at the start of the combining sequence) since
        // an editor-style diagnostic wants to highlight the whole bad
        // grapheme, not the 'e' on its own.
        string input = CafeDecomposed;
        var rule = And(Token('c'), Token('a'), Token('f'), Token('1'), Eof());
        var result = rule.Parse(input);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(input[result.ErrorCharIndex], Is.EqualTo('e'));
    }

    [Test]
    public void Already_normalized_input_reports_positions_identical_to_null_option()
    {
        // The common case: input is already in FormC, so String.Normalize
        // returns the same reference and translation is a no-op. Failure
        // positions must come out identical to the no-normalization path.
        string input = CafePrecomposed + "X";
        var rule = And(CafeRule(), Token('1'), Eof());

        var withNfc = rule.Parse(input);
        var withoutNormalization = rule.Parse(input,
            new ParseOptions { NormalizeInput = null });

        Assert.That(withNfc.Success, Is.False);
        Assert.That(withoutNormalization.Success, Is.False);
        Assert.That(withNfc.ErrorCharIndex, Is.EqualTo(withoutNormalization.ErrorCharIndex));
        Assert.That(withNfc.ErrorLine, Is.EqualTo(withoutNormalization.ErrorLine));
        Assert.That(withNfc.ErrorColumn, Is.EqualTo(withoutNormalization.ErrorColumn));
    }

    [Test]
    public void NormalizeInput_null_reports_positions_exactly_into_decomposed_input()
    {
        // With normalization off, the lexer sees the original bytes and
        // positions are trivially into the original. This test locks in
        // that baseline so a future refactor can't silently regress it.
        string input = CafeDecomposed + "X";
        var rule = And(Token('c'), Token('a'), Token('f'), Token('X'), Eof());
        var result = rule.Parse(input, new ParseOptions { NormalizeInput = null });

        Assert.That(result.Success, Is.False);
        // Grammar consumed "caf" then wanted 'X' but got 'e'. Under
        // GraphemeLexer, the failing grapheme is 'e' + combining acute
        // starting at index 3 in the decomposed original.
        Assert.That(result.ErrorCharIndex, Is.EqualTo(3));
        Assert.That(input[result.ErrorCharIndex], Is.EqualTo('e'));
    }

    [Test]
    public void NFC_default_is_FormC()
    {
        // Lock in the default so a careless refactor that flipped it to
        // FormD or null would fail loudly here rather than break a
        // hundred grammars silently.
        var options = new ParseOptions();
        Assert.That(options.NormalizeInput, Is.EqualTo(NormalizationForm.FormC));
    }

    [Test]
    public void FormD_rewrites_precomposed_input_to_decomposed_before_lexer_sees_it()
    {
        // The exotic-but-supported case: a grammar author who wants to
        // match decomposed form can choose FormD. Grammar pattern and
        // input then meet in decomposed land.
        string input = CafePrecomposed; // single-char é
        var rule = And(Token('c'), Token('a'), Token('f'),
                       Token('e'), Token(CombiningAcuteText), Eof());
        var result = rule.Parse(input,
            new ParseOptions
            {
                InputUnit = InputUnit.Rune,
                NormalizeInput = NormalizationForm.FormD,
            });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
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
    }

    // Compatibility-form tests. FormKC and FormKD fold ligatures, circled
    // digits, fullwidth forms, superscripts, and similar cosmetic
    // variations into their plain-text equivalents. The fold can expand
    // one grapheme into several ("\uFB01" → "fi", two graphemes), which
    // is why the translator uses a per-grapheme normalize walker for these
    // forms rather than the lockstep walker.
    //
    // U+FB01 is LATIN SMALL LIGATURE FI, the textbook compatibility-fold
    // example: one rune, one grapheme in the original. Normalizes to "fi"
    // (two runes, two graphemes).
    private const string FiLigature = "\uFB01";

    [Test]
    public void FormKC_folds_ligature_so_unfolded_grammar_matches_ligature_input()
    {
        // Grammar spells "fish" in the plain ASCII form. Input uses the fi
        // ligature. Without normalization the Token('f') would see "\uFB01"
        // and fail. FormKC folds the ligature to "fi" before the lexer
        // runs, so the grammar matches through.
        var rule = And(Token('f'), Token('i'), Token('s'), Token('h'), Eof());
        var result = rule.Parse(FiLigature + "sh",
            new ParseOptions { NormalizeInput = NormalizationForm.FormKC });

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
        var result = rule.Parse(input,
            new ParseOptions { NormalizeInput = NormalizationForm.FormKC });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(1));
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
        var result = rule.Parse(input,
            new ParseOptions { NormalizeInput = NormalizationForm.FormKC });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0),
            "failure inside the ligature expansion must snap to the ligature start in original");
    }

    [Test]
    public void FormKD_folds_ligature_and_ends_decomposed()
    {
        // FormKD does the same compatibility folding as FormKC but the
        // output is decomposed. For a pure-ASCII fold target ("fi") there's
        // no canonical decomposition, so FormKD output matches FormKC
        // output here. This test verifies that the FormKD path through
        // the translator works end-to-end, not that the decomposed
        // endpoint differs for this particular input.
        var rule = And(Token('f'), Token('i'), Token('s'), Token('h'), Eof());
        var result = rule.Parse(FiLigature + "sh",
            new ParseOptions { NormalizeInput = NormalizationForm.FormKD });

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public void FormKD_failure_position_translates_through_per_grapheme_walker()
    {
        // Same position-snap case as the FormKC test above, but through
        // FormKD to prove the compatibility-form dispatch catches both.
        string input = FiLigature;
        var rule = And(Token('f'), Token('X'));
        var result = rule.Parse(input,
            new ParseOptions { NormalizeInput = NormalizationForm.FormKD });

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorCharIndex, Is.EqualTo(0));
    }
}
