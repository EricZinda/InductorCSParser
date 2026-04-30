using System;
using System.Collections.Generic;
using InductorParser.SyntaxTree;

namespace InductorParser.StateMachine;

// Walks the OutputOps stream produced by a successful state-machine
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
        List<OutputOp> ops,
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
        List<OutputOp> ops,
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
                case OutputKind.OpenComposite:
                {
                    cursor++;
                    BuildComposite(ops, ref cursor, end, input, preserveAllSymbols, operation, sink);
                    break;
                }
                case OutputKind.CloseComposite:
                {
                    // Caller's responsibility to consume. Stop here.
                    return;
                }
                case OutputKind.EmitLeaf:
                {
                    cursor++;
                    var declared = operation.FlattenType;
                    // Effective Delete drops the leaf entirely. In fast
                    // mode the lowerer already declined to emit Delete
                    // leaves, so this guard is mostly defensive there.
                    // In debug mode (preserveAllSymbols) the effective
                    // type is Preserve regardless of declared, so a
                    // Delete-declared leaf still survives into the tree
                    // carrying its declared FlattenType, mirroring what
                    // the recursive engine produces under the same flag.
                    if (!preserveAllSymbols && declared == FlattenType.Delete) break;
                    var leafChars = input.AsMemory(operation.Offset, operation.Length);
                    sink.Add(new Symbol(operation.SymbolId, declared, leafChars));
                    break;
                }
                case OutputKind.Prebuilt:
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
        List<OutputOp> ops,
        ref int cursor,
        int end,
        string input,
        bool preserveAllSymbols,
        OutputOp open,
        List<Symbol> parentSink)
    {
        var declared = open.FlattenType;

        // Fast mode honors the declared type as the effective one. Drop
        // Delete subtrees entirely; let Flatten subtrees flow children
        // into the parent without a wrapper.
        if (!preserveAllSymbols && declared == FlattenType.Delete)
        {
            int depth = 1;
            while (cursor < end && depth > 0)
            {
                var operation = ops[cursor++];
                if (operation.Kind == OutputKind.OpenComposite) depth++;
                else if (operation.Kind == OutputKind.CloseComposite) depth--;
            }
            return;
        }

        if (!preserveAllSymbols && declared == FlattenType.Flatten)
        {
            BuildRange(ops, ref cursor, end, input, preserveAllSymbols, parentSink);
            if (cursor < end && ops[cursor].Kind == OutputKind.CloseComposite) cursor++;
            return;
        }

        // Preserve (or PreserveAllSymbols flipping every composite to a
        // wrapper): build a wrapper Symbol carrying the rule's declared
        // FlattenType, even when that declared type is Delete or
        // Flatten. The recursive engine puts the declared FlattenType
        // on the wrapper too under the same flag.
        var children = new List<Symbol>();
        BuildRange(ops, ref cursor, end, input, preserveAllSymbols, children);
        if (cursor < end && ops[cursor].Kind == OutputKind.CloseComposite) cursor++;
        parentSink.Add(new Symbol(open.SymbolId, declared, children));
    }
}
