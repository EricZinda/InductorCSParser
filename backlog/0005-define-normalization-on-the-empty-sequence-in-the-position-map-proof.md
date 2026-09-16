# Define normalization on the empty sequence in the position-map proof

`docs/MappingPositionsAfterNormalization.md` uses an empty accepted prefix in
the invariant's base case and an empty remainder in the final candidate, but
Appendix A builds its formal domain from D12's definition of a coded character
sequence as one or more code points. The proof never explicitly defines `N`
or `D` on the empty sequence.

Introduce the empty sequence explicitly and state `N(ε) = ε` and `D(ε) = ε`.
Note that canonical ordering and composition also leave it unchanged, and use
that extension in the base case, empty-input case, and final-remainder case.
