# Mapping Positions Through Unicode Normalization

This doc explains how we map a position in a normalized string back to a position in the caller's original input. Read this if you're working on `NormalizedPositionMap` or want to know why the position translation is shaped the way it is.

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
- `FormKC` and `FormKD` are the *compatibility* forms. They do everything the canonical forms do, then go further. They expand ligatures ("ﬁ" becomes "fi"), unify half-width and full-width Latin, flatten superscripts and other typographic variants, and handle other "looks similar but stored differently" cases.

A *grapheme* is what a user thinks of as one character, even when it's stored as multiple chars. "é" is one grapheme whether stored as 1 char or 2. The parser's segmenter (`GraphemeSegmentation`) processes a string one grapheme at a time.

## The problem

By default, the parser normalizes input before lexing, so the positions the engine works with are offsets into the *normalized* string. The caller wants the corresponding position in the *original* string they started with, so editor highlighting points at the right place. This comes up two ways: a failure position when parsing fails, and a symbol's source range (`Symbol.SourceRange`) on a successful parse. Both go through `NormalizedPositionMap.TranslateToOriginal`.

Two examples show why this isn't trivial.

The easy case: the "ﬁ" ligature (one char) becomes "fi" (two chars) under FormKC. If the lexer fails at position 1 in the normalized string (between the 'f' and 'i'), there's no separate 'i' in the original. Both came from the one ligature char. The right answer is position 0 in the original, so editor highlighting points at the whole ligature.

The hard case: the two characters U+3131 and U+314F (Korean compatibility jamo, displayed as "ㄱㅏ") compose into one syllable character `가` (U+AC00) under FormKC. Two original chars become one normalized char. If the lexer fails at position 1 in the normalized string (end of `가`), the right answer in the original is position 2 (after both original chars). Simple char counting gets this wrong.

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
the runtime uses ICU internally, its edits aren't exposed through
`String.Normalize`. The built-in normalizer could produce its own edit map, but
runtime mode would still need another solution.

ICU therefore demonstrates an alternative approach, but not one this parser
can use in all supported configurations, and it doesn't prove the
comparison-based algorithm used here. The comparison-based algorithm is proved
independently below.

## The mapping algorithm

Let `N` be one Unicode normalization operation: FormC, FormD, FormKC, or
FormKD.
Let `O` be the original string and let `Z = N(O)` be the normalized string.
Assume that normalization succeeds and every call uses this same `N`. Because
.NET stores strings as UTF-16, the algorithm compares UTF-16 code units
ordinally.

The algorithm divides `O` and `Z` into paired spans. Each original span
contains one or more whole graphemes, and its paired span is its normalization:

```text
O: |     C0     |     C1     | ... |     Ck     |
Z: |    N(C0)   |    N(C1)   | ... |    N(Ck)   |
```

The vertical lines are paired boundaries. Positions are measured as UTF-16
indexes because that's the coordinate system used by .NET, but every boundary
accepted by the proof is also between complete Unicode code points. A *span*
is the text between two boundaries.

For FormKC and FormKD, one original grapheme may not be enough to form the next
pair. The algorithm therefore starts with the next unclaimed original
grapheme, normalizes it, and compares the result with the next unclaimed text
in `Z`. If they don't match, the algorithm adds another whole original
grapheme and tries again. If they match, their endpoints are the proposed next
pair of boundaries.

The comparison alone establishes only that `N(C)` appears next in `Z`. The
important question is whether that match could be accidental: could accepting
it leave a suffix of `Z` that isn't the normalization of the remaining suffix
of `O`? The proof below shows that this can't happen.

## Proof of correctness

The theorem to prove is that the algorithm terminates and produces chunks:

```text
O = C0 + C1 + ... + Ck
```

such that:

```text
Z = N(C0) + N(C1) + ... + N(Ck)
```

Furthermore, at every accepted boundary, the complete original prefix must
normalize to the complete prefix of `Z`, and the remaining original suffix must
normalize to the remaining suffix of `Z`. This stronger statement rules out
accidental prefix matches and allows the same operation to be repeated
iteratively on each remaining suffix until the end of the string.

## Results used by the proof

The proof uses these results. Their Unicode derivations and citations are in
[Appendix A](#appendix-a-unicode-details-behind-the-comparison-proof):

1. **Normalizing a concatenation directly equals normalizing its parts, then
   the whole**
   ([proof](#normalizing-a-concatenation-directly-equals-normalizing-its-parts-then-the-whole)).
   For any strings `X` and `Y`:

   ```text
   N(X + Y) = N(N(X) + N(Y))
   ```

   The outer call to `N` is essential. Unicode doesn't guarantee that
   `N(X) + N(Y)` is normalized, as discussed in the linked proof.

2. **A code-point-aligned substring of normalized text is normalized**
   ([Fact 6](#fact-6-a-substring-of-normalized-text-is-normalized)).
   Every prefix used below is code-point-aligned
   ([Fact 1](#fact-1-every-cut-is-at-a-code-point-boundary))
   and, because it's a substring of normalized text, is therefore normalized
   by this result.

3. **The unchanged-prefix lemma**
   ([proof](#the-unchanged-prefix-lemma)).
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

## The invariant

Throughout the proof, `+` means string concatenation. A vertical bar `|` marks
the boundary between the same concatenated strings. It isn't part of either
string. Thus, `O = P + U` and `O = P | U` state the same equality: `O` consists
of `P` immediately followed by `U`. The proof uses the two notations
interchangeably, using `|` when the boundary itself is important.

Suppose the algorithm has already accepted an original prefix `P`. Let `U` be
all the original text that remains. The accepted pair of boundaries must
satisfy:

```text
O = P    | U
Z = N(P) | N(U)  (1)
```

This is the algorithm's invariant. It says that the two accepted prefixes
really correspond and that the complete unclaimed suffix of `Z` is exactly the
normalization of the unclaimed suffix `U`. This is what allows the algorithm
to repeat the same process on `U` to find the remaining pairs of boundaries.

The invariant is true before the first iteration. At that point `P` is empty,
`U = O`, `N(P)` is empty, and `Z = N(O)`.

## A successful comparison preserves the invariant

The algorithm chooses a candidate `C` from the beginning of `U`, initially one
whole grapheme, and lets `R` be everything after it:

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

A successful comparison finds `N(C)` at the beginning of the unclaimed suffix
`N(C + R)` in `Z`. Let `S` be everything after that exact match:

```text
O: |       P       |    C    |    R    |
Z: |     N(P)      |  N(C)   |    S    |
```

If that comparison succeeds, then this must be true:

```text
N(C + R) = N(C) + S  (2)
```

Rule 1,
“[Normalizing a concatenation directly equals normalizing its parts, then the whole](#normalizing-a-concatenation-directly-equals-normalizing-its-parts-then-the-whole),”
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

“[The unchanged-prefix lemma](#the-unchanged-prefix-lemma)”
states:

```text
If Q and V are normalized and N(Q + V) = Q + S, then S = V
```

Apply the lemma with `Q = N(C)` and `V = N(R)`. The lemma becomes:

```text
N(N(C) + N(R)) = N(C) + S,
```

which is exactly equation (4). It therefore gives:

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

To accept `C` and move the invariant to the next position, we must convert this
to the invariant form:

```text
O = P + C    | R
Z = N(P + C) | N(R)  (7)
```

Equation (6) says that the corresponding prefix of `Z` is `N(P) + N(C)`.
Equation (7) requires that prefix to be `N(P + C)`. UAX #15 explains why this
requires proof:

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

The string `N(P) + N(C)` is therefore a prefix of the normalized string `Z`.
Its `N(C)` part begins at the previously established code-point boundary and
is a complete normalization output, so it contains only complete code points.
Its end (and therefore the end of the prefix) is also a code-point boundary
([Fact 1](#fact-1-every-cut-is-at-a-code-point-boundary)).
By “[Fact 6: A substring of normalized text is normalized](#fact-6-a-substring-of-normalized-text-is-normalized),”
the concatenation `N(P) + N(C)` is therefore also normalized. In other words:

```text
N(N(P) + N(C)) = N(P) + N(C)  (8)
```

Recall that we are trying to prove `N(P + C) = N(P) + N(C)`.
To finish the proof, we must connect `N(P + C)` to `N(N(P) + N(C))`.

Apply Rule 1,
“[Normalizing a concatenation directly equals normalizing its parts, then the whole](#normalizing-a-concatenation-directly-equals-normalizing-its-parts-then-the-whole),”
with `X = P` and `Y = C`:

```text
N(P + C) = N(N(P) + N(C))
```

Equation (8) replaces the right side with `N(P) + N(C)`, giving:

```text
N(P + C) = N(P) + N(C)  (9)
```

Equation (9) is the proof we were looking for. It holds here because its
right-hand side is a code-point-aligned substring of the normalized string
`Z`. It isn't a general rule that `N(P + C) = N(P) + N(C)`.

Now, recall that equation (6) gives the current known state:

```text
O = P    | C + R
Z = N(P) | N(C) + N(R)
```

By equation (9), we can replace `N(P) + N(C)` with `N(P + C)`. Moving `C` into
the accepted prefix then gives:

```text
O = P + C    | R
Z = N(P + C) | N(R)  (10)
             ^ newly accepted pair of boundaries
```

Equation (10) is the original invariant, now at the newly accepted pair of
boundaries. The algorithm can therefore accept `C` and repeat the same
operation with `P + C` as the accepted prefix and `R` as the unclaimed suffix.

Notice what this proof does and doesn't establish. It proves that the accepted
prefix of `Z` is exactly `N(P + C)` and that the text remaining in `Z` is
exactly `N(R)`. It doesn't claim that no combining mark moved across the join
during an intermediate normalization step. Such movement can occur and later
become invisible, as the
[example in the lemma proof](#example-movement-across-the-join-can-be-invisible)
shows. The unchanged-prefix lemma is precisely what makes the conclusion valid
without tracking where each code point came from.

## The search can't get stuck

After a failed comparison, the algorithm adds one nonempty original grapheme
to `C` and tries again. If every shorter candidate fails, `C` eventually
becomes the entire unclaimed suffix `U`. Then `R` is empty, and the invariant
gives:

```text
remaining Z = N(U) = N(C)
```

That final candidate must match. The original string contains finitely many
graphemes, so the algorithm can't keep growing `C` forever and can't run out
of text without finding a successful candidate.

Because candidates are tried in increasing order of their original grapheme
count, the first successful `C` is the shortest successful span ending at an
original grapheme boundary. Boundaries inside an original grapheme are
deliberately not candidates because we want the mapper to return user-facing
grapheme boundaries.

## Proof summary

Initially the invariant is true. Every successful comparison preserves it at
a later pair of boundaries, and a successful candidate is always eventually
found. Every accepted candidate consumes at least one original grapheme, so
only finitely many iterations are possible. By induction, the algorithm
reaches the ends of both strings and produces:

```text
O = C0 + C1 + ... + Ck
Z = N(C0) + N(C1) + ... + N(Ck)
```

At every intermediate cut, not merely at the end, the original prefix and
suffix normalize to the corresponding prefix and suffix of `Z`. Thus every
accepted pair of boundaries is valid, every accepted original span is locally
shortest among the whole-grapheme candidates, and the algorithm consumes all of
`O` and `Z`.

## Turning the paired boundaries into a position

If the requested position in `Z` is exactly an accepted boundary, the mapper
returns its paired boundary in `O`. If it lies strictly inside an accepted
normalized span `N(Ci)`, normalization may provide no exact original boundary
for it, so the mapper returns the beginning of the paired original span `Ci`.
This approach is the project's position convention, not a further Unicode
theorem.

## Optimization for FormC/D

The general comparison algorithm proved above works for FormC and FormD.
However, a simpler algorithm is possible for FormC and FormD due to the
properties of Unicode: it takes one extended grapheme cluster from `O`, takes
one extended grapheme cluster from `Z`, and pairs them without performing the
comparison. It can repeat that step until both strings end.

This proof of the simpler approach assumes that both strings are segmented with
[UAX #29's default extended-grapheme-cluster rules](https://www.unicode.org/reports/tr29/tr29-45.html#Default_Grapheme_Cluster_Table)
using Unicode data consistent with the normalizer.

For this optimization to be valid, we must establish both of the following
statements from the Unicode Standard:

1. `O` and `Z` can each be segmented directly, as they are at the start of the
   algorithm.
2. `O` and `Z` have the same extended grapheme clusters in the same order.

### Requirement 1: `O` and `Z` can each be segmented directly

UAX #29 identifies the relevant specification explicitly:

> “The following is a general specification for grapheme cluster
> boundaries—language-specific rules in [CLDR] should be used where <!-- style-lint-ok: verbatim Unicode quote -->
> available.”
>
> (from [UAX #29 for Unicode 16.0, Default Grapheme Cluster Boundary Specification](https://www.unicode.org/reports/tr29/tr29-45.html#Default_Grapheme_Cluster_Table))

For that grapheme-boundary specification, Section 2 explains how the rules can
be applied directly:

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

In plain English, the specification uses FormD to define where the grapheme
boundaries must be. However, an implementation can apply the grapheme rules
directly to a string without first converting it to FormD and obtain the
equivalent grapheme segmentation.

For `O`, this matters because `O` might not already be in any normalization
form. The quoted rule permits segmenting it directly anyway. For `Z`, there
are two cases:

1. If `N` is FormD, `Z` is already in the form on which Unicode bases the
   boundary specification.
2. If `N` is FormC, `Z` isn't necessarily in FormD, and the quotation above
   explicitly says that the default rules can be applied directly to it.

Thus `Z` can be segmented directly in either case.

This proves only that both segmentation operations are permitted. It doesn't,
by itself, prove that the first grapheme of `O` pairs with the first grapheme
of `Z`, the second pairs with the second, and so on. Requirement 2 proves that.

### Requirement 2: `O` and `Z` have the same extended grapheme clusters in the same order

UAX #29 gives the guarantee needed for this requirement:

> “A key feature of Unicode grapheme clusters (both legacy and
> extended) is that they remain unchanged across all *canonically equivalent*
> [italics added] forms of the underlying text. Thus the boundaries remain
> unchanged whether the text is in NFC or NFD. Using a grapheme cluster as the
> fundamental unit of matching thus provides a very clear and easily explained
> basis for canonically equivalent matching. This is important for applications
> from searching to regular expressions.”
>
> (from [UAX #29 for Unicode 16.0, Grapheme Cluster Boundaries](https://www.unicode.org/reports/tr29/tr29-45.html#Grapheme_Cluster_Boundaries))

An extended grapheme cluster is the span between two consecutive grapheme
boundaries. Therefore, if `O` and `Z` are canonically equivalent, the quoted
guarantee gives them corresponding grapheme boundaries in the same order. The
spans between those boundaries (the extended grapheme clusters) also correspond
in the same order, although the boundaries may have different UTF-16 offsets.

We must therefore prove that `O` and `Z` are canonically equivalent. The
Unicode Standard defines canonical equivalence precisely:

> “Two character sequences are said to be canonical equivalents if their full
> canonical decompositions are identical.”
>
> (from [The Unicode Standard 16.0, Section 3.7, definition D70](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G743))

UAX #15 identifies FormD as canonical decomposition:

> “Normalization Form D (NFD) — Canonical Decomposition” <!-- style-lint-ok: verbatim Unicode quote -->
>
> (from [UAX #15 for Unicode 16.0, Normalization Forms](https://www.unicode.org/reports/tr15/tr15-56.html#Norm_Forms))

Therefore, to prove that `O` and `Z` have identical full canonical
decompositions, we must prove:

```text
FormD(O) = FormD(Z)
```

UAX #15 gives the two identities needed to prove that equality:

> `toNFD(toNFD(x)) = toNFD(x)`
>
> `toNFD(toNFC(x)) = toNFD(x)`
>
> (from [UAX #15 for Unicode 16.0, Design Goals](https://www.unicode.org/reports/tr15/tr15-56.html#Design_Goals))

In the notation used here, `toNFD` is `FormD` and `toNFC` is `FormC`.
Recall that `Z = N(O)`. There are two cases:

- If `N` is FormD, the first identity gives
  `FormD(Z) = FormD(FormD(O)) = FormD(O)`.
- If `N` is FormC, the second identity gives
  `FormD(Z) = FormD(FormC(O)) = FormD(O)`.

Thus, in either case:

```text
FormD(Z) = FormD(N(O)) = FormD(O)
```

That equality is exactly the condition in definition D70, so `O` and `Z` are
canonically equivalent. The UAX #29 guarantee now applies: segmenting each
string directly produces the same number of extended grapheme clusters in the
same order.

Write those clusters as:

```text
O = G0 + G1 + ... + Gm
Z = H0 + H1 + ... + Hm
```

There's one `Hi` for every `Gi`. This UAX #29 guarantee (not merely the
direct-segmentation rule in Requirement 1) is what allows taking one grapheme
from each string on every iteration and pairing them as canonically equivalent
spellings of the same grapheme. Each side advances by the UTF-16 length of its
own cluster, so the two offsets don't have to be equal.

It remains to connect that correspondence to the exact comparison skipped by
the optimization. Each `Hi` is a substring of `Z`, and `Z = N(O)` is
normalized. UAX #15 states:

> “all of the Normalization Forms are closed under substringing.”
>
> (from [UAX #15 for Unicode 16.0, Concatenation of Normalized Strings](https://www.unicode.org/reports/tr15/tr15-56.html#Concatenation))

Therefore `Hi` is normalized and `N(Hi) = Hi`.

The UAX #29 guarantee above says that each paired `Gi` and `Hi` is canonically
equivalent. UAX #15 states:

> “If two strings x and y are canonical equivalents, then”
>
> - `toNFC(x) = toNFC(y)`
> - `toNFD(x) = toNFD(y)`
>
> (from [UAX #15 for Unicode 16.0, Design Goals](https://www.unicode.org/reports/tr15/tr15-56.html#Design_Goals))

Therefore `Gi` and `Hi` have the same FormC or FormD result:

```text
N(Gi) = N(Hi) = Hi
```

This is the exact comparison that the general algorithm would perform for its
first, one-grapheme candidate. It always succeeds for FormC and FormD, so the
implementation may skip the normalization and comparison and pair `Gi` with
`Hi` directly.

### Conclusion: why the FormC/FormD algorithm is valid

Requirement 1 establishes that the algorithm may find the grapheme boundaries
directly in `O` and `Z`. Neither string has to be converted to FormD first.
Requirement 2 establishes that canonical normalization preserves those
graphemes in the same order, so there's exactly one `Hi` for every `Gi`.

The algorithm may therefore take one grapheme from each string and pair them
on every iteration. The two graphemes may occupy different numbers of UTF-16
code units, so their numeric offsets may differ, but each side advances by its
own grapheme's length. Because the two strings have the same number of
graphemes, both sides reach the end together. The boundary after each `Hi`
therefore maps to the boundary after its paired `Gi`. A position inside `Hi`
maps to the start of `Gi`, as required by the position-mapping rule.

### Why this simple algorithm doesn't work for FormKC and FormKD

UAX #29's guarantee applies to canonical equivalence. FormKC and FormKD also
apply compatibility mappings, so their results don't have to be canonically
equivalent to the original. Compatibility normalization can turn one original
grapheme into several normalized graphemes or several original graphemes into
one normalized grapheme. A one-for-one lockstep algorithm therefore can't be
used for these forms. They use the comparison algorithm proved above.

## Appendix A: Unicode details behind the comparison proof

This appendix proves the rule for normalizing a concatenation and the
unchanged-prefix lemma used above.

For the selected normalization operation `N`, let `D` mean its corresponding
Unicode-defined fully decomposed form:

| Selected `N` | `D` |
| --- | --- |
| FormC or FormD | FormD |
| FormKC or FormKD | FormKD |

The two proofs below use these six facts, the validity of which is shown after:

1. Every cut used by the mapper is at a code-point boundary.
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

The rule for normalizing a concatenation uses Facts 2, 4, and 5. The
unchanged-prefix lemma uses all six. Each fact is established below before
either proof uses it.

### Fact 1: Every cut is at a code-point boundary

On the original side, each candidate `C` contains whole extended grapheme
clusters. Unicode doesn't state in one rule that an extended grapheme cluster
begins and ends at code-point boundaries. That fact must be derived from the
definition of a cluster and the formal syntax of the boundary rules. Here's
the complete derivation.

1. **An extended grapheme cluster is the text between two extended
   grapheme-cluster boundaries.** Definition D61 states:

> “Extended grapheme cluster: The text between extended grapheme cluster
> boundaries as specified by Unicode Standard Annex #29, ‘Unicode Text
> Segmentation.’”
>
> (from [The Unicode Standard 16.0, Section 3.6.2, definition D61](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G41732))

2. **Every UAX #29 boundary rule has one boundary position between its left
   and right sides.** UAX #29 defines the rule syntax as follows:

> “Each rule consists of a left side, a boundary symbol (see Table 1), and a
> right side.”
>
> (from [UAX #29 for Unicode 16.0, Notation](https://www.unicode.org/reports/tr29/tr29-45.html#Notation))

It also places this constraint on the rules:

> “Single boundaries. Each rule has exactly one boundary position.”
>
> (from [UAX #29 for Unicode 16.0, Rule Constraints](https://www.unicode.org/reports/tr29/tr29-45.html#Rule_Constraints))

3. **The operand `Any` represents one arbitrary code point, and `÷` represents
   a boundary.** The grapheme-boundary property table defines `Any` as:

> `Any`: “This is not a property value; it is used in the rules to represent <!-- style-lint-ok: verbatim Unicode quote -->
> any code point.”
>
> (from [UAX #29 for Unicode 16.0, Grapheme_Cluster_Break Property Values](https://www.unicode.org/reports/tr29/tr29-45.html#Grapheme_Cluster_Break_Property_Values))

The boundary symbol `÷` is defined as:

> `÷`: “Boundary (allow break here)”
>
> (from [UAX #29 for Unicode 16.0, Notation](https://www.unicode.org/reports/tr29/tr29-45.html#Notation))

4. **The catch-all rule places a boundary between any two adjacent code
   points unless an earlier rule applies.** The final grapheme-boundary rule
   is:

> “Otherwise, break everywhere.”
>
> `GB999    Any    ÷    Any`
>
> (from [UAX #29 for Unicode 16.0, Grapheme Cluster Boundary Rules](https://www.unicode.org/reports/tr29/tr29-45.html#Grapheme_Cluster_Boundary_Rules))

Each `Any` matches one code point. Because the two operands are adjacent,
`Any ÷ Any` places `÷` in the gap between two adjacent code points. UAX #29
explains how earlier rules relate to this catch-all rule:

> “The rules are numbered for reference and are applied in sequence to
> determine whether there is a boundary at any given offset.”
>
> (from [UAX #29 for Unicode 16.0, Notation](https://www.unicode.org/reports/tr29/tr29-45.html#Notation))

Every earlier rule has the same single-boundary syntax established in step 2.
At a gap between code points, an earlier rule may report either a boundary
(`÷`) or no boundary (`×`). If none applies, `GB999` reports a boundary there.
The rules don't supply any position inside a code point at which a boundary
could be reported.

5. **The start and end of the text are also boundaries.** Rules `GB1` and
   `GB2` state:

> “Break at the start and end of text, unless the text is empty.”
>
> `GB1    sot    ÷    Any`
>
> `GB2    Any    ÷    eot`
>
> (from [UAX #29 for Unicode 16.0, Grapheme Cluster Boundary Rules](https://www.unicode.org/reports/tr29/tr29-45.html#Grapheme_Cluster_Boundary_Rules))

Therefore every extended grapheme-cluster boundary is at the start of the
text, at its end, or between two code points. It's never inside a code point.

On the normalized side, the Unicode Standard defines the unit on which the
normalization forms operate:

> “D12 Coded character sequence: An ordered sequence of one or more code
> points.”
>
> (from [The Unicode Standard 16.0, Section 3.4, definition D12](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G45138))

This is the connection: whenever definitions D118-D121 refer to a “coded
character sequence,” definition D12 says that sequence is made of code points.
Those definitions therefore define every normalization form as an operation on
a sequence of code points:

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
> (from [The Unicode Standard 16.0, Section 3.11, D118-D121](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49623))

Consequently, a complete normalization result `N(C)` contains whole code
points, so its endpoint can't split a code point. Each comparison begins at
an already established code-point boundary, and both possible new endpoints
(the end of `C` and the end of `N(C)`) are also code-point boundaries.

### Fact 2: Normalization preserves the decomposition

For every string `X`:

```text
D(N(X)) = D(X)                                      (A1)
```

UAX #15 gives the four cases explicitly:

> `toNFD(toNFD(x)) = toNFD(x)`
>
> `toNFD(toNFC(x)) = toNFD(x)`
>
> `toNFKD(toNFKD(x)) = toNFKD(x)`
>
> `toNFKD(toNFKC(x)) = toNFKD(x)`
>
> (from [UAX #15 for Unicode 16.0, Design Goals](https://www.unicode.org/reports/tr15/tr15-56.html#Design_Goals))

The first two equations apply when `D` is FormD. The last two apply when `D`
is FormKD. Together they prove equation (A1) for every possible `N`.

### Fact 3: Canonical ordering is stable

Each code point has a canonical combining class, abbreviated `ccc`. Definitions
D108 and D109 state both the condition for reordering and the algorithm that
uses it:

> “D108 Reorderable pair: Two adjacent characters A and B in a coded character
> sequence `<A, B>` are a Reorderable Pair if and only if
> `ccc(A) > ccc(B) > 0`.”
>
> “D109 Canonical Ordering Algorithm: In a decomposed character sequence D,
> exchange the positions of the characters in each Reorderable Pair until the
> sequence contains no more Reorderable Pairs.”
>
> (from [The Unicode Standard 16.0, Canonical Ordering Algorithm, D108-D109](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G49592))

A code point with `ccc = 0` is called a *starter*. Because equal `ccc` values
don't satisfy the D108 inequality, code points with equal classes never
exchange places. Canonical ordering is therefore a stable sort within each
stretch of nonstarters between starters.

### Fact 4: Decomposing a concatenation orders the two decompositions together

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

Therefore decomposing `X + Y` first produces the decompositions of `X` and `Y`
in that order. `D(X)` and `D(Y)` are already ordered internally. Ordering their
concatenation performs any additional ordering required where they meet. By
Fact 3, that ordering is stable. Stably ordering the pieces and then their
concatenation produces the same result as stably ordering the complete
decomposed sequence once. This proves equation (A2).

### Fact 5: Equal decompositions give equal normalized forms

The fact to be proved is:

```text
D(X) = D(Y)  implies  N(X) = N(Y)
```

UAX #15 states the result this fact needs directly:

> “If two strings x and y are canonical equivalents, then”
>
> - `toNFC(x) = toNFC(y)`
> - `toNFD(x) = toNFD(y)`
>
> “If two strings are compatibility equivalents, then”
>
> - `toNFKC(x) = toNFKC(y)`
> - `toNFKD(x) = toNFKD(y)`
>
> (from [UAX #15 for Unicode 16.0, Design Goals](https://www.unicode.org/reports/tr15/tr15-56.html#Design_Goals))

The Unicode Standard defines the two kinds of equivalence used in that
guarantee:

> “D70 Canonical equivalent: Two character sequences are said to be canonical
> equivalents if their full canonical decompositions are identical.”
>
> (from [The Unicode Standard 16.0, Section 3.7, definition D70](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G743))

> “D67 Compatibility equivalent: Two character sequences are said to be
> compatibility equivalents if their full compatibility decompositions are
> identical.”
>
> (from [The Unicode Standard 16.0, Section 3.7, definition D67](https://www.unicode.org/versions/Unicode16.0.0/core-spec/chapter-3/#G753))

For FormC and FormD, `D` is the full canonical decomposition. Therefore:

```text
D(X) = D(Y)
```

means that `X` and `Y` are canonical equivalents under definition D70, and the
first UAX #15 guarantee gives `N(X) = N(Y)`.

For FormKC and FormKD, `D` is the full compatibility decomposition. The same
equality means that `X` and `Y` are compatibility equivalents under definition
D67, and the second UAX #15 guarantee again gives `N(X) = N(Y)`.

### Fact 6: A substring of normalized text is normalized

UAX #15 states:

> "all of the Normalization Forms are closed under substringing."
>
> (from [UAX #15 for Unicode 16.0, Concatenation of Normalized Strings](https://www.unicode.org/reports/tr15/tr15-56.html#Concatenation))

Therefore any substring cut from normalized text at code-point boundaries is
normalized on its own. Fact 1 establishes that the cuts used here satisfy that
condition.

### Normalizing a concatenation directly equals normalizing its parts, then the whole

The rule to be proved is that, for any strings `X` and `Y`:

```text
N(X + Y) = N(N(X) + N(Y))                          (A5)
```

In words, normalizing two concatenated strings produces the same result as
normalizing each string first, concatenating those results, and then
normalizing the complete concatenation.

Fact 4, equation (A2), is the following general rule:

```text
D(left + right)
    = canonically order (D(left) + D(right))        (A2)
```

Substitute `N(X)` for `left` and `N(Y)` for `right`:

```text
D(N(X) + N(Y))
    = canonically order (D(N(X)) + D(N(Y)))         (A3)
```

Fact 2, equation (A1), says that `D(N(X)) = D(X)` and
`D(N(Y)) = D(Y)`. Substitute those equal strings into equation (A3):

```text
D(N(X) + N(Y))
    = canonically order (D(X) + D(Y))
    = D(X + Y)                                      (A4)
```

Equation (A4) states that the two strings `N(X) + N(Y)` and `X + Y` have the
same decomposition. Fact 5 says that two strings with the same decomposition
have the same normalized form. Applying that fact directly to these
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

### The unchanged-prefix lemma

We must prove:

> If `Q` and `V` are normalized and
>
> ```text
> N(Q + V) = Q + S,
> ```
>
> then `S = V`.

Here, saying that a string is *normalized* means that it's already in the
normalization form `N`, so applying `N` again returns exactly the same string.
This is stronger than merely saying that the second result is normalized.
UAX #15 calls this property *idempotence* and states:

> “All of the transformations are idempotent: that is,”
>
> - `toNFC(toNFC(x)) = toNFC(x)`
> - `toNFD(toNFD(x)) = toNFD(x)`
> - `toNFKC(toNFKC(x)) = toNFKC(x)`
> - `toNFKD(toNFKD(x)) = toNFKD(x)`
>
> (from [UAX #15 for Unicode 16.0, Design Goals](https://www.unicode.org/reports/tr15/tr15-56.html#Design_Goals))

#### Example: movement across the join can be invisible

It would be tempting to conclude that, if normalization leaves the prefix `Q`
unchanged, no code point from `V` crossed the `Q | V` join. That conclusion is
false.

Let `N` be FormC, let `Q` be `Ậ` (U+1EAC, LATIN CAPITAL LETTER A WITH
CIRCUMFLEX AND DOT BELOW), and let `V` be U+0323 COMBINING DOT BELOW. Both `Q`
and `V` are already normalized. Before normalization, their join is:

```text
U+1EAC[Q] | U+0323[V]
```

The bracketed labels record where each code-point occurrence came from. They
aren't part of the string. Decomposing U+1EAC produces `A`, a dot below, and a
circumflex:

```text
U+0041[Q]  U+0323[Q](ccc 220)  U+0302[Q](ccc 230)  U+0323[V](ccc 220)
```

Canonical ordering places class 220 before class 230, so the dot from `V`
moves left across the circumflex from `Q`:

```text
U+0041[Q]  U+0323[Q](ccc 220)  U+0323[V](ccc 220)  U+0302[Q](ccc 230)
```

Canonical composition then rebuilds `Ậ` (U+1EAC) from the `A`, the first dot
below, and the circumflex. The second dot remains, producing:

```text
U+1EAC U+0323
```

That's exactly `Q + V`. The final prefix is still the exact `Q`, even though
the dot originating in `V` crossed a decomposed code point originating in `Q`.
The decomposition and combining classes are recorded in the
[Unicode 16.0 Character Database](https://www.unicode.org/Public/16.0.0/ucd/UnicodeData.txt).

The proof has three steps.

#### Proof step 1: `S` is normalized

`N(Q + V)` is normalized. The premise says that it's exactly `Q + S`, so `S`
is a suffix (and therefore a substring) of that normalized string. Fact 6 states
that a substring of normalized text is normalized. Therefore `S` is normalized
too.

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

Fact 2, equation (A1), states that `D(N(X)) = D(X)`. Apply it with
`X = Q + V`:

```text
D(N(Q + V)) = D(Q + V)
```

Substitute that equal expression into the preceding equation:

```text
D(Q + V) = D(Q + S)                                (A6)
```

Fact 4, equation (A2), states:

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

Recall the goal of this step: prove `D(V) = D(S)`. Equation (A7) already says
that joining each of those strings to the same known `D(Q)` and canonically
ordering the result produces identical strings. To reach the goal, we must show
that a known `D(Q)` and the ordered result uniquely determine the string that
followed `D(Q)`. The next paragraphs show how to recover that following string,
even though ordering may mix combining marks where the strings meet.

Fact 3 established that canonical ordering is a stable sort within each
stretch of nonstarters between starters. Consequently:

- a starter never moves
- nonstarters finish in nondecreasing `ccc` order between starters
- code points with equal `ccc` values never exchange places

`D(Q)`, `D(V)`, and `D(S)` are already ordered internally. Split the known
string `D(Q)` into two parts:

```text
D(Q) = unchanged prefix + trailing nonstarters
```

The second part is the complete stretch of nonstarters at the end of `D(Q)`.
It's empty if `D(Q)` ends with a starter. Split the string following `D(Q)` in
the opposite way:

```text
following string = leading nonstarters + unchanged rest
```

The first part is the complete stretch of nonstarters at its beginning. The
unchanged rest is empty or begins with its first starter.

Fact 3 quoted D108: adjacent code points `A` and `B` can be exchanged only when
`ccc(A) > ccc(B) > 0`. If `B` is a starter, then `ccc(B) = 0`, so the final
inequality fails. If `A` is a starter, then `ccc(A) = 0`, so it can't be
greater than the positive `ccc(B)`. A starter therefore can't participate in
any exchange. D109 performs canonical ordering only through these adjacent
exchanges, so another code point can't cross a starter: doing so would require
an exchange with that starter. Canonical ordering can therefore change only the
two stretches of nonstarters where the strings meet:

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
stable merged run:      220:q1,r1,r2 | 230:q2,q3 | 232:r3
```

For each `ccc` value, stable ordering puts the known entries from `D(Q)` before
the entries from the following string. After the stable merge, remove `q1`
from the start of the merged `220` group and remove `q2,q3` from the start of
the merged `230` group. What remains is exactly
`220:r1,r2 | 232:r3`, the leading nonstarters of the following string. If some
`q` and `r` code points are identical, remove the known number of `q` entries
from the front of that merged group. The remainder is still determined
exactly.

The unchanged prefix is known from `D(Q)`, so remove it directly. The process
above recovers the leading nonstarters, and the unchanged rest already appears
unchanged after the merged stretch. Together, those two recovered parts are the
complete string that followed `D(Q)`.

Now return to equation (A7):

```text
canonically order (D(Q) + D(V))
    = canonically order (D(Q) + D(S))               (A7)
```

Its two ordered results are identical, and both were produced using the same
`D(Q)`. The recovery just described must therefore produce the same following
string on each side. It produces `D(V)` on the left and `D(S)` on the right.
Hence:

```text
D(V) = D(S)                                         (A8)
```

This argument doesn't assume that no nonstarter crossed the join during
normalization. It proves only what is needed: identical ordered results made
from the same known left input must have identical right inputs.

#### Proof step 3: Equal decompositions give equal normalized strings

Recall the unchanged-prefix lemma's setup. `V` is the string following `Q`
before the concatenation is normalized. The normalized result begins with the
same unchanged `Q`, and `S` is everything after it:

```text
before normalization:   Q | V
after normalization:    Q | S    because N(Q + V) = Q + S
```

Proof step 2 established that these two suffixes have the same decomposition:

```text
D(V) = D(S)                                         (A8)
```

Equation (A8) says directly that `S` and `V` have the same decomposition `D`.
Fact 5 states that strings with the same decomposition have the same normalized
form. Therefore:

```text
N(V) = N(S)                                         (A9)
```

The unchanged-prefix lemma begins by assuming that `Q` and `V` are normalized.
By the meaning of *normalized* stated above, the assumption about `V` means:

```text
N(V) = V
```

Proof step 1 established that `S` is also normalized, which means:

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

The facts used in the proof apply to FormC, FormD, FormKC, and FormKD, so the
unchanged-prefix lemma holds for all four forms.
