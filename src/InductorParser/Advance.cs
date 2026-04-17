namespace InductorParser;

// Classifies a rule by what a successful match does to the lexer.
// Always   — every success path advances the lexer by at least one rune.
//            Char, Literal, RuneIn, AnyChar, OneOrMore over a consumer.
// Sometimes — success paths split: some advance, some don't. Optional,
//            ZeroOrMore, StringChars (whose body can be zero-length),
//            combinators with a mix of consuming and non-consuming
//            children.
// Never    — no success path advances the lexer. Zero-width predicates
//            (Peek, Not) and Eof.
internal enum Advance
{
    Always,
    Sometimes,
    Never,
}
