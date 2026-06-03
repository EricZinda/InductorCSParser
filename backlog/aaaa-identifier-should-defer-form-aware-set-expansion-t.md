# Identifier should defer form-aware set expansion to Compile

Today `Identifier` takes a `NormalizationForm? form` parameter that has to match whatever you later pass to `Compile`. The parameter exists so XidStart and XidContinue can be pre-expanded for FormKC / FormKD compatibility entries (ligatures like U+0132 IJ, fullwidth Latin, math-bold letters) whose NFKC conversion is a multi-grapheme sequence a single `OneOf` member can't represent. If the two don't match (the common shape is to let `Identifier`'s form default to FormC and pass FormKC to `Compile`), `Compile` throws `InvalidOperationException` with a 1500-entry list, and the user has to repeat themselves:

```csharp
// Today's correct form: NormalizationForm.FormKC appears twice
var python = Identifier(form: NormalizationForm.FormKC,
                        extraStartRunes: TokenSet.Runes("_"))
    .Compile(NormalizationForm.FormKC);
```

This bit the UnicodeGotchas.md Python and Rust recipes (see BugSearchLog `2026-06-03-unicodegotchas-python-rust-identifier-recipes-throw-at-compile.md`), where the doc dropped the form from `Identifier` and the snippets threw at Compile time. The doc has been fixed and the requirement is now tested, but the trap is still there for anyone writing the same shape themselves.

What we actually want is a single source of truth for the form:

```csharp
// Proposal: form lives on Compile, full stop
var python = Identifier(extraStartRunes: TokenSet.Runes("_"))
    .Compile(NormalizationForm.FormKC);
```

`Identifier` drops its `form` parameter (or keeps it as a no-op overload for one release for source compatibility). The set-expansion work moves into `Compile`.

Why this isn't just "auto-expand any OneOf member that decomposes under the form"

Auto-expanding members of a bare OneOf would silently change the rule's semantics. `OneOf(TokenSet.Single(0x0132))` ("IJ" ligature) under FormKC would turn into "match I, then a separate J", which a bare OneOf can't represent (it matches one token). The expansion only makes sense when the OneOf sits inside a surrounding rule shape that consumes the pieces one at a time, which is exactly the shape `Identifier` builds:

```csharp
WithinToken(And(OneOf(start), ZeroOrMore(OneOf(body))))
```

Compile can't tell from looking at a OneOf in isolation whether it's safe to expand. It needs a marker from the rule that built it.

Proposal: a deferred-expansion marker on the rule

`Identifier` builds its OneOfs as today but flags them with a "form-aware expansion pending" attribute that points at the source TokenSet (XidStart / XidContinue) and the role (start / body). `Compile`, once it knows the form, walks every flagged OneOf and applies `WithCompatibilityEquivalents(form)` to the set in place before the form-validation pass runs. Non-flagged OneOfs go through validation untouched, so a hand-written `OneOf(TokenSet.Single(0x0132))` still throws (which is the right call: the user clearly meant the ligature and the rule's surrounding shape isn't there to consume the pieces).

Shape sketch:

```csharp
// OneOfRule (or whichever leaf carries the TokenSet)
internal NormalizationForm? PendingCompatExpansion { get; set; }

// In Identifier:
var startRule = new OneOfRule(start) { PendingCompatExpansion = form ?? FormC };
var bodyRule  = new OneOfRule(body)  { PendingCompatExpansion = form ?? FormC };
// ... except we want Compile's form, not Identifier's, so make it a sentinel:
var startRule = new OneOfRule(start) { PendingCompatExpansion = ExpandUnderCompileForm };
var bodyRule  = new OneOfRule(body)  { PendingCompatExpansion = ExpandUnderCompileForm };

// In Compile (before the form-validation pass):
foreach (var rule in ReachableRules)
{
    if (rule is OneOfRule oneOf && oneOf.PendingCompatExpansion == ExpandUnderCompileForm
        && (form == FormKC || form == FormKD))
    {
        oneOf.TokenSet = oneOf.TokenSet.WithCompatibilityEquivalents(form.Value);
    }
}
```

Things to settle when this is built

- `Identifier`'s `form` parameter today doubles as documentation in a grammar that uses `Compile(null)` (no normalization): the form parameter says "I want UAX #31 with these compatibility rules" even though no input normalization happens. If we drop the parameter entirely, that intent has nowhere to live. Probably fine. `Compile(null)` plus `Identifier()` already means "strict UAX #31, no compatibility expansion" today, and that's what the user gets.
- The `WithinToken` rule Identifier builds today still has to walk runes inside a grapheme. The expansion has to happen before that walk is structured, so the marker has to survive any tree rewrites Compile does before the form-validation pass.
- `Compile`'s form-validation pass runs once and produces a single aggregated error listing every offending rule. The expansion has to happen before that pass so the flagged rules don't show up in the list. Order matters: expand, then validate.
- If a user calls `Identifier()` and then never compiles (or compiles with `null`), no expansion needs to happen. The marker is dormant.
- Backwards compatibility: the existing `Identifier(form, ...)` overload can keep working for one release with the form parameter ignored, plus an `[Obsolete]` annotation pointing at the new shape. After the deprecation window, drop it.

Test surface

The existing `UnicodeGotchasExamples.Python3_identifier_recipe` and `Rust_identifier_recipe` tests assert the corrected double-form shape. After this change they should switch to the single-form shape and still pass. The `Identifier_default_form_with_FormKC_compile_throws` test asserts the trap exists. After this change that test inverts: `Identifier(extraStartRunes: TokenSet.Runes("_")).Compile(FormKC)` should now succeed, not throw. So three test updates land with the implementation, plus new coverage for the "bare OneOf with a decomposing entry still throws" case to confirm the auto-expand doesn't leak past Identifier's structural shape.
