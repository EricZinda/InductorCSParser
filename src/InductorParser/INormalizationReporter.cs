using System;

namespace InductorParser;

// Sink handed to Rule.ValidateNormalization during Compile so a rule can
// report when the text the user typed can't be matched under the chosen
// normalization form. A rule reports rather than throwing because Compile
// gathers every report across the whole grammar and throws one combined
// error at the end:
//
//   * ReportOffender: "this stored text can't be matched under `form`",
//     with a suggested replacement for the Compile error message. Records
//     the offender and returns. Compile collects every offender across the
//     whole grammar and throws one InvalidOperationException at the end.
//
//   * ReportNormalizeFailure: string.Normalize threw (in practice an
//     unpaired surrogate, which it rejects regardless of form). The
//     exception is surfaced as the thrown InvalidOperationException's
//     InnerException. The engine also records a matching offender so the
//     rule still appears in the user-facing list. TryConvertToForm calls
//     this for you, so most rules never call it directly.
public interface INormalizationReporter
{
    void ReportOffender(Rule rule, string original, string suggestedReplacement);

    void ReportNormalizeFailure(Rule rule, string original, ArgumentException failure);
}
