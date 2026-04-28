using System;
using System.Collections.Generic;
using InductorParser.SyntaxTree;

namespace InductorParser.StateMachine;

// The result of Lowerer.Lower(rule). Immutable after construction.
// One CompiledProgram per Rule, cached on the Rule via
// StateMachineParser.GetOrBuild.
//
// The runtime reads States, Literals, RuneSets, and SymbolMetadata
// while the inner loop is running. Nothing else.
internal sealed class CompiledProgram
{
    public State[] States { get; }
    public string[] Literals { get; }
    public RuneSet[] RuneSets { get; }
    public SymbolMetadata[] SymbolMetadata { get; }
    public StringBodySpec[] StringBodySpecs { get; }
    public ScanSpec[] ScanSpecs { get; }
    public ScanAndPairSpec[] ScanAndPairSpecs { get; }
    public RuleStopperSpec[] RuleStopperSpecs { get; }
    public Rule[] BridgeRules { get; }
    // One 128-entry int[] per Or that uses the LoadPeekedRuneAndJumpAlt
    // opcode. Entry r holds the state index to dispatch to when the
    // peeked rune equals r (0..127). Built once at lowering time and
    // read on the hot path with one indexed load.
    public int[][] OrJumpTables { get; }
    public int EntryState { get; }
    public Rule RootRule { get; }

    // True when at least one state in this program can append to the
    // emission list (any OpenComposite / CloseComposite / EmitLeaf*
    // opcode, any fused-scan opcode whose ScanSpec emits leaves, any
    // BridgeToRecursive). False when the entire grammar reduces to
    // matching with no tree output. The Machine constructor uses
    // this to skip allocating an emission-list slot when emissions
    // aren't possible.
    public bool HasEmissions { get; }

    public CompiledProgram(
        State[] states,
        string[] literals,
        RuneSet[] runeSets,
        SymbolMetadata[] symbolMetadata,
        StringBodySpec[] stringBodySpecs,
        ScanSpec[] scanSpecs,
        ScanAndPairSpec[] scanAndPairSpecs,
        RuleStopperSpec[] ruleStopperSpecs,
        Rule[] bridgeRules,
        int[][] orJumpTables,
        int entryState,
        Rule rootRule,
        bool hasEmissions)
    {
        States = states;
        Literals = literals;
        RuneSets = runeSets;
        SymbolMetadata = symbolMetadata;
        StringBodySpecs = stringBodySpecs;
        ScanSpecs = scanSpecs;
        ScanAndPairSpecs = scanAndPairSpecs;
        RuleStopperSpecs = ruleStopperSpecs;
        BridgeRules = bridgeRules;
        OrJumpTables = orJumpTables;
        EntryState = entryState;
        RootRule = rootRule;
        HasEmissions = hasEmissions;
    }
}

// Spec for ScanUntilStopperEligibleRune. Carries the RuneSet index
// of the stopper rule's FirstConsumedRunes — runes outside this set
// cannot start a stopper match, so the scan can advance past them
// without calling the stopper subprogram.
internal readonly struct RuleStopperSpec
{
    public readonly int StopperFirstRunesIndex;

    public RuleStopperSpec(int stopperFirstRunesIndex)
    {
        StopperFirstRunesIndex = stopperFirstRunesIndex;
    }
}

// Spec for ScanLiteralOneOfRune. Carries the literal index for the
// And's left child (Token / Literal), the runeset index for the right
// child (OneOf), the loop bounds, and an optional error-metadata
// index. The fused opcode requires both children to be effectively
// Delete (no leaves emitted per iteration), which is the common case
// for "repeated separator-and-content" patterns used as zero-width
// counts.
internal readonly struct ScanAndPairSpec
{
    public readonly int LeftLiteralIndex;
    public readonly int RightRuneSetIndex;
    public readonly int AtLeast;
    public readonly int AtMost;
    public readonly int ErrorMetadataIndex;

    public ScanAndPairSpec(int leftLiteralIndex, int rightRuneSetIndex, int atLeast, int atMost, int errorMetadataIndex)
    {
        LeftLiteralIndex = leftLiteralIndex;
        RightRuneSetIndex = rightRuneSetIndex;
        AtLeast = atLeast;
        AtMost = atMost;
        ErrorMetadataIndex = errorMetadataIndex;
    }
}

// One entry per fused-scan opcode in the lowered program. Carries
// everything ScanOneOfRune / ScanNoneOfRune needs to run a tight
// inline scan: which RuneSet to test, how many iterations are
// allowed, whether to emit per-iteration leaves, and an optional
// error-message attribution slot for the BetweenInclusive's WithError.
internal readonly struct ScanSpec
{
    public readonly int RuneSetIndex;
    public readonly int AtLeast;
    public readonly int AtMost;
    public readonly int LeafMetadataIndex;
    public readonly int ErrorMetadataIndex;

    public ScanSpec(int runeSetIndex, int atLeast, int atMost, int leafMetadataIndex, int errorMetadataIndex)
    {
        RuneSetIndex = runeSetIndex;
        AtLeast = atLeast;
        AtMost = atMost;
        LeafMetadataIndex = leafMetadataIndex;
        ErrorMetadataIndex = errorMetadataIndex;
    }
}

// Per-StringBodyRule data referenced by the StringBodyScanFast opcode.
// Captures the stopper set, the optional single-rune escape start, and
// the entry state of the escape-end subprogram. The general-form
// stopper-as-rule and escape-start-as-rule paths are not yet supported
// in the state-machine evaluator (a TODO for a later iteration).
internal readonly struct StringBodySpec
{
    public readonly int StopperSetIndex;
    public readonly int EscapeStartRune;
    public readonly bool HasEscape;
    public readonly int EscapeEndEntry;

    public StringBodySpec(int stopperSetIndex, int escapeStartRune, bool hasEscape, int escapeEndEntry)
    {
        StopperSetIndex = stopperSetIndex;
        EscapeStartRune = escapeStartRune;
        HasEscape = hasEscape;
        EscapeEndEntry = escapeEndEntry;
    }
}

// Per-rule metadata referenced by Open / EmitLeaf opcodes through an
// index into CompiledProgram.SymbolMetadata. Carrying it as a struct
// in a flat array keeps the runtime path free of dictionary lookups
// and lets the JIT keep a base pointer in a register across the loop.
internal readonly struct SymbolMetadata
{
    public readonly SymbolId Id;
    public readonly FlattenType FlattenType;
    public readonly string? ErrorMessage;

    public SymbolMetadata(SymbolId id, FlattenType flattenType, string? errorMessage)
    {
        Id = id;
        FlattenType = flattenType;
        ErrorMessage = errorMessage;
    }
}
