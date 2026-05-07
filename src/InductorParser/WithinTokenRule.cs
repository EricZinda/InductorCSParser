using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;

namespace InductorParser;

// Reads one token from the outer lexer and runs an inner rule against
// the runes inside that token. The outer token is a StringInfo text
// element that may span several runes, and the inner rule gets to walk
// and validate each of them.
//
// Semantics: the inner rule must consume every rune of the token. If
// it matches only a prefix, the whole WithinToken match fails and the
// outer lexer rolls back. A token is atomic from the outer view: we
// don't leave half of it on the floor for a following rule to pick up.
//
// This is how Rules.Identifier handles Devanagari / Thai / Arabic-with-
// vowels under token-level tokenization. It's also the reusable building
// block for any rule that needs to look inside a token: emoji-with-
// modifier matchers, jamo-cluster validators, "reject any multi-rune
// token" strictness rules, etc. See Rules.WithinToken for the factory
// and docs/UnicodeGotchas.md for the broader context.
//
// Scope limits worth calling out:
//
// - The inner rule runs against a bounded sub-lexer that shares the
//   outer lexer's input string (no Substring copy) and walks one rune
//   per Read instead of one token. The sub-lexer doesn't share trace
//   or budget state with the outer lexer; trace output from the inner
//   rule doesn't appear in the outer trace. It can only consume inside
//   the outer token's span, so normal character-consuming rules are
//   tiny. Avoid using arbitrary long-running user code here, because
//   the sub-lexer has no shared budget counters.
//
// - Inner-rule Symbols are discarded. WithinToken emits one leaf
//   Symbol representing the whole token on success. Callers that
//   want structure inside the token would need a different rule.
internal sealed class WithinTokenRule : Rule
{
    private readonly Rule _innerRule;

    public WithinTokenRule(Rule innerRule)
        : base(FlattenType.Preserve, innerRule ?? throw new ArgumentNullException(nameof(innerRule)))
    {
        _innerRule = innerRule;
    }

    internal override Symbol? TryParseRule(Lexer outerLexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var outerTransaction = outerLexer.BeginTransaction();

        var token = outerLexer.Read();
        if (token.IsEof)
        {
            TraceFailure(outerLexer, $"found '<EOF>'");
            outerLexer.RecordFailure(outerTransaction.StartPosition, ErrorMessage);
            return null;
        }

        // Sub-lexer over the token's runes. Owns a substring of the
        // outer input: the inner rule walks subInput[0 .. token.Length),
        // and the sub-lexer's Input / Position / IsEof / DeepestFailure
        // / Read() Token offsets are all 0-based on that substring.
        // Other rules don't need to know what mode the lexer is in.
        // Switched to one-rune-per-token mode so the inner rule sees
        // each rune of the outer token as its own token.
        //
        // The substring allocation costs one short string per
        // WithinToken match (typically 1-4 chars per cluster). The
        // payoff is that lexer.Input.Length, lexer.Position, and
        // lexer.IsEof all agree about the readable range, so any rule
        // that bounds its own loop on lexer.Input.Length stays correct.
        // That removes a trap that bit ScanUntilRule (it looped on
        // lexer.Input.Length, which used to be the FULL outer string,
        // and infinite-looped past the sub-lexer's bound) and would
        // have bitten any user-defined Rule subclass following the same
        // pattern.
        string subInput = outerLexer.Input.Substring(token.Offset, token.Length);
        var subLexer = new Lexer(
            subInput,
            startPosition: 0,
            endPosition: subInput.Length,
            traceSink: null,
            traceLevel: TraceLevel.Normal,
            oneRunePerToken: true);

        // Throwaway output list for the inner rule. Any symbols the inner
        // rule emits are discarded: WithinToken exposes one leaf per
        // token to the outer parse, not the rune-level substructure.
        var innerOutputs = new List<Symbol>();
        var innerResult = _innerRule.TryParse(subLexer, innerOutputs);

        if (innerResult == null && innerOutputs.Count == 0)
        {
            // Sub-lexer positions are 0-based on the substring, so they're
            // rune offsets within the outer token. Snap the recorded
            // outer-coordinate failure back to the outer cluster's start
            // so the parser-wide "errors land at cluster boundaries"
            // invariant holds. Trace cites the rune-level offset for
            // debug.
            int innerFailurePos = Math.Max(subLexer.DeepestFailure, subLexer.Position);
            TraceFailure(outerLexer, $"inner rule failed at token rune offset {innerFailurePos}");
            outerLexer.RecordFailure(outerTransaction.StartPosition, subLexer.DeepestFailureMessage ?? ErrorMessage);
            return null;
        }

        if (!subLexer.IsEof)
        {
            // Inner rule matched a prefix of the token but not all of it.
            // A token is atomic from the outer view, so the failure
            // belongs at the outer cluster's start, not at the rune
            // offset where the inner rule stopped reading (which is
            // mid-cluster from outside). subLexer.Position is 0-based on
            // the substring, so it's the consumed rune count directly.
            int consumed = subLexer.Position;
            TraceFailure(outerLexer, $"inner rule consumed only {consumed}/{token.Length} of the token");
            outerLexer.RecordFailure(outerTransaction.StartPosition, ErrorMessage);
            return null;
        }

        TraceSuccess(outerLexer, $"token '{outerLexer.Input.AsSpan(token.Offset, token.Length)}' matched inner rule");
        outerTransaction.Commit();

        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;

        // One leaf Symbol per token. See Rule.ResolveLeafId for the
        // leaf-id rule shared across OneOfRule / NoneOfRule /
        // AnyTokenRule / WithinTokenRule. The Memory points into the
        // outer input, not the substring we passed to the sub-lexer, so
        // callers that walk the tree get spans that reference the
        // caller's original string.
        SymbolId leafId = ResolveLeafId(token.RuneValue);
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    // WithinToken always consumes exactly one outer token (one grapheme
    // cluster) on success, so Advance.Always. The first rune of that
    // cluster has to satisfy whatever the inner rule requires, so the
    // outer first-token shape forwards the inner's set and polarity.
    // Calls _innerRule.ComputeRuleStart() rather than going through
    // PassesThroughTo because the inner rule's compiled fields may not
    // yet be populated at this point in the Compile walk.
    internal override RuleStartRequirements ComputeRuleStart() =>
        _innerRule.ComputeRuleStart().WithAdvance(Advance.Always);
}
