using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Scaffold for the per-rule SourceRange / SourceText matrix tests in
// `Rules/*Tests.cs`. Each per-rule test parameterizes
// AssertTargetAfterLiteralPrefix below over
// NormalizationExamples.RowFormPairs and supplies its own target
// leaf, so every leaf-construction path gets the same translation
// assertion under every (row, form) pair without repeating the
// build-grammar / parse / assert / skip-on-lone-surrogate
// boilerplate per rule.
public static class SourceRangeMatrixHelper
{
    // Build `And(Literal(row.Source), target [, afterTarget])`,
    // compile under `form`, parse `row.Source + targetText + extraInput`,
    // and assert that `targetSymbol.SourceRange` reports the
    // original-input span where `targetText` sits and that
    // `targetSymbol.SourceText` returns `targetText` verbatim. Both
    // verify the parseInput-to-original translation: the leaf's memory
    // points into parseInput (the lexer's normalized form), and any
    // (form, grapheme behavior) pair where Normalize changes the
    // input length would silently leak parseInput coordinates without
    // the Symbol-level translation through `NormalizedPositionMap`.
    //
    // `target` must reach the parse tree as a findable Symbol
    // (typically `.As(name)`). `targetText` is the text
    // the target captures. `extraInput` and `afterTarget` cover
    // shapes like `ScanUntil(TokenSet.Runes("!"))` where the target
    // matches a body and a stopper has to be consumed by something
    // else. Pass the stopper string as `extraInput` and the rule
    // that eats it as `afterTarget`.
    //
    // Lone-surrogate rows are skipped: `Literal(row.Source).Compile(form)`
    // throws on them, and the per-rule matching matrix already
    // covers the throw.
    public static void AssertTargetAfterLiteralPrefix(
        NormalizationExamples.NormalizationCase row,
        NormalizationForm form,
        Rule target,
        string targetText,
        string extraInput = "",
        Rule? afterTarget = null)
    {
        if (row.Category == NormalizationExamples.NormalizationCategory.LoneSurrogateNotNormalizable)
            return;

        string input = row.Source + targetText + extraInput;
        Rule grammar = afterTarget != null
            ? And(Literal(row.Source), target, afterTarget)
            : And(Literal(row.Source), target);
        grammar.Compile(form);
        var result = grammar.Parse(input);

        Assert.That(result.Success, Is.True,
            $"{row.Description} | {form} | parse failed: {result.ErrorMessage}. " +
            $"Source: {NormalizationExamples.Hex(row.Source)}, " +
            $"projected: {NormalizationExamples.Hex(NormalizationExamples.Project(row, form))}");

        var symbol = result.Tree!.Find(target);
        Assert.That(symbol, Is.Not.Null,
            $"{row.Description} | {form}: target leaf not found in parse tree.");

        var range = symbol!.SourceRange!.Value;
        Assert.That(range.Start.CharIndex, Is.EqualTo(row.Source.Length),
            $"{row.Description} | {form}: target should start at original-input offset " +
            $"{row.Source.Length}.");
        Assert.That(range.End.CharIndex, Is.EqualTo(row.Source.Length + targetText.Length),
            $"{row.Description} | {form}: target should end one past offset " +
            $"{row.Source.Length + targetText.Length}.");
        Assert.That(input.Substring(range.Start.CharIndex, range.End.CharIndex - range.Start.CharIndex),
            Is.EqualTo(targetText),
            $"{row.Description} | {form}: original-input substring at the translated range " +
            $"should equal \"{targetText}\".");
        Assert.That(symbol.SourceText, Is.EqualTo(targetText),
            $"{row.Description} | {form}: Symbol.SourceText should return the original (pre-normalization) text " +
            $"\"{targetText}\", not the normalized form.");
    }
}
