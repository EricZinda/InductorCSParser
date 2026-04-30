namespace InductorParser.StateMachine;

// One entry on Machine's backtrack stack. Pushed by PushBacktrack /
// PushBetween, popped by PopBacktrack on success or by FailRestore /
// PopIterationCheck on failure or loop exit. Restoring from a frame
// rolls the lexer position, the emit cursor, and the call-stack
// height back to where they were when the frame was pushed.
internal struct BacktrackFrame
{
    public int LexerPosition;
    public int EmitCursor;
    public int CallStackHeight;

    // BetweenInclusive carries its loop counter and bounds on the same
    // stack slot to avoid a second stack. Counter is unused by FirstOf /
    // Optional / Not / Peek frames.
    public int Counter;
    public int AtLeast;
    public int AtMost;
}
