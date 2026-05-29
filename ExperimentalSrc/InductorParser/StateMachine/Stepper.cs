using System;
using System.Runtime.CompilerServices;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;

namespace InductorParser.StateMachine;

// The state-machine driver. Run is the entry point. Step is the
// per-state body, switched by opcode. The switch is a dense small-int
// jump table after JIT lowering, which is the predictor-friendly
// shape an interpreter loop wants.
//
// Each Step_* helper is [MethodImpl(MethodImplOptions.AggressiveInlining)]
// so the JIT folds them into the switch case body. The result is one
// indirect branch per loop iteration (the switch's jump-table read)
// plus straight-line code per opcode body.
internal static class Stepper
{
    // Run the compiled program against the lexer, building output
    // ops as it goes. Returns true on HaltSuccess, false on HaltFailure.
    // The caller hands OutputOps to the TreeBuilder on success and
    // reads DeepestFailure / DeepestFailureMessage on failure.
    public static bool Run(CompiledProgram program, Lexer lexer, out Machine machineOut)
    {
        // Assign machineOut up front so its pooled buffers stay reachable
        // through `ref machineOut` mutations inside the loop. If a Step
        // throws (the budget-exceeded path under MaxDepth /
        // RuleCountLimit / Timeout / Cancellation), the caller can still
        // Release the machine to return its arrays / list to the pool.
        machineOut = new Machine(lexer, program);
        State[] states = program.States;
        int current = program.EntryState;

        while (current >= 0)
        {
            current = Step(in states[current], ref machineOut);
        }

        return current == State.HaltSuccess;
    }

    // The dispatcher. One switch on the opcode. Each case computes the
    // next state index and returns it. The caller's loop just keeps
    // feeding the result back as `current` until it goes negative.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step(in State state, ref Machine machine)
    {
        switch (state.OpCode)
        {
            case LoweredOpCode.MatchLiteral:
                return Step_MatchLiteral(in state, ref machine);
            case LoweredOpCode.MatchLiteralIgnoreAsciiCase:
                return Step_MatchLiteralIgnoreAsciiCase(in state, ref machine);
            case LoweredOpCode.MatchOneOf:
                return Step_MatchOneOf(in state, ref machine);
            case LoweredOpCode.MatchNoneOf:
                return Step_MatchNoneOf(in state, ref machine);
            case LoweredOpCode.MatchAnyToken:
                return Step_MatchAnyToken(in state, ref machine);
            case LoweredOpCode.MatchEof:
                return Step_MatchEof(in state, ref machine);
            case LoweredOpCode.Jump:
                return state.OnSuccess;
            case LoweredOpCode.PushBacktrack:
                return Step_PushBacktrack(in state, ref machine);
            case LoweredOpCode.PopBacktrack:
                return Step_PopBacktrack(in state, ref machine);
            case LoweredOpCode.FailRestore:
                return Step_FailRestore(in state, ref machine);
            case LoweredOpCode.PushBetween:
                return Step_PushBetween(in state, ref machine);
            case LoweredOpCode.PopIterationCheck:
                return Step_PopIterationCheck(in state, ref machine);
            case LoweredOpCode.BetweenExitCheckMin:
                return Step_BetweenExitCheckMin(in state, ref machine);
            case LoweredOpCode.BetweenIncrementCheckMax:
                return Step_BetweenIncrementCheckMax(in state, ref machine);
            case LoweredOpCode.Call:
                return Step_Call(in state, ref machine);
            case LoweredOpCode.CallSuppressOutputs:
                return Step_CallSuppressOutputs(in state, ref machine);
            case LoweredOpCode.ReturnSuccess:
                return Step_ReturnSuccess(in state, ref machine);
            case LoweredOpCode.ReturnFailure:
                return Step_ReturnFailure(in state, ref machine);
            case LoweredOpCode.OpenComposite:
                return Step_OpenComposite(in state, ref machine);
            case LoweredOpCode.CloseComposite:
                return Step_CloseComposite(in state, ref machine);
            case LoweredOpCode.EmitLeafLiteral:
                return Step_EmitLeafLiteral(in state, ref machine);
            case LoweredOpCode.EmitLeafOneOf:
                return Step_EmitLeafOneOf(in state, ref machine);
            case LoweredOpCode.LoadPeekedRune:
                return Step_LoadPeekedRune(in state, ref machine);
            case LoweredOpCode.CheckPeekedRuneInSet:
                return Step_CheckPeekedRuneInSet(in state, ref machine);
            case LoweredOpCode.LoadPeekedRuneAndJumpAlt:
                return Step_LoadPeekedRuneAndJumpAlt(in state, ref machine);
            case LoweredOpCode.ScanUntilFast:
                return Step_ScanUntilFast(in state, ref machine);
            case LoweredOpCode.EmitScanUntilLeaf:
                return Step_EmitScanUntilLeaf(in state, ref machine);
            case LoweredOpCode.BridgeToRecursive:
                return Step_BridgeToRecursive(in state, ref machine);
            case LoweredOpCode.ScannerSkipAdvance:
                return Step_ScannerSkipAdvance(in state, ref machine);
            case LoweredOpCode.RecordRuleFailure:
                return Step_RecordRuleFailure(in state, ref machine);
        }
        return State.HaltFailure;
    }

    // Record the rule's WithError text at the current lexer position
    // and continue to OnSuccess. state.Data is the SymbolMetadata
    // index for the rule whose ErrorMessage should ride along. The
    // call goes through Machine.RecordFailure so the "deepest failure
    // wins" rules apply: the message claims an empty equal-depth slot
    // an inner rule left, or it stamps a strictly-deeper position.
    // Used by Not / Peek on the inner-led-to-rule-failure exits, where
    // the recursive evaluator's lexer.RecordFailure(StartPosition,
    // ErrorMessage) call has to be reproduced. The matching call in
    // BetweenInclusive lives inline in Step_BetweenExitCheckMin.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_RecordRuleFailure(in State state, ref Machine machine)
    {
        string? errorMessage = machine.Program.SymbolMetadata[state.Data].ErrorMessage;
        machine.RecordFailure(machine.Lexer.Position, errorMessage);
        return state.OnSuccess;
    }

    // Bulk-skip at the top of a ZeroOrMore(Or(match..., AnyToken.Delete))
    // scanner loop. Advances the lexer to the next position where one of
    // the candidate matches could plausibly start, so the inner Or
    // doesn't waste an attempt + fail-over to AnyToken.Delete on every
    // non-candidate rune. Always succeeds (it just advances; never fails).
    // The actual scan logic lives in Lexer.AdvanceUntil*; we just feed it
    // the precomputed spec from CompiledProgram.ScannerSkipSpecs.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_ScannerSkipAdvance(in State state, ref Machine machine)
    {
        ScannerSkipSpec spec = machine.Program.ScannerSkipSpecs[state.Data];
        Lexer lexer = machine.Lexer;
        // One tick per scanner-skip dispatch. The underlying AdvanceUntil
        // inside the lexer is a single pass that can cross the whole
        // input, so without a tick here the surrounding ZeroOrMore frame
        // pushes alone wouldn't drive the periodic budget check.
        lexer.Budget.TickPeriodic();
        TokenSet candidates = machine.Program.TokenSets[spec.CandidatesTokenSetIndex];

        if (spec.Literals is { Length: > 0 })
        {
            int[]? positions = null;
            if (spec.UseLiteralPositionsCache)
            {
                machine.ScannerSkipPositions ??= new int[machine.Program.ScannerSkipSpecs.Length][];
                positions = machine.ScannerSkipPositions[state.Data];
                if (positions == null)
                {
                    positions = new int[spec.Literals.Length];
                    for (int i = 0; i < positions.Length; i++) positions[i] = -2;
                    machine.ScannerSkipPositions[state.Data] = positions;
                }
            }
            lexer.AdvanceUntilLiteralCandidateIn(candidates, spec.BmpCandidates, spec.Literals, positions);
        }
        else
        {
            lexer.AdvanceUntilRuneIn(candidates, spec.BmpCandidates);
        }
        return state.OnSuccess;
    }

    // Match opcodes pack their data field as:
    //   bits 0-15:  payload index (literal index, TokenSet index, or 0
    //               for the no-payload opcodes MatchAnyToken / MatchEof)
    //   bits 16-31: error-metadata index, or 0xFFFF for "no message"
    // The error-metadata index points at SymbolMetadata.ErrorMessage,
    // set by the rule's .WithError("...") call during grammar
    // construction. The match opcode reads it on the failure path so
    // RecordFailure surfaces the user's message at the deepest
    // position, mirroring what the recursive evaluator does.
    private const int NoErrorMetadata = 0xFFFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? ResolveMatchErrorMessage(in Machine machine, int data)
    {
        int errorMetaIndex = (int)((uint)data >> 16);
        return errorMetaIndex == NoErrorMetadata
            ? null
            : machine.Program.SymbolMetadata[errorMetaIndex].ErrorMessage;
    }

    // MatchLiteral consumes tokens until the expected string is fully
    // matched, or fails on the first mismatch. Atomic: on failure the
    // lexer position is rolled back to the entry value, so no
    // surrounding backtrack frame is needed.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchLiteral(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int literalIndex = state.Data & 0xFFFF;
        string expected = machine.Program.Literals[literalIndex];
        int entryPosition = lexer.Position;
        int consumed = 0;

        while (consumed < expected.Length)
        {
            int tokenStart = lexer.Position;
            if (lexer.IsEof)
            {
                lexer.SetPositionUnchecked(entryPosition);
                machine.RecordFailure(tokenStart, ResolveMatchErrorMessage(in machine, state.Data));
                return state.OnFailure;
            }
            var token = lexer.Read();
            if (consumed + token.Length > expected.Length
                || !token.Chars.SequenceEqual(expected.AsSpan(consumed, token.Length)))
            {
                lexer.SetPositionUnchecked(entryPosition);
                machine.RecordFailure(tokenStart, ResolveMatchErrorMessage(in machine, state.Data));
                return state.OnFailure;
            }
            consumed += token.Length;
        }
        return state.OnSuccess;
    }

    // MatchOneOf reads one token and checks the rune-value membership
    // against the named TokenSet. EOF or a multi-rune grapheme token
    // (which has RuneValue == -1) fails. Records the token start on
    // success so the matching EmitLeaf can build the leaf span.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchOneOf(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int entryPosition = lexer.Position;
        int tokenSetIndex = state.Data & 0xFFFF;
        if (lexer.IsEof)
        {
            machine.RecordFailure(entryPosition, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }
        var token = lexer.Read();
        TokenSet set = machine.Program.TokenSets[tokenSetIndex];
        if (!set.ContainsRune(token.RuneValue))
        {
            lexer.SetPositionUnchecked(entryPosition);
            machine.RecordFailure(entryPosition, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }
        machine.LastConsumedTokenStart = entryPosition;
        return state.OnSuccess;
    }

    // MatchLiteralIgnoreAsciiCase mirrors MatchLiteral but compares each
    // token to the corresponding section of expected with ASCII letters
    // treated case-insensitively. Non-ASCII chars compare bit-exact, so
    // Greek / Cyrillic / Turkish casing pairs are NOT folded. Same scope
    // as the recursive LiteralIgnoreAsciiCaseRule.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchLiteralIgnoreAsciiCase(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int literalIndex = state.Data & 0xFFFF;
        string expected = machine.Program.Literals[literalIndex];
        int entryPosition = lexer.Position;
        int consumed = 0;

        while (consumed < expected.Length)
        {
            int tokenStart = lexer.Position;
            if (lexer.IsEof)
            {
                lexer.SetPositionUnchecked(entryPosition);
                machine.RecordFailure(tokenStart, ResolveMatchErrorMessage(in machine, state.Data));
                return state.OnFailure;
            }
            var token = lexer.Read();
            if (consumed + token.Length > expected.Length
                || !AsciiCaseEquals(token.Chars, expected.AsSpan(consumed, token.Length)))
            {
                lexer.SetPositionUnchecked(entryPosition);
                machine.RecordFailure(tokenStart, ResolveMatchErrorMessage(in machine, state.Data));
                return state.OnFailure;
            }
            consumed += token.Length;
        }
        return state.OnSuccess;
    }

    // ASCII-only case-insensitive compare. Bit-exact match for non-letters,
    // case-invariant match within A-Z / a-z. Same logic as the recursive
    // LiteralIgnoreAsciiCaseRule.AsciiCaseEquals.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AsciiCaseEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i];
            char cb = b[i];
            if (ca == cb) continue;
            if (IsAsciiLetter(ca) && IsAsciiLetter(cb) && (ca | 0x20) == (cb | 0x20)) continue;
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiLetter(char c) =>
        (uint)((c | 0x20) - 'a') <= ('z' - 'a');

    // MatchNoneOf mirrors MatchOneOf but with the membership test
    // inverted. Multi-rune tokens (RuneValue == -1) trivially aren't
    // single runes in any set, so they pass NoneOf unconditionally
    // when the set has no multi-rune entries. EOF still fails.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchNoneOf(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int entryPosition = lexer.Position;
        int tokenSetIndex = state.Data & 0xFFFF;
        if (lexer.IsEof)
        {
            machine.RecordFailure(entryPosition, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }
        var token = lexer.Read();
        int runeValue = token.RuneValue;
        if (runeValue >= 0)
        {
            TokenSet set = machine.Program.TokenSets[tokenSetIndex];
            if (set.ContainsRune(runeValue))
            {
                lexer.SetPositionUnchecked(entryPosition);
                machine.RecordFailure(entryPosition, ResolveMatchErrorMessage(in machine, state.Data));
                return state.OnFailure;
            }
        }
        machine.LastConsumedTokenStart = entryPosition;
        return state.OnSuccess;
    }

    // MatchAnyToken consumes one token, whatever it is. Fails only at
    // EOF. The token is whatever the lexer hands back: one
    // user-visible character (a grapheme cluster), possibly built
    // from several runes underneath.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchAnyToken(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int entryPosition = lexer.Position;
        if (lexer.IsEof)
        {
            machine.RecordFailure(entryPosition, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }
        lexer.Read();
        machine.LastConsumedTokenStart = entryPosition;
        return state.OnSuccess;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchEof(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        if (lexer.IsEof)
            return state.OnSuccess;
        machine.RecordFailure(lexer.Position, ResolveMatchErrorMessage(in machine, state.Data));
        return state.OnFailure;
    }

    // [Rune-only opcode handlers removed in Step 2: with one (grapheme)
    // lexer the inline rune-decode handlers (MatchOneOfRune /
    // MatchNoneOfRune / MatchAnyTokenRune / MatchLiteralRune /
    // MatchLiteralIgnoreAsciiCaseRune / PeekRejectOneOfRune /
    // PeekRejectLiteralRune / ScanLiteralOneOfRune /
    // ScanUntilStopperEligibleRune / AdvanceOneRune / ScanOneOfRune /
    // ScanNoneOfRune / ScanAnyTokenRune) are no longer emitted by the
    // lowerer. The non-rune opcodes (MatchOneOf / MatchNoneOf /
    // MatchAnyToken / MatchLiteral / MatchLiteralIgnoreAsciiCase /
    // generic Not lowering / generic Between loop / BridgeToRecursive
    // for rule-stoppered ScanUntil) handle the same shapes through
    // Lexer.Read.]
    // "é" against input "ex" matches the first rune ('e') and
    // Inline rune decoder used by LoadPeekedRuneAndJumpAlt.
    // Precondition: entryPos < inputLen. Returns the rune value
    // (-1 for a stray surrogate, matching Token.RuneValue) and the
    // number of chars consumed (1 or 2).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeRuneInline(string input, int entryPos, int inputLen, out int runeValue, out int runeLen)
    {
        char c = input[entryPos];
        if (char.IsHighSurrogate(c))
        {
            if (entryPos + 1 < inputLen && char.IsLowSurrogate(input[entryPos + 1]))
            {
                runeValue = char.ConvertToUtf32(c, input[entryPos + 1]);
                runeLen = 2;
                return;
            }
            runeValue = -1;
            runeLen = 1;
            return;
        }
        if (char.IsLowSurrogate(c))
        {
            runeValue = -1;
            runeLen = 1;
            return;
        }
        runeValue = c;
        runeLen = 1;
    }

    // Push a backtrack frame snapshotting lexer position, emit cursor,
    // and call-stack height. Used as the per-alternative frame for Or,
    // the per-iteration frame inside BetweenInclusive's loop, and the
    // wrapper frame for Not / Peek.
    //
    // PushBacktrack is the SM's "doing real branching work" signal on the
    // inlined path. Non-cyclic grammars never hit Step_Call, so without
    // this tick the periodic budget check (RuleCountLimit / Timeout /
    // Cancellation) would never fire and a pathological grammar could
    // run forever. Counting frame pushes mirrors the recursive engine's
    // "every TryParseRule invocation counts" rate well enough on
    // catastrophic-backtracking shapes (the thing the budget is for).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_PushBacktrack(in State state, ref Machine machine)
    {
        machine.Lexer.Budget.TickPeriodic();
        machine.PushBacktrack(machine.Lexer.Position, machine.OutputOps.Count, machine.CallTop);
        return state.OnSuccess;
    }

    // Discard the topmost backtrack frame on the success path. Lexer
    // and emit cursor stay where the matched alternative left them.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_PopBacktrack(in State state, ref Machine machine)
    {
        machine.BacktrackTop--;
        return state.OnSuccess;
    }

    // Restore lexer, emit cursor, and call-stack height from the
    // topmost frame, then continue at OnSuccess. Lowering routes the
    // OnFailure of every state inside a backtrack region here, so
    // FailRestore is the universal "an alternative failed, recover"
    // handler.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_FailRestore(in State state, ref Machine machine)
    {
        ref var frame = ref machine.BacktrackStack[--machine.BacktrackTop];
        machine.Lexer.SetPositionUnchecked(frame.LexerPosition);
        machine.TruncateOutputs(frame.EmitCursor);
        machine.CallTop = frame.CallStackHeight;
        return state.OnSuccess;
    }

    // Push the BetweenInclusive loop frame. LexerPosition / EmitCursor
    // are the entry values used for full rollback if the loop ends
    // below atLeast. Counter starts at 0. AtLeast / AtMost are packed
    // into state.Data: low 16 bits = atLeast, high 16 bits = atMost,
    // with 0xFFFF in the high half meaning int.MaxValue.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_PushBetween(in State state, ref Machine machine)
    {
        machine.Lexer.Budget.TickPeriodic();
        int dataPacked = state.Data;
        int atLeast = dataPacked & 0xFFFF;
        int atMost = (dataPacked >> 16) & 0xFFFF;
        if (atMost == 0xFFFF) atMost = int.MaxValue;
        machine.PushBetween(machine.Lexer.Position, machine.OutputOps.Count, machine.CallTop, atLeast, atMost);
        return state.OnSuccess;
    }

    // PopIterationCheck runs at the end of a successful inner-rule
    // attempt inside a BetweenInclusive loop. The topmost frame is the
    // per-iteration PushBacktrack frame. The frame under it is the
    // PushBetween frame holding the counter and bounds.
    //
    // Pops the per-iteration frame, increments the Between counter,
    // applies the zero-width guard (inner advanced the lexer) and the
    // upper-bound check. If either fails, exits the loop via OnFailure
    // (which the lowerer pointed at BetweenExitCheckMin). On continue,
    // the lowerer routes OnSuccess back to the loop's PushBacktrack.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_PopIterationCheck(in State state, ref Machine machine)
    {
        ref var perIter = ref machine.BacktrackStack[machine.BacktrackTop - 1];
        bool advanced = machine.Lexer.Position > perIter.LexerPosition;
        machine.BacktrackTop--;
        ref var between = ref machine.BacktrackStack[machine.BacktrackTop - 1];
        between.Counter++;
        if (!advanced || between.Counter >= between.AtMost)
            return state.OnFailure;
        return state.OnSuccess;
    }

    // Loop exit. Pops the Between frame. If the counter reached
    // atLeast, the rule succeeds. Otherwise it restores from the
    // entry-time snapshot the Between frame stored and reports
    // failure.
    // Fast-path increment used by BetweenInclusive when the inner rule
    // is atomic and always advances (Literal, Grapheme, OneOf). The
    // generic loop's per-iteration backtrack frame and zero-width
    // guard aren't needed here, so this drops them and only
    // bumps the counter and checks the upper bound.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_BetweenIncrementCheckMax(in State state, ref Machine machine)
    {
        // The atomic-inner BetweenInclusive loop runs without any
        // per-iteration PushBacktrack, so this is the only opcode that
        // fires on every iteration. Tick the budget here so a tight
        // loop like OneOrMore(OneOf(letters)) on a long input still
        // drives the periodic RuleCountLimit / Timeout / Cancellation
        // check.
        machine.Lexer.Budget.TickPeriodic();
        ref var between = ref machine.BacktrackStack[machine.BacktrackTop - 1];
        between.Counter++;
        if (between.Counter < between.AtMost)
            return state.OnSuccess;
        return state.OnFailure;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_BetweenExitCheckMin(in State state, ref Machine machine)
    {
        ref var between = ref machine.BacktrackStack[--machine.BacktrackTop];
        if (between.Counter >= between.AtLeast)
            return state.OnSuccess;
        // Record the failure at the loop's stop position (where inner
        // gave up) BEFORE rolling the lexer back to entry, so the
        // rule's WithError text rides along. Mirrors the recursive
        // BetweenInclusive: it calls lexer.RecordFailure(lexer.Position,
        // ErrorMessage) before its `using` transaction's Dispose
        // restores the lexer. state.Data is the SymbolMetadata index
        // (or -1 when the rule has no WithError), encoded by Lowerer.
        // RecordFailure with a null message at the same depth is a
        // no-op, so a metadata entry that exists only for the
        // composite wrapper (no WithError on the rule) doesn't leak
        // an unwanted message into DeepestFailureMessage.
        string? errorMessage = state.Data >= 0
            ? machine.Program.SymbolMetadata[state.Data].ErrorMessage
            : null;
        machine.RecordFailure(machine.Lexer.Position, errorMessage);
        machine.Lexer.SetPositionUnchecked(between.LexerPosition);
        machine.TruncateOutputs(between.EmitCursor);
        machine.CallTop = between.CallStackHeight;
        return state.OnFailure;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_Call(in State state, ref Machine machine)
    {
        // Look up the source rule so the matching ReturnSuccess /
        // ReturnFailure can label its trace line. Cyclic-rule entries
        // and ScanUntil's escape-end / rule-stopper subprograms all
        // register themselves during lowering, so the lookup hits for
        // every Call the lowerer emits.
        machine.Program.SubprogramRuleByEntry.TryGetValue(state.Data, out Rule? sourceRule);
        machine.PushCall(state.OnSuccess, state.OnFailure, suppressOutputsCursor: -1, sourceRule);
        // The SM emits Call only at cyclic-rule entry (Lowerer.LowerCyclicCall);
        // non-cyclic rules are inlined. CallTop after the push is the
        // current call depth, which the lexer's budget check compares
        // against MaxDepth and increments the periodic-check counter. No
        // matching ExitRule is needed because backtrack
        // frames carry CallStackHeight and restore CallTop directly when
        // they fire.
        machine.Lexer.Budget.EnterRuleAtDepth(machine.CallTop);
        return state.Data;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_CallSuppressOutputs(in State state, ref Machine machine)
    {
        machine.Program.SubprogramRuleByEntry.TryGetValue(state.Data, out Rule? sourceRule);
        machine.PushCall(state.OnSuccess, state.OnFailure, machine.OutputOps.Count, sourceRule);
        machine.Lexer.Budget.EnterRuleAtDepth(machine.CallTop);
        return state.Data;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_ReturnSuccess(in State state, ref Machine machine)
    {
        ref var frame = ref machine.CallStack[--machine.CallTop];
        if (frame.SuppressOutputsCursor >= 0)
            machine.TruncateOutputs(frame.SuppressOutputsCursor);
        EmitCallReturnTrace(machine.Lexer, frame.CallSourceRule, TraceOutcome.Success);
        return frame.OnSuccess;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_ReturnFailure(in State state, ref Machine machine)
    {
        ref var frame = ref machine.CallStack[--machine.CallTop];
        if (frame.SuppressOutputsCursor >= 0)
            machine.TruncateOutputs(frame.SuppressOutputsCursor);
        EmitCallReturnTrace(machine.Lexer, frame.CallSourceRule, TraceOutcome.Failure);
        return frame.OnFailure;
    }

    // Emit a single trace line at the rule-call boundary. Mirrors the
    // recursive engine's TraceSuccess / TraceFailure on a rule's
    // TryParseRule exit, but without the rule-specific body text the
    // recursive engine builds (the SM doesn't run the per-rule
    // TryParseRule body, so it doesn't compute "count= 3" / "found 3"
    // / etc.). The label is the same "{Name}:{ruleClassName}" string
    // the recursive engine uses, so callers can assert on rule names
    // visited and outcomes across both engines.
    //
    // Gated by IsTracing so the off-path cost is one bool check per
    // Call / Return* opcode. The per-frame Rule reference on the call
    // stack is paid whether tracing is on or off, but it's a single
    // reference field on a stack frame allocated once at parse start.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitCallReturnTrace(Lexer lexer, Rule? sourceRule, TraceOutcome outcome)
    {
        if (sourceRule == null) return;
        if (!lexer.IsTracing(TraceLevel.Diagnostic)) return;
        lexer.WriteTraceLine(sourceRule.TraceLabel, outcome, "");
    }

    // Step_BridgeToRecursive delegates to the rule's recursive
    // TryParse, then translates whatever Symbols it produced into
    // Prebuilt output ops so the surrounding state-machine tree
    // sees them as if a native opcode emitted them. Used for rule
    // types the lowerer doesn't have a native opcode for.
    //
    // Failure path: the recursive evaluator already rolled the lexer
    // back via its own Transaction, so we just record the deepest
    // failure (preferring the recursive evaluator's own deepest
    // tracking when it surfaced a deeper one) and return OnFailure.
    private static int Step_BridgeToRecursive(in State state, ref Machine machine)
    {
        Rule rule = machine.Program.BridgeRules[state.Data];

        // The recursive evaluator's TryParse may write into a caller-
        // supplied list when the rule's effective FlattenType is
        // Flatten. Pass a fresh list so any flattened children land
        // here for us to forward into our output stream.
        var collected = new System.Collections.Generic.List<SyntaxTree.Symbol>();
        SyntaxTree.Symbol? result = rule.TryParse(machine.Lexer, collected);

        if (result == null && collected.Count == 0)
        {
            // Rule failed. The recursive evaluator's per-rule
            // RecordFailure already updated the lexer's deepest tracker.
            // Mirror that into our own deepest tracker via the same
            // "deepest failure wins" rules Machine.RecordFailure
            // applies: a strictly-deeper position takes the (possibly
            // null) message, and a non-null message at the current
            // deepest claims the slot when nobody filled it yet. The
            // equal-depth claim is what lets a WithError-bearing rule
            // (ScanWhileRule, WithinGraphemeRule) surface its message
            // when a sibling already recorded an empty slot at the
            // same position.
            machine.RecordFailure(
                machine.Lexer.DeepestFailurePosition,
                machine.Lexer.DeepestFailureMessage);
            return state.OnFailure;
        }

        // Translate the recursive evaluator's output into Prebuilt
        // output ops:
        //   * Preserve effective: result is the wrapper Symbol; emit it.
        //   * Flatten effective: result == Discarded, collected has
        //     children; emit each as a Prebuilt op.
        //   * Delete effective: result == Discarded, collected empty;
        //     emit nothing.
        if (result != null && !ReferenceEquals(result, SyntaxTree.Symbol.Discarded))
        {
            machine.OutputOps.Add(OutputOp.Prebuilt(result));
        }
        for (int i = 0; i < collected.Count; i++)
        {
            machine.OutputOps.Add(OutputOp.Prebuilt(collected[i]));
        }
        return state.OnSuccess;
    }

    // [METHOD-DELETED-Step_ScanOneOfRune]

    // [METHOD-DELETED-Step_ScanNoneOfRune]

    // [METHOD-DELETED-Step_ScanAnyTokenRune]

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_OpenComposite(in State state, ref Machine machine)
    {
        SymbolMetadata metadata = machine.Program.SymbolMetadata[state.Data];
        machine.OutputOps.Add(OutputOp.Open(metadata.Id, metadata.FlattenType));
        return state.OnSuccess;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_CloseComposite(in State state, ref Machine machine)
    {
        machine.OutputOps.Add(OutputOp.Close());
        return state.OnSuccess;
    }

    // EmitLeafLiteral fires after a successful MatchLiteral (or
    // MatchLiteralIgnoreAsciiCase) and emits a leaf carrying the
    // matched text. State.Data uses the same low-16 / high-16 packing
    // as the matching opcode: low 16 = literal index, high 16 =
    // metadata index. Reusing the layout means lowering can pack
    // both states' Data identically.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_EmitLeafLiteral(in State state, ref Machine machine)
    {
        int dataPacked = state.Data;
        int literalIndex = dataPacked & 0xFFFF;
        int metadataIndex = (int)((uint)dataPacked >> 16);
        SymbolMetadata metadata = machine.Program.SymbolMetadata[metadataIndex];
        string literal = machine.Program.Literals[literalIndex];
        int endPosition = machine.Lexer.Position;
        int startPosition = endPosition - literal.Length;
        machine.OutputOps.Add(OutputOp.Leaf(metadata.Id, metadata.FlattenType, startPosition, literal.Length));
        return state.OnSuccess;
    }

    // LoadPeekedRune peeks the next rune at the current lexer position
    // (without consuming) and stashes the value on machine.PeekedRune.
    // EOF or a stray surrogate yields -1, which fails membership in
    // any non-Universe set. Always succeeds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_LoadPeekedRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int pos = lexer.Position;
        int runeValue = -1;
        if (pos < input.Length)
            Lexer.TryPeekRune(input, pos, out runeValue, out _);
        machine.PeekedRune = runeValue;
        return state.OnSuccess;
    }

    // CheckPeekedRuneInSet returns success iff the stashed PeekedRune
    // is in the named TokenSet. Used by the Or first-rune-skip lowering
    // to drop alternatives whose FirstConsumedTokens can't possibly
    // match the next input rune.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_CheckPeekedRuneInSet(in State state, ref Machine machine)
    {
        TokenSet set = machine.Program.TokenSets[state.Data];
        if (set.ContainsRune(machine.PeekedRune))
            return state.OnSuccess;
        return state.OnFailure;
    }

    // LoadPeekedRuneAndJumpAlt fuses the LoadPeekedRune + leading
    // CheckPeekedRuneInSet chain for a Or. Decodes one rune at the
    // current lexer position; for an ASCII rune jumps directly to the
    // table-determined alternative's PushBacktrack (or to the
    // all-alts-failed target). For non-ASCII or EOF stashes -1 / the
    // rune in PeekedRune and falls through to OnSuccess (the original
    // chain head), so any subsequent CheckPeekedRuneInSet checks for
    // alternatives whose first sets contain non-ASCII runes still
    // work. PeekedRune is also stashed on the ASCII path so that if
    // the chosen alt fails internally, the FailRestore-routed jump to
    // the next alt's CheckPeekedRuneInSet has a current PeekedRune to
    // read.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_LoadPeekedRuneAndJumpAlt(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int pos = lexer.Position;
        int inputLen = input.Length;

        if (pos >= inputLen)
        {
            machine.PeekedRune = -1;
            return state.OnSuccess;
        }

        DecodeRuneInline(input, pos, inputLen, out int runeValue, out _);
        machine.PeekedRune = runeValue;

        if ((uint)runeValue < 128u)
        {
            int[] table = machine.Program.OrJumpTables[state.Data];
            return table[runeValue];
        }

        return state.OnSuccess;
    }

    // ScanUntilFast walks runes from the current lexer position
    // until one of three exits:
    //   * Stopper rune found: returns OnSuccess (loop done, ready to
    //     emit the leaf).
    //   * EOF or malformed surrogate: returns OnSuccess when the spec
    //     says eofIsTerminator is true (the tolerant and ScanUntilEof
    //     cases). Otherwise returns spec.OnEofFailState (the outerFail
    //     state the lowerer wires up), failing the rule as
    //     "unterminated body."
    //   * Escape-start rune found: consumes the start rune and returns
    //     OnFailure. The lowerer wires OnFailure to a Call(escapeEnd)
    //     state whose OnSuccess routes back to this scan state, so the
    //     loop resumes after the escape body completes.
    //
    // Operates rune-by-rune because the stopper-set and escape-start
    // checks are rune-scoped. Same as the recursive ScanUntilRule.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_ScanUntilFast(in State state, ref Machine machine)
    {
        ScanUntilSpec spec = machine.Program.ScanUntilSpecs[state.Data];
        TokenSet stopperSet = machine.Program.TokenSets[spec.StopperSetIndex];
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int inputLen = input.Length;

        while (true)
        {
            lexer.Budget.TickPeriodic();
            int pos = lexer.Position;
            if (pos >= inputLen)
                return spec.EofIsTerminator ? state.OnSuccess : spec.OnEofFailState;

            char first = input[pos];
            int runeValue;
            int runeLen;
            if (char.IsHighSurrogate(first)
                && pos + 1 < inputLen
                && char.IsLowSurrogate(input[pos + 1]))
            {
                runeValue = char.ConvertToUtf32(first, input[pos + 1]);
                runeLen = 2;
            }
            else if (char.IsSurrogate(first))
            {
                // Stray surrogate halves can't form a rune. Halting
                // here means we never found the stopper, so the
                // success / fail decision matches real EOF.
                return spec.EofIsTerminator ? state.OnSuccess : spec.OnEofFailState;
            }
            else
            {
                runeValue = first;
                runeLen = 1;
            }

            if (stopperSet.ContainsRune(runeValue)) return state.OnSuccess;

            if (spec.HasEscape && runeValue == spec.EscapeStartRune)
            {
                // Consume the escape-start rune in place, then route
                // to the escape-end Call state via OnFailure.
                lexer.SetPositionUnchecked(pos + runeLen);
                return state.OnFailure;
            }

            // Body rune. Advance and keep scanning.
            lexer.SetPositionUnchecked(pos + runeLen);
        }
    }

    // EmitScanUntilLeaf builds the leaf Symbol that covers the entire
    // scanned range. The start position lives on the topmost backtrack
    // frame's LexerPosition (pushed by the lowerer at ScanUntil
    // entry); the end position is the lexer's current position.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_EmitScanUntilLeaf(in State state, ref Machine machine)
    {
        ref var frame = ref machine.BacktrackStack[machine.BacktrackTop - 1];
        int startPosition = frame.LexerPosition;
        int endPosition = machine.Lexer.Position;
        int length = endPosition - startPosition;
        SymbolMetadata metadata = machine.Program.SymbolMetadata[state.Data];
        machine.OutputOps.Add(OutputOp.Leaf(metadata.Id, metadata.FlattenType, startPosition, length));
        return state.OnSuccess;
    }

    // EmitLeafOneOf (also used by NoneOf and AnyToken) fires after a
    // successful per-token match. The matched span comes from
    // machine.LastConsumedTokenStart..lexer.Position, set by the
    // matching opcode. The leaf's SymbolId is the matched rune's
    // code point when the token is exactly one rune (1 char for BMP,
    // 2 chars for a surrogate pair), or the rule's own Id when the
    // token is a multi-rune grapheme. This mirrors what OneOfRule /
    // NoneOfRule / AnyTokenRule do: rune leaves carry the rune's
    // value as their id; multi-rune leaves fall back to the rule id
    // assigned at Compile time.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_EmitLeafOneOf(in State state, ref Machine machine)
    {
        SymbolMetadata template = machine.Program.SymbolMetadata[state.Data];
        int startPosition = machine.LastConsumedTokenStart;
        int endPosition = machine.Lexer.Position;
        int length = endPosition - startPosition;
        string input = machine.Lexer.Input;

        SymbolId leafId;
        if (length == 1)
        {
            leafId = new SymbolId(input[startPosition]);
        }
        else if (length == 2
            && char.IsHighSurrogate(input[startPosition])
            && char.IsLowSurrogate(input[startPosition + 1]))
        {
            leafId = new SymbolId(char.ConvertToUtf32(input[startPosition], input[startPosition + 1]));
        }
        else
        {
            // Multi-rune grapheme. Use the rule's own id rather than
            // a code-point id, matching the recursive rules' behavior.
            leafId = template.Id;
        }
        machine.OutputOps.Add(OutputOp.Leaf(leafId, template.FlattenType, startPosition, length));
        return state.OnSuccess;
    }
}
