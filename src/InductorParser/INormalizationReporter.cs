using System;

namespace InductorParser;

/// <summary>
/// The sink <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see>
/// hands to each rule's
/// <see cref="Rule.ValidateNormalization(System.Text.NormalizationForm, INormalizationReporter)">Rule.ValidateNormalization</see>
/// so the rule can report text it stores that can't be matched under the grammar's normalization
/// form. Only rule writers meet it: a grammar built from the built-in rules never sees it, and the
/// library implements it. A rule reports rather than throwing because Compile gathers every report
/// across the whole grammar and throws one InvalidOperationException at the end that lists them
/// all.
/// </summary>
/// <remarks>
/// Most user-defined rules never call either method directly. A rule with one fixed expected
/// string calls
/// <see cref="Rule.TryConvertToForm(Rule, string, System.Text.NormalizationForm, INormalizationReporter)">Rule.TryConvertToForm</see>,
/// which reports for it.
/// </remarks>
public interface INormalizationReporter
{
    /// <summary>
    /// Reports that <paramref name="original"/>, text stored on <paramref name="rule"/>, can't be
    /// matched under the grammar's normalization form. Compile lists every offender in the
    /// exception it throws, each with its suggested replacement, so the grammar author can see
    /// what to change.
    /// </summary>
    /// <param name="rule">The rule holding the text.</param>
    /// <param name="original">The stored text as the grammar author wrote it.</param>
    /// <param name="suggestedReplacement">What to write instead, for the error message.</param>
    void ReportOffender(Rule rule, string original, string suggestedReplacement);

    /// <summary>
    /// Reports that converting <paramref name="original"/>, text stored on <paramref name="rule"/>,
    /// to the grammar's normalization form failed because the text isn't well-formed: it contains
    /// an unpaired surrogate or U+FFFE. Compile lists the rule as an offender and attaches the
    /// failures to the exception it throws as its InnerException.
    /// </summary>
    /// <param name="rule">The rule holding the text.</param>
    /// <param name="original">The stored text that couldn't be normalized.</param>
    /// <param name="failure">The exception describing what's wrong with the text.</param>
    void ReportNormalizeFailure(Rule rule, string original, ArgumentException failure);
}
