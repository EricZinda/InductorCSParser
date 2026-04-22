namespace InductorParser;

// Classifies a rule by what a successful match does to the lexer.
// Always   — every success path advances the lexer by at least one rune.
// Sometimes — success paths split: some advance, some don't.
// Never    — no success path advances the lexer. i.e. Zero-width predicates
//            (Peek, Not) and Eof.
//
// Advance only matters as part of the opt-in "can I skip this rule?"
// shortcut described in Rule.FirstConsumedRunes / Rule.Advance. A rule
// that doesn't override Rule.ComputeRuleStart inherits the pessimistic
// defaults (Universe, Sometimes) and never gets shortcutted, which is
// always correct — just slower. Subclasses only need to report a real
// Advance value when they want an enclosing Or or repetition rule to
// be able to skip them based on the next lookahead rune.
internal enum Advance
{
    Always,
    Sometimes,
    Never,
}
