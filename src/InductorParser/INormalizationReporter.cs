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
//   * ReportNormalizeFailure: converting the stored text to `form` failed
//     because the text can't be normalized: an unpaired surrogate, or
//     U+FFFE, the two things the rejection scan flags (string.Normalize
//     itself throwing is only the backstop case). The reported exceptions
//     are gathered into an AggregateException that becomes the thrown
//     InvalidOperationException's InnerException, even when there's just
//     one. The engine also records a matching offender so the
//     rule still appears in the user-facing list. TryConvertToForm calls
//     this for you, so most rules never call it directly.
public interface INormalizationReporter
{
    void ReportOffender(Rule rule, string original, string suggestedReplacement);

    void ReportNormalizeFailure(Rule rule, string original, ArgumentException failure);
}
