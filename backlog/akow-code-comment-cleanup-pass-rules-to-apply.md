# Code comment cleanup pass: rules to apply

A scrub of all code comments (// prose, /// XML doc, and test comments) to bring them in line with the comment rules Eric has flagged
repeatedly. Not a rewrite pass for the prose itself, just a rules-driven sweep.
The rules below come from corrections across the worktrees' memory files and
commit history. Global writing-style rules in ~/.claude/CLAUDE.md apply too,
the ones below are specific to comments.

Before doing anything: Make sure the text is factually correct! Actually compare with the code to ensure it. Then:

The universal rule for every comment and doc

No jargon. Keep it simple. Keep it short. Focus on the points that matter.
A comment a reader has to decode is worse than no comment. If the same idea
fits in fewer words or plainer words, say it that way. If the comment isn't
earning its space, delete it. This applies to // comments, /// doc comments,
and test comments equally.

Rules to apply

1. No bug-fix history. Comments describe what the code does now, not how it
   got there. Drop "used to X", "previously", "before the fix", "would
   silently break", "now does Y", and any framing that references a bug, a
   commit, or a prior implementation. Counterfactuals like "would otherwise
   lose the name" are in the same category. If the only reason for the
   comment is to document the bug's history, delete the comment. The
   narrative belongs in the PR description and the backlog item, not in the
   source tree. This is the most-flagged correction (three separate memories
   across worktrees).

2. No doc comment on a delegating method. If the body is `return Other(...)`
   or `if (x == null) throw ...; return Other(...)`, skip the comment. The
   delegate is the definitive source. Restating the same description on the
   calling method duplicates information that will drift. Exception: if the
   calling method is public, add a one-line `<see cref="..." />` pointing
   at the definitive source so IntelliSense lands the reader there.

3. No banner-section headers in test files. Drop `// === Group N ===` and
   `// ---- section ----` dividers. Order related tests near each other and
   let each test's own comment carry the context. (Some older files use
   `// ==== Group N ====` headers. Treat those as the old style and remove
   them when editing nearby code.)

4. No benchmark mentions in comments, especially /// XML doc. No benchmark
   suite names (rebar, the Sherlock haystack), no measured numbers ("2x
   speedup", "23x slower"), no paths to results files. Qualitative,
   mechanism-based statements are fine ("opens one transaction instead of
   N"). Just drop the benchmark citation and the number.

5. No state-machine mentions in production code under src/InductorParser/.
   When a comment must explain code that exists for the experimental
   evaluator (EnterRuleAtDepth, TickPeriodicBudget, the Lowering*
   accessors, ResetForReuse pooling), describe it generically: "an
   alternative evaluator", "a pooled Lexer". Never name "the state machine",
   "SM", "the lowering pass", or SM-internal concepts (Machine.CallTop,
   Stepper, Step_Call, opcodes). Genuine false positives stay: C# async/await
   state machines, the Unicode "Sm" general category.

6. Stale facts. If code changes invalidate a claim in a comment (e.g.
   "derives from InvalidOperationException" after the exception was changed
   to derive from Exception), correct it. Same for comments that reference
   renamed API members (Symbol.Name → DisplayName, ParseResult.RawSourceTextOf
   → Symbol.SourceText). Two commits already cleaned up specific instances
   (27d4a11, 1a899af). The scrub should catch the rest.

7. Vocabulary fixes that hit comments specifically (the global CLAUDE.md
   vocabulary list applies everywhere, but these came from comment-review
   sessions and are the highest-density offenders in src/ and tests/):
   - "wrapper" / "wrapper Symbol" → "Symbol" or "the rule's Symbol". <!-- style-lint-ok -->
     14 occurrences across 5 files at last count.
   - "by factory" → "by default" / "defaults to". 12 occurrences across <!-- style-lint-ok -->
     4 files in TomlParser / TomlGrammar.
   - "PEG" / "PEG rules" / "natural PEG translation" → "Inductor parser"
     or describe the behavior directly. Keep "PEG" only when the topic
     genuinely is the algorithm class (comparing to LR/LL, citing the
     PEG paper).
   - "rune-pin" / "rune-pinning" → "use the rune value as the leaf id" <!-- style-lint-ok -->
     in OneOfRule, AnyTokenRule, NoneOfRule, WithinTokenRule comments and
     in test names. The existing GraphemeRule "pinned" usage at <!-- style-lint-ok -->
     construction predates this and doesn't need to change unless the
     surrounding code is being edited.
   - "note" / "record" for a recorded parse failure → "failure". Keep the
     verb "records" and the C# `record` keyword as-is.
   - "honest" / "honestly" / "honest comparison" as a framing word → drop <!-- style-lint-ok -->
     it or use a direct word ("directly comparable", "measured").

8. Public-member comments: convert // to /// when developer-facing. If a `//`
   comment on a public type or member describes behavior, usage, or anything
   a caller needs to know to use the API, convert it to `///` so it surfaces
   in IntelliSense. Keep `//` for code-shape comments: design-rationale
   paragraphs (the Grapheme design block, the Symbol GC-rooting note, the
   transaction-semantics block in Lexer), implementation-detail notes inside
   method bodies, and similar discursive material aimed at contributors.
   Doc-comment style:
   - `<summary>` stays tight. One or two sentences describing what the
     member does. If the summary is growing past that, the rest belongs in
     `<remarks>`, not in the summary.
   - `<remarks>` is where the longer explanation goes. Still subject to the
     universal rule: short, plain, focused.
   - `<param>` / `<returns>` / `<exception>` for the mechanical pieces.

9. Every public member has a meaningful doc comment. Walk the public surface
   of src/InductorParser/ and confirm every public type, method, property,
   and field has a `///` with at least a `<summary>`. "Meaningful" is the
   word that matters: the summary has to tell the caller something they
   couldn't have inferred from the identifier name. A restatement of the
   signature ("Gets the count" on a `Count` property, "Parses the input" on
   `Parse`) is worse than nothing. Either say what's worth knowing
   (preconditions, what the return value represents, what error modes
   exist) or leave it for a real pass later. Subsumes the part of x2x2
   that was about adding `///` where none exists. x2x2 also has the
   inventory of types and members that need coverage.

Done when the following is true for what you worked on:

- The linter used for checkin runs clean except for true exceptions
- A grep for the vocabulary offenders above returns nothing except the documented exceptions (build machinery, false positives).
- Test files have no `// ===` / `// ----` banner dividers.
- No comment under src/InductorParser/ names the state-machine evaluator or
  its internals.
- No comment narrates a fix or references a prior implementation.
- Every `//` comment on a public member that describes behavior or usage has
  been converted to `///`. Comments left as `//` are code-shape /
  design-rationale, not behavior the caller cares about.
- `///` summaries are one or two sentences. Longer material lives in
  `<remarks>`.
- Every public type and member under src/InductorParser/ has a meaningful
  `<summary>`, not a restatement of the identifier name.
- Spot-check: pick five recently-changed files and confirm their comments
  read as short, plain descriptions of current behavior, not change history
  and not jargon-laden.
