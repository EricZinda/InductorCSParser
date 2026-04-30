using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser.StateMachine;

// Walks an existing Rule tree (after Rule.Compile has run) and produces
// a flat CompiledProgram the Stepper can run. Two passes:
//
//   1. CycleDetect finds rules that participate in a cycle. Those are
//      lowered as subprograms with a Call/Return shape so a cyclic
//      reference can re-enter the same code without infinite inlining.
//
//   2. LowerRule walks the graph from the root, inlining acyclic rules
//      and emitting Call states for cyclic ones. Each call to
//      LowerRule writes states to the context and returns the index of
//      the entry state for that rule's lowered chunk. The caller passes
//      onSuccess and onFailure indices saying where to jump after the
//      rule completes one way or the other.
internal static class Lowerer
{
    public static CompiledProgram Lower(Rule rootRule, bool preserveAllSymbols = false, InputUnit inputUnit = InputUnit.Grapheme)
    {
        rootRule.Compile();
        var context = new LoweringContext(rootRule, preserveAllSymbols, inputUnit);

        // Cycle pre-pass.
        var onStack = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        DetectCycles(rootRule, onStack, visited, context.CyclicRules);

        // Main pass: lower the root with HaltSuccess / HaltFailure as
        // its terminators. The root's entry state is the program entry.
        int rootEntry = context.LowerRule(rootRule, State.HaltSuccess, State.HaltFailure);

        // Walk the lowered states once to decide whether any opcode
        // can append to the output list. A grammar with all-Delete
        // leaves and Flatten-or-Delete composites lowers to a program
        // with no emit-style opcodes, so the runtime can skip
        // allocating an output-list slot on the Machine struct.
        bool hasOutputs = ProgramHasOutputs(context.States, context.ScanSpecs);

        // Invert SubprogramEntries (Rule -> entry state index) into the
        // entry-state-index -> Rule lookup the Stepper reads on Call /
        // CallSuppressOutputs to label trace lines.
        var subprogramRuleByEntry = new Dictionary<int, Rule>(context.SubprogramEntries.Count);
        foreach (var entryByRule in context.SubprogramEntries)
            subprogramRuleByEntry[entryByRule.Value] = entryByRule.Key;

        return new CompiledProgram(
            context.States.ToArray(),
            context.Literals.ToArray(),
            context.RuneSets.ToArray(),
            context.SymbolMetadata.ToArray(),
            context.ScanUntilSpecs.ToArray(),
            context.ScanSpecs.ToArray(),
            context.ScanAndPairSpecs.ToArray(),
            context.RuleStopperSpecs.ToArray(),
            context.ScannerSkipSpecs.ToArray(),
            context.BridgeRules.ToArray(),
            context.OrJumpTables.ToArray(),
            subprogramRuleByEntry,
            rootEntry,
            rootRule,
            hasOutputs);
    }

    private static bool ProgramHasOutputs(IReadOnlyList<State> states, IReadOnlyList<ScanSpec> scanSpecs)
    {
        for (int i = 0; i < states.Count; i++)
        {
            var op = states[i].OpCode;
            switch (op)
            {
                case LoweredOpCode.OpenComposite:
                case LoweredOpCode.CloseComposite:
                case LoweredOpCode.EmitLeafLiteral:
                case LoweredOpCode.EmitLeafOneOf:
                case LoweredOpCode.EmitScanUntilLeaf:
                case LoweredOpCode.BridgeToRecursive:
                    return true;
                case LoweredOpCode.ScanOneOfRune:
                case LoweredOpCode.ScanNoneOfRune:
                case LoweredOpCode.ScanAnyTokenRune:
                    // Fused-scan opcodes emit per-rune leaves only
                    // when their ScanSpec carries a non-negative
                    // metadata index. matcher-mode lowering passes -1.
                    if (scanSpecs[states[i].Data].LeafMetadataIndex >= 0)
                        return true;
                    break;
            }
        }
        return false;
    }

    private static void DetectCycles(Rule rule, HashSet<Rule> onStack, HashSet<Rule> visited, HashSet<Rule> cyclic)
    {
        // Resolve LateBoundRule transparently: cycles run through the
        // target. Without this step a self-referential LateBound looks
        // acyclic because the LateBound itself has the target as its
        // single child but isn't on the stack from the target's side.
        Rule effective = rule is LateBoundRule lb ? UnwrapLateBound(lb) : rule;

        if (onStack.Contains(effective))
        {
            cyclic.Add(effective);
            return;
        }
        if (!visited.Add(effective)) return;

        onStack.Add(effective);
        foreach (var child in effective.Children)
            DetectCycles(child, onStack, visited, cyclic);
        onStack.Remove(effective);
    }

    private static Rule UnwrapLateBound(LateBoundRule lb)
    {
        Rule current = lb;
        // Defensive: walk through chained LateBound -> LateBound -> ...
        // until we hit a non-LateBound. Compile validates that each
        // LateBound has a target, so children[0] is always non-null.
        while (current is LateBoundRule innerLb)
            current = innerLb.Children[0];
        return current;
    }
}

internal sealed class LoweringContext
{
    public readonly List<State> States = new();
    public readonly List<string> Literals = new();
    public readonly List<RuneSet> RuneSets = new();
    public readonly List<SymbolMetadata> SymbolMetadata = new();
    public readonly List<ScanUntilSpec> ScanUntilSpecs = new();
    public readonly List<ScanSpec> ScanSpecs = new();
    public readonly List<ScanAndPairSpec> ScanAndPairSpecs = new();
    public readonly List<RuleStopperSpec> RuleStopperSpecs = new();
    public readonly List<ScannerSkipSpec> ScannerSkipSpecs = new();
    public readonly List<Rule> BridgeRules = new();
    public readonly List<int[]> OrJumpTables = new();
    public readonly HashSet<Rule> CyclicRules = new(ReferenceComparer<Rule>.Instance);
    public readonly Rule RootRule;
    public readonly bool PreserveAllSymbols;
    public readonly InputUnit InputUnit;

    // For cyclic rules: the index of the "shared" entry that callers
    // Call into. Allocated lazily the first time a cyclic rule is
    // lowered. Subsequent encounters Call this index.
    public readonly Dictionary<Rule, int> SubprogramEntries = new(ReferenceComparer<Rule>.Instance);

    // Dedup tables. Same literal text or same RuneSet appearing in
    // multiple rules shares one slot in the runtime table. Keeps the
    // tables small and improves CPU cache behavior.
    private readonly Dictionary<string, int> _literalIndex = new();
    private readonly Dictionary<RuneSet, int> _runeSetIndex = new();

    public LoweringContext(Rule rootRule, bool preserveAllSymbols, InputUnit inputUnit)
    {
        RootRule = rootRule;
        PreserveAllSymbols = preserveAllSymbols;
        InputUnit = inputUnit;
    }

    // Resolve the FlattenType the rule's outputs actually contribute
    // under. PreserveAllSymbols promotes everything to Preserve. The
    // output-skip optimizations below check this rather than
    // rule.FlattenType directly so they don't accidentally drop
    // outputs a debug parse needs.
    private FlattenType ResolveEffective(FlattenType declared) =>
        PreserveAllSymbols ? FlattenType.Preserve : declared;

    public int LowerRule(Rule rule, int onSuccess, int onFailure)
    {
        // LateBoundRule is transparent at parse time. Lower its target
        // directly. If the target is itself part of a cycle this falls
        // through to the cyclic-rule path below.
        if (rule is LateBoundRule lb)
            rule = UnwrapForLowering(lb);

        if (CyclicRules.Contains(rule))
            return LowerCyclicCall(rule, onSuccess, onFailure);

        return rule switch
        {
            LiteralRule literal => LowerLiteral(literal, onSuccess, onFailure),
            LiteralIgnoreAsciiCaseRule literalIc => LowerLiteralIgnoreAsciiCase(literalIc, onSuccess, onFailure),
            TokenRule token => LowerToken(token, onSuccess, onFailure),
            OneOfRule oneOf => LowerOneOf(oneOf, onSuccess, onFailure),
            NoneOfRule noneOf => LowerNoneOf(noneOf, onSuccess, onFailure),
            AnyTokenRule anyToken => LowerAnyToken(anyToken, onSuccess, onFailure),
            EofRule eof => LowerEof(eof, onSuccess, onFailure),
            AllOfRule allOf => LowerAllOf(allOf, onSuccess, onFailure),
            FirstOfRule firstOf => LowerFirstOf(firstOf, onSuccess, onFailure),
            BetweenInclusiveRule between => LowerBetween(between, onSuccess, onFailure),
            NotRule not => LowerNot(not, onSuccess, onFailure),
            PeekRule peek => LowerPeek(peek, onSuccess, onFailure),
            ScanUntilRule scanUntil => LowerScanUntil(scanUntil, onSuccess, onFailure),
            _ => LowerViaBridge(rule, onSuccess, onFailure)
        };
    }

    private static Rule UnwrapForLowering(LateBoundRule lb)
    {
        Rule current = lb;
        while (current is LateBoundRule innerLb)
            current = innerLb.Children[0];
        return current;
    }

    private int LowerCyclicCall(Rule rule, int onSuccess, int onFailure)
    {
        int subprogramEntry = GetOrCreateSubprogram(rule);
        return AddState(LoweredOpCode.Call, subprogramEntry, onSuccess, onFailure);
    }

    private int LowerRuleBody(Rule rule, int onSuccess, int onFailure)
    {
        return rule switch
        {
            LiteralRule literal => LowerLiteral(literal, onSuccess, onFailure),
            LiteralIgnoreAsciiCaseRule literalIc => LowerLiteralIgnoreAsciiCase(literalIc, onSuccess, onFailure),
            TokenRule token => LowerToken(token, onSuccess, onFailure),
            OneOfRule oneOf => LowerOneOf(oneOf, onSuccess, onFailure),
            NoneOfRule noneOf => LowerNoneOf(noneOf, onSuccess, onFailure),
            AnyTokenRule anyToken => LowerAnyToken(anyToken, onSuccess, onFailure),
            EofRule eof => LowerEof(eof, onSuccess, onFailure),
            AllOfRule allOf => LowerAllOf(allOf, onSuccess, onFailure),
            FirstOfRule firstOf => LowerFirstOf(firstOf, onSuccess, onFailure),
            BetweenInclusiveRule between => LowerBetween(between, onSuccess, onFailure),
            NotRule not => LowerNot(not, onSuccess, onFailure),
            PeekRule peek => LowerPeek(peek, onSuccess, onFailure),
            ScanUntilRule scanUntil => LowerScanUntil(scanUntil, onSuccess, onFailure),
            _ => LowerViaBridge(rule, onSuccess, onFailure)
        };
    }

    // Both Token and Literal funnel into the same MatchLiteral opcode.
    // Their match logic in the existing code is identical: read tokens
    // until the expected string is consumed, fail on first mismatch.
    // The construction-time validation differs (Token requires one
    // grapheme; Literal accepts any non-empty string) but the runtime
    // semantics line up.
    private int LowerLiteral(LiteralRule rule, int onSuccess, int onFailure)
    {
        return LowerLiteralLike(rule, GetLiteralExpected(rule), onSuccess, onFailure);
    }

    private int LowerToken(TokenRule rule, int onSuccess, int onFailure)
    {
        return LowerLiteralLike(rule, GetTokenExpected(rule), onSuccess, onFailure);
    }

    private int LowerLiteralLike(Rule rule, string expected, int onSuccess, int onFailure)
    {
        int literalIndex = InternLiteral(expected);

        // Allocate metadata if either an output or a WithError
        // message needs it. Match opcode's state.Data packs both:
        // low 16 = literalIndex, high 16 = metadataIndex (or 0xFFFF
        // for "no message"). Same packing on the matching EmitLeaf.
        var effective = ResolveEffective(rule.FlattenType);
        bool needMetadataForEmit = effective != FlattenType.Delete;
        bool needMetadataForError = rule.ErrorMessage != null;
        int metadataIndex = (needMetadataForEmit || needMetadataForError)
            ? AddSymbolMetadata(rule)
            : NoErrorMetadataSentinel;

        int afterMatch = onSuccess;
        if (needMetadataForEmit)
        {
            int packed = literalIndex | (metadataIndex << 16);
            afterMatch = AddState(LoweredOpCode.EmitLeafLiteral, packed, onSuccess, onSuccess);
        }
        int matchPacked = literalIndex | (metadataIndex << 16);
        var matchOpcode = InputUnit == InputUnit.Rune
            ? LoweredOpCode.MatchLiteralRune
            : LoweredOpCode.MatchLiteral;
        return AddState(matchOpcode, matchPacked, afterMatch, onFailure);
    }

    private int LowerOneOf(OneOfRule rule, int onSuccess, int onFailure)
    {
        int runeSetIndex = InternRuneSet(GetOneOfSet(rule));
        var opcode = InputUnit == InputUnit.Rune
            ? LoweredOpCode.MatchOneOfRune
            : LoweredOpCode.MatchOneOf;
        return EmitTokenMatch(rule, opcode, runeSetIndex, onSuccess, onFailure);
    }

    private int LowerNoneOf(NoneOfRule rule, int onSuccess, int onFailure)
    {
        int runeSetIndex = InternRuneSet(rule.LoweringSet);
        var opcode = InputUnit == InputUnit.Rune
            ? LoweredOpCode.MatchNoneOfRune
            : LoweredOpCode.MatchNoneOf;
        return EmitTokenMatch(rule, opcode, runeSetIndex, onSuccess, onFailure);
    }

    private int LowerAnyToken(AnyTokenRule rule, int onSuccess, int onFailure)
    {
        // MatchAnyToken takes no payload, so the low 16 bits are 0.
        // The high 16 bits still carry the optional error-metadata
        // index for WithError.
        var opcode = InputUnit == InputUnit.Rune
            ? LoweredOpCode.MatchAnyTokenRune
            : LoweredOpCode.MatchAnyToken;
        return EmitTokenMatch(rule, opcode, 0, onSuccess, onFailure);
    }

    // Shared helper for the per-token rules (OneOf / NoneOf / AnyToken)
    // that all use EmitLeafOneOf to produce their leaf.
    private int EmitTokenMatch(Rule rule, LoweredOpCode matchOpcode, int payloadIndex, int onSuccess, int onFailure)
    {
        var effective = ResolveEffective(rule.FlattenType);
        bool needMetadataForEmit = effective != FlattenType.Delete;
        bool needMetadataForError = rule.ErrorMessage != null;
        int metadataIndex = (needMetadataForEmit || needMetadataForError)
            ? AddSymbolMetadata(rule)
            : NoErrorMetadataSentinel;

        int afterMatch = onSuccess;
        if (needMetadataForEmit)
            afterMatch = AddState(LoweredOpCode.EmitLeafOneOf, metadataIndex, onSuccess, onSuccess);

        int matchPacked = (payloadIndex & 0xFFFF) | (metadataIndex << 16);
        return AddState(matchOpcode, matchPacked, afterMatch, onFailure);
    }

    private int LowerLiteralIgnoreAsciiCase(LiteralIgnoreAsciiCaseRule rule, int onSuccess, int onFailure)
    {
        int literalIndex = InternLiteral(rule.LoweringExpected);
        var effective = ResolveEffective(rule.FlattenType);
        bool needMetadataForEmit = effective != FlattenType.Delete;
        bool needMetadataForError = rule.ErrorMessage != null;
        int metadataIndex = (needMetadataForEmit || needMetadataForError)
            ? AddSymbolMetadata(rule)
            : NoErrorMetadataSentinel;

        int afterMatch = onSuccess;
        if (needMetadataForEmit)
        {
            int packed = literalIndex | (metadataIndex << 16);
            afterMatch = AddState(LoweredOpCode.EmitLeafLiteral, packed, onSuccess, onSuccess);
        }
        int matchPacked = literalIndex | (metadataIndex << 16);
        var matchOpcode = InputUnit == InputUnit.Rune
            ? LoweredOpCode.MatchLiteralIgnoreAsciiCaseRune
            : LoweredOpCode.MatchLiteralIgnoreAsciiCase;
        return AddState(matchOpcode, matchPacked, afterMatch, onFailure);
    }

    // Sentinel matching Stepper.NoErrorMetadata — the high-16-bit value
    // a Match opcode reads when the rule had no .WithError("...") and
    // no FlattenType-driven metadata entry.
    private const int NoErrorMetadataSentinel = 0xFFFF;

    private int LowerEof(EofRule rule, int onSuccess, int onFailure)
    {
        // Eof is zero-width. Only Preserve produces a wrapper Symbol.
        // Delete (default) and Flatten produce nothing, so skip the
        // Open/Close states entirely on those paths. Allocate a
        // metadata entry only if needed for either the wrapper or a
        // WithError message.
        var effective = ResolveEffective(rule.FlattenType);
        bool needMetadataForWrap = effective == FlattenType.Preserve;
        bool needMetadataForError = rule.ErrorMessage != null;
        int metadataIndex = (needMetadataForWrap || needMetadataForError)
            ? AddSymbolMetadata(rule)
            : NoErrorMetadataSentinel;

        int afterMatch = onSuccess;
        if (needMetadataForWrap)
        {
            int closeIdx = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
            afterMatch = AddState(LoweredOpCode.OpenComposite, metadataIndex, closeIdx, closeIdx);
        }
        // MatchEof takes no payload, so the low 16 bits are 0.
        int matchPacked = metadataIndex << 16;
        return AddState(LoweredOpCode.MatchEof, matchPacked, afterMatch, onFailure);
    }

    private int LowerAllOf(AllOfRule rule, int onSuccess, int onFailure)
    {
        // Skip Open/Close for Flatten composites: children flow into
        // the enclosing Preserve naturally without a wrapper, and
        // TreeBuilder treats the missing wrapper the same as a Flatten
        // wrapper. Keep Open/Close for Delete and Preserve.
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int metadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            metadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        int next = compositeAfter;
        for (int childIndex = rule.Children.Count - 1; childIndex >= 0; childIndex--)
        {
            next = LowerRule(rule.Children[childIndex], next, onFailure);
        }

        if (effective != FlattenType.Flatten)
            next = AddState(LoweredOpCode.OpenComposite, metadataIndex, next, onFailure);
        return next;
    }

    private int LowerFirstOf(FirstOfRule rule, int onSuccess, int onFailure)
    {
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int metadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            metadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        // The "all alternatives failed" handler. Each PushBacktrack at
        // the LAST alternative routes its frame's failure target here
        // (via the FailRestore that pops it). Earlier alternatives
        // route to handlers that continue trying.
        int outerFailRestore = AddState(LoweredOpCode.FailRestore, 0, onFailure, onFailure);

        // Decide whether this FirstOf benefits from the first-rune-skip
        // optimization. We need at least one alternative whose
        // FirstConsumedRunes is non-trivial (Advance.Always and
        // strictly smaller than Universe), and skipping has to be
        // safe under the existing semantics: alternatives carrying
        // a custom WithError still need to run so their message can
        // surface.
        bool anyCanSkip = false;
        for (int i = 0; i < rule.Children.Count; i++)
        {
            if (CanSkipUnreachableAlt(rule.Children[i]))
            {
                anyCanSkip = true;
                break;
            }
        }

        // Lower alternatives in reverse so each alt's failure target
        // is known when we lower it. Two failure-target tracks here:
        //   * nextFailHandlerWithPop: where the inner rule's failure
        //     should jump. A frame was pushed for this alt, so the
        //     handler pops it (FailRestore) before continuing.
        //   * nextAltStartWithoutPop: where a peek-skip miss should
        //     jump. The peek check runs BEFORE the alt's PushBacktrack,
        //     so no frame to pop. Routes straight to the next
        //     alternative's start (or onFailure for the last alt).
        int nextFailHandlerWithPop = outerFailRestore;
        int nextAltStartWithoutPop = onFailure;
        int firstAltEntry = -1;

        // Per-alt records for the ASCII jump table. Filled in
        // reverse-priority order (last alt first); reversed after the
        // lowering loop so we can walk in priority order while filling
        // the table.
        var altRecords = anyCanSkip
            ? new List<(Rule child, int pushIdx, bool skipEligible)>(rule.Children.Count)
            : null;

        for (int altIndex = rule.Children.Count - 1; altIndex >= 0; altIndex--)
        {
            Rule child = rule.Children[altIndex];
            int jumpToEnd = AddState(LoweredOpCode.Jump, 0, compositeAfter, compositeAfter);
            int popIdx = AddState(LoweredOpCode.PopBacktrack, 0, jumpToEnd, jumpToEnd);
            int altEntry = LowerRule(child, popIdx, nextFailHandlerWithPop);
            int pushIdx = AddState(LoweredOpCode.PushBacktrack, 0, altEntry, altEntry);

            // Prefix the alternative's PushBacktrack with a peek check
            // when this child can't match a peeked rune outside its
            // FirstConsumedRunes set. The check skips the
            // PushBacktrack/inner-attempt entirely on a mismatch and
            // routes straight to the next alternative's start (no
            // frame to pop because we never pushed one).
            bool skipEligible = anyCanSkip && CanSkipUnreachableAlt(child);
            int altStart = pushIdx;
            if (skipEligible)
            {
                int runeSetIdx = InternRuneSet(child.FirstConsumedRunes);
                altStart = AddState(LoweredOpCode.CheckPeekedRuneInSet, runeSetIdx, pushIdx, nextAltStartWithoutPop);
            }

            altRecords?.Add((child, pushIdx, skipEligible));

            firstAltEntry = altStart;
            // For the PRECEDING alternative (next iteration of this
            // reverse loop), set up its two failure targets:
            //   * its inner-failure handler is a fresh FailRestore that
            //     pops the preceding alt's frame and jumps to THIS
            //     alt's start (which may be a peek check or PushBacktrack)
            //   * its peek-skip target is THIS alt's start directly
            if (altIndex > 0)
            {
                nextFailHandlerWithPop = AddState(LoweredOpCode.FailRestore, 0, altStart, altStart);
                nextAltStartWithoutPop = altStart;
            }
        }

        // Prefix with one peek-and-dispatch state when at least one alt
        // benefits from the skip. The ASCII jump table maps the peeked
        // rune (0..127) to the first eligible alt's PushBacktrack
        // index, bypassing the leading CheckPeekedRuneInSet chain
        // entirely on the common ASCII path. For non-ASCII / EOF the
        // opcode falls through to the chain head, where the existing
        // CheckPeekedRuneInSet logic handles the alternatives whose
        // first sets contain non-ASCII runes.
        int orEntry = firstAltEntry;
        if (anyCanSkip)
        {
            int tableIndex = BuildOrJumpTable(altRecords!, onFailure);
            orEntry = AddState(LoweredOpCode.LoadPeekedRuneAndJumpAlt, tableIndex, firstAltEntry, firstAltEntry);
        }

        if (effective != FlattenType.Flatten)
            return AddState(LoweredOpCode.OpenComposite, metadataIndex, orEntry, onFailure);
        return orEntry;
    }

    // Whether this alternative could be safely skipped on a peeked-rune
    // mismatch. Mirrors FirstOfRule's runtime guard: only skip when the
    // child Always advances (so its first rune is guaranteed to be
    // consumed) AND has a strictly tighter FirstConsumedRunes than the
    // universe. Custom WithError alternatives are NOT skipped because
    // the existing code lets them run so their error message can reach
    // DeepestFailureMessage on a parse failure.
    private static bool CanSkipUnreachableAlt(Rule child)
    {
        if (child.Advance != Advance.Always) return false;
        if (child.ErrorMessage != null) return false;
        // FirstConsumedRunes equality with Universe means the set
        // accepts any rune, so the peek check would never skip. Avoid
        // the wasted state.
        if (child.FirstConsumedRunes.Equals(RuneSet.Universe)) return false;
        return true;
    }

    // Build the 128-entry ASCII jump table for a FirstOf that uses
    // first-rune-skip. altRecords is filled in reverse-priority order
    // by the reverse-lowering loop, so we walk it tail-to-head to
    // restore priority order. For each ASCII rune r:
    //   * Walk alts in priority order. For each alt:
    //     * skipEligible alt: if its FirstConsumedRunes contains r,
    //       this alt wins (table[r] = pushIdx). Else skip.
    //     * non-skipEligible alt: the chain would attempt this alt
    //       unconditionally, so it wins for any rune that hasn't
    //       already been claimed by an earlier alt.
    //   * If no alt wins, table[r] = onFailure (the chain's
    //     all-skipped tail target).
    private int BuildOrJumpTable(List<(Rule child, int pushIdx, bool skipEligible)> altRecords, int onFailure)
    {
        int[] table = new int[128];
        for (int rune = 0; rune < 128; rune++)
            table[rune] = onFailure;

        // altRecords is reverse-priority. Walk forward through indices
        // count-1 down to 0 so we visit alts in priority order.
        for (int rune = 0; rune < 128; rune++)
        {
            for (int recordIndex = altRecords.Count - 1; recordIndex >= 0; recordIndex--)
            {
                var record = altRecords[recordIndex];
                if (record.skipEligible)
                {
                    if (record.child.FirstConsumedRunes.Contains(rune))
                    {
                        table[rune] = record.pushIdx;
                        break;
                    }
                }
                else
                {
                    // The chain would attempt this alt no matter what,
                    // so it claims any rune not already won by an
                    // earlier skip-eligible alt.
                    table[rune] = record.pushIdx;
                    break;
                }
            }
        }

        int tableIndex = OrJumpTables.Count;
        OrJumpTables.Add(table);
        return tableIndex;
    }

    private int LowerBetween(BetweenInclusiveRule rule, int onSuccess, int onFailure)
    {
        // BetweenInclusive(_, 0, _) is a degenerate "match nothing"
        // rule: BetweenInclusive's constructor requires
        // atMost >= atLeast, so atMost == 0 implies atLeast == 0,
        // which is the existing rule's no-op success path. Skip the
        // loop machinery entirely and emit only the wrapper Symbol if
        // Preserve.
        if (rule.AtMost == 0)
        {
            var effectiveZeroMost = ResolveEffective(rule.FlattenType);
            if (effectiveZeroMost == FlattenType.Preserve)
            {
                int wrapperMeta = AddSymbolMetadata(rule);
                int closeIdx = AddState(LoweredOpCode.CloseComposite, wrapperMeta, onSuccess, onSuccess);
                return AddState(LoweredOpCode.OpenComposite, wrapperMeta, closeIdx, closeIdx);
            }
            return onSuccess;
        }

        // Scanner-shape skip: ZeroOrMore(FirstOf(match..., AnyToken.Delete)).
        // When the inner is a FirstOf whose last alternative is a deleted
        // AnyToken, non-matching input would just be consumed one rune at
        // a time. The scanner-skip opcode jumps straight to the next
        // candidate first-rune (or, for literal-only alternatives, to the
        // next position where the literal text could match) so the inner
        // FirstOf isn't attempted at every non-candidate position. Mirrors
        // the recursive evaluator's ScannerSkip in BetweenInclusiveRule.
        if (TryLowerBetweenScanner(rule, onSuccess, onFailure, out int scannerEntry))
            return scannerEntry;

        // Even faster path when the inner is OneOf or NoneOf and the
        // input unit is Rune: emit a single fused-scan opcode that
        // walks runes in one tight inline loop instead of a multi-state
        // per-iteration loop. Saves the per-iteration PushBacktrack,
        // the per-iteration BetweenIncrementCheckMax, and the per-rune
        // Lexer.Read call. Rune-only because grapheme tokens can be
        // multi-rune and the inline rune decode would split them.
        Rule inner = rule.Children[0];
        if (InputUnit == InputUnit.Rune && CanFuseScan(rule, inner))
        {
            if (inner is OneOfRule oneOfInner)
                return LowerBetweenScan(rule, oneOfInner, oneOfInner.LoweringSet, LoweredOpCode.ScanOneOfRune, onSuccess, onFailure);
            if (inner is NoneOfRule noneOfInner)
                return LowerBetweenScan(rule, noneOfInner, noneOfInner.LoweringSet, LoweredOpCode.ScanNoneOfRune, onSuccess, onFailure);
            if (inner is AnyTokenRule anyTokenInner)
                return LowerBetweenScanAnyToken(rule, anyTokenInner, onSuccess, onFailure);
        }

        // AllOf-pair fast path: BetweenInclusive(min, max, AllOf(Literal, OneOf))
        // where both AllOf children are effectively Delete. Common in
        // separator-and-content scans like HrSpaced's
        // AtLeast(2, AllOf(Token(' '), OneOf("-*+"))).
        if (InputUnit == InputUnit.Rune
            && TryLowerBetweenScanLiteralOneOfRune(rule, inner, onSuccess, onFailure, out int fusedEntry))
        {
            return fusedEntry;
        }

        // Optional fast path: BetweenInclusive(0, 1, inner). Drops the
        // PushBetween/PopIterationCheck/BetweenExitCheckMin trio and
        // lowers to the minimal Push/Pop pair plus a FailRestore. The
        // generic loop path runs five wrapper opcodes per attempt; the
        // Optional shape runs two. Big win on Optional(FirstOf(...)) and
        // Optional(LiteralIgnoreAsciiCase(...)) shapes that don't qualify
        // for the scan paths or the atomic-inner shape.
        if (rule.AtLeast == 0 && rule.AtMost == 1 && rule.ErrorMessage == null)
            return LowerOptional(rule, inner, onSuccess, onFailure);

        // Pick the atomic-inner fast path when the inner is one of the
        // always-advancing single-state matches. Drops the per-iteration
        // PushBacktrack and the zero-width guard, which together
        // dominate the inner-loop cost on tight repetition grammars.
        if (IsAtomicAdvancingInner(inner))
            return LowerBetweenAtomic(rule, inner, onSuccess, onFailure);

        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int metadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            metadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        // BetweenExitCheckMin: pops the Between frame, succeeds if
        // counter >= atLeast, otherwise restores to entry-time state
        // and routes to onFailure.
        int exitCheckMin = AddState(LoweredOpCode.BetweenExitCheckMin, 0, compositeAfter, onFailure);

        // Per-iteration FailRestore: when inner fails, pops the
        // per-iteration backtrack frame and continues at exitCheckMin.
        int exitViaFailRestore = AddState(LoweredOpCode.FailRestore, 0, exitCheckMin, exitCheckMin);

        // PopIterationCheck: needs to know loopStart's index, but
        // loopStart wraps the inner rule which we lower below. Reserve
        // its slot.
        int popIterCheck = ReserveState();

        // Lower inner with onSuccess = popIterCheck, onFailure = exitViaFailRestore
        int innerEntry = LowerRule(rule.Children[0], popIterCheck, exitViaFailRestore);

        // loopStart: pushes the per-iteration backtrack frame and jumps
        // to inner's entry.
        int loopStart = AddState(LoweredOpCode.PushBacktrack, 0, innerEntry, innerEntry);

        FillState(popIterCheck, LoweredOpCode.PopIterationCheck, 0, loopStart, exitCheckMin);

        // PushBetween: holds entry-time lexer position, emit cursor,
        // and the bounds. The atLeast / atMost are packed into Data.
        int packedBounds = PackBetweenBounds(rule.AtLeast, rule.AtMost);
        int pushBetween = AddState(LoweredOpCode.PushBetween, packedBounds, loopStart, onFailure);

        if (effective != FlattenType.Flatten)
            return AddState(LoweredOpCode.OpenComposite, metadataIndex, pushBetween, onFailure);
        return pushBetween;
    }

    // Atomic-inner fast path. Inner is one of LiteralRule / TokenRule /
    // OneOfRule, all of which (a) are atomic on failure (the match
    // restores its own lexer state) and (b) always advance on success.
    // Both properties together let the loop drop its per-iteration
    // PushBacktrack frame and its zero-width guard. The remaining work
    // per iteration is just inner + counter increment + bounds check.
    private int LowerBetweenAtomic(BetweenInclusiveRule rule, Rule inner, int onSuccess, int onFailure)
    {
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int metadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            metadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        int exitCheckMin = AddState(LoweredOpCode.BetweenExitCheckMin, 0, compositeAfter, onFailure);

        // BetweenIncrementCheckMax needs loopStart (= innerEntry) as its
        // OnSuccess, but we can't lower inner until incrementCheck has
        // a slot. Reserve the slot first.
        int incrementCheck = ReserveState();
        int innerEntry = LowerRule(inner, incrementCheck, exitCheckMin);
        FillState(incrementCheck, LoweredOpCode.BetweenIncrementCheckMax, 0, innerEntry, exitCheckMin);

        int packedBounds = PackBetweenBounds(rule.AtLeast, rule.AtMost);
        int pushBetween = AddState(LoweredOpCode.PushBetween, packedBounds, innerEntry, onFailure);

        if (effective != FlattenType.Flatten)
            return AddState(LoweredOpCode.OpenComposite, metadataIndex, pushBetween, onFailure);
        return pushBetween;
    }

    // True for the iteration-1 set of single-state, always-advancing
    // matches. LateBoundRule wrapping one of these is intentionally
    // not unwrapped: cyclic late-bound references go through the
    // subprogram-call path, which can't share the atomic fast path
    // (the subprogram terminates with Return, not a direct exit).
    private static bool IsAtomicAdvancingInner(Rule inner)
    {
        return inner is LiteralRule || inner is TokenRule || inner is OneOfRule;
    }

    // Optional fast path: BetweenInclusive(0, 1, inner). The Between
    // counter and bounds are unnecessary here, so the lowering reduces
    // to one PushBacktrack frame around the inner attempt. Inner
    // success pops the frame (lexer left where inner advanced).
    // Inner failure runs FailRestore which rolls the lexer / emit
    // cursor back and routes to compositeAfter (the Optional itself
    // succeeds with no advance). The Optional itself never reports
    // failure to its caller.
    private int LowerOptional(BetweenInclusiveRule rule, Rule inner, int onSuccess, int onFailure)
    {
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int metadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            metadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        // Inner failed: pop the frame, restore lexer / emit, exit.
        int innerFailRestore = AddState(LoweredOpCode.FailRestore, 0, compositeAfter, compositeAfter);

        // Inner succeeded: pop the frame, exit (lexer stays advanced).
        int popOk = AddState(LoweredOpCode.PopBacktrack, 0, compositeAfter, compositeAfter);

        int innerEntry = LowerRule(inner, popOk, innerFailRestore);
        int pushIdx = AddState(LoweredOpCode.PushBacktrack, 0, innerEntry, innerEntry);

        if (effective != FlattenType.Flatten)
            return AddState(LoweredOpCode.OpenComposite, metadataIndex, pushIdx, onFailure);
        return pushIdx;
    }

    // Whether a BetweenInclusive(min, max, inner) is eligible for the
    // fused-scan opcode. Inner has to be OneOf or NoneOf (the two
    // rule shapes ScanOneOfRune / ScanNoneOfRune lower from), and
    // neither inner nor the BetweenInclusive itself can carry a
    // WithError message. Inner's WithError matters because the fused
    // opcode collapses inner failures and only records the outer
    // BetweenInclusive's failure at entry; we'd lose inner's
    // attribution. The atMost-fits-in-int check is the same width
    // limit PackBetweenBounds enforces on the non-fused path.
    private bool CanFuseScan(BetweenInclusiveRule rule, Rule inner)
    {
        if (rule.ErrorMessage != null) return false;
        if (inner.ErrorMessage != null) return false;
        if (inner is OneOfRule || inner is NoneOfRule || inner is AnyTokenRule) return true;
        return false;
    }

    // Detects BetweenInclusive(min, max, AllOf(L, R)) where L is a
    // Literal/Token, R is a OneOf, and both are effectively Delete
    // (no leaves emitted per iteration). Lowers to ScanLiteralOneOfRune
    // and writes the entry-state index to entryState. Returns false
    // when the pattern doesn't match, leaving the caller to fall
    // through to the generic loop. Constraints chosen so the fused
    // opcode's straight-line body stays correct: no WithError on
    // either side (the fused failure path records at entry only),
    // no per-iteration leaves to emit, and no Preserve wrapper above
    // the inner AllOf (we don't have a place to thread that through).
    private bool TryLowerBetweenScanLiteralOneOfRune(
        BetweenInclusiveRule rule,
        Rule inner,
        int onSuccess,
        int onFailure,
        out int entryState)
    {
        entryState = -1;
        if (rule.ErrorMessage != null) return false;
        if (inner is not AllOfRule allOfInner) return false;
        if (allOfInner.Children.Count != 2) return false;
        if (allOfInner.ErrorMessage != null) return false;

        Rule left = allOfInner.Children[0];
        Rule right = allOfInner.Children[1];
        if (left.ErrorMessage != null || right.ErrorMessage != null) return false;
        if (right is not OneOfRule rightOneOf) return false;

        string leftExpected;
        if (left is LiteralRule leftLiteral) leftExpected = leftLiteral.LoweringExpected;
        else if (left is TokenRule leftToken) leftExpected = leftToken.LoweringExpected;
        else return false;

        // Children must be effectively Delete so the fused loop
        // doesn't drop emit ops the recursive evaluator would have
        // produced.
        if (ResolveEffective(left.FlattenType) != FlattenType.Delete) return false;
        if (ResolveEffective(rightOneOf.FlattenType) != FlattenType.Delete) return false;

        // Outer wrapper if the BetweenInclusive itself is Preserve.
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int outerMetadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            outerMetadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, outerMetadataIndex, onSuccess, onSuccess);
        }

        int leftLitIdx = InternLiteral(leftExpected);
        int rightSetIdx = InternRuneSet(rightOneOf.LoweringSet);
        int atMostClamped = rule.AtMost == int.MaxValue ? int.MaxValue : rule.AtMost;
        int specIndex = ScanAndPairSpecs.Count;
        ScanAndPairSpecs.Add(new ScanAndPairSpec(leftLitIdx, rightSetIdx, rule.AtLeast, atMostClamped, errorMetadataIndex: -1));

        int scanState = AddState(LoweredOpCode.ScanLiteralOneOfRune, specIndex, compositeAfter, onFailure);
        if (effective != FlattenType.Flatten)
            scanState = AddState(LoweredOpCode.OpenComposite, outerMetadataIndex, scanState, onFailure);

        entryState = scanState;
        return true;
    }

    // Detects ZeroOrMore(FirstOf(match..., AnyToken.Delete)). When the
    // shape matches, lowers to the generic Between loop with a
    // ScannerSkipAdvance opcode injected at the top of each iteration so
    // the loop jumps past non-candidate runes in bulk instead of
    // attempting the inner FirstOf at every position. Mirrors the
    // recursive evaluator's TryCreateScannerSkip in BetweenInclusiveRule.
    private bool TryLowerBetweenScanner(
        BetweenInclusiveRule rule,
        int onSuccess,
        int onFailure,
        out int entryState)
    {
        entryState = -1;

        if (rule.AtLeast != 0 || rule.AtMost != int.MaxValue) return false;
        // PreserveAllSymbols changes which states the lowerer skips, so
        // the cache split on (PreserveAllSymbols, InputUnit) keeps
        // grammar-author behavior unchanged when debug parsing. The
        // recursive ScannerSkip is also disabled under PreserveAllSymbols.
        if (PreserveAllSymbols) return false;

        if (rule.Children.Count == 0) return false;
        Rule inner = rule.Children[0];
        if (inner is not FirstOfRule firstOf) return false;
        if (inner.ErrorMessage != null) return false;
        if (firstOf.Children.Count < 2) return false;

        Rule fallback = firstOf.Children[firstOf.Children.Count - 1];
        if (fallback is not AnyTokenRule
            || fallback.FlattenType != FlattenType.Delete
            || fallback.ErrorMessage != null)
            return false;

        RuneSet candidates = RuneSet.Empty;
        var literalCandidates = new List<LiteralScannerCandidate>();
        bool allCandidatesAreLiterals = true;
        for (int index = 0; index < firstOf.Children.Count - 1; index++)
        {
            Rule alternative = firstOf.Children[index];
            if (alternative.ErrorMessage != null || alternative.Advance != Advance.Always)
                return false;
            candidates |= alternative.FirstConsumedRunes;

            if (allCandidatesAreLiterals
                && !TryCollectScannerLiteralCandidates(alternative, literalCandidates))
            {
                allCandidatesAreLiterals = false;
                literalCandidates.Clear();
            }
        }

        if (candidates.IsEmpty || candidates == RuneSet.Universe) return false;

        candidates.TryGetBmpChars(maxChars: 256, out var bmpCandidates);
        LiteralScannerCandidate[]? literals =
            allCandidatesAreLiterals && literalCandidates.Count > 0
                ? literalCandidates.ToArray()
                : null;

        // Cache only for single-literal. Multi-literal alternates take
        // the per-position IndexOfAny path inside Lexer; an attempt to
        // enable the cache for them measured 23x slower on the rebar
        // Sherlock haystack than the IndexOfAny path. See the matching
        // gate in BetweenInclusiveRule.TryCreateScannerSkip and the rebar
        // CSV at src/Benchmarks/Rebar/results/multi-literal-cache-rebar-2026-04-28.csv.
        bool useLiteralPositionsCache = literals is { Length: 1 };

        int candidatesRuneSetIndex = InternRuneSet(candidates);
        int specIndex = ScannerSkipSpecs.Count;
        ScannerSkipSpecs.Add(new ScannerSkipSpec(
            candidatesRuneSetIndex,
            bmpCandidates.Length == 0 ? null : bmpCandidates,
            literals,
            useLiteralPositionsCache));

        // Now lay out the same generic loop the non-scanner path uses,
        // but route loopStart through a ScannerSkipAdvance opcode that
        // bulk-skips before each per-iteration PushBacktrack.
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int metadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            metadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        int exitCheckMin = AddState(LoweredOpCode.BetweenExitCheckMin, 0, compositeAfter, onFailure);
        int exitViaFailRestore = AddState(LoweredOpCode.FailRestore, 0, exitCheckMin, exitCheckMin);
        int popIterCheck = ReserveState();
        int innerEntry = LowerRule(inner, popIterCheck, exitViaFailRestore);
        int pushBacktrack = AddState(LoweredOpCode.PushBacktrack, 0, innerEntry, innerEntry);
        int loopStart = AddState(LoweredOpCode.ScannerSkipAdvance, specIndex, pushBacktrack, pushBacktrack);
        FillState(popIterCheck, LoweredOpCode.PopIterationCheck, 0, loopStart, exitCheckMin);

        int packedBounds = PackBetweenBounds(rule.AtLeast, rule.AtMost);
        int pushBetween = AddState(LoweredOpCode.PushBetween, packedBounds, loopStart, onFailure);

        if (effective != FlattenType.Flatten)
            entryState = AddState(LoweredOpCode.OpenComposite, metadataIndex, pushBetween, onFailure);
        else
            entryState = pushBetween;
        return true;
    }

    // Walk an alternative looking for a flat list of literal candidates.
    // Returns false on the first non-literal-shaped alternative, leaving
    // the caller to drop the literal payload and fall back to the
    // generic first-rune skip. Mirrors the recursive evaluator's
    // TryCollectLiteralScannerCandidates.
    private static bool TryCollectScannerLiteralCandidates(
        Rule rule,
        List<LiteralScannerCandidate> candidates)
    {
        if (rule.ErrorMessage != null || rule.Advance != Advance.Always)
            return false;

        switch (rule)
        {
            case LiteralRule literal:
                candidates.Add(new LiteralScannerCandidate(literal.LoweringExpected, ignoreAsciiCase: false));
                return true;
            case LiteralIgnoreAsciiCaseRule literalIc:
                candidates.Add(new LiteralScannerCandidate(literalIc.LoweringExpected, ignoreAsciiCase: true));
                return true;
            case TokenRule token:
                candidates.Add(new LiteralScannerCandidate(token.LoweringExpected, ignoreAsciiCase: false));
                return true;
            case FirstOfRule firstOf:
                if (firstOf.Children.Count == 0) return false;
                for (int i = 0; i < firstOf.Children.Count; i++)
                    if (!TryCollectScannerLiteralCandidates(firstOf.Children[i], candidates))
                        return false;
                return true;
            default:
                return false;
        }
    }

    private int LowerBetweenScan(
        BetweenInclusiveRule rule,
        Rule inner,
        RuneSet runeSet,
        LoweredOpCode scanOpcode,
        int onSuccess,
        int onFailure)
    {
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int outerMetadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            outerMetadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, outerMetadataIndex, onSuccess, onSuccess);
        }

        // Inner-leaf metadata. The fused opcode emits one Leaf op per
        // matched rune when inner's effective FlattenType isn't Delete.
        var innerEffective = ResolveEffective(inner.FlattenType);
        int leafMetadataIndex = -1;
        if (innerEffective != FlattenType.Delete)
            leafMetadataIndex = AddSymbolMetadata(inner);

        // CanFuseScan rejects rules with WithError, so error-metadata
        // is always the no-message sentinel; we still leave the slot
        // wired in case a future variant wants to attach an outer
        // error message at the Between level.
        int errorMetadataIndex = -1;

        int runeSetIndex = InternRuneSet(runeSet);
        int atMostClamped = rule.AtMost == int.MaxValue ? int.MaxValue : rule.AtMost;
        int scanSpecIndex = ScanSpecs.Count;
        ScanSpecs.Add(new ScanSpec(runeSetIndex, rule.AtLeast, atMostClamped, leafMetadataIndex, errorMetadataIndex));

        int scanState = AddState(scanOpcode, scanSpecIndex, compositeAfter, onFailure);

        if (effective != FlattenType.Flatten)
            return AddState(LoweredOpCode.OpenComposite, outerMetadataIndex, scanState, onFailure);
        return scanState;
    }

    // BetweenInclusive(min, max, AnyTokenRule) under InputUnit.Rune.
    // Same shape as LowerBetweenScan but no runeset to test — every
    // rune matches AnyToken. Reuses ScanSpec for atLeast / atMost /
    // leafMetadataIndex / errorMetadataIndex; the runtime opcode
    // ignores the RuneSetIndex slot.
    private int LowerBetweenScanAnyToken(BetweenInclusiveRule rule, AnyTokenRule inner, int onSuccess, int onFailure)
    {
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int outerMetadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            outerMetadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, outerMetadataIndex, onSuccess, onSuccess);
        }

        var innerEffective = ResolveEffective(inner.FlattenType);
        int leafMetadataIndex = -1;
        if (innerEffective != FlattenType.Delete)
            leafMetadataIndex = AddSymbolMetadata(inner);

        int atMostClamped = rule.AtMost == int.MaxValue ? int.MaxValue : rule.AtMost;
        int scanSpecIndex = ScanSpecs.Count;
        ScanSpecs.Add(new ScanSpec(/*runeSetIndex*/-1, rule.AtLeast, atMostClamped, leafMetadataIndex, /*errorMetadataIndex*/-1));

        int scanState = AddState(LoweredOpCode.ScanAnyTokenRune, scanSpecIndex, compositeAfter, onFailure);

        if (effective != FlattenType.Flatten)
            return AddState(LoweredOpCode.OpenComposite, outerMetadataIndex, scanState, onFailure);
        return scanState;
    }

    private int LowerNot(NotRule rule, int onSuccess, int onFailure)
    {
        // Fast path: under InputUnit.Rune, Not(OneOf) / Not(Literal) /
        // Not(Token) lowers to a single peek-and-reject opcode. No
        // backtrack frame, no inner-rule dispatch. Constraints:
        //   * effective FlattenType isn't Preserve (which needs a
        //     wrapper Symbol; the fused opcode can't emit one)
        //   * neither Not nor inner has a WithError message (the
        //     fused opcode collapses inner's record-on-success path
        //     into the rule's own record-on-failure)
        Rule inner = rule.Children[0];
        if (InputUnit == InputUnit.Rune
            && rule.ErrorMessage == null
            && inner.ErrorMessage == null
            && ResolveEffective(rule.FlattenType) != FlattenType.Preserve)
        {
            if (inner is OneOfRule oneOfInner)
            {
                int setIdx = InternRuneSet(oneOfInner.LoweringSet);
                return AddState(LoweredOpCode.PeekRejectOneOfRune,
                    setIdx | (NoErrorMetadataSentinel << 16),
                    onSuccess, onFailure);
            }
            if (inner is LiteralRule literalInner)
            {
                int litIdx = InternLiteral(literalInner.LoweringExpected);
                return AddState(LoweredOpCode.PeekRejectLiteralRune,
                    litIdx | (NoErrorMetadataSentinel << 16),
                    onSuccess, onFailure);
            }
            if (inner is TokenRule tokenInner)
            {
                int litIdx = InternLiteral(tokenInner.LoweringExpected);
                return AddState(LoweredOpCode.PeekRejectLiteralRune,
                    litIdx | (NoErrorMetadataSentinel << 16),
                    onSuccess, onFailure);
            }
        }

        // Not's overall result is zero-width: lexer and emit cursor
        // restore to entry regardless of inner outcome. Result is
        // success iff inner FAILED.
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int metadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            metadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        // notSucceededExit: inner failed -> Not succeeds. The
        // per-frame FailRestore already popped the frame and restored
        // lexer / emit. We just continue.
        int notSucceededExit = AddState(LoweredOpCode.FailRestore, 0, compositeAfter, compositeAfter);

        // notFailedExit: inner succeeded -> Not fails. We need to pop
        // the frame and restore lexer / emit ourselves (since no
        // failure rolled it back).
        int notFailedExit = AddState(LoweredOpCode.FailRestore, 0, onFailure, onFailure);

        int innerEntry = LowerRule(rule.Children[0], notFailedExit, notSucceededExit);
        int pushIdx = AddState(LoweredOpCode.PushBacktrack, 0, innerEntry, innerEntry);

        if (effective != FlattenType.Flatten)
            return AddState(LoweredOpCode.OpenComposite, metadataIndex, pushIdx, onFailure);
        return pushIdx;
    }

    private int LowerPeek(PeekRule rule, int onSuccess, int onFailure)
    {
        // Peek's overall result is zero-width and matches inner's
        // outcome (success iff inner succeeded).
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int metadataIndex = -1;
        if (effective != FlattenType.Flatten)
        {
            metadataIndex = AddSymbolMetadata(rule);
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        // Inner succeeded -> Peek succeeds. We need to pop the frame
        // and restore lexer / emit ourselves.
        int peekSucceededExit = AddState(LoweredOpCode.FailRestore, 0, compositeAfter, compositeAfter);

        // Inner failed -> Peek fails. Frame already popped + restored
        // by the per-frame failure routing.
        int peekFailedExit = AddState(LoweredOpCode.FailRestore, 0, onFailure, onFailure);

        int innerEntry = LowerRule(rule.Children[0], peekSucceededExit, peekFailedExit);
        int pushIdx = AddState(LoweredOpCode.PushBacktrack, 0, innerEntry, innerEntry);

        if (effective != FlattenType.Flatten)
            return AddState(LoweredOpCode.OpenComposite, metadataIndex, pushIdx, onFailure);
        return pushIdx;
    }

    private int LowerScanUntil(ScanUntilRule rule, int onSuccess, int onFailure)
    {
        // Rule-stopper, no escape: native scan with peeked stopper
        // calls. Eligibility check: stopper rule has Advance.Always
        // (the recursive scan only checks the stopper after seeing
        // a rune that could possibly start it), and a non-Universe
        // FirstConsumedRunes (otherwise every rune is "eligible" and
        // we'd be calling the stopper subprogram on every rune,
        // negating the win). Rune-only because the inline scan
        // decodes runes directly.
        if (rule.LoweringStopperRule != null
            && !rule.LoweringHasEscape
            && InputUnit == InputUnit.Rune
            && rule.LoweringStopperRule.Advance == Advance.Always
            && !rule.LoweringStopperRule.FirstConsumedRunes.Equals(RuneSet.Universe))
        {
            return LowerScanUntilRuleStopper(rule, onSuccess, onFailure);
        }

        // Other rule-stopper / rule-escape-start forms still bridge to
        // the recursive evaluator. Multi-rune boundaries with
        // Universe FirstConsumedRunes (no first-rune skip possible)
        // would call the stopper rule on every body rune, which is
        // basically what the recursive evaluator already does — no
        // win from going native there.
        if (rule.LoweringStopperRule != null)
            return LowerViaBridge(rule, onSuccess, onFailure);
        if (rule.LoweringHasEscape && rule.LoweringEscapeStartRule != null)
            return LowerViaBridge(rule, onSuccess, onFailure);

        int stopperSetIdx = InternRuneSet(rule.LoweringStopperSet);
        int escapeStartRune = rule.LoweringHasEscape ? rule.LoweringEscapeStartRune : -1;

        // Lower escape-end as a subprogram so the scan loop's escape
        // path can Call it and resume scanning afterward. Does not
        // require the escape-end rule to be cyclic; the subprogram
        // shape is what the Call/Return mechanism needs.
        int escapeEndEntry = -1;
        if (rule.LoweringHasEscape)
            escapeEndEntry = GetOrCreateSubprogram(rule.LoweringEscapeEnd!);

        int specIdx = AddScanUntilSpec(stopperSetIdx, escapeStartRune, rule.LoweringHasEscape, escapeEndEntry);

        // The leaf-emit / no-emit decision lives inline in the lowering
        // path. Delete-effective ScanUntil bypasses both the metadata
        // entry and the EmitScanUntilLeaf state.
        var effective = ResolveEffective(rule.FlattenType);
        bool emitLeaf = effective != FlattenType.Delete;

        int afterScan;
        if (emitLeaf)
        {
            int metadataIndex = AddSymbolMetadata(rule);
            int popOk = AddState(LoweredOpCode.PopBacktrack, 0, onSuccess, onSuccess);
            afterScan = AddState(LoweredOpCode.EmitScanUntilLeaf, metadataIndex, popOk, popOk);
        }
        else
        {
            afterScan = AddState(LoweredOpCode.PopBacktrack, 0, onSuccess, onSuccess);
        }

        // Outer fail handler: rolls back lexer and emit cursor to entry
        // and propagates failure. Only triggered when escape-end fails.
        // (The scan loop itself never fails — empty body is legal, and
        // EOF / surrogate halt count as successful exit.)
        int outerFail = AddState(LoweredOpCode.FailRestore, 0, onFailure, onFailure);

        // The scan state. OnFailure points to the escape-call setup,
        // which is wired below.
        int scanState = ReserveState();

        // Escape-call state, lowered only when escape is supported. Use
        // the suppressing variant so any outputs inside escape-end
        // (e.g. a Preserve-default OneOf) get truncated on Return.
        // Mirrors the recursive evaluator passing outputSymbols=null
        // when invoking escape-end.
        int escapeCall = -1;
        if (rule.LoweringHasEscape)
            escapeCall = AddState(LoweredOpCode.CallSuppressOutputs, escapeEndEntry, scanState, outerFail);

        FillState(scanState, LoweredOpCode.ScanUntilFast, specIdx, afterScan,
            rule.LoweringHasEscape ? escapeCall : outerFail);

        // Outer push: snapshots entry-time lexer position so
        // EmitScanUntilLeaf can read the start, and so outerFail can
        // restore on escape-end failure.
        return AddState(LoweredOpCode.PushBacktrack, 0, scanState, scanState);
    }

    // Native rule-stoppered ScanUntil scan, no escape. Lowered shape:
    //
    //   entry: PushBacktrack(failTarget=outerFail)
    //   loopStart: ScanUntilStopperEligibleRune(specIdx)
    //     OnSuccess (eligible rune found): -> peekStopperPush
    //     OnFailure (EOF without stopper): -> exitLeaf
    //   peekStopperPush: PushBacktrack(failTarget=stopperFailedRestore)
    //   stopperCall: Call(stopperEntry, OnSuccess=stopperSucceededRestore, OnFailure=stopperFailedRestore)
    //   stopperSucceededRestore: FailRestore -> exitLeaf
    //   stopperFailedRestore: FailRestore -> advanceRune
    //   advanceRune: AdvanceOneRune -> loopStart
    //   exitLeaf: [EmitScanUntilLeaf if applicable] -> popEntry -> onSuccess
    //   popEntry: PopBacktrack -> onSuccess
    //   outerFail: FailRestore -> onFailure
    //
    // The outer entry frame saves the body's start position so
    // EmitScanUntilLeaf can compute the leaf span. The inner peek
    // frame around each stopper Call lets us roll the lexer back to
    // before the call regardless of outcome (the recursive evaluator
    // does this via a Transaction that never commits). When the
    // stopper succeeds in peek mode, the outer AllOf's next rule
    // consumes it; ScanUntil itself never advances past the stopper.
    private int LowerScanUntilRuleStopper(ScanUntilRule rule, int onSuccess, int onFailure)
    {
        Rule stopper = rule.LoweringStopperRule!;
        int stopperFirstRunesIdx = InternRuneSet(stopper.FirstConsumedRunes);
        int stopperEntry = GetOrCreateSubprogram(stopper);

        var effective = ResolveEffective(rule.FlattenType);
        bool emitLeaf = effective != FlattenType.Delete;

        int specIdx = RuleStopperSpecs.Count;
        RuleStopperSpecs.Add(new RuleStopperSpec(stopperFirstRunesIdx));

        // popEntry pops the outer entry frame. Outer fail uses
        // FailRestore to propagate failure, but the rule-stopper scan
        // never fails — the body is whatever was matched up to a
        // stopper or EOF. outerFail is wired for completeness.
        int popEntry = AddState(LoweredOpCode.PopBacktrack, 0, onSuccess, onSuccess);
        int outerFail = AddState(LoweredOpCode.FailRestore, 0, onFailure, onFailure);

        // exitLeaf: emit the leaf (if applicable) and pop the entry frame.
        int exitLeaf;
        if (emitLeaf)
        {
            int leafMetaIdx = AddSymbolMetadata(rule);
            exitLeaf = AddState(LoweredOpCode.EmitScanUntilLeaf, leafMetaIdx, popEntry, popEntry);
        }
        else
        {
            exitLeaf = popEntry;
        }

        // After the stopper succeeded in peek mode, roll the lexer
        // back to the position before the Call (saved in the inner
        // peek frame) and exit the scan.
        int stopperSucceededRestore = AddState(LoweredOpCode.FailRestore, 0, exitLeaf, exitLeaf);

        // After the stopper failed, roll the lexer back to the
        // position before the Call and advance one rune (consuming
        // the body rune that didn't actually start a stopper match).
        int advanceRune = ReserveState();
        int stopperFailedRestore = AddState(LoweredOpCode.FailRestore, 0, advanceRune, advanceRune);

        int stopperCall = AddState(LoweredOpCode.Call, stopperEntry, stopperSucceededRestore, stopperFailedRestore);
        int peekStopperPush = AddState(LoweredOpCode.PushBacktrack, 0, stopperCall, stopperCall);

        // ScanUntilStopperEligibleRune: walks runes until either EOF
        // (OnFailure -> exitLeaf, body extends to EOF) or a rune in
        // the stopper's first-rune set (OnSuccess -> peekStopperPush
        // to attempt the stopper).
        int loopStart = AddState(LoweredOpCode.ScanUntilStopperEligibleRune, specIdx, peekStopperPush, exitLeaf);

        FillState(advanceRune, LoweredOpCode.AdvanceOneRune, 0, loopStart, loopStart);

        // Outer entry push saves the body's start position so
        // EmitScanUntilLeaf can compute the leaf span.
        return AddState(LoweredOpCode.PushBacktrack, 0, loopStart, outerFail);
    }

    // Catch-all lowering: runs the rule via the recursive evaluator's
    // TryParse and forwards its Symbol output into our output stream.
    // Used for rule types the lowerer doesn't have a native opcode for:
    // WithinGrapheme, the rule-stoppered / rule-escape-start variants
    // of ScanUntil, and any user-defined Rule subclass. Slower than
    // a native lowering by the cost of one virtual TryParseRule call
    // per invocation, but correctness-preserving for everything the
    // recursive evaluator handles.
    //
    // This call is the seam where state-machine evaluation hands control
    // back to recursive evaluation, so all existing recursive
    // semantics (per-rule transactions, rule-specific FlattenType,
    // PreserveAllSymbols, RecordFailure with WithError messages) come
    // through unchanged. The caller's lexer is the live lexer the
    // bridged rule reads from and writes to.
    private int LowerViaBridge(Rule rule, int onSuccess, int onFailure)
    {
        int bridgeIndex = BridgeRules.Count;
        BridgeRules.Add(rule);
        return AddState(LoweredOpCode.BridgeToRecursive, bridgeIndex, onSuccess, onFailure);
    }

    // Lower `rule` as a Call/Return-shaped subprogram, returning the
    // entry state index. Used both by the cyclic-rule path and by
    // ScanUntil for its escape-end. Caches per-rule so two callers
    // referencing the same rule (e.g. a shared FirstOf used as the escape
    // end of two StringBodies) share one subprogram body.
    private int GetOrCreateSubprogram(Rule rule)
    {
        if (SubprogramEntries.TryGetValue(rule, out int existingEntry))
            return existingEntry;

        int subprogramEntry = ReserveState();
        SubprogramEntries[rule] = subprogramEntry;

        int returnSuccess = AddState(LoweredOpCode.ReturnSuccess, 0, 0, 0);
        int returnFailure = AddState(LoweredOpCode.ReturnFailure, 0, 0, 0);
        int bodyEntry = LowerRuleBody(rule, returnSuccess, returnFailure);

        FillState(subprogramEntry, LoweredOpCode.Jump, 0, bodyEntry, bodyEntry);
        return subprogramEntry;
    }

    private int AddScanUntilSpec(int stopperSetIndex, int escapeStartRune, bool hasEscape, int escapeEndEntry)
    {
        int newIndex = ScanUntilSpecs.Count;
        ScanUntilSpecs.Add(new ScanUntilSpec(stopperSetIndex, escapeStartRune, hasEscape, escapeEndEntry));
        return newIndex;
    }

    // Internal helpers. Reflection-free access to a few private
    // rule-specific fields the lowerer needs. Each rule type stores
    // its data in a private field; the existing TryParseRule reads
    // those fields directly. Since the StateMachine subdir lives in
    // the same assembly, "internal" reach is enough for the rule
    // factory methods, but private fields on rules need accessors.
    // We add internal-visible getters via reflection-free helpers
    // (added on the rule classes in companion edits).

    private static string GetLiteralExpected(LiteralRule rule) => rule.LoweringExpected;
    private static string GetTokenExpected(TokenRule rule) => rule.LoweringExpected;
    private static RuneSet GetOneOfSet(OneOfRule rule) => rule.LoweringSet;

    private int InternLiteral(string text)
    {
        if (_literalIndex.TryGetValue(text, out int existing)) return existing;
        int newIndex = Literals.Count;
        Literals.Add(text);
        _literalIndex[text] = newIndex;
        if (newIndex >= 0xFFFF)
            throw new NotSupportedException(
                "State-machine evaluator literal-table overflow: more than 65535 unique literals.");
        return newIndex;
    }

    private int InternRuneSet(RuneSet set)
    {
        if (_runeSetIndex.TryGetValue(set, out int existing)) return existing;
        int newIndex = RuneSets.Count;
        RuneSets.Add(set);
        _runeSetIndex[set] = newIndex;
        return newIndex;
    }

    private int AddSymbolMetadata(Rule rule)
    {
        // Store the rule's DECLARED FlattenType. Lowering already
        // collapsed the PreserveAllSymbols override into the program's
        // structure (skipping Open/Close where effective is Flatten and
        // EmitLeaf where effective is Delete), so any metadata that
        // survives is attached to outputs that need to reach the tree.
        // TreeBuilder reapplies the override at tree-build time so the
        // resulting Symbol carries the rule's declared FlattenType,
        // matching what the recursive engine produces (e.g. a
        // Token('a') wrapper under PreserveAllSymbols still has
        // FlattenType.Delete on it).
        int newIndex = SymbolMetadata.Count;
        if (newIndex >= 0xFFFF)
            throw new NotSupportedException(
                "State-machine evaluator symbol-metadata overflow: more than 65535 entries.");
        SymbolMetadata.Add(new SymbolMetadata(rule.Id, rule.FlattenType, rule.ErrorMessage));
        return newIndex;
    }

    private int AddState(LoweredOpCode op, int data, int onSuccess, int onFailure)
    {
        States.Add(new State(op, data, onSuccess, onFailure));
        return States.Count - 1;
    }

    private int ReserveState()
    {
        States.Add(default);
        return States.Count - 1;
    }

    private void FillState(int index, LoweredOpCode op, int data, int onSuccess, int onFailure)
    {
        States[index] = new State(op, data, onSuccess, onFailure);
    }

    private static int PackBetweenBounds(int atLeast, int atMost)
    {
        int low = atLeast & 0xFFFF;
        int high = atMost == int.MaxValue ? 0xFFFF : (atMost & 0xFFFF);
        return low | (high << 16);
    }
}
