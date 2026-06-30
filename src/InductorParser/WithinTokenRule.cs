using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Reads one token from the outer lexer and runs an inner rule against
// the runes inside that token. The outer token is one grapheme that may
// span several runes, and the inner rule gets to walk and validate each
// of them.
//
// Semantics: the inner rule can be any rule, a composition of built-ins
// (And, Or, OneOf, repetition) or a user's own Rule subclass. Nothing about
// it is special-cased. WithinToken hands it the sub-lexer below through the
// same ParseChild path every nested rule goes through. The sub-lexer feeds
// the outer token's runes one per Read, and the inner rule can't tell those
// from ordinary one-rune tokens: it's the same stream it would see parsing
// plain ASCII, where every grapheme is already a single rune. So the inner
// rule does nothing different from ordinary parsing, no "inside a token" code
// path, and any rule that uses only the lexer it's given works, whoever wrote
// it. The inner rule must consume every rune of the token. If it matches only
// a prefix, the whole WithinToken match fails and the outer lexer rolls back.
// A token is atomic from the outer view: we don't leave half of it on the
// floor for a following rule to pick up.
//
// This is how Rules.Identifier validates a script like Devanagari, Thai, or
// Arabic-with-vowels, where one identifier character is a grapheme
// spanning several runes: WithinToken walks into the token and checks each
// rune against the XID sets (the sets of valid starts and continue characters), 
// rather than matching the whole token as one
// unit. It's also the reusable building
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
//   rule doesn't appear in the outer trace. It does delegate its
//   budget bookkeeping to the outer via ParseBudget.InheritFrom, so
//   the inner's recursion counts on top of the outer's current depth:
//   MaxDepth and RuleCountLimit cover
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
        : base(FlattenType.Preserve, emitsLeaf: true, innerRule ?? throw new ArgumentNullException(nameof(innerRule)))
    {
        _innerRule = innerRule;
    }

    protected override Symbol? TryParseRule(Lexer outerLexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
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
        // (built-in or user-defined) that bounds its own loop on
        // lexer.Input.Length stays correct rather than running past the
        // sub-lexer's bound.
        string subInput = outerLexer.Input.Substring(token.Offset, token.Length);
        var subLexer = new Lexer(subInput, oneRunePerToken: true);
        // The sub-lexer's budget delegates its depth, rule-count, and
        // periodic timeout/cancellation bookkeeping to the outer budget
        // so the inner's recursion counts on top of the outer's current
        // depth. MaxDepth and RuleCountLimit cover the combined
        // outer-plus-inner work rather than letting the inner spend a
        // fresh MaxDepth on top of the outer's depth. Without this, a
        // recursive inner rule on a cluster crafted with many combining
        // marks could crash the host process with a stack overflow. The
        // wall-clock Stopwatch and the ParseCancellation reach the inner
        // through the same delegation: a Cancel() or expired Timeout
        // observed by either lexer trips both.
        subLexer.InheritBudgetFrom(outerLexer);

        // Throwaway output list for the inner rule. Any symbols the inner
        // rule emits are discarded: WithinToken exposes one leaf per
        // token to the outer parse, not the rune-level substructure.
        var innerOutputs = new List<Symbol>();
        var innerResult = ParseChild(_innerRule, subLexer, innerOutputs);

        if (innerResult == null && innerOutputs.Count == 0)
        {
            // Snap the recorded outer-coordinate failure back to the outer
            // cluster's start so the parser-wide "errors land at cluster
            // boundaries" invariant holds. Trace cites the rune-level offset
            // for debug. Sub-lexer positions are 0-based UTF-16 offsets into
            // the substring, so they're char offsets, not rune offsets: a
            // supplementary-plane rune (an emoji, a CJK-extension character)
            // is two chars but one rune. RuneHelpers.RuneCount converts
            // to the rune offset the rune-per-token model debugs in. The
            // conversion sits inside the trace hole, so the handler skips it
            // when tracing is off.
            TraceFailure(outerLexer,
                $"inner rule failed at token rune offset {RuneHelpers.RuneCount(subInput.AsSpan(0, Math.Max(subLexer.DeepestFailurePosition, subLexer.Position)))}");
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
            // The inner rule succeeded but stopped before the end of the
            // token, so WithinToken fails (the whole token has to match).
            // Record the failure at startPosition, the token's start. A token
            // is atomic from outside, so there's no meaningful place to point
            // inside it: the rune offset where the inner rule stopped is
            // mid-grapheme from the outer view.
            //
            // The trace's "consumed X/Y" counts runes, but subLexer.Position
            // is a UTF-16 char offset (a supplementary-plane rune is two
            // chars, one rune), so RuneHelpers.RuneCount converts both the
            // consumed amount and the token length to runes. The conversions
            // sit inside the trace string, so they cost nothing when tracing
            // is off.
            TraceFailure(outerLexer,
                $"inner rule consumed only {RuneHelpers.RuneCount(subInput.AsSpan(0, subLexer.Position))}/{RuneHelpers.RuneCount(subInput.AsSpan())} of the token");
            // Same idea as the branch above. When WithinToken fails, the
            // parser later shows the user the best error it collected. If
            // something inside the token failed with a custom .WithError
            // message, that's usually more helpful than WithinToken's generic
            // one. But the inner rule overall succeeded here (it just didn't
            // match the whole token), so its message wasn't kept. Grab it from
            // the sub-lexer and record it too, keeping whether it was forced,
            // so it competes with WithinToken's own. Without this, the user
            // only ever sees the generic message.
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
        // AnyTokenRule / WithinTokenRule. The Symbol's text is token.Memory, a
        // ReadOnlyMemory<char> window into the outer input (the caller's
        // original string), not the throwaway substring we passed to the
        // sub-lexer. So callers that walk the tree get spans pointing into
        // their own string.
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
