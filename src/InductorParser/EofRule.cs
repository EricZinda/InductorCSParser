using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Succeeds only at end of input. Consumes nothing either way. Used as
// the last element of a grammar's top-level rule to assert that the
// parse consumed the entire input rather than stopping early.
internal sealed class EofRule : Rule
{
    public EofRule() : base(FlattenType.Delete)
    {
        // EofRule only checks for end-of-input. It never moves the
        // cursor, so it needs no rollback transaction from Rule.TryParse.
        OpensTransaction = false;
    }

    protected internal override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        if (!lexer.IsEof)
        {
            // Render the unconsumed character at lexer.Position as the
            // full token the lexer would have read, not lexer.Input[Position]
            // which is one UTF-16 code unit and shows a lone surrogate
            // half for any supplementary-plane rune (every emoji past
            // the BMP) and only the first rune of a multi-rune cluster
            // under Compile(null) (decomposed graphemes, ZWJ sequences).
            // The same shape was fixed in BuildErrorMessage's {character}
            // placeholder; trace lines that quote a token need the same
            // grapheme-aware rendering.
            int tokenLength = lexer.PeekTokenLength(lexer.Position);
            TraceFailure(lexer, $"found {lexer.Input.Substring(lexer.Position, tokenLength)}");
            // Error Positioning: the position of the unexpected content. EofRule
            // doesn't read anything. It just checks whether we've reached
            // end-of-input. When the check fails, lexer.Position is
            // pointing straight at the stuff that shouldn't be here.
            lexer.RecordFailure(lexer.Position, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"");
        // Zero-width: no children to merge. Delete and Flatten both
        // return Discarded (nothing to add anywhere). Only Preserve
        // builds the empty-children marker Symbol, anchored at
        // lexer.Position so the consumed span has a position even
        // though it has zero length.
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>(), lexer.Input.AsMemory(lexer.Position, 0), lexer.Context)
            : Symbol.Discarded;
    }

}
