using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Reads one token from the outer lexer and runs an inner rule against the
// runes inside that token. Under GraphemeLexer (the default) the outer
// token is a grapheme cluster that may span several runes, and the inner
// rule gets to walk and validate each of them. Under RuneLexer the outer
// token is already one rune, so the inner rule sees a one-rune stream and
// behaves identically to what you'd get without the wrapper.
//
// Semantics: the inner rule must consume every rune of the grapheme. If it
// matches only a prefix, the whole WithinGrapheme match fails and the
// outer lexer rolls back. A grapheme is atomic from the outer view: we
// don't leave half of it on the floor for a following rule to pick up.
//
// This is how Rules.Identifier handles Devanagari / Thai / Arabic-with-
// vowels under the default grapheme lexer. It's also the reusable
// building block for any rule that needs to look inside a grapheme:
// emoji-with-modifier matchers, jamo-cluster validators, "reject any
// multi-rune grapheme" strictness rules, etc. See Rules.WithinGrapheme
// for the factory and docs/UnicodeGotchas.md for the broader context.
//
// Scope limits worth calling out:
//
// - The inner rule runs against a bounded RuneLexer that shares the
//   outer lexer's input string (no Substring copy). That sub-lexer
//   does not share trace or budget state with the outer lexer. Trace
//   output from the inner rule doesn't appear in the outer trace.
//   Budget-wise this is safe: the inner parse is bounded by the
//   grapheme's rune count (a few dozen at most), so runaway is
//   impossible in practice.
//
// - Inner-rule Symbols are discarded. WithinGrapheme emits one leaf
//   Symbol representing the whole grapheme on success. Callers that
//   want structure inside the grapheme would need a different rule.
internal sealed class WithinGraphemeRule : Rule
{
    private readonly Rule _innerRule;

    public WithinGraphemeRule(Rule innerRule)
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

        // Sub-lexer over the grapheme's runes. Same input string as the
        // outer lexer, bounded to [token.Offset, token.Offset + token.Length).
        // No Substring allocation: the sub-lexer shares the outer's string
        // and uses absolute positions, so its DeepestFailure and Position
        // are already outer-input coordinates.
        var subLexer = new RuneLexer(
            outerLexer.Input,
            token.Offset,
            token.Offset + token.Length);

        // Throwaway output list for the inner rule. Any symbols the inner
        // rule emits are discarded: WithinGrapheme exposes one leaf per
        // grapheme to the outer parse, not the rune-level substructure.
        var innerOutputs = new List<Symbol>();
        var innerResult = _innerRule.TryParse(subLexer, innerOutputs);

        if (innerResult == null && innerOutputs.Count == 0)
        {
            // Inner rule failed. Sub-lexer positions are already absolute
            // in the outer input, so propagate them directly.
            int failurePos = Math.Max(subLexer.DeepestFailure, subLexer.Position);
            TraceFailure(outerLexer, $"inner rule failed at grapheme rune offset {failurePos - token.Offset}");
            outerLexer.RecordFailure(failurePos, subLexer.DeepestFailureMessage ?? ErrorMessage);
            return null;
        }

        if (!subLexer.IsEof)
        {
            // Inner rule matched a prefix of the grapheme but not all of
            // it. A grapheme is atomic, so partial matches don't count.
            int consumed = subLexer.Position - token.Offset;
            TraceFailure(outerLexer, $"inner rule consumed only {consumed}/{token.Length} of the grapheme");
            outerLexer.RecordFailure(subLexer.Position, ErrorMessage);
            return null;
        }

        TraceSuccess(outerLexer, $"grapheme '{outerLexer.Input.AsSpan(token.Offset, token.Length)}' matched inner rule");
        outerTransaction.Commit();

        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;

        // One leaf Symbol per grapheme. The Memory points into the outer
        // input, not the substring we passed to the sub-lexer, so callers
        // that walk the tree get spans that reference the caller's
        // original string.
        int runeValue = token.RuneValue;
        SymbolId leafId = runeValue >= 0 ? new SymbolId(runeValue) : Id;
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    internal override RuleStartRequirements ComputeRuleStart()
    {
        // We always consume exactly one outer token on success, so Advance
        // is Always. The first rune of that token has to satisfy whatever
        // the inner rule's first-rune requirement is, so we can propagate
        // the inner's FirstConsumedRunes to the outer fast-fail path. That
        // lets FirstOf(WithinGrapheme(...), ...) skip this alternative without
        // calling into it when the next grapheme starts with a rune the
        // inner rule can't accept.
        var innerStart = _innerRule.ComputeRuleStart();
        return new RuleStartRequirements(innerStart.FirstConsumedRunes, Advance.Always);
    }
}
