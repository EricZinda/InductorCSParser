using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.StateMachine;
using static InductorParser.Rules;
using static InductorParser.Tests.UnicodeExamples;

namespace InductorParser.Tests.StateMachine;

// Cross-validates the state-machine evaluator and the recursive
// evaluator under ParseOptions.NormalizeInput. Both engines have to
// agree on accept/reject and on ErrorCharIndex when the parse fails,
// across FormC, FormD, and the null opt-out.
//
// The grammar is "café" spelled in precomposed form ("é"). The
// inputs cover precomposed and decomposed renderings of the same word
// so the canonical-form rewrite has a real effect on the lexer's view
// of the text. Failure cases tack a stray character onto the end so
// both engines can report a position back into the caller's original
// (un-normalized) string.
[TestFixture]
public class StateMachineNormalizationCompareTests
{
    private const string CafePrecomposed = "café";
    private const string CafeDecomposed = "cafe" + CombiningAcuteText;

    // Grammar matches "café" in precomposed form. Without normalization
    // it only accepts the precomposed input. Under FormC and FormD
    // Compile-time auto-conversion rewrites the grammar's 'é' into the
    // form's canonical equivalent, so both forms accept both renderings
    // of the input.
    private static Rule CafeRule() =>
        And(Literal("café"), Eof());

    [TestCase(CafePrecomposed, true)]
    [TestCase(CafeDecomposed, true)]
    [TestCase(CafePrecomposed + "x", false)]
    [TestCase(CafeDecomposed + "x", false)]
    [TestCase("cafX", false)]
    [TestCase("", false)]
    public void FormC_agrees_with_recursive_evaluator(string input, bool expectSuccess)
    {
        AssertEvaluatorsAgree(CafeRule(), input, NormalizationForm.FormC, expectSuccess);
    }

    [TestCase(CafePrecomposed, true)]
    [TestCase(CafeDecomposed, true)]
    [TestCase(CafePrecomposed + "x", false)]
    [TestCase(CafeDecomposed + "x", false)]
    [TestCase("cafX", false)]
    [TestCase("", false)]
    public void FormD_agrees_with_recursive_evaluator(string input, bool expectSuccess)
    {
        // FormD decomposes the input. Under Option 1 Compile-time
        // auto-conversion also decomposes the grammar's precomposed 'é'
        // so the literal matches FormD-normalized input. Both engines
        // should accept the same renderings, plus reject the trailing-x
        // cases at the same position.
        AssertEvaluatorsAgree(CafeRule(), input, NormalizationForm.FormD, expectSuccess);
    }

    [TestCase(CafePrecomposed, true)]
    [TestCase(CafeDecomposed, false)]
    [TestCase(CafePrecomposed + "x", false)]
    [TestCase("cafX", false)]
    public void NoNormalization_agrees_with_recursive_evaluator(string input, bool expectSuccess)
    {
        AssertEvaluatorsAgree(CafeRule(), input, normalizationForm: null, expectSuccess);
    }

    private static void AssertEvaluatorsAgree(Rule rule, string input, NormalizationForm? normalizationForm, bool expectSuccess)
    {
        // Normalization is committed at Compile time. Under Option 1
        // Compile auto-converts the grammar's literals to the chosen
        // form, so any non-surrogate text compiles cleanly under any
        // non-null form.
        rule.Compile(normalizationForm);
        var options = new ParseOptions();
        var legacy = rule.ParseRecursive(input, options);
        var stateMachine = StateMachineParser.Parse(rule, input, options);

        Assert.That(legacy.Success, Is.EqualTo(expectSuccess), $"recursive outcome: {legacy.ErrorMessage}");
        Assert.That(stateMachine.Success, Is.EqualTo(expectSuccess), $"state-machine outcome: {stateMachine.ErrorMessage}");

        if (expectSuccess)
        {
            Assert.That(stateMachine.ToString(), Is.EqualTo(legacy.ToString()),
                $"tree text differs on '{input}' under {normalizationForm}");
        }
        else
        {
            Assert.That(stateMachine.ErrorCharIndex, Is.EqualTo(legacy.ErrorCharIndex),
                $"ErrorCharIndex differs on '{input}' under {normalizationForm}");
            Assert.That(stateMachine.ErrorCharIndex, Is.LessThanOrEqualTo(input.Length),
                "ErrorCharIndex must be a valid index into the caller's original input");
        }
    }
}
