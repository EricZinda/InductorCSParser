using System;
using InductorParser.SyntaxTree;

namespace InductorParser.StateMachine;

// One entry in the parse-time emission script. Lowering wires opcode
// states to push EmissionOp values into Machine.EmissionOps as the
// state machine runs. After the state machine reaches HaltSuccess the
// TreeBuilder walks the emission list and produces the Symbol[]
// ParseResult expects.
//
// On a backtrack, the FailRestore opcode truncates the emission list
// back to the cursor saved in the BacktrackFrame, so partial emissions
// from a failed alternative never reach the TreeBuilder.
internal enum EmissionKind : byte
{
    // OpenComposite / CloseComposite frame a composite rule's
    // children. SymbolId is on the matching Open. Close has no extra
    // payload.
    OpenComposite,
    CloseComposite,

    // EmitLeaf is a self-contained leaf node. SymbolId, FlattenType,
    // and the (Source, Offset, Length) span point into the input.
    EmitLeaf,

    // Prebuilt is a pre-constructed Symbol the BridgeToRecursive opcode
    // captured from a recursive-evaluator parse. The Symbol carries
    // its own children, leaf chars, id, and FlattenType already, so
    // the TreeBuilder just appends it to the surrounding sink without
    // synthesizing a new wrapper. Lets the bridge inject native
    // recursive-parser output into the state-machine emission stream
    // without round-tripping through Open/Close/Leaf ops.
    Prebuilt,
}

internal readonly struct EmissionOp
{
    public readonly EmissionKind Kind;
    public readonly SymbolId SymbolId;
    public readonly FlattenType FlattenType;
    public readonly int Offset;
    public readonly int Length;
    public readonly Symbol? PrebuiltSymbol;

    public EmissionOp(EmissionKind kind, SymbolId symbolId, FlattenType flattenType, int offset, int length, Symbol? prebuiltSymbol)
    {
        Kind = kind;
        SymbolId = symbolId;
        FlattenType = flattenType;
        Offset = offset;
        Length = length;
        PrebuiltSymbol = prebuiltSymbol;
    }

    public static EmissionOp Open(SymbolId id, FlattenType flattenType) =>
        new EmissionOp(EmissionKind.OpenComposite, id, flattenType, 0, 0, null);

    public static EmissionOp Close() =>
        new EmissionOp(EmissionKind.CloseComposite, default, default, 0, 0, null);

    public static EmissionOp Leaf(SymbolId id, FlattenType flattenType, int offset, int length) =>
        new EmissionOp(EmissionKind.EmitLeaf, id, flattenType, offset, length, null);

    public static EmissionOp Prebuilt(Symbol symbol) =>
        new EmissionOp(EmissionKind.Prebuilt, default, default, 0, 0, symbol);
}
