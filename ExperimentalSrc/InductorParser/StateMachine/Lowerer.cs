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
    public static CompiledProgram Lower(Rule rootRule, bool preserveAllSymbols = false)
    {
        // Only force-compile when the caller hasn't already done so.
        // If they pre-compiled with a non-default normalization form
        // (FormD, null, etc.), calling Compile() here would throw a
        // form-conflict.
        if (!rootRule.IsCompiled) rootRule.Compile();
        var context = new LoweringContext(rootRule, preserveAllSymbols);

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
        bool hasOutputs = ProgramHasOutputs(context.States);

        // Invert SubprogramEntries (Rule -> entry state index) into the
        // entry-state-index -> Rule lookup the Stepper reads on Call /
        // CallSuppressOutputs to label trace lines.
        var subprogramRuleByEntry = new Dictionary<int, Rule>(context.SubprogramEntries.Count);
        foreach (var entryByRule in context.SubprogramEntries)
            subprogramRuleByEntry[entryByRule.Value] = entryByRule.Key;

        return new CompiledProgram(
            context.States.ToArray(),
            context.Literals.ToArray(),
            context.TokenSets.ToArray(),
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

    private static bool ProgramHasOutputs(IReadOnlyList<State> states)
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
    public readonly List<TokenSet> TokenSets = new();
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

    // For cyclic rules: the index of the "shared" entry that callers
    // Call into. Allocated lazily the first time a cyclic rule is
    // lowered. Subsequent encounters Call this index.
    public readonly Dictionary<Rule, int> SubprogramEntries = new(ReferenceComparer<Rule>.Instance);

    // Dedup tables. Same literal text or same TokenSet appearing in
    // multiple rules shares one slot in the runtime table. Keeps the
    // tables small and improves CPU cache behavior.
    private readonly Dictionary<string, int> _literalIndex = new();
    private readonly Dictionary<TokenSet, int> _tokenSetIndex = new();

    public LoweringContext(Rule rootRule, bool preserveAllSymbols)
    {
        RootRule = rootRule;
        PreserveAllSymbols = preserveAllSymbols;
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
            GraphemeRule grapheme => LowerGrapheme(grapheme, onSuccess, onFailure),
            OneOfRule oneOf => LowerOneOf(oneOf, onSuccess, onFailure),
            NoneOfRule noneOf => LowerNoneOf(noneOf, onSuccess, onFailure),
            AnyTokenRule anyToken => LowerAnyToken(anyToken, onSuccess, onFailure),
            EofRule eof => LowerEof(eof, onSuccess, onFailure),
            AndRule andRule => LowerAnd(andRule, onSuccess, onFailure),
            OrRule orRule => LowerOr(orRule, onSuccess, onFailure),
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
            GraphemeRule grapheme => LowerGrapheme(grapheme, onSuccess, onFailure),
            OneOfRule oneOf => LowerOneOf(oneOf, onSuccess, onFailure),
            NoneOfRule noneOf => LowerNoneOf(noneOf, onSuccess, onFailure),
            AnyTokenRule anyToken => LowerAnyToken(anyToken, onSuccess, onFailure),
            EofRule eof => LowerEof(eof, onSuccess, onFailure),
            AndRule andRule => LowerAnd(andRule, onSuccess, onFailure),
            OrRule orRule => LowerOr(orRule, onSuccess, onFailure),
            BetweenInclusiveRule between => LowerBetween(between, onSuccess, onFailure),
            NotRule not => LowerNot(not, onSuccess, onFailure),
            PeekRule peek => LowerPeek(peek, onSuccess, onFailure),
            ScanUntilRule scanUntil => LowerScanUntil(scanUntil, onSuccess, onFailure),
            _ => LowerViaBridge(rule, onSuccess, onFailure)
        };
    }

    // Both Grapheme and Literal funnel into the same MatchLiteral opcode.
    // Their match logic in the existing code is identical: read tokens
    // until the expected string is consumed, fail on first mismatch.
    // The construction-time validation differs (Grapheme requires one
    // grapheme; Literal accepts any non-empty string) but the runtime
    // semantics line up.
    private int LowerLiteral(LiteralRule rule, int onSuccess, int onFailure)
    {
        return LowerLiteralLike(rule, GetLiteralExpected(rule), onSuccess, onFailure);
    }

    private int LowerGrapheme(GraphemeRule rule, int onSuccess, int onFailure)
    {
        return LowerLiteralLike(rule, GetGraphemeExpected(rule), onSuccess, onFailure);
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
        return AddState(LoweredOpCode.MatchLiteral, matchPacked, afterMatch, onFailure);
    }

    private int LowerOneOf(OneOfRule rule, int onSuccess, int onFailure)
    {
        int tokenSetIndex = InternTokenSet(GetOneOfSet(rule));
        return EmitTokenMatch(rule, LoweredOpCode.MatchOneOf, tokenSetIndex, onSuccess, onFailure);
    }

    private int LowerNoneOf(NoneOfRule rule, int onSuccess, int onFailure)
    {
        int tokenSetIndex = InternTokenSet(rule.LoweringSet);
        return EmitTokenMatch(rule, LoweredOpCode.MatchNoneOf, tokenSetIndex, onSuccess, onFailure);
    }

    private int LowerAnyToken(AnyTokenRule rule, int onSuccess, int onFailure)
    {
        // MatchAnyToken takes no payload, so the low 16 bits are 0.
        // The high 16 bits still carry the optional error-metadata
        // index for WithError.
        return EmitTokenMatch(rule, LoweredOpCode.MatchAnyToken, 0, onSuccess, onFailure);
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
        int literalIndex = InternLiteral(rule.ExpectedText!);
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
        return AddState(LoweredOpCode.MatchLiteralIgnoreAsciiCase, matchPacked, afterMatch, onFailure);
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

    private int LowerAnd(AndRule rule, int onSuccess, int onFailure)
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

    private int LowerOr(OrRule rule, int onSuccess, int onFailure)
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

        // Decide whether this Or benefits from the first-rune-skip
        // optimization. We need at least one alternative whose
        // FirstConsumedTokens is non-trivial (Advance.Always and
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
            // FirstConsumedTokens set. The check skips the
            // PushBacktrack/inner-attempt entirely on a mismatch and
            // routes straight to the next alternative's start (no
            // frame to pop because we never pushed one).
            bool skipEligible = anyCanSkip && CanSkipUnreachableAlt(child);
            int altStart = pushIdx;
            if (skipEligible)
            {
                int tokenSetIdx = InternTokenSet(child.FirstConsumedTokens);
                altStart = AddState(LoweredOpCode.CheckPeekedRuneInSet, tokenSetIdx, pushIdx, nextAltStartWithoutPop);
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
                // When the next alt's start is a CheckPeekedRuneInSet,
                // refresh machine.PeekedRune after the failed alt's
                // FailRestore. The failed alt's body may have run a
                // nested Or that called LoadPeekedRune /
                // LoadPeekedRuneAndJumpAlt at a different lexer
                // position and overwrote the stash. After FailRestore
                // rolls the lexer back, the chain's CheckPeekedRuneInSet
                // states need the rune at the rolled-back position, not
                // the stale one. Skip the refresh when the next alt
                // isn't skip-eligible — its altStart doesn't read
                // PeekedRune (any nested Or inside the alt body
                // does its own peek on entry).
                int handlerTarget = altStart;
                if (skipEligible)
                    handlerTarget = AddState(LoweredOpCode.LoadPeekedRune, 0, altStart, altStart);
                nextFailHandlerWithPop = AddState(LoweredOpCode.FailRestore, 0, handlerTarget, handlerTarget);
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
    // mismatch. Mirrors OrRule's runtime guard: only skip when the
    // child Always advances (so its first rune is guaranteed to be
    // consumed) AND has a strictly tighter FirstConsumedTokens than the
    // universe. Alternatives whose subtree carries any .WithError are NOT
    // skipped, because the existing code lets them run so the message
    // can reach DeepestFailureMessage on a parse failure.
    // HasErrorMessageInSubtree is the subtree-aware check; the immediate-
    // only `child.ErrorMessage == null` would be a half-check that would
    // skip a composite child whose own ErrorMessage is null even when a
    // deeper rule in its subtree carries the user's message. Same shape
    // as OrRule's runtime guard.
    private static bool CanSkipUnreachableAlt(Rule child)
    {
        if (child.Advance != Advance.Always) return false;
        if (child.HasErrorMessageInSubtree) return false;
        // FirstConsumedTokens equality with Universe means the set
        // accepts any rune, so the peek check would never skip. Avoid
        // the wasted state.
        if (child.FirstConsumedTokens.Equals(TokenSet.Universe)) return false;
        return true;
    }

    // Build the 128-entry ASCII jump table for a Or that uses
    // first-rune-skip. altRecords is filled in reverse-priority order
    // by the reverse-lowering loop, so we walk it tail-to-head to
    // restore priority order. For each ASCII rune r:
    //   * Walk alts in priority order. For each alt:
    //     * skipEligible alt: if its FirstConsumedTokens contains r,
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
                    if (record.child.FirstConsumedTokens.Contains(rune))
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

        // Scanner-shape skip: ZeroOrMore(Or(match..., AnyToken.Delete)).
        // When the inner is a Or whose last alternative is a deleted
        // AnyToken, non-matching input would just be consumed one rune at
        // a time. The scanner-skip opcode jumps straight to the next
        // candidate first-rune (or, for literal-only alternatives, to the
        // next position where the literal text could match) so the inner
        // Or isn't attempted at every non-candidate position. Mirrors
        // the recursive evaluator's ScannerSkip in BetweenInclusiveRule.
        if (TryLowerBetweenScanner(rule, onSuccess, onFailure, out int scannerEntry))
            return scannerEntry;

        // The fused-scan opcodes (ScanOneOfRune / ScanNoneOfRune /
        // ScanAnyTokenRune / ScanLiteralOneOfRune) inline rune decode,
        // which would split multi-rune graphemes under grapheme
        // tokenization, so they were Rune-mode only. With the parser
        // collapsed to one (grapheme) lexer, those paths are dead.
        // The generic Between loop below handles the same shapes
        // through Lexer.Read.
        Rule inner = rule.Children[0];

        // Optional fast path: BetweenInclusive(0, 1, inner). Drops the
        // PushBetween/PopIterationCheck/BetweenExitCheckMin trio and
        // lowers to the minimal Push/Pop pair plus a FailRestore. The
        // generic loop path runs five wrapper opcodes per attempt; the
        // Optional shape runs two. Big win on Optional(Or(...)) and
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
        bool needMetadataForWrap = effective != FlattenType.Flatten;
        bool needMetadataForError = rule.ErrorMessage != null;
        if (needMetadataForWrap || needMetadataForError)
        {
            metadataIndex = AddSymbolMetadata(rule);
        }
        if (needMetadataForWrap)
        {
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        // BetweenExitCheckMin: pops the Between frame, succeeds if
        // counter >= atLeast, otherwise records the rule's WithError
        // text at the loop's stop position, restores to entry-time
        // state, and routes to onFailure. state.Data carries the
        // SymbolMetadata index so the opcode can read ErrorMessage,
        // or -1 when the rule has neither a wrapper nor a WithError.
        int exitCheckData = needMetadataForError ? metadataIndex : -1;
        int exitCheckMin = AddState(LoweredOpCode.BetweenExitCheckMin, exitCheckData, compositeAfter, onFailure);

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

        if (needMetadataForWrap)
            return AddState(LoweredOpCode.OpenComposite, metadataIndex, pushBetween, onFailure);
        return pushBetween;
    }

    // Atomic-inner fast path. Inner is one of LiteralRule / GraphemeRule /
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
        bool needMetadataForWrap = effective != FlattenType.Flatten;
        bool needMetadataForError = rule.ErrorMessage != null;
        if (needMetadataForWrap || needMetadataForError)
        {
            metadataIndex = AddSymbolMetadata(rule);
        }
        if (needMetadataForWrap)
        {
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        // exitCheckMin records the rule's WithError text at the loop's
        // stop position before rolling back to entry; pass the
        // metadata index (or -1 when no WithError) so the opcode can
        // read it.
        int exitCheckData = needMetadataForError ? metadataIndex : -1;
        int exitCheckMin = AddState(LoweredOpCode.BetweenExitCheckMin, exitCheckData, compositeAfter, onFailure);

        // BetweenIncrementCheckMax needs loopStart (= innerEntry) as its
        // OnSuccess, but we can't lower inner until incrementCheck has
        // a slot. Reserve the slot first.
        int incrementCheck = ReserveState();
        int innerEntry = LowerRule(inner, incrementCheck, exitCheckMin);
        FillState(incrementCheck, LoweredOpCode.BetweenIncrementCheckMax, 0, innerEntry, exitCheckMin);

        int packedBounds = PackBetweenBounds(rule.AtLeast, rule.AtMost);
        int pushBetween = AddState(LoweredOpCode.PushBetween, packedBounds, innerEntry, onFailure);

        if (needMetadataForWrap)
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
        return inner is LiteralRule || inner is GraphemeRule || inner is OneOfRule;
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

    // Detects ZeroOrMore(Or(match..., AnyToken.Delete)). When the
    // shape matches, lowers to the generic Between loop with a
    // ScannerSkipAdvance opcode injected at the top of each iteration so
    // the loop jumps past non-candidate runes in bulk instead of
    // attempting the inner Or at every position. Mirrors the
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
        // the cache split on PreserveAllSymbols keeps grammar-author
        // behavior unchanged when debug parsing. The recursive
        // ScannerSkip is also disabled under PreserveAllSymbols.
        if (PreserveAllSymbols) return false;

        if (rule.Children.Count == 0) return false;
        Rule inner = rule.Children[0];
        if (inner is not OrRule orRule) return false;
        if (inner.ErrorMessage != null) return false;
        if (orRule.Children.Count < 2) return false;

        Rule fallback = orRule.Children[orRule.Children.Count - 1];
        if (fallback is not AnyTokenRule
            || fallback.FlattenType != FlattenType.Delete
            || fallback.ErrorMessage != null)
            return false;

        TokenSet candidates = TokenSet.Empty;
        var literalCandidates = new List<LiteralScannerCandidate>();
        bool allCandidatesAreLiterals = true;
        for (int index = 0; index < orRule.Children.Count - 1; index++)
        {
            Rule alternative = orRule.Children[index];
            if (alternative.ErrorMessage != null || alternative.Advance != Advance.Always)
                return false;
            candidates |= alternative.FirstConsumedTokens;

            if (allCandidatesAreLiterals
                && !TryCollectScannerLiteralCandidates(alternative, literalCandidates))
            {
                allCandidatesAreLiterals = false;
                literalCandidates.Clear();
            }
        }

        if (candidates.IsEmpty || candidates == TokenSet.Universe) return false;

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

        int candidatesTokenSetIndex = InternTokenSet(candidates);
        int specIndex = ScannerSkipSpecs.Count;
        ScannerSkipSpecs.Add(new ScannerSkipSpec(
            candidatesTokenSetIndex,
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
                candidates.Add(new LiteralScannerCandidate(literal.ExpectedText!, ignoreAsciiCase: false));
                return true;
            case LiteralIgnoreAsciiCaseRule literalIc:
                candidates.Add(new LiteralScannerCandidate(literalIc.ExpectedText!, ignoreAsciiCase: true));
                return true;
            case GraphemeRule grapheme:
                candidates.Add(new LiteralScannerCandidate(grapheme.ExpectedText!, ignoreAsciiCase: false));
                return true;
            case OrRule orRule:
                if (orRule.Children.Count == 0) return false;
                for (int i = 0; i < orRule.Children.Count; i++)
                    if (!TryCollectScannerLiteralCandidates(orRule.Children[i], candidates))
                        return false;
                return true;
            default:
                return false;
        }
    }

    private int LowerNot(NotRule rule, int onSuccess, int onFailure)
    {
        // The PeekReject*Rune fused-Not opcodes inlined rune decode and
        // were Rune-mode only. With one (grapheme) lexer they would
        // mis-handle multi-rune graphemes, so they're gone; the generic
        // Not lowering below applies in every case.

        // Not's overall result is zero-width: lexer and emit cursor
        // restore to entry regardless of inner outcome. Result is
        // success iff inner FAILED.
        var effective = ResolveEffective(rule.FlattenType);
        int compositeAfter = onSuccess;
        int metadataIndex = -1;
        bool needMetadataForWrap = effective != FlattenType.Flatten;
        bool needMetadataForError = rule.ErrorMessage != null;
        if (needMetadataForWrap || needMetadataForError)
        {
            metadataIndex = AddSymbolMetadata(rule);
        }
        if (needMetadataForWrap)
        {
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        // notSucceededExit: inner failed -> Not succeeds. The
        // per-frame FailRestore already popped the frame and restored
        // lexer / emit. We just continue.
        int notSucceededExit = AddState(LoweredOpCode.FailRestore, 0, compositeAfter, compositeAfter);

        // notFailedExit: inner succeeded -> Not fails. We need to pop
        // the frame and restore lexer / emit ourselves (since no
        // failure rolled it back). When the rule carries a
        // .WithError("..."), insert a RecordRuleFailure step after
        // the FailRestore so the message can ride along. The
        // recursive NotRule does this via lexer.RecordFailure(
        // transaction.StartPosition, ErrorMessage) on its inner-
        // matched path; the FailRestore here has already restored
        // lexer.Position to the entry, so RecordRuleFailure reads
        // the same position the recursive engine would.
        int notFailedExit;
        if (needMetadataForError)
        {
            int recordFailure = AddState(LoweredOpCode.RecordRuleFailure, metadataIndex, onFailure, onFailure);
            notFailedExit = AddState(LoweredOpCode.FailRestore, 0, recordFailure, recordFailure);
        }
        else
        {
            notFailedExit = AddState(LoweredOpCode.FailRestore, 0, onFailure, onFailure);
        }

        int innerEntry = LowerRule(rule.Children[0], notFailedExit, notSucceededExit);
        int pushIdx = AddState(LoweredOpCode.PushBacktrack, 0, innerEntry, innerEntry);

        if (needMetadataForWrap)
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
        bool needMetadataForWrap = effective != FlattenType.Flatten;
        bool needMetadataForError = rule.ErrorMessage != null;
        if (needMetadataForWrap || needMetadataForError)
        {
            metadataIndex = AddSymbolMetadata(rule);
        }
        if (needMetadataForWrap)
        {
            compositeAfter = AddState(LoweredOpCode.CloseComposite, metadataIndex, onSuccess, onSuccess);
        }

        // Inner succeeded -> Peek succeeds. We need to pop the frame
        // and restore lexer / emit ourselves.
        int peekSucceededExit = AddState(LoweredOpCode.FailRestore, 0, compositeAfter, compositeAfter);

        // Inner failed -> Peek fails. Frame already popped + restored
        // by the per-frame failure routing. When the rule carries a
        // .WithError("..."), insert a RecordRuleFailure step after
        // the FailRestore so the message rides along, mirroring the
        // recursive PeekRule's lexer.RecordFailure(transaction.
        // StartPosition, ErrorMessage) on its inner-fails path.
        int peekFailedExit;
        if (needMetadataForError)
        {
            int recordFailure = AddState(LoweredOpCode.RecordRuleFailure, metadataIndex, onFailure, onFailure);
            peekFailedExit = AddState(LoweredOpCode.FailRestore, 0, recordFailure, recordFailure);
        }
        else
        {
            peekFailedExit = AddState(LoweredOpCode.FailRestore, 0, onFailure, onFailure);
        }

        int innerEntry = LowerRule(rule.Children[0], peekSucceededExit, peekFailedExit);
        int pushIdx = AddState(LoweredOpCode.PushBacktrack, 0, innerEntry, innerEntry);

        if (needMetadataForWrap)
            return AddState(LoweredOpCode.OpenComposite, metadataIndex, pushIdx, onFailure);
        return pushIdx;
    }

    private int LowerScanUntil(ScanUntilRule rule, int onSuccess, int onFailure)
    {
        // Rule-stopper / rule-escape-start forms bridge to the
        // recursive evaluator. The Rune-mode native rule-stopper
        // scan (LowerScanUntilRuleStopper) inlined rune decode and
        // would split multi-rune graphemes under grapheme tokenization,
        // so it's gone. The bridge handles every shape the rune-mode
        // path used to handle.
        if (rule.LoweringStopperRule != null)
            return LowerViaBridge(rule, onSuccess, onFailure);
        if (rule.LoweringHasEscape && rule.LoweringEscapeStartRule != null)
            return LowerViaBridge(rule, onSuccess, onFailure);

        int stopperSetIdx = InternTokenSet(rule.LoweringStopperSet);
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
    // referencing the same rule (e.g. a shared Or used as the escape
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

    private static string GetLiteralExpected(LiteralRule rule) => rule.ExpectedText!;
    private static string GetGraphemeExpected(GraphemeRule rule) => rule.ExpectedText!;
    private static TokenSet GetOneOfSet(OneOfRule rule) => rule.LoweringSet;

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

    private int InternTokenSet(TokenSet set)
    {
        if (_tokenSetIndex.TryGetValue(set, out int existing)) return existing;
        int newIndex = TokenSets.Count;
        TokenSets.Add(set);
        _tokenSetIndex[set] = newIndex;
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
        // Grapheme('a') wrapper under PreserveAllSymbols still has
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
