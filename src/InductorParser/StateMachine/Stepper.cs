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
            case LoweredOpCode.ScanOneOfRune:
                return Step_ScanOneOfRune(in state, ref machine);
            case LoweredOpCode.ScanNoneOfRune:
                return Step_ScanNoneOfRune(in state, ref machine);
            case LoweredOpCode.ScanAnyTokenRune:
                return Step_ScanAnyTokenRune(in state, ref machine);
            case LoweredOpCode.MatchOneOfRune:
                return Step_MatchOneOfRune(in state, ref machine);
            case LoweredOpCode.MatchNoneOfRune:
                return Step_MatchNoneOfRune(in state, ref machine);
            case LoweredOpCode.MatchAnyTokenRune:
                return Step_MatchAnyTokenRune(in state, ref machine);
            case LoweredOpCode.MatchLiteralRune:
                return Step_MatchLiteralRune(in state, ref machine);
            case LoweredOpCode.MatchLiteralIgnoreAsciiCaseRune:
                return Step_MatchLiteralIgnoreAsciiCaseRune(in state, ref machine);
            case LoweredOpCode.PeekRejectOneOfRune:
                return Step_PeekRejectOneOfRune(in state, ref machine);
            case LoweredOpCode.PeekRejectLiteralRune:
                return Step_PeekRejectLiteralRune(in state, ref machine);
            case LoweredOpCode.ScanLiteralOneOfRune:
                return Step_ScanLiteralOneOfRune(in state, ref machine);
            case LoweredOpCode.ScanUntilStopperEligibleRune:
                return Step_ScanUntilStopperEligibleRune(in state, ref machine);
            case LoweredOpCode.AdvanceOneRune:
                return Step_AdvanceOneRune(in state, ref machine);
            case LoweredOpCode.ScannerSkipAdvance:
                return Step_ScannerSkipAdvance(in state, ref machine);
        }
        return State.HaltFailure;
    }

    // Bulk-skip at the top of a ZeroOrMore(FirstOf(match..., AnyToken.Delete))
    // scanner loop. Advances the lexer to the next position where one of
    // the candidate matches could plausibly start, so the inner FirstOf
    // doesn't waste an attempt + fail-over to AnyToken.Delete on every
    // non-candidate rune. Always succeeds (it just advances; never fails).
    // The actual scan logic lives in Lexer.AdvanceUntil*; we just feed it
    // the precomputed spec from CompiledProgram.ScannerSkipSpecs.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_ScannerSkipAdvance(in State state, ref Machine machine)
    {
        ScannerSkipSpec spec = machine.Program.ScannerSkipSpecs[state.Data];
        Lexer lexer = machine.Lexer;
        RuneSet candidates = machine.Program.RuneSets[spec.CandidatesRuneSetIndex];

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
    //   bits 0-15:  payload index (literal index, RuneSet index, or 0
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
    // against the named RuneSet. EOF or a multi-rune grapheme token
    // (which has RuneValue == -1) fails. Records the token start on
    // success so the matching EmitLeaf can build the leaf span.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchOneOf(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int entryPosition = lexer.Position;
        int runeSetIndex = state.Data & 0xFFFF;
        if (lexer.IsEof)
        {
            machine.RecordFailure(entryPosition, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }
        var token = lexer.Read();
        RuneSet set = machine.Program.RuneSets[runeSetIndex];
        if (!set.Contains(token.RuneValue))
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
    // case-folded match within A-Z / a-z. Same logic as the recursive
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
    // inverted. Multi-rune grapheme tokens (RuneValue == -1 under
    // GraphemeLexer) trivially aren't single runes in any set, so they
    // pass NoneOf unconditionally. EOF still fails.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchNoneOf(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int entryPosition = lexer.Position;
        int runeSetIndex = state.Data & 0xFFFF;
        if (lexer.IsEof)
        {
            machine.RecordFailure(entryPosition, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }
        var token = lexer.Read();
        int runeValue = token.RuneValue;
        if (runeValue >= 0)
        {
            RuneSet set = machine.Program.RuneSets[runeSetIndex];
            if (set.Contains(runeValue))
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
    // EOF. Under GraphemeLexer the token is a grapheme cluster; under
    // RuneLexer it's a single rune.
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

    // Rune-inline variants of the standalone Match opcodes. Same
    // shape as the originals but the per-token read is inlined here
    // instead of going through Lexer.Read (which constructs a Token
    // ref struct and dispatches NextTokenLength virtually). Lowering
    // emits these only under InputUnit.Rune.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchOneOfRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int entryPos = lexer.Position;
        string input = lexer.Input;
        int inputLen = input.Length;
        int runeSetIndex = state.Data & 0xFFFF;

        if (entryPos >= inputLen)
        {
            machine.RecordFailure(entryPos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }

        DecodeRuneInline(input, entryPos, inputLen, out int runeValue, out int runeLen);

        RuneSet set = machine.Program.RuneSets[runeSetIndex];
        if (!set.Contains(runeValue))
        {
            machine.RecordFailure(entryPos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }
        lexer.SetPositionUnchecked(entryPos + runeLen);
        machine.LastConsumedTokenStart = entryPos;
        return state.OnSuccess;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchNoneOfRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int entryPos = lexer.Position;
        string input = lexer.Input;
        int inputLen = input.Length;
        int runeSetIndex = state.Data & 0xFFFF;

        if (entryPos >= inputLen)
        {
            machine.RecordFailure(entryPos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }

        DecodeRuneInline(input, entryPos, inputLen, out int runeValue, out int runeLen);

        // NoneOf matches when the rune is NOT in the stop set. EOF
        // already failed above. RuneValue == -1 (stray surrogate)
        // can't be in any set, so it passes (same as the recursive
        // NoneOfRule which lets multi-rune RuneValue==-1 through).
        if (runeValue >= 0)
        {
            RuneSet set = machine.Program.RuneSets[runeSetIndex];
            if (set.Contains(runeValue))
            {
                machine.RecordFailure(entryPos, ResolveMatchErrorMessage(in machine, state.Data));
                return state.OnFailure;
            }
        }
        lexer.SetPositionUnchecked(entryPos + runeLen);
        machine.LastConsumedTokenStart = entryPos;
        return state.OnSuccess;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchAnyTokenRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int entryPos = lexer.Position;
        string input = lexer.Input;
        int inputLen = input.Length;

        if (entryPos >= inputLen)
        {
            machine.RecordFailure(entryPos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }

        DecodeRuneInline(input, entryPos, inputLen, out _, out int runeLen);
        lexer.SetPositionUnchecked(entryPos + runeLen);
        machine.LastConsumedTokenStart = entryPos;
        return state.OnSuccess;
    }

    // MatchLiteralRune compares the literal against input directly via
    // SequenceEqual instead of looping per token through Lexer.Read.
    // Under RuneLexer this is correct as long as the literal doesn't
    // end in a stray high surrogate that would pair with the input's
    // next char (in which case the recursive evaluator would fail
    // because the rune-token at the end exceeds the literal's length).
    // For real grammars this edge case never appears. The trailing
    // half-surrogate guard catches it cheaply on the success path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchLiteralRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int literalIndex = state.Data & 0xFFFF;
        string expected = machine.Program.Literals[literalIndex];
        int entryPos = lexer.Position;
        string input = lexer.Input;
        int inputLen = input.Length;
        int expectedLen = expected.Length;

        if (entryPos + expectedLen > inputLen
            || !input.AsSpan(entryPos, expectedLen).SequenceEqual(expected.AsSpan()))
        {
            machine.RecordFailure(entryPos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }

        // Trailing-surrogate guard: if the literal ends mid-surrogate-
        // pair with the input, the recursive rune lexer would have
        // consumed past the literal and failed. Mirror that here.
        if (expectedLen > 0
            && char.IsHighSurrogate(expected[expectedLen - 1])
            && entryPos + expectedLen < inputLen
            && char.IsLowSurrogate(input[entryPos + expectedLen]))
        {
            machine.RecordFailure(entryPos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }

        lexer.SetPositionUnchecked(entryPos + expectedLen);
        return state.OnSuccess;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_MatchLiteralIgnoreAsciiCaseRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int literalIndex = state.Data & 0xFFFF;
        string expected = machine.Program.Literals[literalIndex];
        int entryPos = lexer.Position;
        string input = lexer.Input;
        int inputLen = input.Length;
        int expectedLen = expected.Length;

        if (entryPos + expectedLen > inputLen
            || !AsciiCaseEquals(input.AsSpan(entryPos, expectedLen), expected.AsSpan()))
        {
            machine.RecordFailure(entryPos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }

        // Same trailing-surrogate guard as MatchLiteralRune. ASCII-only
        // grammars hit it for free since IsHighSurrogate is false on
        // every ASCII char.
        if (expectedLen > 0
            && char.IsHighSurrogate(expected[expectedLen - 1])
            && entryPos + expectedLen < inputLen
            && char.IsLowSurrogate(input[entryPos + expectedLen]))
        {
            machine.RecordFailure(entryPos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }

        lexer.SetPositionUnchecked(entryPos + expectedLen);
        return state.OnSuccess;
    }

    // PeekRejectOneOfRune fuses Not(OneOf(set)) into a single
    // peek-and-check. Zero-width: never advances the lexer. Returns
    // success when the next rune is NOT in the set (i.e. inner OneOf
    // would have failed) and failure when it IS in the set (inner
    // OneOf would have succeeded, so Not fails). EOF is "not in set"
    // and so passes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_PeekRejectOneOfRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int pos = lexer.Position;
        string input = lexer.Input;
        int inputLen = input.Length;
        if (pos >= inputLen)
            return state.OnSuccess;

        int runeSetIndex = state.Data & 0xFFFF;
        DecodeRuneInline(input, pos, inputLen, out int runeValue, out _);
        RuneSet set = machine.Program.RuneSets[runeSetIndex];
        if (set.Contains(runeValue))
        {
            machine.RecordFailure(pos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }
        return state.OnSuccess;
    }

    // PeekRejectLiteralRune fuses Not(Literal) and Not(Token) into a
    // single peek-and-check. Zero-width. Failure when the literal
    // would have matched the next chars; success otherwise (including
    // when the input has fewer chars left than the literal needs).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_PeekRejectLiteralRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        int pos = lexer.Position;
        string input = lexer.Input;
        int inputLen = input.Length;
        int literalIndex = state.Data & 0xFFFF;
        string expected = machine.Program.Literals[literalIndex];
        int expectedLen = expected.Length;

        if (pos + expectedLen <= inputLen
            && input.AsSpan(pos, expectedLen).SequenceEqual(expected.AsSpan()))
        {
            machine.RecordFailure(pos, ResolveMatchErrorMessage(in machine, state.Data));
            return state.OnFailure;
        }
        return state.OnSuccess;
    }

    // Fused-scan opcode for BetweenInclusive(min, max, AllOf(Literal, OneOf))
    // when both AllOf children are effectively Delete. Matches the
    // common "separator-and-content" pattern (HrSpaced's
    // AtLeast(2, AllOf(Token(' '), OneOf("-*+"))) being the canonical
    // example). Per iteration: span-equal compare for the left
    // literal, then inline rune decode + RuneSet membership for the
    // right OneOf. No per-iteration backtrack frame, no per-iteration
    // state-machine dispatch, no Lexer.Read.
    private static int Step_ScanLiteralOneOfRune(in State state, ref Machine machine)
    {
        ScanAndPairSpec spec = machine.Program.ScanAndPairSpecs[state.Data];
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int inputLen = input.Length;
        string leftLit = machine.Program.Literals[spec.LeftLiteralIndex];
        int leftLen = leftLit.Length;
        RuneSet rightSet = machine.Program.RuneSets[spec.RightRuneSetIndex];
        int atLeast = spec.AtLeast;
        int atMost = spec.AtMost;

        int entryPos = lexer.Position;
        int pos = entryPos;
        int count = 0;

        while (count < atMost)
        {
            int afterLeft = pos + leftLen;
            if (afterLeft > inputLen) break;
            if (!input.AsSpan(pos, leftLen).SequenceEqual(leftLit.AsSpan())) break;

            if (afterLeft >= inputLen) break;
            DecodeRuneInline(input, afterLeft, inputLen, out int runeValue, out int runeLen);
            if (!rightSet.Contains(runeValue)) break;

            pos = afterLeft + runeLen;
            count++;
        }

        if (count < atLeast)
        {
            string? errorMessage = spec.ErrorMetadataIndex >= 0
                ? machine.Program.SymbolMetadata[spec.ErrorMetadataIndex].ErrorMessage
                : null;
            machine.RecordFailure(entryPos, errorMessage);
            return state.OnFailure;
        }

        lexer.SetPositionUnchecked(pos);
        return state.OnSuccess;
    }

    // ScanUntilStopperEligibleRune walks runes inline until it finds
    // one that's in the stopper rule's FirstConsumedRunes set, or
    // reaches EOF. Returns OnSuccess when an eligible rune is found
    // (lexer positioned at that rune; the surrounding lowering then
    // peek-Calls the stopper subprogram to confirm). Returns OnFailure
    // when EOF is hit without finding an eligible rune (the scan
    // exits successfully — the body extends to EOF). Stray surrogates
    // halt the scan in OnFailure mode.
    private static int Step_ScanUntilStopperEligibleRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int inputLen = input.Length;
        RuleStopperSpec spec = machine.Program.RuleStopperSpecs[state.Data];
        RuneSet firstRunes = machine.Program.RuneSets[spec.StopperFirstRunesIndex];

        int pos = lexer.Position;
        while (pos < inputLen)
        {
            char c = input[pos];
            int runeValue;
            int runeLen;
            if (char.IsHighSurrogate(c))
            {
                if (pos + 1 < inputLen && char.IsLowSurrogate(input[pos + 1]))
                {
                    runeValue = char.ConvertToUtf32(c, input[pos + 1]);
                    runeLen = 2;
                }
                else
                {
                    break;
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                break;
            }
            else
            {
                runeValue = c;
                runeLen = 1;
            }

            if (firstRunes.Contains(runeValue))
            {
                lexer.SetPositionUnchecked(pos);
                return state.OnSuccess;
            }

            pos += runeLen;
        }

        // EOF or stray surrogate: body extends to here, no stopper found.
        lexer.SetPositionUnchecked(pos);
        return state.OnFailure;
    }

    // AdvanceOneRune moves the lexer forward by one rune (1 char for
    // BMP, 2 for a surrogate pair). Used by the rule-stoppered
    // ScanUntil scan after a peeked stopper attempt failed: we know
    // the rune at the current position couldn't actually start a
    // stopper match, so we consume it as a body rune and resume the
    // scan. Always succeeds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_AdvanceOneRune(in State state, ref Machine machine)
    {
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int inputLen = input.Length;
        int pos = lexer.Position;
        if (pos < inputLen)
        {
            DecodeRuneInline(input, pos, inputLen, out _, out int runeLen);
            lexer.SetPositionUnchecked(pos + runeLen);
        }
        return state.OnSuccess;
    }


    // Inline rune decoder shared by the rune-inline Match opcodes.
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
    // and call-stack height. Used as the per-alternative frame for FirstOf,
    // the per-iteration frame inside BetweenInclusive's loop, and the
    // wrapper frame for Not / Peek.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_PushBacktrack(in State state, ref Machine machine)
    {
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
    // is atomic and always advances (Literal, Token, OneOf). The
    // generic loop's per-iteration backtrack frame and zero-width
    // guard aren't needed here, so this drops them and only
    // bumps the counter and checks the upper bound.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_BetweenIncrementCheckMax(in State state, ref Machine machine)
    {
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
        machine.Lexer.SetPositionUnchecked(between.LexerPosition);
        machine.TruncateOutputs(between.EmitCursor);
        machine.CallTop = between.CallStackHeight;
        machine.RecordFailure(machine.Lexer.Position, null);
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
        // matching ExitRule is needed because backtrack frames carry
        // CallStackHeight and restore CallTop directly when they fire.
        machine.Lexer.EnterRuleAtDepth(machine.CallTop);
        return state.Data;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_CallSuppressOutputs(in State state, ref Machine machine)
    {
        machine.Program.SubprogramRuleByEntry.TryGetValue(state.Data, out Rule? sourceRule);
        machine.PushCall(state.OnSuccess, state.OnFailure, machine.OutputOps.Count, sourceRule);
        machine.Lexer.EnterRuleAtDepth(machine.CallTop);
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
            // Mirror that into our own deepest tracker so error
            // reporting after the parse sees both.
            int lexerDeepest = machine.Lexer.DeepestFailure;
            string? lexerMessage = machine.Lexer.DeepestFailureMessage;
            if (lexerDeepest > machine.DeepestFailure)
            {
                machine.DeepestFailure = lexerDeepest;
                machine.DeepestFailureMessage = lexerMessage;
            }
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

    // Fused scan for BetweenInclusive(min, max, OneOf(set)) under
    // InputUnit.Rune. Replaces the multi-state inner loop with one
    // tight inline scan: read a rune from input directly, check
    // membership, advance, repeat. No per-iteration state-machine
    // dispatch, no Lexer.Read virtual call, no Token ref-struct
    // construction. The leaf-emit branch fires once per matched rune
    // when the rule's effective FlattenType isn't Delete.
    private static int Step_ScanOneOfRune(in State state, ref Machine machine)
    {
        ScanSpec spec = machine.Program.ScanSpecs[state.Data];
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int inputLen = input.Length;
        RuneSet set = machine.Program.RuneSets[spec.RuneSetIndex];
        int atLeast = spec.AtLeast;
        int atMost = spec.AtMost;
        int leafMetaIdx = spec.LeafMetadataIndex;
        FlattenType leafFlatten = leafMetaIdx >= 0
            ? machine.Program.SymbolMetadata[leafMetaIdx].FlattenType
            : FlattenType.Delete;

        int entryPos = lexer.Position;
        int entryEmit = machine.OutputOps.Count;
        int pos = entryPos;
        int count = 0;

        while (count < atMost && pos < inputLen)
        {
            char c = input[pos];
            int runeLen;
            int runeValue;
            if (char.IsHighSurrogate(c))
            {
                if (pos + 1 < inputLen && char.IsLowSurrogate(input[pos + 1]))
                {
                    runeValue = char.ConvertToUtf32(c, input[pos + 1]);
                    runeLen = 2;
                }
                else
                {
                    // Stray high surrogate: not a valid rune. Halt
                    // the scan (matches OneOfRule's RuneValue == -1
                    // failure).
                    break;
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                break;
            }
            else
            {
                runeValue = c;
                runeLen = 1;
            }

            if (!set.Contains(runeValue)) break;

            if (leafMetaIdx >= 0)
                machine.OutputOps.Add(OutputOp.Leaf(new SymbolId(runeValue), leafFlatten, pos, runeLen));

            pos += runeLen;
            count++;
        }

        if (count < atLeast)
        {
            // Roll back any outputs and report failure at entry.
            // Lexer position hasn't been advanced yet (we worked in a
            // local), so no lexer rollback needed.
            if (machine.OutputOps.Count > entryEmit)
                machine.TruncateOutputs(entryEmit);
            string? errorMessage = spec.ErrorMetadataIndex >= 0
                ? machine.Program.SymbolMetadata[spec.ErrorMetadataIndex].ErrorMessage
                : null;
            machine.RecordFailure(entryPos, errorMessage);
            return state.OnFailure;
        }

        lexer.SetPositionUnchecked(pos);
        return state.OnSuccess;
    }

    // Fused scan for BetweenInclusive(min, max, NoneOf(set)) under
    // InputUnit.Rune. Same shape as ScanOneOfRune but with the
    // membership test inverted: a rune in the stop-set ends the scan
    // (the corresponding NoneOfRule fails on it). Multi-rune
    // graphemes don't appear under InputUnit.Rune (each rune is its
    // own token), so the recursive evaluator's "RuneValue == -1
    // passes" branch is unreachable here and we can skip it.
    private static int Step_ScanNoneOfRune(in State state, ref Machine machine)
    {
        ScanSpec spec = machine.Program.ScanSpecs[state.Data];
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int inputLen = input.Length;
        RuneSet stopSet = machine.Program.RuneSets[spec.RuneSetIndex];
        int atLeast = spec.AtLeast;
        int atMost = spec.AtMost;
        int leafMetaIdx = spec.LeafMetadataIndex;
        FlattenType leafFlatten = leafMetaIdx >= 0
            ? machine.Program.SymbolMetadata[leafMetaIdx].FlattenType
            : FlattenType.Delete;

        int entryPos = lexer.Position;
        int entryEmit = machine.OutputOps.Count;
        int pos = entryPos;
        int count = 0;

        while (count < atMost && pos < inputLen)
        {
            char c = input[pos];
            int runeLen;
            int runeValue;
            if (char.IsHighSurrogate(c))
            {
                if (pos + 1 < inputLen && char.IsLowSurrogate(input[pos + 1]))
                {
                    runeValue = char.ConvertToUtf32(c, input[pos + 1]);
                    runeLen = 2;
                }
                else break;
            }
            else if (char.IsLowSurrogate(c))
            {
                break;
            }
            else
            {
                runeValue = c;
                runeLen = 1;
            }

            if (stopSet.Contains(runeValue)) break;

            if (leafMetaIdx >= 0)
                machine.OutputOps.Add(OutputOp.Leaf(new SymbolId(runeValue), leafFlatten, pos, runeLen));

            pos += runeLen;
            count++;
        }

        if (count < atLeast)
        {
            if (machine.OutputOps.Count > entryEmit)
                machine.TruncateOutputs(entryEmit);
            string? errorMessage = spec.ErrorMetadataIndex >= 0
                ? machine.Program.SymbolMetadata[spec.ErrorMetadataIndex].ErrorMessage
                : null;
            machine.RecordFailure(entryPos, errorMessage);
            return state.OnFailure;
        }

        lexer.SetPositionUnchecked(pos);
        return state.OnSuccess;
    }

    // Fused scan for BetweenInclusive(min, max, AnyTokenRule) under
    // InputUnit.Rune. No membership test (every rune passes), so the
    // per-rune cost is just rune-decode + advance + count. Stops at
    // EOF or a stray surrogate. Common idiom: ZeroOrMore(AnyToken())
    // at the tail of a grammar to consume remaining input.
    private static int Step_ScanAnyTokenRune(in State state, ref Machine machine)
    {
        ScanSpec spec = machine.Program.ScanSpecs[state.Data];
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int inputLen = input.Length;
        int atLeast = spec.AtLeast;
        int atMost = spec.AtMost;
        int leafMetaIdx = spec.LeafMetadataIndex;
        FlattenType leafFlatten = leafMetaIdx >= 0
            ? machine.Program.SymbolMetadata[leafMetaIdx].FlattenType
            : FlattenType.Delete;

        int entryPos = lexer.Position;
        int entryEmit = machine.OutputOps.Count;
        int pos = entryPos;
        int count = 0;

        while (count < atMost && pos < inputLen)
        {
            char c = input[pos];
            int runeLen;
            int runeValue;
            if (char.IsHighSurrogate(c))
            {
                if (pos + 1 < inputLen && char.IsLowSurrogate(input[pos + 1]))
                {
                    runeValue = char.ConvertToUtf32(c, input[pos + 1]);
                    runeLen = 2;
                }
                else break;
            }
            else if (char.IsLowSurrogate(c))
            {
                break;
            }
            else
            {
                runeValue = c;
                runeLen = 1;
            }

            if (leafMetaIdx >= 0)
                machine.OutputOps.Add(OutputOp.Leaf(new SymbolId(runeValue), leafFlatten, pos, runeLen));

            pos += runeLen;
            count++;
        }

        if (count < atLeast)
        {
            if (machine.OutputOps.Count > entryEmit)
                machine.TruncateOutputs(entryEmit);
            string? errorMessage = spec.ErrorMetadataIndex >= 0
                ? machine.Program.SymbolMetadata[spec.ErrorMetadataIndex].ErrorMessage
                : null;
            machine.RecordFailure(entryPos, errorMessage);
            return state.OnFailure;
        }

        lexer.SetPositionUnchecked(pos);
        return state.OnSuccess;
    }

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
    // is in the named RuneSet. Used by the FirstOf first-rune-skip lowering
    // to drop alternatives whose FirstConsumedRunes can't possibly
    // match the next input rune.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_CheckPeekedRuneInSet(in State state, ref Machine machine)
    {
        RuneSet set = machine.Program.RuneSets[state.Data];
        if (set.Contains(machine.PeekedRune))
            return state.OnSuccess;
        return state.OnFailure;
    }

    // LoadPeekedRuneAndJumpAlt fuses the LoadPeekedRune + leading
    // CheckPeekedRuneInSet chain for a FirstOf. Decodes one rune at the
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
    //   * EOF or malformed surrogate: same as stopper; the leaf
    //     covers everything scanned so far.
    //   * Escape-start rune found: consumes the start rune and returns
    //     OnFailure. The lowerer wires OnFailure to a Call(escapeEnd)
    //     state whose OnSuccess routes back to this scan state, so the
    //     loop resumes after the escape body completes.
    //
    // Operates rune-by-rune even under the GraphemeLexer because the
    // stopper-set / escape-start checks are rune-scoped. Same as the
    // recursive ScanUntilRule.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Step_ScanUntilFast(in State state, ref Machine machine)
    {
        ScanUntilSpec spec = machine.Program.ScanUntilSpecs[state.Data];
        RuneSet stopperSet = machine.Program.RuneSets[spec.StopperSetIndex];
        Lexer lexer = machine.Lexer;
        string input = lexer.Input;
        int inputLen = input.Length;

        while (true)
        {
            int pos = lexer.Position;
            if (pos >= inputLen) return state.OnSuccess;

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
                // Stray surrogate halves can't form a rune. Stop the
                // scan and let the surrounding grammar decide what to
                // do with the input.
                return state.OnSuccess;
            }
            else
            {
                runeValue = first;
                runeLen = 1;
            }

            if (stopperSet.Contains(runeValue)) return state.OnSuccess;

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
