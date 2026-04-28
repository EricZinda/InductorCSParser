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
    // matches (Literal / Token / OneOf), the loop doesn't need a
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
    // CheckPeekedRuneInSet tests that stashed rune against a RuneSet
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

    // ScanUntil scan loop (RuneSet stopper + optional Rune escape
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

    // Fused-scan opcodes for the BetweenInclusive(min, max, inner)
    // shape where inner is one of the always-advancing single-rune
    // matches. Lowering detects this pattern under InputUnit.Rune and
    // emits one of these opcodes in place of a multi-state inner loop
    // (PushBacktrack -> MatchX -> EmitLeafX -> BetweenIncrementCheckMax).
    // The body is one tight loop with inline rune decode, no
    // per-iteration state-machine dispatch and no Lexer.Read call.
    // state.Data is an index into CompiledProgram.ScanSpecs which
    // carries bounds, runeset / payload index, and the optional leaf-
    // emit and error-message metadata indices.
    //
    // Rune-only on purpose: inline rune decode would split multi-rune
    // graphemes under the GraphemeLexer (treat "é" decomposed as 'e'
    // followed by a combining mark, when GraphemeLexer treats both
    // runes as one token that fails OneOf). The lowerer falls back to
    // the un-fused atomic path under InputUnit.Grapheme.
    ScanOneOfRune,
    ScanNoneOfRune,

    // Fused scan for BetweenInclusive(min, max, AnyTokenRule) under
    // InputUnit.Rune. Walks runes inline up to atMost (or to EOF /
    // stray surrogate), no membership test (any rune passes). The
    // common idiom is ZeroOrMore(AnyToken()) at the tail of a grammar
    // to consume the rest of the input — this opcode replaces the
    // ~25-30ns per rune of the generic Between loop with ~4-5ns per
    // rune of straight-line code.
    ScanAnyTokenRune,

    // Rune-inline variants of the standalone Match opcodes. Same
    // semantics and same state.Data layout as the non-rune originals,
    // but the opcode body bypasses Lexer.Read entirely: it indexes
    // input directly and decodes runes inline (one BMP char or one
    // surrogate pair per match). Saves one virtual NextTokenLength
    // call and one Token ref-struct construction per match.
    //
    // Rune-only because GraphemeLexer's per-token segmentation can
    // span more than two chars (ZWJ emoji, decomposed accents). The
    // lowerer emits these only when InputUnit.Rune; under Grapheme
    // the original opcodes are emitted instead.
    MatchOneOfRune,
    MatchNoneOfRune,
    MatchAnyTokenRune,
    MatchLiteralRune,
    MatchLiteralIgnoreAsciiCaseRune,

    // Fused Not(SimpleMatch) opcodes. Replace the Not's three-state
    // PushBacktrack/inner/FailRestore shape with one peek-and-reject
    // opcode that decodes one rune (or one literal-length-worth of
    // chars) inline, returns failure if it matches the inner pattern,
    // success otherwise. Zero-width: the lexer position never moves.
    // Rune-only because they decode rune-by-rune; under Grapheme the
    // existing Not lowering still applies.
    PeekRejectOneOfRune,
    PeekRejectLiteralRune,

    // Fused-scan opcode for BetweenInclusive(min, max, AllOf(L, R)) where
    // L is a Literal/Token and R is a OneOf, both effectively Delete.
    // Matches the common shape "repeated-token-followed-by-rune-class"
    // (HrSpaced, separator-then-content patterns, etc.). Per iteration:
    // span-equal compare for L plus inline rune decode + set membership
    // for R, in straight-line code with no per-iteration state-machine
    // dispatch and no per-iteration backtrack frame. Rune-only.
    ScanLiteralOneOfRune,

    // Rule-stoppered ScanUntil scan loop. Walks runes inline; on each
    // rune, checks whether it's in the stopper rule's
    // FirstConsumedRunes set. When the rune isn't in the set, advance
    // and continue (it can't possibly be the start of a stopper
    // match). When the rune IS in the set, exit OnSuccess so the
    // surrounding lowering can Call the stopper rule (in peek mode)
    // and decide whether to break the scan or continue. Replaces the
    // BridgeToRecursive entry/exit cost on rule-stoppered ScanUntil
    // forms (CDATA's ]]>, Python triple-quote, paragraph terminators).
    // AdvanceOneRune is the helper that consumes one rune when the
    // peeked stopper failed.
    ScanUntilStopperEligibleRune,
    AdvanceOneRune,

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
}
