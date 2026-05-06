namespace InductorParser.StateMachine;

// One byte per state. The state machine's dispatcher switches on this
// value to pick the per-step body. RyuJIT lowers a dense switch on a
// small enum to a jump table, which gives the inner loop one indirect
// branch per step that the predictor handles well.
internal enum LoweredOpCode : byte
{
    // Match operations. Each is atomic: on failure the lexer is in the
    // same state it was in on entry. So no surrounding backtrack frame
    // is needed for individual matches.
    MatchLiteral,
    MatchLiteralIgnoreAsciiCase,
    MatchOneOf,
    MatchNoneOf,
    MatchAnyToken,
    MatchEof,

    // Control flow with no input or stack effect. Unconditional jumps
    // set OnSuccess == OnFailure so the dispatcher's ternary picks the
    // same target either way.
    Jump,

    // Backtrack-stack management. PushBacktrack snapshots lexer position,
    // emit cursor, and the failure target into a frame on the stack.
    // PopBacktrack discards the topmost frame on the success path
    // (no restore). FailRestore is the failure handler at a backtrack
    // boundary: pops the frame, restores lexer and emit cursor, then
    // continues at OnSuccess (which the lowering pass set to the next
    // alternative or the parent's failure path).
    PushBacktrack,
    PopBacktrack,
    FailRestore,

    // BetweenInclusive bookkeeping. PushBetween snapshots the loop
    // counter, the bounds, and the per-iteration position so the
    // matching loop can:
    //   * detect zero-width inner matches (PopIterationCheck)
    //   * decide whether the loop exit is a success or failure
    //     (BetweenExitCheckMin)
    //
    // BetweenIncrementCheckMax is the atomic-inner fast path: when
    // the inner rule is one of the always-advancing single-state
    // matches (Literal / Grapheme / OneOf), the loop doesn't need a
    // per-iteration backtrack frame (the match is already atomic and
    // the zero-width guard is unnecessary). The bookkeeping reduces
    // to "increment counter, exit if >= atMost" inline against the
    // Between frame.
    PushBetween,
    PopIterationCheck,
    BetweenExitCheckMin,
    BetweenIncrementCheckMax,

    // Subprogram call/return. Call pushes a CallFrame carrying the
    // caller's success and failure targets, then jumps to the
    // subprogram entry. ReturnSuccess and ReturnFailure pop the
    // topmost CallFrame and jump to its OnSuccess / OnFailure.
    //
    // CallSuppressOutputs is a Call variant that records the current
    // output cursor on the frame; ReturnSuccess/ReturnFailure truncate
    // OutputOps back to that cursor on pop. Used by ScanUntil's
    // escape-end Call so the escape rule's outputs never reach the
    // enclosing tree (mirrors the recursive evaluator's
    // outputSymbols=null escape-end call).
    Call,
    CallSuppressOutputs,
    ReturnSuccess,
    ReturnFailure,

    // Output ops. These never fail. Their action just appends to the
    // output list. Lowering wraps each composite rule in
    // OpenComposite / CloseComposite, and each preserved leaf in EmitLeaf.
    OpenComposite,
    CloseComposite,
    EmitLeafLiteral,
    EmitLeafOneOf,

    // FirstOf first-rune skip. LoadPeekedRune peeks the next rune in
    // the input and stashes it on Machine.PeekedRune.
    // CheckPeekedRuneInSet tests that stashed rune against a TokenSet
    // without re-peeking, so an N-alternative FirstOf pays one peek +
    // N membership checks instead of N peeks + N checks. Mirrors what
    // the recursive FirstOfRule does.
    LoadPeekedRune,
    CheckPeekedRuneInSet,

    // ASCII jump-table dispatch for FirstOf first-rune-skip. Replaces the
    // LoadPeekedRune + leading CheckPeekedRuneInSet chain when at least
    // one alternative can be peek-skipped. Decodes one rune at the
    // lexer position; for an ASCII rune (0..127) jumps directly to the
    // table-determined alt's PushBacktrack (or to the all-alts-failed
    // target when no alt accepts that rune). For non-ASCII or EOF
    // stashes the rune in Machine.PeekedRune and falls through to
    // OnSuccess (the head of the original CheckPeekedRuneInSet chain),
    // which then handles the non-ASCII alternatives. State.Data is an
    // index into CompiledProgram.OrJumpTables.
    LoadPeekedRuneAndJumpAlt,

    // ScanUntil scan loop (TokenSet stopper + optional Rune escape
    // start). ScanUntilFast walks runes until it hits a stopper
    // (success exit), EOF / malformed surrogate (success exit, empty
    // tail), or an escape-start rune (consumes the start rune, then
    // routes to OnFailure where the lowerer wired in a Call to the
    // escape-end subprogram). After the escape returns, the dispatcher
    // jumps back to the same scan state and resumes. EmitScanUntilLeaf
    // builds the leaf Symbol covering the matched range; the start
    // position lives on the topmost backtrack frame's LexerPosition.
    ScanUntilFast,
    EmitScanUntilLeaf,

    // Bridge to the recursive evaluator. Used for rule types the
    // state machine doesn't have a native lowering for: WithinGrapheme,
    // ScanUntil general-form variants (Rule stopper, Rule escape
    // start), and any user-defined Rule subclass. The bridge invokes
    // the rule's TryParse against the current lexer, captures whatever
    // Symbol(s) it produces, and emits them as Prebuilt output ops
    // that flow into the surrounding tree as if a native opcode had
    // produced them. Slower per call than a native lowering but
    // correctness-preserving for everything the recursive evaluator
    // can handle.
    BridgeToRecursive,

    // (The Rune-mode-only opcodes that used to live here — ScanOneOfRune,
    // ScanNoneOfRune, ScanAnyTokenRune, MatchOneOfRune, MatchNoneOfRune,
    // MatchAnyTokenRune, MatchLiteralRune, MatchLiteralIgnoreAsciiCaseRune,
    // PeekRejectOneOfRune, PeekRejectLiteralRune, ScanLiteralOneOfRune,
    // ScanUntilStopperEligibleRune, AdvanceOneRune — were removed in
    // Step 2. With one (grapheme) lexer their inline rune decode would
    // split multi-rune graphemes, so the lowerer emits the non-rune
    // opcodes instead and routes the surviving rule-stoppered
    // ScanUntil shapes through BridgeToRecursive.)

    // Bulk skip at the top of a ZeroOrMore(FirstOf(match..., AnyToken.Delete))
    // scanner loop. Advances the lexer to the next position where one of
    // the candidate matches could plausibly start, so the inner FirstOf
    // doesn't waste a per-rune attempt + fail-over to the deleted
    // AnyToken on every non-candidate rune. state.Data indexes into
    // CompiledProgram.ScannerSkipSpecs. Always succeeds (advances the
    // lexer; never fails). Mirrors the recursive evaluator's ScannerSkip
    // in BetweenInclusiveRule. Inert by construction when the lowerer
    // doesn't recognize the shape (the opcode is just never emitted).
    ScannerSkipAdvance,

    // Record a rule's WithError("...") message at the current lexer
    // position and fall through to OnSuccess. state.Data is an index
    // into CompiledProgram.SymbolMetadata; the opcode reads the
    // metadata's ErrorMessage and calls Machine.RecordFailure with
    // it. Used at composite-rule failure boundaries (Not / Peek's
    // inner-led-to-rule-failure exits) where the rule's own
    // user-friendly message has to ride along for the deepest-failure
    // tracker to surface it on a parse failure. Mirrors the
    // recursive evaluator's lexer.RecordFailure(position, ErrorMessage)
    // call inside NotRule / PeekRule on the failure path.
    RecordRuleFailure,
}
