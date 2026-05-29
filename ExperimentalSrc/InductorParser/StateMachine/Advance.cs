namespace InductorParser.StateMachine;

// Classifies a rule by what a successful match does to the lexer.
// Always:    every success path advances the lexer past the lookahead token.
// Sometimes: success paths split (some advance, some don't).
// Never:     no success path advances the lexer. i.e. Zero-width predicates
//            (Peek, Not) and Eof.
//
// Advance only matters as part of the StateMachine's "can I skip this rule?"
// dispatch shortcut described on RuleStartRequirements. The recursive
// evaluator in src/InductorParser/ doesn't consult these values.
internal enum Advance
{
    Always,
    Sometimes,
    Never,
}
