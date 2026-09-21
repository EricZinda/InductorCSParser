# Mapping Positions Through Unicode Normalization

This doc explains how we map a position in a normalized string back to a position in the caller's original input. Read this if you're working on `NormalizedPositionMap`, want to know why the position translation is designed the way it is, or want to be convinced that position translation works.

If you just want to use normalization in a grammar, start with these:

- [Primer3.md](Primer3.md) explains what normalization is and how to pick a form.
- [UnicodeInternalsArchitecture.md, Normalization](UnicodeInternalsArchitecture.md#normalization)
  explains how a grammar selects a normalization form and how `Compile`
  normalizes rule literals into that form.
- [Primer4.md](Primer4.md) shows security uses of FormKC.
- [UnicodeGotchas.md](UnicodeGotchas.md) lists what normalization can't fix.

## Background

Unicode lets you write the same visible character multiple ways. The letter "é" can be stored as one .NET `char` (U+00E9) or as two chars ("e" plus U+0301, the combining acute). Both render identically. The "ﬁ" ligature can be one char (U+FB01) or two ("f" plus "i"). A Korean syllable can be one precomposed character or several separate component characters.

*Normalization* converts text to one chosen representation. .NET's `string.Normalize(NormalizationForm form)` does this. The four forms split into two pairs:

- `FormC` and `FormD` are the *canonical* forms. They convert between precomposed and decomposed Unicode (like "é" turning into "e" plus combining acute, or back). They don't expand ligatures, don't change ASCII width, and don't change anything visible.
- `FormKC` and `FormKD` are the *compatibility* forms. They do everything the canonical forms do, then go further. They expand ligatures ("ﬁ" becomes the two characters "fi"), unify half-width and full-width Latin, flatten superscripts and other typographic variants, and handle other "looks similar but stored differently" cases.

An *extended grapheme cluster* is Unicode's default approximation of one
user-perceived character, even when it's stored as multiple chars. "é" is one
extended grapheme cluster whether stored as 1 char or 2. In this document,
*grapheme* and *cluster* are readability shorthands for the extended grapheme
clusters produced by the parser's `GraphemeSegmentation`. They don't mean a
language-specific linguistic grapheme. The proof uses the full term wherever
the distinction matters.

## The problem

By default, the parser normalizes input before lexing, so the positions the engine works with are offsets into the *normalized* string. The caller wants the corresponding position in the *original* string they started with, so editor highlighting points at the right place. This comes up two ways: a failure position when parsing fails, and a symbol's source range (`Symbol.SourceRange`) on a successful parse. Both go through `NormalizedPositionMap.TranslateToOriginal`.

Two examples show why this isn't trivial.

The "ﬁ" ligature: the "ﬁ" ligature (one char) becomes "fi" (two chars) under FormKC. If the lexer fails at position 1 in the normalized string (between the 'f' and 'i'), there's no separate 'i' in the original. Both came from the one ligature char. The right answer is position 0 in the original, so editor highlighting points at the whole ligature.

Korean compatibility jamo: the two characters U+3131 and U+314F (Korean compatibility jamo, displayed as "ㄱㅏ") compose into one syllable character `가` (U+AC00) under FormKC. Two original chars become one normalized char. If the lexer fails at position 1 in the normalized string (end of `가`), the right answer in the original is position 2 (after both original chars). Simple char counting gets this wrong.

## Why not use ICU?

[ICU](https://unicode-org.github.io/icu/) (International Components for
Unicode) is the Unicode Consortium's implementation library. Its
[`Normalizer2`](https://unicode-org.github.io/icu-docs/apidoc/released/icu4c/classNormalizer2.html)
UTF-8 normalization API can populate an
[`Edits`](https://unicode-org.github.io/icu-docs/apidoc/released/icu4c/classicu_1_1Edits.html)
object that records how source spans correspond to changed and unchanged result
spans. If this project always normalized through ICU, it could use that mapping
directly.

This parser, however, supports both its built-in normalizer and
[.NET's `String.Normalize`](https://learn.microsoft.com/en-us/dotnet/api/system.string.normalize).
The .NET API returns the normalized string but exposes no edit map. Even when
the .NET runtime uses ICU internally, its edits aren't exposed through
`String.Normalize`. The normalizer built into the parser could produce its own edit map, but
runtime mode would still need another solution.

ICU therefore demonstrates an alternative approach that is possible due to the data it has available. Unfortunately, it isn't one this parser can use in all supported configurations, so we need an alternative. Because it uses data that isn't available after normalization is done, it also doesn't prove the validity of the
comparison-based algorithm used here. The validity of the algorithm this parser uses is proved
independently below.

## The mapping algorithm

Let `N` be one Unicode normalization operation: FormC, FormD, FormKC, or
FormKD.
Let `O` be the original string and let `Z = N(O)` be the normalized string.
Assume that normalization succeeds and every call uses this same `N`. Because
.NET stores strings as UTF-16, the algorithm works with UTF-16 code units, and
compares them ordinally (meaning as exact byte value comparisons).

In this project, normalization succeeding also means `O` is well-formed
UTF-16. A .NET string can hold an unpaired surrogate half, and such a string
isn't a coded character sequence in Unicode's sense (definition D12, quoted in
[Lemma 1](#lemma-1-every-comparison-cut-is-at-a-code-point-boundary)), so
none of the Unicode definitions cited below apply to it. Whether
`String.Normalize` rejects such input depends on the runtime: .NET throws,
and Unity's Mono returns the text unchanged. The parser therefore doesn't rely
on the runtime. Both normalizer modes scan for an unpaired surrogate before
normalizing (`UnicodeNormalization.FindFirstUnnormalizableIndex`) and report
an error if they find one. So `O`, and every candidate cut from it, is a
sequence of whole code points, and Lemma 1 shows the same for `Z` and every
other normalization result.

The algorithm divides `O` and `Z` into paired spans. Each original span
contains one or more whole extended grapheme clusters, and its paired span is
its normalization. The corresponding spans are only valid when this is true:

```text
O: |     C0     |     C1     | ... |     Ck     |
Z: |    N(C0)   |    N(C1)   | ... |    N(Ck)   |
```

The vertical lines are paired boundaries. Boundary positions are UTF-16 indexes because
that's the coordinate system used by .NET. Every boundary used by the algorithm
is also a Unicode code-point boundary. A *span* is the text between two
boundaries.

For FormKC and FormKD, one original extended grapheme cluster may not be enough
to form the next pair of boundaries since those forms can convert multiple graphemes to one normalized grapheme. The algorithm therefore starts with the next unclaimed
original grapheme, normalizes it, and compares the result with the next
unclaimed text in `Z`. If no prefix of the unclaimed text in `Z` matches, the algorithm adds another whole
original grapheme and tries again. If a match is found, their endpoints are the
proposed next pair of boundaries.

The comparison alone establishes only that `N(C)` appears as a prefix of the unclaimed text in `Z`. The
important question is whether that match could be accidental: could accepting
it leave a suffix of `Z` that isn't the normalization of the remaining suffix
of `O`? The proof below shows that this can't happen, and thus that
continuing the algorithm on the remaining suffix of `O` will segment the
entire string.

### Proof of correctness

The theorem to prove is that the algorithm terminates and produces chunks:

```text
O = C0 + C1 + ... + Ck
```

such that:

```text
Z = N(C0) + N(C1) + ... + N(Ck)
```

Furthermore, at every accepted pair of boundaries, the entire original string
from its start to the original boundary must normalize to the normalized string
from its start to the corresponding normalized boundary. Likewise, the
original text after the boundary must normalize to the normalized text after
its corresponding boundary. This stronger statement rules out accidental
prefix matches and allows the same operation to be repeated iteratively on
each remaining suffix until the end of the string.

### Results used by the proof

The proof uses Lemmas 6, 7, and 8 below. All three are proved in
[the appendix](#appendix-unicode-details-behind-the-comparison-proof), with the
relevant Unicode citations. The appendix first establishes Lemmas 1–6, then
uses those supporting lemmas to prove the two derived results, Lemmas 7 and 8.

1. **Lemma 6: A code-point-aligned substring of normalized text is normalized**
   ([proof](#lemma-6-a-substring-of-normalized-text-is-normalized)).
   Every prefix used below is code-point-aligned
   ([Lemma 1](#lemma-1-every-comparison-cut-is-at-a-code-point-boundary))
   and, because it's a substring of normalized text, is therefore normalized
   by this result.

2. **Lemma 7: Normalizing a concatenation directly equals normalizing its parts, then
   the whole**
   ([proof](#lemma-7-normalizing-a-concatenation-directly-equals-normalizing-its-parts-then-the-whole)).
   For any strings `X` and `Y`:

   ```text
   N(X + Y) = N(N(X) + N(Y))
   ```

   The outer call to `N` on the right side is essential. Unicode doesn't guarantee that
   `N(X) + N(Y)` is normalized, as discussed in the linked proof.

3. **Lemma 8: The unchanged-prefix lemma**
   ([proof](#lemma-8-the-unchanged-prefix-lemma)).
   If `Q` and `V` are normalized and the following equality holds:

   ```text
   N(Q + V) = Q + S,
   ```

   then the suffix must be:

   ```text
   S = V
   ```

   This lemma doesn't assume that no normalization operation crossed the join.
   It proves that, if the complete normalized prefix is unchanged, any such
   interaction can't leave a different normalized suffix behind.

### The invariant

Throughout the proof, `+` means string concatenation. A vertical bar `|` marks
a boundary between two concatenated strings. Thus, `O = P + U` and `O = P | U` state the same equality: `O` consists
of `P` immediately followed by `U`. The proof uses the two notations
interchangeably, using `|` when the boundary itself is important.

Now, for the invariant: Suppose the algorithm has already accepted an original prefix `P` (which might be empty). Let `U` be
all the original text that remains. The accepted pair of boundaries must
satisfy:

```text
O = P    | U
Z = N(P) | N(U)  (1)
```

This is the algorithm's invariant. It says that the prefix of `Z` ending at the
accepted boundary is exactly `N(P)`, the normalization of the original prefix
`P`. It also says that the complete unclaimed suffix of `Z` is exactly `N(U)`,
the normalization of the unclaimed original suffix `U`. This is what allows
the algorithm to repeat the same process on `U` to find the remaining pairs of
boundaries.

The invariant is true before the first iteration. At that point `P` is empty,
`U = O`, `N(P)` is empty, and `Z = N(O)`.

### A successful comparison preserves the invariant

The algorithm chooses a candidate `C` from the beginning of `U`, initially one
whole extended grapheme cluster, and lets `R` be everything after it:

```text
U = C + R
```

Recall the invariant:

```text
O = P    | U
Z = N(P) | N(U)
```

Substituting `U = C + R` gives:

```text
O = P    | C + R
Z = N(P) | N(C + R)
```

The algorithm now normalizes the candidate `C` by itself, producing `N(C)`.
It compares that result ordinally with the same number of UTF-16 code units at
the beginning of `N(C + R)` since this is the unclaimed suffix of `Z`.

The comparison succeeds only if those code units are exactly equal, so a
successful comparison establishes that `N(C)` is a prefix of `N(C + R)`. Let
`S` be everything in that suffix after the exact match:

```text
O: |       P       |    C    |    R    |
Z: |     N(P)      |  N(C)   |    S    |
```

When the ordinal comparison between `N(C)` and the beginning of the unclaimed
suffix `N(C + R)` succeeds, this must be true:

```text
N(C + R) = N(C) + S  (2)
```

Now, Lemma 7,
“[Normalizing a concatenation directly equals normalizing its parts, then the whole](#lemma-7-normalizing-a-concatenation-directly-equals-normalizing-its-parts-then-the-whole),”
states that for any strings `X` and `Y`:

```text
N(X + Y) = N(N(X) + N(Y))
```

Apply the rule with `X = C` and `Y = R`:

```text
N(C + R) = N(N(C) + N(R))  (3)
```

Equations (2) and (3) have the same left-hand side, so their right-hand sides
are equal:

```text
N(N(C) + N(R)) = N(C) + S  (4)
```

“[Lemma 8: The unchanged-prefix lemma](#lemma-8-the-unchanged-prefix-lemma)”
states:

```text
If Q and V are normalized and N(Q + V) = Q + S, then S = V
```

Apply the lemma with `Q = N(C)` and `V = N(R)`. We must verify all three of its
premises to use that lemma:

1. `N(C)` is normalized because it's a normalization result.
2. `N(R)` is normalized because it's a normalization result.
3. Equation (4) above establishes the third premise:

   ```text
   N(N(C) + N(R)) = N(C) + S
   ```

All three premises hold, so Lemma 8 gives:

```text
S = N(R)  (5)
```

This proves that the remaining suffix `S` of `Z` is exactly `N(R)`, which is
the normalization of the remaining suffix `R` of `O`. The complete state is
now:

```text
O: |       P       |    C    |    R    |
Z: |     N(P)      |  N(C)   |  N(R)   |
                   ^ current accepted pair of boundaries
```

At this point, the invariant is still positioned before `C`, but we can rewrite
it using what we now know is true:

```text
O = P    | C + R
Z = N(P) | N(C) + N(R)  (6)
         ^ current accepted pair of boundaries
```

To accept `C` and move the invariant to the next boundary, we must convert this
to the invariant form, which is:

```text
O = P + C    | R
Z = N(P + C) | N(R)  (7)
```

Equation (6) says that the prefix of `Z` ending immediately after the matched
`N(C)` is `N(P) + N(C)`. Before accepting the proposed boundary after `C`,
equation (7) requires us to prove that this prefix is equal to `N(P + C)`.
UAX #15 explains why that equality requires proof:

> “In using normalization functions, it is important to realize that none of
> the Normalization Forms are closed under string concatenation.”
>
> (from [UAX #15 for Unicode 16.0, Concatenation of Normalized Strings](https://www.unicode.org/reports/tr15/tr15-56.html#Concatenation))

Therefore, even though `N(P)` and `N(C)` are each normalized, their
concatenation may not be. So we need to prove that:

```text
N(P + C) = N(P) + N(C)
```

Recall from equation (6) that:

```text
Z = N(P) + N(C) + N(R)
```

The prefix `N(P) + N(C)` starts at the beginning of `Z` and ends after the
complete normalization result `N(C)`, so both of its ends are code-point
boundaries ([Lemma 1](#lemma-1-every-comparison-cut-is-at-a-code-point-boundary)). It's
therefore a code-point-aligned substring of normalized `Z` and is itself
normalized by “[Lemma 6: A substring of normalized text is normalized](#lemma-6-a-substring-of-normalized-text-is-normalized).”
In other words:

```text
N(N(P) + N(C)) = N(P) + N(C)  (8)
```

Recall that we are trying to prove `N(P + C) = N(P) + N(C)`.
To finish the proof, we must connect `N(P + C)` to `N(N(P) + N(C))`.

Apply Lemma 7,
“[Normalizing a concatenation directly equals normalizing its parts, then the whole](#lemma-7-normalizing-a-concatenation-directly-equals-normalizing-its-parts-then-the-whole),”
with `X = P` and `Y = C`:

```text
N(P + C) = N(N(P) + N(C))  (9)
```

Equation (8) shows that the right side of equation (9) is equal to
`N(P) + N(C)`, giving:

```text
N(P + C) = N(P) + N(C)  (10)
```

Equation (10) is the proof we were looking for. It holds here because its
right-hand side is a code-point-aligned substring of the normalized string
`Z`. It isn't a general rule that `N(P + C) = N(P) + N(C)`.

Now, recall that equation (6) gives the current known state:

```text
O = P    | C + R
Z = N(P) | N(C) + N(R)
```

By equation (10), we can replace `N(P) + N(C)` with `N(P + C)`. Moving `C` into
the accepted prefix then gives:

```text
O = P + C    | R
Z = N(P + C) | N(R)  (11)
             ^ newly accepted pair of boundaries
```

Equation (11) is the original invariant, now at the newly accepted pair of
boundaries. The algorithm can therefore accept `C` and repeat the same
operation with `P + C` as the accepted prefix and `R` as the unclaimed suffix.

Notice what this proof does and doesn't establish. It proves that the accepted
prefix of `Z` is exactly `N(P + C)` and that the text remaining in `Z` is
exactly `N(R)`. It doesn't claim that no combining mark moved across the join
during an intermediate normalization step. Such movement can occur and later
become invisible, as the
[example in Lemma 8's proof](#aside-movement-across-the-join-can-be-invisible)
shows. [Lemma 8](#lemma-8-the-unchanged-prefix-lemma) is precisely what makes
the conclusion valid without tracking where each code point came from.

### The search can't get stuck

The preceding section shows what happens when a comparison succeeds. Let's now look at what happens when it fails.

Recall
the full invariant at the currently accepted pair of boundaries:

```text
O = P    | U
Z = N(P) | N(U)
```

Here, `P` is the accepted original prefix and `U` is the unclaimed original
suffix. The candidate then splits `U` as:

```text
U = C + R
```

Here, `C` is the candidate being tested and `R` is the original text after it.
When a comparison fails, the algorithm moves the next nonempty extended
grapheme cluster from `R` into `C` and tries again. If every shorter candidate
fails, `C` eventually contains all of `U`, leaving no original text after it:

```text
C = U
R = empty
```

The invariant says that the unclaimed suffix of `Z` is `N(U)`. Because the
final candidate is `C = U`, its normalization is the same string:

```text
remaining Z = N(U) = N(C)
```

That final candidate must match. The original string contains finitely many
extended grapheme clusters, so the algorithm can't keep growing `C` forever
and can't run out of text without finding a successful candidate.

The algorithm first tests the next original cluster alone, then the next two
clusters together, and so on. Therefore, the first candidate that matches
contains the fewest possible whole original clusters. Every candidate ends at
one of the parser's cluster boundaries in the original.

No extra rule is needed to keep a candidate from ending in the middle of a
cluster. Each candidate is a sequence of whole extended grapheme clusters, so
its end is a UAX #29 cluster boundary, and a cluster is by definition the text
between two such boundaries (definition D61, quoted in
[Lemma 1](#lemma-1-every-comparison-cut-is-at-a-code-point-boundary)).

### Proof summary

Initially the invariant is true. Every successful comparison preserves it at
a later pair of boundaries, and a successful candidate is always eventually
found. Every accepted candidate consumes at least one original extended
grapheme cluster, so
only finitely many iterations are possible. By induction, the algorithm
reaches the ends of both strings and produces:

```text
O = C0 + C1 + ... + Ck
Z = N(C0) + N(C1) + ... + N(Ck)
```

At every intermediate cut, not merely at the end, the original prefix and
suffix normalize to the corresponding prefix and suffix of `Z`. Thus every
accepted pair of boundaries is valid, every accepted original span is locally
shortest among the whole-cluster candidates, and the algorithm consumes all of
`O` and `Z`.

### Turning the paired boundaries into a position

If the requested position in `Z` is exactly an accepted boundary, the algorithm
returns its paired boundary in `O`. If it lies strictly inside an accepted
normalized span `N(Ci)`, normalization may provide no exact original boundary
for it, so the algorithm returns the beginning of the paired original span `Ci`.
This approach is the project's position convention, not a further Unicode
theorem.

## Optimization for FormC/D

The general comparison algorithm proved above works for FormC and FormD.
However, a simpler algorithm is possible for FormC and FormD due to the
properties of Unicode. The simpler algorithm takes one extended grapheme
cluster from `O` and one extended grapheme cluster from `Z`, and pairs them
without performing the comparison. It can repeat that step until both strings
end.

For this optimization to be valid, we must establish both of the following
statements from the Unicode Standard:

1. `O` and `Z` can each be segmented directly into extended grapheme clusters.
2. The resulting clusters correspond one-for-one and in the same order.

### Requirement 1: `O` and `Z` can each be segmented directly

UAX #29 identifies the relevant specification explicitly:

> “The following is a general specification for grapheme cluster
> boundaries—language-specific rules in [CLDR] should be used where <!-- style-lint-ok: verbatim Unicode quote -->
> available.”
>
> (from [UAX #29 for Unicode 16.0, Default Grapheme Cluster Boundary Specification](https://www.unicode.org/reports/tr29/tr29-45.html#Default_Grapheme_Cluster_Table))

For that extended-grapheme-cluster-boundary specification, Section 2 explains
how the rules can be applied directly:

> “To maintain canonical equivalence, all of the following specifications are
> defined on text normalized in form NFD, as defined in Unicode Standard Annex
> #15, “Unicode Normalization Forms” [UAX15]. Boundaries never occur within a
> combining character sequence or conjoining sequence, so the boundaries
> within non-NFD text can be derived from corresponding boundaries in the NFD
> form of that text. For convenience, the default rules have been written so
> that they can be applied directly to non-NFD text and yield equivalent
> results.”
>
> (from [UAX #29 for Unicode 16.0, Conformance](https://www.unicode.org/reports/tr29/tr29-45.html#Conformance))

In plain English, the specification uses FormD to define where the extended-
grapheme-cluster boundaries must be. However, an implementation can apply the
extended-grapheme-cluster rules directly to a string without first converting
it to FormD and obtain the equivalent segmentation.

That covers any string, whatever its normalization form. So it covers `O`,
which might not be in any normalization form at all, and `Z`, whichever form
`N` produced. Both can be segmented directly into extended grapheme clusters.

This proves only that each segmentation can be performed directly. It doesn't,
by itself, prove that the first extended grapheme cluster of `O` pairs with the
first extended grapheme cluster of `Z`, the second pairs with the second, and
so on.
[Requirement 2](#requirement-2-the-resulting-clusters-correspond-one-for-one-and-in-the-same-order)
proves that.

### Requirement 2: The resulting clusters correspond one-for-one and in the same order

UAX #29 states the property needed for this requirement in two places. The
Grapheme Cluster Boundaries section describes it:

> “A key feature of Unicode grapheme clusters (both legacy and
> extended) is that they remain unchanged across all *canonically equivalent*
> [italics added] forms of the underlying text. Thus the boundaries remain
> unchanged whether the text is in NFC or NFD. Using a grapheme cluster as the
> fundamental unit of matching thus provides a very clear and easily explained
> basis for canonically equivalent matching. This is important for applications
> from searching to regular expressions.”
>
> (from [UAX #29 for Unicode 16.0, Grapheme Cluster Boundaries](https://www.unicode.org/reports/tr29/tr29-45.html#Grapheme_Cluster_Boundaries))

The implementation notes say the same thing more precisely, and spell out that
the matching boundaries can sit at different offsets:

> “The boundary specifications are stated in terms of text normalized
> according to Normalization Form NFD (see Unicode Standard Annex #15,
> “Unicode Normalization Forms” [UAX15]). In practice, normalization of
> the input is not required. To ensure that the same results are returned <!-- style-lint-ok: verbatim Unicode quote -->
> for canonically equivalent text (that is, the same boundary positions will
> be found, although those may be represented by different offsets), the
> grapheme cluster boundary specification has the following features:”
>
> - “There is never a break within a sequence of nonspacing marks.”
> - “There is never a break between a base character and subsequent nonspacing
>   marks.”
>
> (from [UAX #29 for Unicode 16.0, Implementation Notes, Normalization](https://www.unicode.org/reports/tr29/tr29-45.html#Normalization))

An extended grapheme cluster is the span between two consecutive extended-
grapheme-cluster boundaries. So if `O` and `Z` are canonically equivalent,
these statements give them the same boundary positions, possibly at different
UTF-16 offsets, and the spans between those boundaries (the extended grapheme
clusters) correspond in the same order.

We must therefore prove that `O` and `Z` are canonically equivalent. UAX #15
says as much in prose, at least for FormC:

> “There are two forms of normalization that convert to composite characters:
> Normalization Form C and Normalization Form KC. The difference between these
> depends on whether the resulting text is to be a canonical equivalent to the
> original unnormalized text or a compatibility equivalent to the original
> unnormalized text.”
>
> (from [UAX #15 for Unicode 16.0, Normalization Forms](https://www.unicode.org/reports/tr15/tr15-56.html#Norm_Forms))

That sentence is descriptive rather than a numbered definition, and it doesn't
mention FormD at all, so let's prove the claim more formally. The Unicode
Standard defines canonical equivalence precisely:

> “Two character sequences are said to be canonical equivalents if their full
> canonical decompositions are identical.”
>
> (from [The Unicode Standard 16.0, Section 3.7.2, definition D70](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G743))

FormD is that full canonical decomposition. Definition D118 (quoted in
[Lemma 1](#lemma-1-every-comparison-cut-is-at-a-code-point-boundary)) says
FormD is the Canonical Decomposition of a coded character sequence, and D68
defines that operation, reordering step included:

> “D68 Canonical decomposition: The decomposition of a character or character
> sequence that results from recursively applying the canonical mappings
> found in the Unicode Character Database and those described in Section
> 3.12, Conjoining Jamo Behavior, until no characters can be further
> decomposed, and then reordering nonspacing marks according to Section 3.11,
> Normalization Forms.”
>
> (from [The Unicode Standard 16.0, Section 3.7.2, definition D68](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G7425))

The core specification doesn't define “full canonical decomposition” as a
separate term. This document reads it as the D68 operation applied to the
whole sequence, reordering step included. That's the reading Unicode's own
design statement requires:

> “When two combining characters C1 and C2 do not typographically interact, <!-- style-lint-ok: verbatim Unicode quote -->
> the sequence C1+ C2 is canonically equivalent to C2+ C1.”
>
> (from [The Unicode Standard 16.0, Section 3.11.2, Combining Classes](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G62779))

Two such sequences have the same D68 result only after the reordering step,
so a reading without it wouldn't make them canonical equivalents. So by D70,
proving that `O` and `Z` are canonically equivalent means proving:

```text
FormD(O) = FormD(Z)
```

[Lemma 2](#lemma-2-normalization-preserves-the-decomposition) already proves
this. Its equation (A1) states, for every string `X`:

```text
D(N(X)) = D(X)                                      (A1)
```

`D` is FormD whenever `N` is FormC or FormD, which is the case here. Setting
`X = O` gives:

```text
FormD(N(O)) = FormD(O)
```

`Z` is defined as `N(O)`, so the left side is `FormD(Z)`:

```text
FormD(Z) = FormD(O)
```

That's the equality the goal above asked for, so `O` and `Z` are canonically
equivalent under definition D70, for FormD as well as FormC.

The UAX #29 statements now apply. The Conformance paragraph quoted in
[Requirement 1](#requirement-1-o-and-z-can-each-be-segmented-directly) also
explains why they hold, by defining every string's boundaries through its
FormD form:

> “Boundaries never occur within a combining character sequence or conjoining
> sequence, so the boundaries within non-NFD text can be derived from
> corresponding boundaries in the NFD form of that text.”
>
> (from [UAX #29 for Unicode 16.0, Conformance](https://www.unicode.org/reports/tr29/tr29-45.html#Conformance))

Apply that sentence to each string. The boundaries of `O` are derived from the
boundaries of `FormD(O)`, and the boundaries of `Z` are derived from the
boundaries of `FormD(Z)`. The equality just proved says those are the same
string, so both derivations start from one set of boundaries in one string.
Each boundary in that set yields one boundary in `O` and one in `Z`, in the
same left-to-right order. Segmenting each string directly therefore produces
the same number of extended grapheme clusters in the same order, even though
corresponding boundaries may sit at different UTF-16 offsets.

Write those clusters as:

```text
O = G0 + G1 + ... + Gm
Z = H0 + H1 + ... + Hm
```

There's one `Hi` for every `Gi`. This correspondence (not merely the
direct-segmentation rule in
[Requirement 1](#requirement-1-o-and-z-can-each-be-segmented-directly))
is what allows taking one extended
grapheme cluster from each string on every iteration and pairing them as
canonically equivalent spellings of the same cluster. Each side advances by
the UTF-16 length of its own cluster, so the two offsets don't have to be equal.

### Conclusion: why the FormC/FormD algorithm is valid

[Requirement 1](#requirement-1-o-and-z-can-each-be-segmented-directly)
establishes that `O` and `Z` can each be segmented directly.
[Requirement 2](#requirement-2-the-resulting-clusters-correspond-one-for-one-and-in-the-same-order)
establishes that canonical normalization preserves those
clusters in the same order, so there's exactly one `Hi` for every `Gi`.

The algorithm may therefore take one extended grapheme cluster from each
string and pair them on every iteration. The two clusters may occupy different
numbers of UTF-16 code units, so their numeric offsets may differ, but each
side advances by its own cluster's length. Because the two strings have the
same number of clusters, both sides reach the end together. The boundary after
each `Hi` therefore maps to the boundary after its paired `Gi`. A position
inside `Hi` maps to the start of `Gi`, as required by the position-mapping rule.

### Why this simple algorithm doesn't work for FormKC and FormKD

UAX #29's guarantee applies to canonical equivalence. FormKC and FormKD also
apply compatibility mappings, so their results don't have to be canonically
equivalent to the original. Compatibility normalization can turn one original
extended grapheme cluster into several normalized clusters or several original
clusters into one normalized cluster. A one-for-one lockstep algorithm
therefore can't be used for these forms. They use the comparison algorithm
proved above.

### Caveats

Two things about the proof above are worth noting.

#### Caveat 1: the guarantee is descriptive prose, not a conformance clause

The UAX #29 statements quoted in
[Requirement 2](#requirement-2-the-resulting-clusters-correspond-one-for-one-and-in-the-same-order)
are descriptive prose, not numbered definitions or conformance clauses. That
includes the sentence from the Conformance section that the derivation there
rests on: it sits in that section's explanatory text, not in one of its
numbered clauses. The property spans two specifications, and it holds because
the rules and the character data are chosen to keep it true. UAX #29 says so
when it describes what can continue a legacy grapheme cluster (an extended
grapheme cluster continues with everything a legacy one does, plus all spacing
combining marks):

> “The continuing characters include nonspacing marks, the Join_Controls
> (U+200C ZERO WIDTH NON-JOINER and U+200D ZERO WIDTH JOINER) used in Indic
> languages, and a few spacing combining marks to ensure canonical
> equivalence.”
>
> (from [UAX #29 for Unicode 16.0, Grapheme Cluster Boundaries](https://www.unicode.org/reports/tr29/tr29-45.html#Grapheme_Cluster_Boundaries))

Its implementation notes name one of those marks and say why it's there:

> “The specification also avoids certain problems by explicitly assigning the
> Extend property value to certain characters, such as U+09BE (&nbsp;া&nbsp;)
> BENGALI VOWEL SIGN AA, to deal with particular compositions.”
>
> (from [UAX #29 for Unicode 16.0, Implementation Notes, Normalization](https://www.unicode.org/reports/tr29/tr29-45.html#Normalization))

The composition in question is U+09CB BENGALI VOWEL SIGN O. Its canonical
decomposition in the
[Unicode 16.0 Character Database](https://www.unicode.org/Public/16.0.0/ucd/UnicodeData.txt)
is U+09C7 BENGALI VOWEL SIGN E followed by U+09BE, and both halves have
`ccc = 0`, so both are starters. The core specification points out that such
pairs compose anyway:

> “The character C in R1 is not necessarily a non-starter. It is necessary to <!-- style-lint-ok: verbatim Unicode quote -->
> check all characters in the sequence, because there are sequences `<L, C>`
> where both L and C are Starters, yet there is a Primary Composite P which is
> canonically equivalent to that sequence. For example, Indic two-part vowels
> often have canonical decompositions into sequences of two spacing vowel
> signs, each of which has Canonical_Combining_Class = 0 and which is thus a
> Starter by definition. Nevertheless, such a decomposed sequence has an
> equivalent Primary Composite.”
>
> (from [The Unicode Standard 16.0, Section 3.11.6, note under rule R2](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49620))

So FormD turns the one-character spelling U+09CB into two starters, and the
segmentation rules have to keep those two starters in one cluster to match the
single cluster the precomposed spelling forms. For legacy grapheme clusters,
which don't otherwise include spacing marks, the explicit assignment is what
does that. It's recorded as `Other_Grapheme_Extend` in
[PropList.txt](https://www.unicode.org/Public/16.0.0/ucd/PropList.txt), which
gives U+09BE `Grapheme_Extend` and therefore `Grapheme_Cluster_Break = Extend`.
The extended grapheme clusters this parser uses attach every spacing mark
through rule GB9a (`× SpacingMark`), so U+09BE stays attached under either
spelling there as well.

Relying on the guarantee therefore means trusting the Unicode Consortium to
keep its rules and data consistent with its own claim. That trust is
reasonable because UAX #29 presents the property as a key feature of grapheme
clusters, not an accident of the current rules.

#### Caveat 2: neither segmenter this project ships runs that exact configuration

The guarantee is for UAX #29's
[default extended-grapheme-cluster rules](https://www.unicode.org/reports/tr29/tr29-45.html#Default_Grapheme_Cluster_Table)
over Unicode's own data, and neither segmenter this project ships runs
exactly that configuration. The built-in `GraphemeSegmentation` omits GB9c to
match .NET 10, and the runtime `StringInfo` segmenter has whatever rule set
and data version the installed .NET has, which may not match the normalizer's.

The gap is small: the built-in segmenter runs the Unicode 16
rules over the Unicode 16 data minus GB9c, a rule that only joins more text
into a cluster, and .NET 10's `StringInfo` is the same rule set over the same
data (the differential tests under `Lexing/Unicode` ensure it). Rather than
argue that the missing rule can't matter, the test project checks the
property directly. `NormalizationTests` compares the lockstep walker against
a brute-force reference that never assumes the property, for FormC and FormD
over the curated example rows, and the opt-in `NormalizationConformanceTests`
sweep does the same over every line of the Unicode 16 normalization
conformance file, against whichever segmenter and normalizer the test process
resolves to.

## Appendix: Unicode details behind the comparison proof

This appendix proves two derived lemmas used by the main comparison proof:
Lemma 7, for normalizing a concatenation, and Lemma 8, the unchanged-prefix
lemma. Both proofs depend on six supporting Unicode lemmas, established first.
Some of those lemmas are also used independently in the main proof rather than
only through the two derived results.

Several of the lemmas compare strings through their fully decomposed forms and use `N` and `D` to indicate this for shorthand. For
the selected normalization operation `N`, let `D` denote its corresponding
Unicode-defined fully decomposed form:

| Selected `N` | `D` |
| --- | --- |
| FormC or FormD | FormD |
| FormKC or FormKD | FormKD |

The six supporting lemmas are:

1. Every cut used by the mapping algorithm is at a code-point boundary.
2. Normalizing a string doesn't change its decomposition:
   `D(N(X)) = D(X)`.
3. Canonical ordering is stable: code points with equal combining classes
   don't exchange places.
4. Decomposing a concatenation is equivalent to concatenating the two
   decompositions and ordering the complete result:
   `D(X + Y) = canonically order (D(X) + D(Y))`.
5. Strings with the same decomposition have the same selected
   normalized form: if `D(X) = D(Y)`, then `N(X) = N(Y)`.
6. A substring of normalized text is normalized.

Lemma 7 uses Lemmas 2, 4, and 5. Lemma 8 uses all six. Each supporting lemma
is established below
using definitions and statements from official Unicode sources before either
proof uses it.

### Lemma 1: Every comparison cut is at a code-point boundary

Here, a *cut* is where the comparison divides a string: at the end of a
candidate `C` in the original text, or at the end of its complete normalization
result `N(C)` in the normalized text. This lemma shows that neither cut can fall
between the two UTF-16 code units of a supplementary code point. The proof
needs this guarantee before treating a span of normalized text as a Unicode
substring and applying Lemma 6.

We must prove that two kinds of comparison cuts are code-point-aligned:

1. On the original side, candidates contain whole extended grapheme clusters.
   We must show that the boundaries of those clusters can't split a code
   point.
2. On the normalized side, a cut occurs after a complete normalization result
   `N(C)`. We must show that this result ends after a complete code point.

We couldn't find an official Unicode statement that says either conclusion
outright. Therefore, rather than assume code-point alignment, we must prove it.
The two subsections below do so from the relevant Unicode definitions and
rules.

#### Original-side cuts

The point to establish is that UAX #29's rules can manipulate breaks only
before, between, or after complete code points. An arbitrary UTF-16 code-unit
offset (such as the offset between the two code units of a surrogate pair)
isn't a position these rules can make into a boundary. Once that is proved, the
definition of an extended grapheme cluster shows that the boundaries of each
candidate span `C` in the original text can't split a code point.

**Why the rules can manipulate only code-point boundaries.** UAX #29 applies
its rules in sequence to decide whether a boundary exists at an offset:

> “The rules are numbered for reference and are applied in sequence to
> determine whether there is a boundary at any given offset.”
>
> (from [UAX #29 for Unicode 16.0, Notation](https://www.unicode.org/reports/tr29/tr29-45.html#Notation))

Each rule places a boundary symbol between a left and right expression:

> “Each rule consists of a left side, a boundary symbol (see Table 1), and a
> right side.”
>
> (from [UAX #29 for Unicode 16.0, Notation](https://www.unicode.org/reports/tr29/tr29-45.html#Notation))

Each rule has exactly one boundary symbol. UAX #29 states that as the first of
its rule constraints:

> “Single boundaries. Each rule has exactly one boundary position.”
>
> (from [UAX #29 for Unicode 16.0, Rule Constraints](https://www.unicode.org/reports/tr29/tr29-45.html#Rule_Constraints))

Table 1 lists three boundary symbols. The third one doesn't appear in any of
the grapheme cluster rules, which use only the first two:

> `÷` &nbsp; “Boundary (allow break here)”
>
> `×` &nbsp; “No boundary (do not allow break here)” <!-- style-lint-ok: verbatim Unicode quote -->
>
> `→` &nbsp; “Treat whatever on the left side as if it were what is on the right side”
>
> (from [UAX #29 for Unicode 16.0, Notation, Table 1](https://www.unicode.org/reports/tr29/tr29-45.html#Table_Boundary_Symbols))

The left and right expressions match sequences of boundary-property values:

> “The left and right sides use the boundary property values in regular
> expressions.”
>
> (from [UAX #29 for Unicode 16.0, Notation](https://www.unicode.org/reports/tr29/tr29-45.html#Notation))

`sot` and `eot` mean “start of text” and “end of text,” respectively
([UAX #29 for Unicode 16.0, Notation](https://www.unicode.org/reports/tr29/tr29-45.html#Notation)).
The operand `Any` represents any code point:

> “This is not a property value; it is used in the rules to represent any code <!-- style-lint-ok: verbatim Unicode quote -->
> point.”
>
> (from [UAX #29 for Unicode 16.0, Grapheme_Cluster_Break Property Values, `Any`](https://www.unicode.org/reports/tr29/tr29-45.html#Grapheme_Cluster_Break_Property_Values))

The following rules show how this notation can manipulate breaks only at the
text edges or between complete code points:

```text
GB1      sot    ÷    Any
GB2      Any    ÷    eot
GB3      CR     ×    LF
GB999    Any    ÷    Any
```

([UAX #29 for Unicode 16.0, Grapheme Cluster Boundary Rules](https://www.unicode.org/reports/tr29/tr29-45.html#Grapheme_Cluster_Boundary_Rules))

- `GB1` manipulates the position before the first code point: it permits a
  break at the start of the text.
- `GB2` manipulates the position after the last code point: it permits a break
  at the end of the text.
- `GB3` manipulates the gap between the adjacent `CR` and `LF` code points: it
  suppresses a break there.
- `GB999` manipulates the gap between any two adjacent code points: it permits
  a break there if no earlier rule has decided that position.

`GB999` supplies a default decision for every internal gap because each `Any`
matches a complete code point. An earlier rule can override that decision, as
`GB3` does for a `CR` followed by `LF`, but it uses the same notation to change
the decision at the gap. It can't move the gap inside either code point. The
other rules likewise place their boundary symbol between expressions that match
complete code points, so they can change the break decision at a gap but can't
create a break inside a code point.

The rules establish where extended grapheme-cluster boundaries can occur.
Definition D61 then specifies what lies between those boundaries:

> “Extended grapheme cluster: The text between extended grapheme cluster
> boundaries as specified by Unicode Standard Annex #29, ‘Unicode Text
> Segmentation.’”
>
> (from [The Unicode Standard 16.0, Section 3.6.2, definition D61](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G41732))

An extended grapheme cluster starts and ends at two of those boundaries. Since
the rules can place them only at the text edges or between complete code
points, the cluster can't start or end inside a code point. Each candidate
`C` consists of whole clusters, so its start and end are code-point boundaries
too.

#### Normalized-side cuts

The question here is about *output*: can the complete normalization result
`N(C)` end inside a code point? Defining the input as a coded character
sequence isn't enough to answer that. We must check what the normalization
steps produce.

The Unicode Standard defines a coded character sequence as:

> “D12 Coded character sequence: An ordered sequence of one or more code
> points.”
>
> (from [The Unicode Standard 16.0, Section 3.4, definition D12](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G45138))

This matters because the operation definitions below use the word *character*.
The normalization section explicitly explains that usage:

> “The specification of Unicode Normalization Forms applies to all Unicode
> coded character sequences (D12). For clarity of exposition in the definitions
> and rules specified here, the terms “character” and “character sequence” are
> used, but coded character sequences refer also to sequences containing
> noncharacters or reserved code points. Unicode Normalization Forms are
> specified for all Unicode code points, and not just for ordinary, assigned
> graphic characters.”
>
> (from [The Unicode Standard 16.0, Section 3.11.3, Specification of Unicode Normalization Forms](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49576))

Thus, *character* in the following normalization steps refers to a complete
code-point element of the sequence, not to a UTF-16 code unit or part of a code
point.

The normalization steps operate on those complete code points and produce
complete code points. First, a decomposition mapping replaces a character with
a sequence of one or more characters:

> “D62 Decomposition mapping: A mapping from a character to a sequence of one
> or more characters that is a canonical or compatibility equivalent, and that
> is listed in the character names list or described in Section 3.12,
> Conjoining Jamo Behavior.”
>
> (from [The Unicode Standard 16.0, Section 3.7, definition D62](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G40097))

Canonical ordering exchanges the positions of whole characters in a
decomposed sequence:

> “D109 Canonical Ordering Algorithm: In a decomposed character sequence D,
> exchange the positions of the characters in each Reorderable Pair until the
> sequence contains no more Reorderable Pairs.”
>
> (from [The Unicode Standard 16.0, Section 3.11.5, definition D109](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49593))

To identify the operands of the composition rule, note that `C` in the Unicode
rules quoted here names a single character being composed. It isn't the
candidate span `C` used elsewhere in this document. A Starter `L` is defined
as a code point:

> “D107 Starter: Any code point (assigned or not) with combining class of zero
> (ccc = 0).”
>
> (from [The Unicode Standard 16.0, Section 3.11.4, definition D107](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49580))

It defines a Primary Composite `P` as a character:

> “D114 Primary composite: A Canonical Decomposable Character (D69) which is
> not a Full Composition Exclusion.”
>
> (from [The Unicode Standard 16.0, Section 3.11.6, definition D114](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49608))

Rule R1 identifies `C` as a character in the coded character sequence:

> “R1 Seek back (left) in the coded character sequence from the character C to
> find the last Starter L preceding C in the character sequence.”
>
> (from [The Unicode Standard 16.0, Section 3.11.6, definition D117, rule R1](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49614))

With `L`, `C`, and `P` thus identified as complete code-point elements, R2
specifies the replacement and deletion:

> “R2 If there is such an L, and C is not blocked from L, and there exists a <!-- style-lint-ok: verbatim Unicode quote -->
> Primary Composite P which is canonically equivalent to the sequence
> `<L, C>`, then replace L by P in the sequence and delete C from the sequence.”
>
> (from [The Unicode Standard 16.0, Section 3.11.6, definition D117, rule R2](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49614))

R2 therefore replaces complete `L` with complete `P` and removes complete
`C`. It doesn't modify only part of a code point.

Definitions D118-D121 specify which of those steps each normalization form
uses:

> “D118 Normalization Form D (NFD): The Canonical Decomposition of a coded
> character sequence.”
>
> “D119 Normalization Form KD (NFKD): The Compatibility Decomposition of a
> coded character sequence.”
>
> “D120 Normalization Form C (NFC): The Canonical Composition of the Canonical
> Decomposition of a coded character sequence.”
>
> “D121 Normalization Form KC (NFKC): The Canonical Composition of the
> Compatibility Decomposition of a coded character sequence.”
>
> (from [The Unicode Standard 16.0, Section 3.11.7, D118-D121](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49623))

Thus, a complete normalization result `N(C)` is a sequence of whole code
points, regardless of which of the four forms `N` selects. Its endpoint can't
split a code point. Each comparison begins at an already established
code-point boundary, and both possible new endpoints (the end of `C` and the
end of `N(C)`) are also code-point boundaries.

One more step connects that to `Z` itself. The cut in `Z` sits at the UTF-16
offset where the matched copy of `N(C)` ends, and the code units before it
are exactly `N(P)` followed by `N(C)`, both sequences of whole code points.
`Z` is well-formed UTF-16 because it's a normalization result. So the last
code unit before the cut is either a code unit that is a whole code point on
its own (a Basic Multilingual Plane character), which is a whole code point in
`Z` too, or a low surrogate whose high surrogate is the code unit just before
it, also inside the prefix. Either way the cut in `Z` falls between whole code
points, which is what lets
[Lemma 6](#lemma-6-a-substring-of-normalized-text-is-normalized) treat the
prefix as a substring of `Z`. The same reasoning covers the cut after `Q` in
Lemma 8, where `Q` is a normalization result and `N(Q + V)` is well-formed.

### Lemma 2: Normalization preserves the decomposition

Recall from the beginning of this section that `D(X)` is the complete, canonically ordered decomposition of `X`:
FormD when `N` is FormC or FormD, and FormKD when `N` is FormKC or FormKD.

We will prove that, for every string `X`:

```text
D(N(X)) = D(X)                                      (A1)
```

UAX #15's Design Goals section states:

> “Another consequence of the definitions is that any chain of normalizations
> is equivalent to a single normalization, which is:”
>
> 1. “a compatibility normalization, if any normalization is a compatibility
>    normalization”
> 2. “a composition normalization, if the final normalization is a composition
>    normalization”
>
> (from [UAX #15 for Unicode 16.0, Design Goals](https://www.unicode.org/reports/tr15/tr15-56.html#Design_Goals))

The same section then says “For example, the following table lists equivalent
chains of two transformations:” and presents the table below. The highlighted
entries give the identities needed to prove equation (A1).
[Lemma 5](#lemma-5-equal-decompositions-give-equal-normalized-forms) uses two
more entries from the same table.

#### UAX #15 two-step normalization table

`toNFC(x)` means apply FormC to `x`, `toNFD(x)` means apply FormD,
`toNFKC(x)` means apply FormKC, and `toNFKD(x)` means apply FormKD. In
`toNFD(toNFC(x))`, the inner FormC step happens first, then FormD. Each
entry below a column heading gives the same result as that heading. The table
is transcribed from UAX #15. The highlighting is ours.

> “
>
> | `toNFC(x)` | `toNFD(x)` | `toNFKC(x)` | `toNFKD(x)` |
> | --- | --- | --- | --- |
> | `=toNFC(toNFC(x))`<br>`=toNFC(toNFD(x))` | <mark><code>=toNFD(toNFC(x))</code></mark><br><mark><code>=toNFD(toNFD(x))</code></mark> | `=toNFC(toNFKC(x))`<br>`=toNFC(toNFKD(x))`<br>`=toNFKC(toNFC(x))`<br>`=toNFKC(toNFD(x))`<br>`=toNFKC(toNFKC(x))`<br>`=toNFKC(toNFKD(x))` | `=toNFD(toNFKC(x))`<br>`=toNFD(toNFKD(x))`<br>`=toNFKD(toNFC(x))`<br>`=toNFKD(toNFD(x))`<br><mark><code>=toNFKD(toNFKC(x))</code></mark><br><mark><code>=toNFKD(toNFKD(x))</code></mark> |
>
> ”
>
> (from [UAX #15 for Unicode 16.0, Design Goals](https://www.unicode.org/reports/tr15/tr15-56.html#Design_Goals))

The highlighted cells cover all four choices for `N`: the `toNFD(x)` column
covers FormC and FormD, and the `toNFKD(x)` column covers FormKC and FormKD.
In every case, decomposing after `N` gives the same result as decomposing `X`
directly. This proves the claim we set out to establish in equation (A1):

```text
D(N(X)) = D(X)                                      (A1)
```

### Lemma 3: Canonical ordering is stable

Here, *stable* has its standard sorting meaning: when two code points have the
same sorting key, ordering doesn't reverse their relative order. This use of
*stable* is unrelated to Unicode's guarantees about normalization remaining
stable across Unicode versions.

For canonical ordering, that sorting key is called the canonical combining class
(`ccc`). Each code point has one. Definitions D108 and D109 state both the
condition for reordering and the algorithm that uses it:

> “D108 Reorderable pair: Two adjacent characters A and B in a coded character
> sequence `<A, B>` are a Reorderable Pair if and only if
> `ccc(A) > ccc(B) > 0`.”
>
> “D109 Canonical Ordering Algorithm: In a decomposed character sequence D,
> exchange the positions of the characters in each Reorderable Pair until the
> sequence contains no more Reorderable Pairs.”
>
> (from [The Unicode Standard 16.0, Section 3.11.5, Canonical Ordering Algorithm, D108-D109](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49592))

D108 never permits two code points with the same `ccc` to exchange places.
Since D109 swaps only adjacent code points, equal-`ccc` code points retain
their original order. That's the stability property.

Sorting is a separate point. A code point with `ccc = 0` is called a *starter*.
D108 can't exchange a starter with a neighbor, so starters stay in place. D109
stops only when no Reorderable Pair remains. At that point, every maximal
stretch of nonstarters (between starters or at either end of the string) is in
nondecreasing `ccc` order. That sorted order, together with the preserved order
of equal-`ccc` code points, uniquely determines the result within each stretch.
Canonical ordering is therefore a stable sort within each such stretch.

### Lemma 4: Decomposing a concatenation orders the two decompositions together

For all strings `X` and `Y`:

```text
D(X + Y) = canonically order (D(X) + D(Y))          (A2)
```

Unicode definition D64 states:

> “A full decomposition of a character sequence results from decomposing each
> of the characters in the sequence until no characters can be further
> decomposed.”
>
> (from [The Unicode Standard 16.0, Section 3.7, definition D64](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G742))

The point of D64 here is that raw decomposition expands each character (and any
decomposable characters it produces) without changing the order of the original
sequence. Thus the raw decomposition of `X + Y` is the raw decomposition of `X`
followed by the raw decomposition of `Y`.

The core specification states that ordering follows full decomposition:

> “Logically, to get the NFD or NFKD (maximally decomposed) normalization form
> for a Unicode string, one first computes the full decomposition of that
> string and then applies the Canonical Ordering Algorithm to it.”
>
> (from [The Unicode Standard 16.0, Section 3.11.7, Definition of Normalization Forms](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49621))

UAX #15 describes the same two stages:

> “Once a string has been fully decomposed, any sequences of combining marks
> that it contains are put into a well-defined order.”
>
> (from [UAX #15 for Unicode 16.0, Description of the Normalization Process](https://www.unicode.org/reports/tr15/tr15-56.html#Description_of_the_Normalization_Process))

Applying `D` to `X` and `Y` separately performs both stages on each piece:
recursive decomposition followed by canonical ordering.
Consequently, `D(X)` and `D(Y)` have each already been canonically ordered, but
their concatenation may still need additional ordering across the point where
the pieces meet. Canonically ordering `D(X) + D(Y)` performs that remaining
work. By Lemma 3, the ordering is stable, so this produces the same result as
canonically ordering the complete raw decomposition of `X + Y` at once: each
maximal stretch of nonstarters ends up in nondecreasing `ccc` order, and within
each class the entries from `X` keep their original order and come before the
entries from `Y`. Lemma 8's proof below walks through the
same merge in detail. This proves equation (A2).

### Lemma 5: Equal decompositions give equal normalized forms

We will prove that `D(X) = D(Y)` implies `N(X) = N(Y)`.

Each normalization form is a function of its decomposition alone, so equal
decompositions can't produce different normalized forms. There are two cases.

When `N` is FormD or FormKD, `N` is `D` itself (definitions D118 and D119,
quoted in
[Lemma 1](#lemma-1-every-comparison-cut-is-at-a-code-point-boundary)), so the
premise `D(X) = D(Y)` already says `N(X) = N(Y)`.

When `N` is FormC or FormKC, the
[two-step normalization table](#uax-15-two-step-normalization-table) in
Lemma 2 includes these two entries:

```text
toNFC(x)  = toNFC(toNFD(x))
toNFKC(x) = toNFKC(toNFKD(x))
```

In this document's notation, the first says `FormC(X) = FormC(FormD(X))` and
the second says `FormKC(X) = FormKC(FormKD(X))`. With `D` matched to `N` as
the table at the start of this appendix specifies, both read `N(X) = N(D(X))`.
The premise says `D(X)` and `D(Y)` are the same string, and applying `N` to
one string gives one result:

```text
N(X) = N(D(X)) = N(D(Y)) = N(Y)
```

Definitions D120 and D121 (also quoted in Lemma 1) say the same thing
directly. FormC is “the Canonical Composition of the Canonical Decomposition
of a coded character sequence” and FormKC is “the Canonical Composition of the
Compatibility Decomposition of a coded character sequence”, and the Canonical
Composition Algorithm (definition D117, quoted in Lemma 1) is a deterministic
procedure on the decomposed sequence. Equal decompositions feed equal input to
the same procedure.

In either case, the selected `N` gives `N(X) = N(Y)`, as required.

### Lemma 6: A substring of normalized text is normalized

UAX #15 states:

> "all of the Normalization Forms are closed under substringing."
>
> (from [UAX #15 for Unicode 16.0, Concatenation of Normalized Strings](https://www.unicode.org/reports/tr15/tr15-56.html#Concatenation))

Therefore any substring cut from normalized text at code-point boundaries is
normalized on its own. Lemma 1 establishes that the cuts used here satisfy that
condition.

### Lemma 7: Normalizing a concatenation directly equals normalizing its parts, then the whole

The rule to be proved is that, for any strings `X` and `Y`:

```text
N(X + Y) = N(N(X) + N(Y))                          (A5)
```

In words, normalizing two concatenated strings produces the same result as
normalizing each string first, concatenating those results, and then
normalizing the complete concatenation.

Lemma 4, equation (A2), is the following general rule:

```text
D(left + right)
    = canonically order (D(left) + D(right))        (A2)
```

Substitute `N(X)` for `left` and `N(Y)` for `right`:

```text
D(N(X) + N(Y))
    = canonically order (D(N(X)) + D(N(Y)))         (A3)
```

Lemma 2, equation (A1), says that `D(N(X)) = D(X)` and
`D(N(Y)) = D(Y)`. Substitute those equal strings into equation (A3):

```text
D(N(X) + N(Y))
    = canonically order (D(X) + D(Y))
```

Lemma 4, equation (A2), also says:

```text
D(X + Y) = canonically order (D(X) + D(Y))
```

The two decompositions equal the same ordered expression, so they equal each
other:

```text
D(N(X) + N(Y)) = D(X + Y)                          (A4)
```

Equation (A4) states that the two strings `N(X) + N(Y)` and `X + Y` have the
same decomposition. Lemma 5 says that two strings with the same decomposition
have the same normalized form. Applying that lemma directly to these
two strings gives:

```text
N(N(X) + N(Y)) = N(X + Y)
```

Equality is symmetric, so the two sides can be reversed:

```text
N(X + Y) = N(N(X) + N(Y))                          (A5)
```

This is the rule stated at the beginning of the section. Substituting `C` for
`X` and `R` for `Y` gives the rule used in the main proof.

Equation (A5) doesn't say that `N(X) + N(Y)` is already normalized. The outer
call to `N` handles any ordering or composition required across the join. UAX
#15 explicitly warns:

> “none of the Normalization Forms are closed under string concatenation.”
>
> (from [UAX #15 for Unicode 16.0, Concatenation of Normalized Strings](https://www.unicode.org/reports/tr15/tr15-56.html#Concatenation))

### Lemma 8: The unchanged-prefix lemma

We must prove:

> If `Q` and `V` are normalized and
>
> ```text
> N(Q + V) = Q + S,
> ```
>
> then `S = V`.

#### Aside: movement across the join can be invisible

This aside rules out a tempting shortcut for proving Lemma 8: if normalization
leaves the prefix `Q` unchanged, one might assume that no code point from `V`
crossed the `Q | V` join and use that assumed lack of movement to conclude
`S = V`.
The assumption is false. The proof below instead uses equality of full
decompositions and the behavior of canonical ordering.

Let `N` be FormC, let `Q` be `Ậ` (U+1EAC, LATIN CAPITAL LETTER A WITH
CIRCUMFLEX AND DOT BELOW), and let `V` be U+0323 COMBINING DOT BELOW. Both `Q`
and `V` are already normalized. Before normalization, their join is:

```text
U+1EAC[Q] | U+0323[V]
```

The `[Q]` and `[V]` labels record where each code-point occurrence came from.
They aren't part of the string. Decomposing U+1EAC produces `A`, a dot below,
and a circumflex. The number before each nonstarter is its `ccc` value:

```text
before ordering:   U+0041[Q] [220:U+0323[Q] | 230:U+0302[Q]] + [220:U+0323[V]]
after ordering:    U+0041[Q] [220:U+0323[Q],U+0323[V] | 230:U+0302[Q]]
```

The second line shows that canonical ordering places class 220 before class
230, so the dot from `V` moves left across the circumflex from `Q`.

Canonical composition then rebuilds `Ậ` (U+1EAC) from the `A`, the first dot
below, and the circumflex. The second dot remains, producing:

```text
U+1EAC U+0323
```

That's exactly `Q + V`. The final prefix is still the exact `Q`, even though
the dot originating in `V` crossed a decomposed code point originating in `Q`.
The decomposition and combining classes are recorded in the
[Unicode 16.0 Character Database](https://www.unicode.org/Public/16.0.0/ucd/UnicodeData.txt).

Now back to the real proof: The proof has three steps.

#### Proof step 1: `S` is normalized

`N(Q + V)` is normalized. The premise says that it is equal to `Q + S`, so `S`
is a suffix (and therefore a substring) of that normalized string, and the cut
after `Q` is at a code-point boundary by the argument at the end of
[Lemma 1](#lemma-1-every-comparison-cut-is-at-a-code-point-boundary). Lemma 6
states that a substring of normalized text is normalized. Therefore `S` is
normalized too.

#### Proof step 2: `S` and `V` have the same decomposition

The lemma assumes:

```text
N(Q + V) = Q + S
```

Fully decompose both equal strings. In other words, apply the same function
`D` to both sides of that equality:

```text
D(N(Q + V)) = D(Q + S)
```

Lemma 2, equation (A1), states that `D(N(X)) = D(X)`. With `X = Q + V`, it
allows us to replace the left side `D(N(Q + V))` with `D(Q + V)`:

```text
D(Q + V) = D(Q + S)                                (A6)
```

Lemma 4, equation (A2), states:

```text
D(left + right)
    = canonically order (D(left) + D(right))        (A2)
```

Apply it first with `left = Q` and `right = V`:

```text
D(Q + V) = canonically order (D(Q) + D(V))
```

Apply it again with `left = Q` and `right = S`:

```text
D(Q + S) = canonically order (D(Q) + D(S))
```

Substitute both expansions into equation (A6):

```text
canonically order (D(Q) + D(V))
    = canonically order (D(Q) + D(S))               (A7)
```

Recall the goal of this step: prove `D(V) = D(S)`. Equation (A7) has the same
fixed `D(Q)` on both sides. To conclude that `D(V) = D(S)`, we must show that
canonical ordering doesn't prevent us from cancelling that common
contribution. The next paragraphs show how stable ordering lets us remove the
code-point occurrences contributed by `D(Q)` and recover the complete string
that followed it, even though ordering may mix combining marks where the
strings meet.

Lemma 3 established that canonical ordering is a stable sort within each
maximal stretch of nonstarters. Consequently:

- a starter never moves
- nonstarters finish in nondecreasing `ccc` order between starters
- code points with equal `ccc` values never exchange places

Why can a starter never move? As established in Lemma 3, canonical ordering
works only by swapping adjacent code points when the left code point has a
greater positive `ccc` value than the right one. A starter has `ccc = 0`, so it
can't take part in such a swap. Because code points move only through adjacent
swaps, no code point can cross a starter.

Consequently, only the nonstarters immediately before and after the join can
intermix. To isolate them, split the known string `D(Q)` into two parts:

```text
D(Q) = unchanged prefix + trailing nonstarters
```

The trailing nonstarters are the complete stretch of nonstarters at the end of
`D(Q)`. They're empty if `D(Q)` ends with a starter. Split the string following
`D(Q)` in the opposite way:

```text
following string = leading nonstarters + unchanged rest
```

The first part is the complete stretch of nonstarters at its beginning. The
unchanged rest is empty or begins with its first starter. `D(Q)`, `D(V)`, and
`D(S)` are already ordered internally, so ordering their concatenation needs
to merge only the two nonstarter stretches where the strings meet:

```text
canonically order (D(Q) + following string)
    = unchanged prefix
    + stable merge (trailing nonstarters, leading nonstarters)
    + unchanged rest
```

Here's a schematic example. The numbers are `ccc` values, `q` labels code
points from `D(Q)`, and `r` labels code points from the following string:

```text
before stable merge:   [220:q1 | 230:q2,q3] + [220:r1,r2 | 232:r3]
after stable merge:     220:q1,r1,r2 | 230:q2,q3 | 232:r3
```

Stable ordering keeps a code-point occurrence from `D(Q)` before one from the
following string when their `ccc` values are the same. After the stable merge,
if you remove `q1` from the start of the merged `220` group and `q2,q3` from the
start of the merged `230` group, what remains is exactly
`220:r1,r2 | 232:r3`, the leading nonstarters of the following string. This shows how the
merge may interleave the two nonstarter stretches, but it preserves the order
within each stretch.

This matters because the proof must recover the actual code-point sequence
following `D(Q)` from the ordered result, not merely its `ccc` values. The `q`
and `r` labels are only for the schematic. The actual output doesn't record
origins. Even if some `q` and `r` code points are identical, `D(Q)` tells us how
many `q` occurrences are at the front of each merged `ccc` group. Remove that
many from the front of the group. What remains within the merged stretch is
exactly the occurrences from the following string, so identical code-point
values don't make the recovery ambiguous.

We know that `D(Q)` has the unchanged prefix identified in the split above.
Just as in the example, the code points contributed by `D(Q)` can be identified
and removed: remove the unchanged prefix directly, then use the process above
to remove its trailing nonstarters from the merged stretch. What remains is the
leading nonstarters from the following string, followed by its unchanged rest.
Together, those parts are the complete string that followed `D(Q)`.

Now return to equation (A7):

```text
canonically order (D(Q) + D(V))
    = canonically order (D(Q) + D(S))               (A7)
```

Apply the recovery procedure described above to both sides of equation (A7):

```text
canonically order (D(Q) + D(V))  -- remove D(Q)'s contribution -->  D(V)
canonically order (D(Q) + D(S))  -- remove D(Q)'s contribution -->  D(S)
```

The preceding argument proved that the first recovery produces exactly `D(V)`
and the second produces exactly `D(S)`, rather than merely sequences with the
same `ccc` values. Equation (A7) says that the two ordered input strings are
identical. The recovery also uses the same `D(Q)` on both sides, so it removes
the same contribution and must produce identical remaining sequences.
Therefore:

```text
D(V) = D(S)                                         (A8)
```

This argument doesn't assume that no nonstarter crossed the join during
normalization. It proves only what is needed: identical ordered results made
from the same known left input must have identical right inputs.

#### Proof step 3: Equal decompositions give equal normalized strings

This lemma assumes `N(Q + V) = Q + S`. Thus the input suffix after `Q` is `V`,
while the output suffix after `Q` is `S`:

```text
before normalization:   Q | V
after normalization:    Q | S    because N(Q + V) = Q + S
```

Proof step 2 established that these two suffixes have the same decomposition:

```text
D(V) = D(S)                                         (A8)
```

Equation (A8) says directly that `S` and `V` have the same decomposition `D`.
Lemma 5 states that strings with the same decomposition have the same normalized
form. Therefore:

```text
N(V) = N(S)                                         (A9)
```

At the beginning of this lemma, we assumed that `V` is normalized. Proof step
1 established that `S` is also normalized. Saying that a string is
*normalized* means that
it's already in the normalization form `N`, so applying `N` again returns
exactly the same string. UAX #15 calls this property *idempotence* and states:

> “All of the transformations are idempotent: that is,”
>
> - `toNFC(toNFC(x)) = toNFC(x)`
> - `toNFD(toNFD(x)) = toNFD(x)`
> - `toNFKC(toNFKC(x)) = toNFKC(x)`
> - `toNFKD(toNFKD(x)) = toNFKD(x)`
>
> (from [UAX #15 for Unicode 16.0, Design Goals](https://www.unicode.org/reports/tr15/tr15-56.html#Design_Goals))

The assumption that `V` is normalized therefore means:

```text
N(V) = V
```

Proof step 1 established that `S` is also normalized, so:

```text
N(S) = S
```

Substitute both equalities into equation (A9):

```text
V = N(V) = N(S) = S
```

The first and last terms in this equality chain give `V = S`, and therefore
`S = V`. That's exactly the conclusion required by the lemma:

```text
Q and V are normalized
N(Q + V) = Q + S
therefore S = V
```

In plain language, if normalizing two already-normalized strings leaves the
first string `Q` unchanged as the complete prefix, then the text after `Q`
must be the original second string `V`. Normalization can't leave `Q`
unchanged while silently replacing `V` with some different suffix `S`.

The lemmas used in the proof apply to FormC, FormD, FormKC, and FormKD, so
Lemma 8 holds for all four forms.
