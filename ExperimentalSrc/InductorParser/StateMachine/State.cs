namespace InductorParser.StateMachine;

// One state in the lowered program. Sixteen bytes (well, twenty with
// EmitOpData but we don't pack), four states per cache line. Lowering
// fixes every field at compile time and the runtime only reads them.
//
// OnSuccess and OnFailure: indices into CompiledProgram.States. The
// dispatcher picks one based on whether the opcode's action returned
// true. Negative values are sentinels: -2 means "halt with success"
// and -1 means "halt with failure". The inner loop terminates when
// current goes negative.
//
// Data: opcode-specific. For MatchLiteral it's an index into
// CompiledProgram.Literals. For MatchOneOf it's an index into
// CompiledProgram.TokenSets. For PushBacktrack it's the failure
// target the frame stores. For Call it's the subprogram entry.
// For OpenComposite and EmitLeaf* it's an index into
// CompiledProgram.SymbolMetadata.
internal readonly struct State
{
    public readonly LoweredOpCode OpCode;
    public readonly int Data;
    public readonly int OnSuccess;
    public readonly int OnFailure;

    public State(LoweredOpCode opCode, int data, int onSuccess, int onFailure)
    {
        OpCode = opCode;
        Data = data;
        OnSuccess = onSuccess;
        OnFailure = onFailure;
    }

    public const int HaltSuccess = -2;
    public const int HaltFailure = -1;
}
