// A user-defined Rule subclass. Always fails at its start position
// with a user-supplied message. Useful for the dead-end branch of a
// classifier where the grammar knows "we got here and there's nothing
// valid left." Today the same effect needs the riddle
// `Not(Optional(AnyToken())).WithError(message)` (see the
// JsonStringEscapes sample's README, which uses that workaround).
//
// FailRuleTests has the surrounding context: why this project exists,
// why the assembly is deliberately external, and what the tests verify.

using System.Collections.Generic;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser.ExternalContractTests;

public sealed class FailRule : Rule
{
    private readonly string _message;

    public FailRule(string message)
        : base(FlattenType.Delete, emitsLeaf: false)
    {
        _message = message;
    }

    // `protected override` rather than `protected internal override`:
    // when an override lives in a different assembly than the base
    // class, the `internal` half of `protected internal` is invisible
    // (it scopes to the BASE class's assembly, not the override's),
    // and C# requires the override declaration to drop it.
    protected override Symbol? TryParseRule(
        Lexer lexer,
        int startPosition,
        FlattenType effectiveFlattenType,
        List<Symbol>? outputSymbols)
    {
        // Emit a failure trace line so a parse run with a
        // ParseOptions.TraceSink attached attributes the failure to
        // this rule. Other failure-path rules (Token, OneOf, Literal,
        // ...) all do this. Without it, the trace shows the failure
        // being recorded with no preceding FAIL line, leaving the
        // reader to guess which rule failed.
        TraceFailure(lexer, $"{_message}");

        // `ErrorMessage ?? _message` so a `.WithError("...")` on the
        // FailRule wins over the constructor message. `ErrorForced` so
        // a `.WithError("...", forced: true)` routes through the
        // forced slot and beats non-forced failures at any depth.
        // Without consulting these, the WithError modifier would be a
        // silent no-op on a FailRule, which would surprise any user
        // who used it.
        lexer.RecordFailure(startPosition, ErrorMessage ?? _message, ErrorForced);
        return null;
    }
}
