using System;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

internal sealed class EofRule : Rule
{
    public EofRule() : base(FlattenType.Delete) { }

    internal override Symbol? TryParseRule(Lexer lexer, bool discard)
    {
        if (!lexer.IsEof)
        {
            TraceFailure(lexer, $"found {lexer.Input[lexer.Position]}");
            // Error Positioning: the position of the unexpected content. EofRule
            // doesn't read anything; it just checks whether we've reached
            // end-of-input. When the check fails, lexer.Position is
            // pointing straight at the stuff that shouldn't be here.
            lexer.RecordFailure(lexer.Position, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"");
        if (discard)
            return Symbol.Discarded;
        return new Symbol(Id, FlattenType, Array.Empty<Symbol>());
    }
}
