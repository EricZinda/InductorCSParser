# Align the FormC/FormD position-map proof with the actual segmenter

`docs/MappingPositionsAfterNormalization.md` says, in the Optimization for
FormC/D section, that the lockstep proof assumes both strings are segmented
with UAX #29's default extended-grapheme-cluster rules using Unicode data
consistent with the normalizer. Neither segmenter the project ships runs
exactly that. The built-in `GraphemeSegmentation` deliberately omits GB9c to
match .NET 10, and the runtime `StringInfo` segmenter uses whatever rule set
and data version the installed .NET has, which may not match the normalizer's
data version. Unicode's canonical-equivalence guarantee for cluster boundaries
is a claim about its own rule set over its own data, so the doc is citing a
guarantee for a configuration it doesn't run.

The decision is to trust Unicode's claim rather than prove it (backlog 0004
was closed for that reason). What's left is narrower:

1. Fix the premise sentence so it says what's actually assumed: that the
   segmenter's cluster boundaries are invariant under canonical equivalence,
   which Unicode guarantees for its default rules and which this project
   relies on holding for its own rule set (no GB9c) and for the runtime
   segmenter.

2. Add FormC and FormD runs to the differential test in `NormalizationTests.cs`
   (`AssertTranslatorAgreesWithWholeString`), which currently covers only
   FormKC and FormKD. The brute-force prefix-normalization reference and the
   Unicode 16 conformance corpus are already in the test project. This checks
   the lockstep walker against the real segmenter and normalizer once, instead
   of relying on the plausible but unchecked reasoning that dropping GB9c
   preserves the invariance and that data-version skew washes out because both
   strings go through the same segmenter.

Don't add GB9c merely to make the prose true if that would break the
intentional .NET compatibility policy.
