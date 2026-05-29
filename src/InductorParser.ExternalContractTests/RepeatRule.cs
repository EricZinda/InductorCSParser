// A composite rule that runs its inner rule exactly N times. The
// built-in `Exactly(n, inner)` factory does the same thing; the point
// of this rule is to exercise the composite-rule surface of the
// external Rule-subclass API, not to add new functionality.
//
// RepeatRuleTests has the surrounding context: this is the composite
// rule counterpart to FailRule (failure-only zero-consumption leaf)
// and OneRuneRule (consuming leaf with success path). Composites
// touch a different set of surfaces:
//
//   - base(FlattenType, params Rule[] children) constructor
//   - Children[i] access
//   - ParseChild(child, lexer, outputSymbols) delegation
//   - Lexer.Position read mid-parse
//   - Lexer.RecordCompositeFailure for the composite's own .WithError
//   - Symbol composite constructor with a children list and a span
//   - effectiveFlattenType branching across all three modes for a
//     composite (Delete drops, Flatten lifts children to the parent
//     list, Preserve allocates its own list)

using System;
using System.Collections.Generic;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser.ExternalContractTests;

public sealed class RepeatRule : Rule
{
    private readonly int _count;

    public RepeatRule(int count, Rule inner)
        // The inner is wired in via the base ctor's `params Rule[]`.
        // Compile walks the rule graph through Children and assigns
        // ids to inner subrules, so as long as the inner is in
        // Children it gets compiled with the parent.
        : base(FlattenType.Preserve, inner)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Repeat count must be non-negative.");
        _count = count;
    }

    private Rule Inner => Children[0];

    protected override Symbol? TryParseRule(
        Lexer lexer,
        int startPosition,
        FlattenType effectiveFlattenType,
        List<Symbol>? outputSymbols)
    {
        // Composites that Preserve their own Symbol need a fresh
        // outputSymbols list of their own so they don't leak the
        // inner's children up to the caller's And/Or. For Flatten and
        // Delete the Symbol isn't kept anyway, so the caller's list
        // (or null in Delete mode) is what ParseChild writes into.
        if (effectiveFlattenType == FlattenType.Preserve)
            outputSymbols = new List<Symbol>(_count);

        for (int iteration = 0; iteration < _count; iteration++)
        {
            var child = ParseChild(Inner, lexer, outputSymbols);
            if (child == null)
            {
                TraceFailure(lexer, $"iteration {iteration + 1} of {_count} failed");
                // RecordCompositeFailure (not RecordFailure) so the
                // composite's own .WithError anchors at the deepest
                // child position, matching how the built-in And/Or do
                // it.
                lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                return null;
            }
            // ParseChild returns the inner's Symbol (Preserve) or
            // Symbol.Discarded (Delete / Flatten). Discarded children
            // shouldn't be appended to the composite's children list;
            // ParseChild already wrote any Flatten-mode children into
            // outputSymbols itself.
            if (outputSymbols != null && !ReferenceEquals(child, Symbol.Discarded))
                outputSymbols.Add(child);
        }

        TraceSuccess(lexer, $"matched {_count} iterations");

        int matchLength = lexer.Position - startPosition;
        return effectiveFlattenType == FlattenType.Preserve
            ? new Symbol(Id, FlattenType, outputSymbols, lexer.Input.AsMemory(startPosition, matchLength), lexer.Context)
            : Symbol.Discarded;
    }
}
