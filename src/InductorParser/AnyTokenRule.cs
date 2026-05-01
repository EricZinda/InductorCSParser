using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches any single token, regardless of content. Fails only at EOF.
// Under RuneLexer each well-formed token is one rune, so AnyToken() matches
// any rune; malformed surrogate halves still arrive as one token.
// Under GraphemeLexer each token is one StringInfo text element (i.e. a Grapheme, possibly
// multi-rune), so AnyToken() matches that whole text element.
//
// This is the "match one token, regardless of what it is" leaf. Its
// companion idiom is ZeroOrMore(AllOf(Not(stopRule), AnyToken())), which
// consumes content until wherever stopRule would fire.
internal sealed class AnyTokenRule : Rule
{
    public AnyTokenRule() : base(FlattenType.Preserve) { }

    // No accessors needed for the state-machine lowering pass: AnyTokenRule
    // carries no per-instance data. The lowerer recognizes the type and
    // emits the MatchAnyToken opcode directly.

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        var token = lexer.Read();
        if (token.IsEof)
        {
            TraceFailure(lexer, $"found '<EOF>'");
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}'");
        transaction.Commit();
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
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

    // Return the set of runes this rule might consume first (can be a superset)
    // (RuneSet.Empty when Advance.Never. RuneSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        return new RuleStartRequirements(RuneSet.Universe, Advance.Always);
    }
}
