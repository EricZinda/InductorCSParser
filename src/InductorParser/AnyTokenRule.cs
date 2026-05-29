using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches any single token, regardless of content. Fails only at EOF.
// A token is one character as the user sees it (a grapheme cluster),
// possibly built from several runes underneath, so AnyToken() consumes
// one whole user-visible character.
//
// This is the "match one token, regardless of what it is" leaf. Its
// companion idiom is ZeroOrMore(And(Not(stopRule), AnyToken())), which
// consumes content until wherever stopRule would fire.
internal sealed class AnyTokenRule : Rule
{
    public AnyTokenRule() : base(FlattenType.Preserve) { }

    // No accessors for an alternative evaluator: AnyTokenRule carries no
    // per-instance data, so there is nothing for one to read.

    protected internal override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        var token = lexer.Read();
        if (token.IsEof)
        {
            TraceFailure(lexer, $"found '<EOF>'");
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}'");
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        // See Rule.ResolveLeafId for the leaf-id rule shared across
        // OneOfRule / NoneOfRule / AnyTokenRule / WithinTokenRule.
        SymbolId leafId = ResolveLeafId(token.RuneValue);
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory, lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

}
