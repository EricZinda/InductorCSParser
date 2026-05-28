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
// - The inner rule runs against a bounded sub-lexer over a Substring
//   of the outer input (one short string per WithinToken match, see
//   the per-call comment below for why we pay the copy) and walks one
//   rune per Read instead of one token. The sub-lexer doesn't share
//   trace state with the outer lexer, so trace output from the inner
//   rule doesn't appear in the outer trace. It DOES delegate every
//   EnterRule / ExitRule / TickPeriodic to the outer via
//   ParseBudget.InheritFrom, so the inner's recursion counts on top
//   of the outer's current depth: MaxDepth and RuleCountLimit cover
//   the combined outer-plus-inner work, and a Cancel() or expired
//   Timeout observed on either lexer trips both. Without this, a
//   recursive inner rule on a grapheme cluster crafted with many
//   combining marks could spend a fresh MaxDepth on top of the outer's
//   depth and crash the host process with a stack overflow.
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

    internal override Symbol? TryParseRule(Lexer outerLexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        var token = outerLexer.Read();
        if (token.IsEof)
        {
            TraceFailure(outerLexer, $"found '<EOF>'");
            outerLexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }

        // Sub-lexer over the token's runes. Owns a substring of the
        // outer input: the inner rule walks subInput[0 .. token.Length),
        // and the sub-lexer's Input / Position / IsEof / DeepestFailurePosition
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
        // The sub-lexer's budget delegates every EnterRule / ExitRule /
        // TickPeriodic to the outer budget so the inner's recursion
        // counts on top of the outer's CURRENT depth. MaxDepth and
        // RuleCountLimit cover the combined outer-plus-inner work
        // rather than letting the inner spend a fresh MaxDepth on top
        // of the outer's depth. Without this, a recursive inner rule
        // on a cluster crafted with many combining marks could crash
        // the host process with a stack overflow. The wall-clock
        // Stopwatch and the ParseCancellation reach the inner through
        // the same delegation: a Cancel() or expired Timeout observed
        // by either lexer trips both.
        subLexer.Budget.InheritFrom(outerLexer.Budget);

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
            int innerFailurePos = Math.Max(subLexer.DeepestFailurePosition, subLexer.Position);
            TraceFailure(outerLexer, $"inner rule failed at token rune offset {innerFailurePos}");
            // The inner rule ran on the sub-lexer, so its deepest failure
            // is recorded there. Surface it on the outer lexer keeping the
            // inner's forced flag (DeepestFailureIsForced), so a forced
            // inner .WithError stays forced. Then record WithinToken's own
            // .WithError so ranking picks between the two. Both land at the
            // outer cluster's start, the position WithinToken reports for
            // any inner failure.
            string? innerMessage = subLexer.DeepestFailureMessage;
            if (innerMessage != null)
                outerLexer.RecordFailure(startPosition, innerMessage,
                    forced: subLexer.DeepestFailureIsForced);
            outerLexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
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
            // Same pattern as the inner-failed branch: surface the inner's
            // deepest failure with its forced flag preserved so a forced
            // .WithError from a rejected alternative inside the cluster
            // competes with WithinToken's own under depth-primary ranking.
            // Without this, a forced inner hint is silently dropped when
            // the inner succeeded via a fallback that consumed only a
            // prefix of the cluster.
            string? innerMessage = subLexer.DeepestFailureMessage;
            if (innerMessage != null)
                outerLexer.RecordFailure(startPosition, innerMessage,
                    forced: subLexer.DeepestFailureIsForced);
            outerLexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }

        TraceSuccess(outerLexer, $"token '{outerLexer.Input.AsSpan(token.Offset, token.Length)}' matched inner rule");

        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;

        // One leaf Symbol per token. See Rule.ResolveLeafId for the
        // leaf-id rule shared across OneOfRule / NoneOfRule /
        // AnyTokenRule / WithinTokenRule. The Memory points into the
        // outer input, not the substring we passed to the sub-lexer, so
        // callers that walk the tree get spans that reference the
        // caller's original string.
        SymbolId leafId = ResolveLeafId(token.RuneValue);
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory, outerLexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

}
