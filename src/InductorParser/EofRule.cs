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
    public EofRule() : base(FlattenType.Delete) { }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
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
        // Zero-width: no children to merge. Delete and Flatten both
        // return Discarded (nothing to add anywhere). Only Preserve
        // builds the empty-children marker Symbol.
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, Array.Empty<Symbol>())
            : Symbol.Discarded;
    }

    // See the FirstConsumedRunes / Advance field docs on Rule for more information on what this does.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // We need to return *all* characters that *might* be consumed as the first charactr
        // Then, we need to say if the first character will Always/Sometimes/Never be consumed
        // For Eof:
        // Eof only matches at end-of-input and never advances, so Advance
        // is Never. No rune satisfies it either (EOF isn't a rune), so
        // FirstConsumedRunes is Empty.
        return new RuleStartRequirements(RuneSet.Empty, Advance.Never);
    }
}
