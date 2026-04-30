namespace InductorParser.StateMachine;

// One entry on Machine's call stack. Pushed by the Call opcode,
// popped by ReturnSuccess and ReturnFailure. Carries the caller's
// continuation: where to go in the caller's program when the
// subprogram succeeds or fails.
internal struct CallFrame
{
    public int OnSuccess;
    public int OnFailure;

    // Output-suppression cursor. -1 means "no suppression": ReturnSuccess
    // and ReturnFailure leave OutputOps as the subprogram left them.
    // Non-negative means "discard everything the subprogram emitted":
    // ReturnSuccess / ReturnFailure truncate OutputOps back to this
    // cursor before jumping. ScanUntil's escape-end Call uses this so
    // an escape-end rule that has its own emit states (e.g. a
    // Preserve-default OneOf) doesn't leak those outputs into the
    // enclosing ScanUntil's parent. The recursive evaluator gets the
    // same effect by passing outputSymbols=null to escape-end's TryParse.
    public int SuppressOutputsCursor;

    // Source rule the Call jumped into, used by ReturnSuccess /
    // ReturnFailure to label trace output with the same
    // "{Name}:{ruleClassName}" string the recursive engine uses. Null
    // when the program had no rule mapped to the called subprogram
    // entry (defensive: every cyclic-rule subprogram registers itself
    // during lowering, so this should always be non-null in practice).
    public Rule? CallSourceRule;
}
