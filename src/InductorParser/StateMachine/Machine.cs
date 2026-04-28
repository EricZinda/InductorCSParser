using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using InductorParser.Lexing;

namespace InductorParser.StateMachine;

// Mutable run state for one parse. Lives on the stack of Stepper.Run as
// a `ref Machine` so the inner loop can read and write fields without
// going through a class indirection.
//
// The state machine reads / writes Lexer through field calls, the
// emit list through index/Add, and the two stacks via top-of-stack
// indices (BacktrackTop / CallTop). The lexer is the only object
// shared with the rest of the parser. Output is built up as a
// flat List<OutputOp> that the TreeBuilder consumes after
// HaltSuccess.
internal struct Machine
{
    // Per-thread pools so back-to-back parses on the same thread reuse
    // the same arrays and list instead of allocating fresh ones every
    // time. Initial sizes (32 frames, 64 output ops) were enough for
    // every grammar in the bench's largest inputs without growing, so
    // the pool reuses without shrinking. ThreadStatic over a real pool
    // because there's only ever one parse in flight per thread and we
    // don't need cross-thread coordination.
    [ThreadStatic]
    private static BacktrackFrame[]? _pooledBacktrackStack;
    [ThreadStatic]
    private static CallFrame[]? _pooledCallStack;
    [ThreadStatic]
    private static List<OutputOp>? _pooledOutputOps;

    public readonly Lexer Lexer;
    public readonly CompiledProgram Program;
    public List<OutputOp> OutputOps;

    public BacktrackFrame[] BacktrackStack;
    public int BacktrackTop;

    public CallFrame[] CallStack;
    public int CallTop;

    public int DeepestFailure;
    public string? DeepestFailureMessage;

    // Single-slot scratch space for the LoadPeekedRune /
    // CheckPeekedRuneInSet pair the FirstOf first-rune-skip uses.
    // Updated by LoadPeekedRune at each FirstOf entry, read by
    // per-alternative CheckPeekedRuneInSet states. -1 means "no rune
    // available" (EOF or stray surrogate), which fails membership in
    // any non-Universe set.
    public int PeekedRune;

    // Start position of the most recent successful token read by a
    // Match opcode (OneOf / NoneOf / AnyToken). Used by the matching
    // EmitLeaf opcode to compute the leaf's char span without walking
    // back through the input. Token length under GraphemeLexer can be
    // arbitrary (multi-rune ZWJ sequences, decomposed accents), so we
    // can't infer it from a fixed-width walk-back the way single-rune
    // OneOf could. Match opcodes set this before advancing the lexer
    // and the next EmitLeaf reads it.
    public int LastConsumedTokenStart;

    // Shared empty list used when CompiledProgram.HasOutputs is
    // false. The state machine is guaranteed not to call any opcode
    // that writes to the output list in that case, so a singleton
    // empty list is safe and avoids the pool-fetch + Clear cost.
    // The list is never modified at runtime.
    private static readonly List<OutputOp> _sharedEmptyOutputs = new(0);

    public Machine(Lexer lexer, CompiledProgram program)
    {
        Lexer = lexer;
        Program = program;

        if (!program.HasOutputs)
        {
            // Matcher-mode parse: no opcode in the program will touch
            // the output list. Use the shared empty list so we
            // don't pay the pool fetch.
            OutputOps = _sharedEmptyOutputs;
        }
        else
        {
            var pooledEmit = _pooledOutputOps;
            if (pooledEmit == null)
            {
                OutputOps = new List<OutputOp>(64);
            }
            else
            {
                _pooledOutputOps = null;
                pooledEmit.Clear();
                OutputOps = pooledEmit;
            }
        }

        var pooledBacktrack = _pooledBacktrackStack;
        if (pooledBacktrack == null)
        {
            BacktrackStack = new BacktrackFrame[32];
        }
        else
        {
            _pooledBacktrackStack = null;
            BacktrackStack = pooledBacktrack;
        }
        BacktrackTop = 0;

        var pooledCall = _pooledCallStack;
        if (pooledCall == null)
        {
            CallStack = new CallFrame[32];
        }
        else
        {
            _pooledCallStack = null;
            CallStack = pooledCall;
        }
        CallTop = 0;

        DeepestFailure = 0;
        DeepestFailureMessage = null;
        PeekedRune = -1;
        LastConsumedTokenStart = 0;
    }

    // Return the heap-allocated buffers to the per-thread pool so the
    // next Parse on this thread can reuse them. Call after the parse
    // result has been built (TreeBuilder is done reading OutputOps).
    // Safe to call multiple times, though only the first call returns
    // anything to the pool.
    public void Release()
    {
        _pooledBacktrackStack = BacktrackStack;
        _pooledCallStack = CallStack;
        // Skip returning the shared empty list to the pool. It's a
        // process-wide singleton, not a parse-specific allocation.
        if (!ReferenceEquals(OutputOps, _sharedEmptyOutputs))
            _pooledOutputOps = OutputOps;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordFailure(int position, string? message)
    {
        // "Deepest failure wins" identical to Lexer.RecordFailure: a
        // strictly-deeper position takes both the position and the
        // (possibly null) message. An equal-depth message can claim
        // the message slot if nobody has yet.
        if (position > DeepestFailure)
        {
            DeepestFailure = position;
            DeepestFailureMessage = message;
            return;
        }
        if (position == DeepestFailure
            && message != null
            && DeepestFailureMessage == null)
        {
            DeepestFailureMessage = message;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushBacktrack(int lexerPosition, int emitCursor, int callHeight)
    {
        if (BacktrackTop == BacktrackStack.Length)
        {
            var grown = new BacktrackFrame[BacktrackStack.Length * 2];
            System.Array.Copy(BacktrackStack, grown, BacktrackStack.Length);
            BacktrackStack = grown;
        }
        ref var frame = ref BacktrackStack[BacktrackTop++];
        frame.LexerPosition = lexerPosition;
        frame.EmitCursor = emitCursor;
        frame.CallStackHeight = callHeight;
        frame.Counter = 0;
        frame.AtLeast = 0;
        frame.AtMost = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushBetween(int lexerPosition, int emitCursor, int callHeight, int atLeast, int atMost)
    {
        if (BacktrackTop == BacktrackStack.Length)
        {
            var grown = new BacktrackFrame[BacktrackStack.Length * 2];
            System.Array.Copy(BacktrackStack, grown, BacktrackStack.Length);
            BacktrackStack = grown;
        }
        ref var frame = ref BacktrackStack[BacktrackTop++];
        frame.LexerPosition = lexerPosition;
        frame.EmitCursor = emitCursor;
        frame.CallStackHeight = callHeight;
        frame.Counter = 0;
        frame.AtLeast = atLeast;
        frame.AtMost = atMost;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushCall(int onSuccess, int onFailure, int suppressOutputsCursor)
    {
        if (CallTop == CallStack.Length)
        {
            var grown = new CallFrame[CallStack.Length * 2];
            System.Array.Copy(CallStack, grown, CallStack.Length);
            CallStack = grown;
        }
        ref var frame = ref CallStack[CallTop++];
        frame.OnSuccess = onSuccess;
        frame.OnFailure = onFailure;
        frame.SuppressOutputsCursor = suppressOutputsCursor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TruncateOutputs(int cursor)
    {
        // Truncate the OutputOps list back to `cursor` entries. List<T>
        // doesn't expose a "set Count to N" API, so we use RemoveRange.
        // Cheap: just drops the count, no per-element work.
        int currentCount = OutputOps.Count;
        if (currentCount > cursor)
            OutputOps.RemoveRange(cursor, currentCount - cursor);
    }
}
