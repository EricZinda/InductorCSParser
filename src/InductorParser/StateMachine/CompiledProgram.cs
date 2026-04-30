using System;
using System.Collections.Generic;
using InductorParser.Lexing;
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
    public ScanUntilSpec[] ScanUntilSpecs { get; }
    public ScanSpec[] ScanSpecs { get; }
    public ScanAndPairSpec[] ScanAndPairSpecs { get; }
    public RuleStopperSpec[] RuleStopperSpecs { get; }
    public ScannerSkipSpec[] ScannerSkipSpecs { get; }
    public Rule[] BridgeRules { get; }

    // Per-subprogram-entry rule lookup, keyed by the state index a Call
    // / CallSuppressOutputs opcode jumps to (state.Data on those
    // opcodes). Populated by the lowerer for every cyclic rule and for
    // ScanUntil's escape-end / rule-stopper subprograms. Read by the
    // Stepper on Call to label trace output, and stashed on the
    // CallFrame so ReturnSuccess / ReturnFailure can label the matching
    // exit line. The cost is one dictionary lookup per Call (off the
    // tightest hot path because Call only fires at cyclic-rule entry,
    // not for inlined rules).
    public Dictionary<int, Rule> SubprogramRuleByEntry { get; }
    // One 128-entry int[] per FirstOf that uses the
    // LoadPeekedRuneAndJumpAlt opcode. Entry r holds the state index
    // to dispatch to when the peeked rune equals r (0..127). Built
    // once at lowering time and read on the hot path with one
    // indexed load.
    public int[][] OrJumpTables { get; }
    public int EntryState { get; }
    public Rule RootRule { get; }

    // True when at least one state in this program can append to the
    // output list (any OpenComposite / CloseComposite / EmitLeaf*
    // opcode, any fused-scan opcode whose ScanSpec emits leaves, any
    // BridgeToRecursive). False when the entire grammar reduces to
    // matching with no tree output. The Machine constructor uses
    // this to skip allocating an output-list slot when outputs
    // aren't possible.
    public bool HasOutputs { get; }

    public CompiledProgram(
        State[] states,
        string[] literals,
        RuneSet[] runeSets,
        SymbolMetadata[] symbolMetadata,
        ScanUntilSpec[] stringBodySpecs,
        ScanSpec[] scanSpecs,
        ScanAndPairSpec[] scanAndPairSpecs,
        RuleStopperSpec[] ruleStopperSpecs,
        ScannerSkipSpec[] scannerSkipSpecs,
        Rule[] bridgeRules,
        int[][] orJumpTables,
        Dictionary<int, Rule> subprogramRuleByEntry,
        int entryState,
        Rule rootRule,
        bool hasOutputs)
    {
        States = states;
        Literals = literals;
        RuneSets = runeSets;
        SymbolMetadata = symbolMetadata;
        ScanUntilSpecs = stringBodySpecs;
        ScanSpecs = scanSpecs;
        ScanAndPairSpecs = scanAndPairSpecs;
        RuleStopperSpecs = ruleStopperSpecs;
        ScannerSkipSpecs = scannerSkipSpecs;
        BridgeRules = bridgeRules;
        OrJumpTables = orJumpTables;
        SubprogramRuleByEntry = subprogramRuleByEntry;
        EntryState = entryState;
        RootRule = rootRule;
        HasOutputs = hasOutputs;
    }
}

// Spec for ScannerSkipAdvance, the per-iteration bulk skip used at the
// top of a ZeroOrMore(FirstOf(match..., AnyToken.Delete)) scanner loop.
// At each iteration the opcode advances the lexer to the next position
// where one of the candidate matches could plausibly start, so the
// inner FirstOf doesn't waste an attempt + fail-over to the deleted
// AnyToken on every non-candidate rune. Mirrors the recursive
// evaluator's ScannerSkip in BetweenInclusiveRule. Inert by
// construction when the lowerer doesn't recognize the shape (the
// opcode just isn't emitted), so the syntax tree never changes.
//
// The rune-set + BMP-char prefilter is universally safe for any inner
// shape that matches via FirstConsumedRunes. The literal payload is
// the stronger prefilter that fires when every alternative is a
// (possibly nested) literal: the scanner walks straight to the next
// full-literal candidate via BCL string search instead of stopping at
// every matching first rune.
internal readonly struct ScannerSkipSpec
{
    public readonly int CandidatesRuneSetIndex;
    public readonly char[]? BmpCandidates;
    public readonly LiteralScannerCandidate[]? Literals;
    public readonly bool UseLiteralPositionsCache;

    public ScannerSkipSpec(
        int candidatesRuneSetIndex,
        char[]? bmpCandidates,
        LiteralScannerCandidate[]? literals,
        bool useLiteralPositionsCache)
    {
        CandidatesRuneSetIndex = candidatesRuneSetIndex;
        BmpCandidates = bmpCandidates;
        Literals = literals;
        UseLiteralPositionsCache = useLiteralPositionsCache;
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
// AllOf's left child (Token / Literal), the runeset index for the right
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

// Per-ScanUntilRule data referenced by the ScanUntilFast opcode.
// Captures the stopper set, the optional single-rune escape start, and
// the entry state of the escape-end subprogram. The general-form
// stopper-as-rule path is handled by the rule-stoppered scan opcode
// (ScanUntilStopperEligibleRune) and the escape-start-as-rule path
// still bridges to the recursive evaluator.
internal readonly struct ScanUntilSpec
{
    public readonly int StopperSetIndex;
    public readonly int EscapeStartRune;
    public readonly bool HasEscape;
    public readonly int EscapeEndEntry;

    public ScanUntilSpec(int stopperSetIndex, int escapeStartRune, bool hasEscape, int escapeEndEntry)
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
