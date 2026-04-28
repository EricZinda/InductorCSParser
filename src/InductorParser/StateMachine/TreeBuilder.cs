using System;
using System.Collections.Generic;
using InductorParser.SyntaxTree;

namespace InductorParser.StateMachine;

// Walks the EmissionOps stream produced by a successful state-machine
// run and produces the IReadOnlyList<Symbol> ParseResult expects.
//
// FlattenType handling mirrors the existing Rule.TryParse path:
//   * Preserve: build a wrapper Symbol carrying the inner children.
//   * Flatten: drop the wrapper; let inner children flow into the
//       enclosing Preserve wrapper (or the top level).
//   * Delete: drop the entire subtree (wrapper and children).
//
// PreserveAllSymbols overrides every FlattenType to Preserve, just like
// the existing Rule.TryParse does at runtime.
internal static class TreeBuilder
{
    public static IReadOnlyList<Symbol> Build(
        List<EmissionOp> ops,
        string input,
        bool preserveAllSymbols)
    {
        var topLevel = new List<Symbol>();
        int cursor = 0;
        BuildRange(ops, ref cursor, ops.Count, input, preserveAllSymbols, topLevel);
        return topLevel;
    }

    // Consumes ops in [cursor, end), appending produced Symbols to
    // sink. Stops when cursor reaches end OR when it hits a
    // CloseComposite (which the caller's matching Open should have
    // delegated to us). The cursor is advanced past the matching
    // Close.
    private static void BuildRange(
        List<EmissionOp> ops,
        ref int cursor,
        int end,
        string input,
        bool preserveAllSymbols,
        List<Symbol> sink)
    {
        while (cursor < end)
        {
            var operation = ops[cursor];
            switch (operation.Kind)
            {
                case EmissionKind.OpenComposite:
                {
                    cursor++;
                    BuildComposite(ops, ref cursor, end, input, preserveAllSymbols, operation, sink);
                    break;
                }
                case EmissionKind.CloseComposite:
                {
                    // Caller's responsibility to consume. Stop here.
                    return;
                }
                case EmissionKind.EmitLeaf:
                {
                    cursor++;
                    var effectiveFlatten = preserveAllSymbols ? FlattenType.Preserve : operation.FlattenType;
                    if (effectiveFlatten == FlattenType.Delete) break;
                    var leafChars = input.AsMemory(operation.Offset, operation.Length);
                    // Both Preserve and Flatten on a leaf look the same
                    // in the existing code: the leaf becomes a child of
                    // the enclosing parent. Same here.
                    sink.Add(new Symbol(operation.SymbolId, effectiveFlatten, leafChars));
                    break;
                }
                case EmissionKind.Prebuilt:
                {
                    cursor++;
                    // Symbol came from the recursive evaluator via the
                    // BridgeToRecursive opcode. Its FlattenType is
                    // already whatever the recursive evaluator chose
                    // (after PreserveAllSymbols normalization on its
                    // side, since we set lexer.PreserveAllSymbols
                    // before bridging). Append directly.
                    sink.Add(operation.PrebuiltSymbol!);
                    break;
                }
            }
        }
    }

    private static void BuildComposite(
        List<EmissionOp> ops,
        ref int cursor,
        int end,
        string input,
        bool preserveAllSymbols,
        EmissionOp open,
        List<Symbol> parentSink)
    {
        var effectiveFlatten = preserveAllSymbols ? FlattenType.Preserve : open.FlattenType;

        if (effectiveFlatten == FlattenType.Delete)
        {
            // Skip the entire subtree without producing any symbols.
            // Walk to the matching CloseComposite by tracking nesting
            // depth.
            int depth = 1;
            while (cursor < end && depth > 0)
            {
                var operation = ops[cursor++];
                if (operation.Kind == EmissionKind.OpenComposite) depth++;
                else if (operation.Kind == EmissionKind.CloseComposite) depth--;
            }
            return;
        }

        if (effectiveFlatten == FlattenType.Flatten)
        {
            // Children flow into the parent's sink directly. Recurse
            // with parentSink as the destination.
            BuildRange(ops, ref cursor, end, input, preserveAllSymbols, parentSink);
            // Consume the matching CloseComposite.
            if (cursor < end && ops[cursor].Kind == EmissionKind.CloseComposite) cursor++;
            return;
        }

        // Preserve: build a wrapper Symbol with the inner children.
        var children = new List<Symbol>();
        BuildRange(ops, ref cursor, end, input, preserveAllSymbols, children);
        if (cursor < end && ops[cursor].Kind == EmissionKind.CloseComposite) cursor++;
        parentSink.Add(new Symbol(open.SymbolId, FlattenType.Preserve, children));
    }
}
