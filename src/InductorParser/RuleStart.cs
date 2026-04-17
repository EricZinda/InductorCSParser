namespace InductorParser;

// Tells OrRule how each child rule can begin matching, so dispatch can
// skip branches that can't possibly fire given the lookahead.
//
//   FirstConsumedRunes — set of all possible first-consumed runes of
//                   a successful match.
//   Advance       — always / sometimes / never advances the lexer on
//                   success (see Advance.cs).
//
// Populated once per rule during Compile by a depth-first walk that
// calls ComputeRuleStart on every reachable rule. See that method for
// the subclass contract.
internal readonly record struct RuleStart(RuneSet FirstConsumedRunes, Advance Advance);
