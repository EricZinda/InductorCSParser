# Prove corresponding FormC/FormD clusters are canonically equivalent

The FormC/FormD optimization currently promotes UAX #29's descriptive statement
that clusters remain unchanged across canonically equivalent forms into the
stronger per-cluster conclusion `Gi` is canonically equivalent to `Hi`. The
document acknowledges that the cited passage isn't a numbered definition or
conformance clause, but the skipped comparison requires this stronger result.

Replace that trust step with a common-NFD derivation. Show that `O` and `Z` have
the same NFD string, that their direct cluster boundaries correspond to the
same boundaries in it, and that decomposition can't interact across those
boundaries. Conclude `FormD(Gi) = FormD(Hi)`, hence each pair is canonically
equivalent by D70, before using substring closure to prove
`N(Gi) = N(Hi) = Hi`.
