# Code comment cleanup pass: rules to apply

Mark these commits as doc updates unless there were features in them

A scrub of all code comments (// prose, /// XML doc, and test comments) to bring them in line with the comment rules Eric has flagged
repeatedly. Not a rewrite pass for the prose itself, just a rules-driven sweep.
The rules below come from corrections across the worktrees' memory files and
commit history. Global writing-style rules in ~/.claude/CLAUDE.md apply too,
the ones below are specific to comments.

Execution model: per file, five phases

For each file, work the phases in order. Finish a phase before starting the
next. Don't bounce between phases on the same file, the point is that a
comment about to be moved shouldn't get its facts checked, an inaccurate
comment shouldn't get its prose polished, and a comment that's going to be
deleted by a lint rule shouldn't get any work at all.

Phase 1: Reordering. Re-arrange the members of each type into this order:

- Fields
- Properties (with their backing fields right next to them, not in the
  Fields block)
- Constructors
- Finalizers
- Delegates
- Events
- Enums
- Interfaces
- Indexers
- Methods

Then nested types at the bottom:

- Structs
- Classes (records count as classes)

This differs from StyleCop SA1201 in one place: Properties are hoisted up
next to Fields so a property and its backing field can sit together.

Exception: if a nested type, delegate, enum, or small helper is used in
exactly one place and is genuinely small, leave it next to where it's used
rather than hoisting it to its slot. The point is to make the file
scannable, not to enforce a mechanical order at the cost of locality.

Reordering goes first so the later passes don't waste effort on comments
that are about to be moved or on dead members that are about to be deleted.

Phase 2: Accuracy. Read each comment and compare it to the code it sits next
to. If a claim isn't true anymore, either fix it or delete the comment. Rule
6 (stale facts) lives here. Don't move on until every comment in the file
describes what the code actually does today.

Phase 3: Lint screens. Apply the mechanical rules below: drop bug-fix
history, remove banner dividers, drop benchmark and state-machine
references, fix the vocabulary offenders, convert public `//` to `///` where
it describes behavior, and confirm every public member has a `///` summary
at all. Rules 1, 3, 4, 5, 7, the mechanical half of 8, and the coverage
half of 9 all live here.

Phase 4: Style. With the content accurate and the lint clean, tighten what's
left. Apply the universal rule (short, plain, focused). Drop doc comments on
delegating methods (Rule 2). Keep `<summary>` to one or two sentences and
push longer material to `<remarks>` (the style half of Rule 8). Make sure
each summary tells the caller something the identifier name couldn't (the
quality half of Rule 9). Drop implementation details that don't help the
reader (Rule 10).

Phase 5: Cross-check docs and tests against the final file. Once the source
file has been through Phases 1 to 4, find the docs and tests that talk about
it (XML doc examples that reference the type, markdown docs under docs/ that
walk through it, test files whose comments describe its behavior) and compare
those against the final version of the file. If the behavior, member names,
ordering, or examples have shifted during the earlier phases, update the docs
and tests to match. This is the last stage on a file because the earlier
phases are the ones that can change what the docs and tests need to say.

Each rule below is tagged with its phase (P1, P2, P3, P4, or P5) in the rule
heading.

The universal rule for every comment and doc

No jargon. Keep it simple. Keep it short. Focus on the points that matter.
A comment a reader has to decode is worse than no comment. If the same idea
fits in fewer words or plainer words, say it that way. If the comment isn't
earning its space, delete it. This applies to // comments, /// doc comments,
and test comments equally.

Rules to apply

1. [P3] No bug-fix history. Comments describe what the code does now, not how it
   got there. Drop "used to X", "previously", "before the fix", "would
   silently break", "now does Y", and any framing that references a bug, a
   commit, or a prior implementation. Counterfactuals like "would otherwise
   lose the name" are in the same category. If the only reason for the
   comment is to document the bug's history, delete the comment. The
   narrative belongs in the PR description and the backlog item, not in the
   source tree. This is the most-flagged correction (three separate memories
   across worktrees).

2. [P4] No doc comment on a delegating method. If the body is `return Other(...)`
   or `if (x == null) throw ...; return Other(...)`, skip the comment. The
   delegate is the definitive source. Restating the same description on the
   calling method duplicates information that will drift. Exception: if the
   calling method is public, add a one-line `<see cref="..." />` pointing
   at the definitive source so IntelliSense lands the reader there.

3. [P3] No banner-section headers in test files. Drop `// === Group N ===` and
   `// ---- section ----` dividers. Order related tests near each other and
   let each test's own comment carry the context. (Some older files use
   `// ==== Group N ====` headers. Treat those as the old style and remove
   them when editing nearby code.)

4. [P3] No benchmark mentions in comments, especially /// XML doc. No benchmark
   suite names (rebar, the Sherlock haystack), no measured numbers ("2x
   speedup", "23x slower"), no paths to results files. Qualitative,
   mechanism-based statements are fine ("opens one transaction instead of
   N"). Just drop the benchmark citation and the number.

5. [P3] No state-machine mentions in production code under src/InductorParser/.
   When a comment must explain code that exists for the experimental
   evaluator (EnterRuleAtDepth, TickPeriodic, the Lowering*
   accessors, ResetForReuse pooling), describe it generically: "an
   alternative evaluator", "a pooled Lexer". Never name "the state machine",
   "SM", "the lowering pass", or SM-internal concepts (Machine.CallTop, <!-- style-lint-ok -->
   Stepper, Step_Call, opcodes). Genuine false positives stay: C# async/await
   state machines, the Unicode "Sm" general category.

6. [P2] Stale facts. If code changes invalidate a claim in a comment (e.g.
   "derives from InvalidOperationException" after the exception was changed
   to derive from Exception), correct it. Same for comments that reference
   renamed API members (Symbol.Name → DisplayName, ParseResult.RawSourceTextOf
   → Symbol.SourceText). Two commits already cleaned up specific instances
   (27d4a11, 1a899af). The scrub should catch the rest.

7. [P3] Vocabulary fixes that hit comments specifically (the global CLAUDE.md
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

8. [P3 mechanical, P4 style] Public-member comments: convert // to /// when developer-facing. If a `//`
   comment on a public type or member describes behavior, usage, or anything
   a caller needs to know to use the API, convert it to `///` so it surfaces
   in IntelliSense. Keep `//` for code-shape comments: design-rationale
   paragraphs (the Grapheme design block, the Symbol GC-rooting note, the
   transaction-semantics block in Lexer), implementation-detail notes inside
   method bodies, and similar discursive material aimed at contributors.
   Doc-comment style:
   - Order: `<summary>` first, then `<remarks>`, then `<param>` /
     `<returns>` / `<exception>` / `<typeparam>`. Keep `<remarks>` right
     after `<summary>` so the high-level description and the extended
     explanation stay together. A reader skimming the doc shouldn't have
     to scroll past per-parameter detail to find the section that
     explains the method's behavior. If a method's `<remarks>` sits at
     the bottom, move it.
   - `<summary>` stays tight. One or two sentences describing what the
     member does. If the summary is growing past that, the rest belongs in
     `<remarks>`, not in the summary.
   - `<remarks>` is where the longer explanation goes. Still subject to the
     universal rule: short, plain, focused.
   - `<param>` / `<returns>` / `<exception>` for the mechanical pieces.

9. [P3 coverage, P4 quality] Every public member has a meaningful doc comment. Walk the public surface
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

10. [P4] No implementation detail for its own sake. Drop notes that describe
    how the code is implemented at the micro level (one bool branch instead
    of a virtual call, stored in a private field, uses a switch instead of
    a Dictionary, called once at construction) unless the detail actually
    helps the reader understand what the code does, why it's structured
    that way, or how to use it. The test: would removing this sentence
    leave the next reader unable to do their job? If no, it's noise.
    Implementation details that earn their place: ones that warn about
    subtle behavior ("returns 0 if `position` is at or past the end"),
    point at collaborators where the topic is actually explained ("see
    Lexing/ParseBudget.cs for the details", "see the Probe struct in
    Lexer.Probe.cs for the full explanation"), or justify a non-obvious
    design choice that the reader would otherwise question.

    Specifically don't write IDE-replaceable navigation. A comment like
    "_traceSink lives in Lexer.Tracing.cs" or "Tracing methods
    (IsTracing, Trace, WriteTraceLine) live in Lexer.Tracing.cs" is just
    duplicating what Go To Definition gives you in one keystroke, and the
    comment goes stale if anything moves. The distinction from the good
    pointers above: those point at *what's explained* in another file.
    These point at *where something is declared*. The IDE handles the
    second case for free.

Done when the following is true for what you worked on:

- Members in each type appear in the Phase 1 order (Fields, Properties,
  Constructors, Finalizers, Delegates, Events, Enums, Interfaces, Indexers,
  Methods, then nested Structs and Classes). Each property's backing field
  sits next to the property, not in the Fields block. Small one-use helpers
  (nested types, delegates, enums) left where they're used are flagged as
  intentional, not strays the next pass should hoist.
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
- No comment describes a private implementation detail (field-vs-virtual
  choice, internal data-structure choice, micro-optimization note) that
  doesn't help the reader understand what the code does or how it works.
- Spot-check: pick five recently-changed files and confirm their comments
  read as short, plain descriptions of current behavior, not change history
  and not jargon-laden.
- Docs and tests that reference each touched file have been compared against
  the file's final state and updated where the earlier phases changed
  something they relied on (member names, ordering, example code, behavior
  descriptions).
