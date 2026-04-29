namespace InductorParser;

// Return type of Rule.ComputeRuleStart. Carries the two compile-time
// fields (FirstConsumedRunes, Advance) that power the "can I skip this
// rule?" shortcut.
//
// ===== Definitive reference for the "can I skip this rule?" shortcut =====
//
// Before dispatching into a child rule, an enclosing rule can peek the next
// rune the lexer would read (the lookahead) and use these two fields to
// decide whether the child could possibly succeed. If not, it skips
// dispatch entirely, avoiding a wasted transaction/read/rollback cycle.
//
//   FirstConsumedRunes: The set of runes that could be this rule's
//       first consumed rune.
//
//       For a rule that *always* consumes something on success
//       (Advance.Always), lookahead being in FirstConsumedRunes is
//       necessary but not sufficient for success. Necessary because
//       it must move past the lookahead rune (possibly as part of a
//       larger lexer token). Sufficient since it may consume more and fail:
//       Literal "hello" has 'h' in its set but still fails on "hxxx".
//
//       For Sometimes/Never rules it's only compositional info since it can't be
//       used for the shortcut (see below).
//
//   Advance: whether this rule moves past the lookahead rune on
//       success: Always / Sometimes / Never (see Advance.cs).
//
// Callers that want to see if they can skip a rule because it can't possibly
// succeed don't inspect these fields directly. They call
// Rule.CannotMatchLookahead(peekRune), which combines them as:
//
//     Advance == Always && !FirstConsumedRunes.Contains(peek)
//
// Only Always makes the filter sound: Sometimes rules (e.g. ZeroOrMore)
// can sometimes (e.g. Optional) succeed without consuming lookahead,
// and Never rules (e.g. Peek, Not, Eof) never consume anything. In both
// cases CannotMatchLookahead returns false and the rule gets attempted.
//
// Advance serves two equally necessary roles:
//
//   1. Ensures we only skip rules that would truly fail. Without it,
//      Sometimes rules (which can succeed on any lookahead via zero-
//      match) and Never rules (whose Empty set would always exclude
//      the lookahead) would be wrongly skipped.
//
//   2. Composition. Parent rules (AllOf/FirstOf/BetweenInclusive) look at
//      their children's Advance when computing their own
//      FirstConsumedRunes. The three-way distinction tells a
//      composite "always claims the lookahead" (Always) vs. "might let the
//      next sibling claim it" (Sometimes) vs. "examines the lookahead without
//      claiming it" (Never):
//
//        * Always child. Say X.Advance == Always in AllOf(X, Y): X
//          definitely reads the lookahead, Y reads later. The
//          composite stops at X since Y's FirstConsumedRunes is
//          irrelevant to the AllOf's own.
//
//        * Sometimes child. Say Optional in AllOf(Optional(X), Y):
//          Optional might match X or match zero. If it matches zero,
//          Y reads the lookahead. The composite has to union both
//          X's and Y's FirstConsumedRunes.
//
//        * Never child. Say Peek in AllOf(Peek(X), Y): Peek never
//          consumes, so Y reads the lookahead. Peek's FirstConsumedRunes
//          is Empty and contributes nothing. Never tells the
//          composite "don't union my set into yours, move to the
//          next sibling."
//
// Notes:
//   * A superset of actual first-consumed runes is safe (just slower).
//     A subset would cause enclosing rules to wrongly skip a rule that
//     could succeed. RuneSet.Universe means "I don't know, don't filter me."
//   * Advance.Never requires FirstConsumedRunes == RuneSet.Empty
//     (Compile enforces this). A rule that never consumes can't have
//     a set of "runes it would consume first."
//
// Why this algebra? It's essentially a tailored variant of LL(1)
// FIRST-set analysis from classical parser theory. In LL(1), every
// grammar symbol publishes a FIRST set (terminals that can begin any
// derivation from it) and a nullable flag (can it derive the empty
// string?). A predictive parser uses FIRST to dispatch. Nullability
// tells FIRST-composition "keep unioning past me" when computing
// FIRST of a sequence. FIRST(XY) = FIRST(X) if X is not nullable,
// else FIRST(X) ∪ FIRST(Y).
//
// We map directly:
//   * FirstConsumedRunes ≈ LL(1)'s FIRST, over runes instead of
//     grammar terminals.
//   * Advance ≈ nullability, but three-valued instead of binary.
//     Always corresponds to "not nullable" (this rule consumes, stop
//     at me). Sometimes corresponds to "nullable" (might not consume,
//     keep unioning past me). Never is PEG-specific: zero-width
//     predicates (Peek, Not, Eof) that inspect the lookahead without
//     consuming. Semantically it overlaps with Sometimes (both are
//     "always nullable"), but we keep it distinct so Compile can
//     enforce the invariant "Advance.Never ⇒ FirstConsumedRunes.Empty".
//     A zero-width rule can't have a meaningful set of first-consumed
//     runes, and the check catches subclass authoring bugs at
//     grammar-build time.
//
// Populated at Compile time. Rule's pessimistic defaults (Universe,
// Sometimes) mean any user-defined Rule subclass that doesn't override
// ComputeRuleStart is safe: it'll never be shortcutted out.
internal readonly record struct RuleStartRequirements(RuneSet FirstConsumedRunes, Advance Advance);
