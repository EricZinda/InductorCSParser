// A user-defined bulk-scan rule: consumes runes while a delegate
// predicate accepts them. The built-in `ScanWhile(TokenSet)` works
// against a static set known at grammar-build time; this is the
// consumer-side variant for runtime-determined predicates.
//
// Covers the bulk-scan surface of the external Rule-subclass API:
// a single TryParseRule invocation walks N tokens of input. The
// engine has no way to enter the rule per iteration, so without a
// per-iteration call to `lexer.TickBudget()` the parse budget
// (Timeout / Cancellation / RuleCountLimit) can't observe the work
// and a long matching run pins the parser. Exposing TickBudget on
// the public Lexer surface lets external rules play by the same
// rules as the built-in ScanWhile / ScanUntil.

using System;
using System.Collections.Generic;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser.ExternalContractTests;

public sealed class ScanRunRule : Rule
{
    private readonly Func<int, bool> _predicate;

    public ScanRunRule(Func<int, bool> predicate)
        : base(FlattenType.Preserve, emitsLeaf: true)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
    }

    protected override Symbol? TryParseRule(
        Lexer lexer,
        int startPosition,
        FlattenType effectiveFlattenType,
        List<Symbol>? outputSymbols)
    {
        while (!lexer.IsEof)
        {
            // The per-iteration budget tick. Same pattern as the
            // built-in ScanWhile and ScanUntil.
            lexer.TickBudget();

            // Probe-and-Read: Read advances unconditionally; the probe
            // rolls back when the predicate rejects so the failed token
            // is left for the next rule.
            using var probe = lexer.BeginProbe();
            var token = lexer.Read();
            if (!_predicate(token.RuneValue)) break;
            probe.Commit();
        }

        int length = lexer.Position - startPosition;
        TraceSuccess(lexer, $"matched {length} chars");

        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;

        var leaf = new Symbol(Id, FlattenType,
            lexer.Input.AsMemory(startPosition, length), lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leaf);
            return Symbol.Discarded;
        }
        return leaf;
    }
}
