# State Machine Evaluator Architecture

This document is for someone joining the project who needs to understand what the state-machine evaluator is, why it exists, and how the pieces fit together. It is not a line-by-line reference. It is the picture you want in your head before you go reading code.

## Two Ways To Run a Grammar

There are two evaluators that can take a `Rule` tree and parse a string against it. They produce the same `ParseResult` and follow the same semantics (deepest-failure error position, FlattenType handling, PreserveAllSymbols). They just get there different ways.

The first is the recursive evaluator, exposed as `rule.Parse(input)`. Each rule type's `TryParseRule` calls into its children's `TryParseRule`, and backtracking falls out of normal C# control flow. It is small, readable, and the way the parser was originally built.

The second is the state-machine evaluator, exposed as `StateMachineParser.Parse(rule, input)`. It takes the same Rule tree, compiles it into a flat array of opcode states, and runs that array as a tight interpreter loop. There is no recursion at parse time. Backtracking is an explicit stack the interpreter manages.

Both evaluators are kept and tested side by side. The recursive one is the reference, the state-machine one is the fast path. When in doubt, the recursive one wins on correctness and the state-machine one is expected to match it.

## Why The Second Path Exists

A recursive PEG parser does a lot of work per matched character that the CPU does not love. Every rule is a virtual call. Every backtrack is a try/catch or a returned-bool unwind. The hot loop touches different code for every rule type. None of it is wrong, it just costs more than it has to once a grammar is hot.

The state-machine evaluator trades up-front compilation cost for a much tighter steady-state. Grammar gets compiled once, cached on the Rule, and reused for every parse. The inner loop is one switch on a `byte` opcode, which the JIT turns into a jump table the branch predictor handles well. Match operations like "literal text" or "rune in this set" become a couple of opcodes that the compiler can sometimes fuse into one. Per-thread pools mean a steady-state parse on a hot grammar allocates basically nothing for the parser infrastructure (only the Symbol nodes the caller asked for).

The state machine is the answer to "we want this faster" without rewriting the rule classes or losing the recursive evaluator as a fallback.

## The Pipeline

There are five stages between handing in a string and getting a tree back. Knowing them in order is most of the architecture.

First, the user builds a Rule tree using the fluent factory functions (`AllOf`, `FirstOf`, `Token`, etc.). This is identical to the recursive path. The state machine does not have its own grammar surface.

Second, on the first call to `StateMachineParser.Parse(rule, input)` for a given root rule, the compiler walks the tree and produces a `CompiledProgram`. The program is cached on the rule (in fact, in one of two caches keyed on PreserveAllSymbols, since that flag shapes which output states the lowerer skips). Subsequent parses on that same rule reuse the cached program.

Third, the stepper runs the compiled program against a `Lexer`. The mutable run state lives in a `Machine` struct on the stack of `Stepper.Run`. The interpreter loop reads the current state index, looks up the state, switches on its opcode, runs the body, and updates the state index. It exits when the index goes negative (HaltSuccess or HaltFailure).

Fourth, while the program runs, opcodes append `OutputOp` records to `Machine.OutputOps`. These record what would have become a Symbol in the recursive path: opens, closes, and leaves. Backtracks truncate the output list back to the cursor saved on the backtrack frame, so outputs from a failed alternative never reach the tree builder.

Fifth, after a successful run, `TreeBuilder` walks the output list and produces the `IReadOnlyList<Symbol>` that `ParseResult` exposes. This is where FlattenType actually shapes the tree (Preserve becomes a wrapper Symbol, Flatten lets children flow up, Delete drops the subtree).

After the parse, the Machine returns its heap buffers (the two stacks and the output list) to per-thread pools, and the lexer goes back to its per-thread slot. The next parse on the same thread reuses them.

## What a Compiled Program Looks Like

The compiled program is, mostly, a `State[]`. Each `State` is sixteen bytes: a one-byte opcode, a four-byte data field, and two four-byte jump targets (`OnSuccess` and `OnFailure`). The dispatcher's job is "given the current state index, run the opcode, and pick OnSuccess or OnFailure as the next index."

The opcodes break into a few groups. Match opcodes (`MatchLiteral`, `MatchOneOf`, `MatchAnyToken`, `MatchEof`) consume input and either succeed or fail without changing any state on failure. They are atomic, so the interpreter does not need a backtrack frame around them. Control-flow opcodes (`Jump`, `Call`, `ReturnSuccess`, `ReturnFailure`) move the state index without touching input. Backtrack opcodes (`PushBacktrack`, `PopBacktrack`, `FailRestore`, `PushBetween`, `BetweenIncrementCheckMax`, etc.) manage the explicit backtrack stack. Output opcodes (`OpenComposite`, `CloseComposite`, `EmitLeafLiteral`, `EmitLeafOneOf`) append to the output list and never fail.

Side tables hang off the program for anything bigger than a four-byte data field can hold: `Literals` for literal strings, `TokenSets` for rune-class membership, `SymbolMetadata` for the (SymbolId, FlattenType, error message) triples that outputs point to, `ScanSpecs` for fused scan loops, and so on. Opcodes index into these tables through their data field, which keeps the State struct small and the side tables in cache when the inner loop hits them repeatedly.

The compiler deduplicates aggressively. Two rules that both want the literal `"true"` share one slot in the Literals table. Two rules with the same TokenSet share one entry in the TokenSets table. The compiled program tends to be small even for grammars with hundreds of rules.

## Backtracking Without Recursion

The recursive evaluator backtracks the natural way: a `TryParseRule` that fails returns false, and its parent gets to try the next alternative. The state machine has to do the same thing without a C# call stack to unwind, so it has its own.

`PushBacktrack` snapshots the lexer position, the output cursor, and the current call-stack height into a `BacktrackFrame`. `PopBacktrack` discards the topmost frame on the success path (no restore). `FailRestore` is the failure handler at a backtrack boundary: it pops the frame, restores the lexer, truncates the output list, and continues at the OnSuccess index, which the compiler wired to the next alternative or the parent's failure path.

The same stack carries the `BetweenInclusive` loop bookkeeping. A Between frame holds the iteration counter and the per-iteration position alongside the same restore data, so the loop can detect zero-width inner matches, decide whether the loop's exit is a success or failure, and unwind partial iterations on failure. This is why `BacktrackFrame` has a few extra fields that most frame uses leave at zero. One stack is cheaper than two.

`FirstOf` is the most common backtrack producer: each alternative pushes a frame before trying, and pops it on success or restores from it on failure. The first-rune-skip optimization lets `FirstOf` peek one rune and jump straight to the alternative whose first-rune set matches, skipping the alternatives that cannot possibly accept that rune. This is the same optimization the recursive `FirstOfRule` does, just expressed as a couple of opcodes (`LoadPeekedRune`, `CheckPeekedRuneInSet`, and an ASCII jump-table variant `LoadPeekedRuneAndJumpAlt` for the dense case).

## Cyclic Rules Become Subprograms

Acyclic rules get inlined when compiled. A grammar where `Json` references `Value` references `Number` and `String` produces one big flat program with all of them inlined into the call sites that reference them, no overhead between them.

Recursion is different. A grammar where `Value` references `Array`, and `Array` references `Value` again, would loop forever if we tried to inline. The compiler detects cycles up front (a normal DFS) and compiles cyclic rules as subprograms. Each cyclic rule gets one entry in the program. References to it become `Call` opcodes that push a `CallFrame` (carrying the caller's success and failure indices), jump to the entry, and either `ReturnSuccess` or `ReturnFailure` pops the frame and goes back. This is the same control-flow shape the C# call stack uses for recursion in the recursive evaluator, just made explicit.

`LateBoundRule` is transparent at compile time. The compiler unwraps it to whatever it points at, so a self-referential late-bound rule shows up as a normal cycle.

## Bridging Back To The Recursive Evaluator

The state machine does not natively compile every rule type. `WithinTokenRule`, the rule-stoppered shape of `ScanUntil`, and any user-defined `Rule` subclass all bridge back to the recursive evaluator at parse time.

Bridging is one opcode: `BridgeToRecursive`. Its body invokes `Rule.TryParse` against the current lexer, captures whatever `Symbol(s)` the recursive evaluator produces, and emits them as `Prebuilt` output ops. The `TreeBuilder` then appends those Symbols directly into the surrounding tree as if a native opcode had produced them. Slower per call than a native compilation, but correctness-preserving for everything the recursive evaluator can handle.

This is why the state machine and the recursive evaluator share the same `Lexer`, the same `Symbol` type, and the same `RecordFailure` semantics. The bridge has to compose with the rest of the parse, not run a parallel one. The opcode that is missing today is a future optimization, not a correctness gap.

## Pattern-Specific Fused Opcodes

Once the basic state machine works, the compiler can recognize specific shapes in the rule tree and emit one fused opcode in place of a multi-state sub-program.

The clearest examples are the rune-only fused-scan opcodes. `BetweenInclusive(min, max, OneOf(set))` is, by default, compiled to a loop of "push backtrack frame, match one rune against set, increment counter, check max, repeat." `ScanOneOfRune` collapses that into one opcode whose body is one tight loop with inline rune decoding, no per-iteration backtrack frame, no virtual lexer call. The same pattern produces `ScanNoneOfRune`, `ScanAnyTokenRune` (for the very common `ZeroOrMore(AnyToken())` tail), and `ScanLiteralOneOfRune` (for the "repeated separator-then-content" shape).

The rune-only `MatchOneOfRune`, `MatchNoneOfRune`, `MatchLiteralRune`, and friends inline the rune decode that the lexer's virtual `Read` would have done. They save one virtual call and one Token ref-struct construction per match. They are only emitted when the surrounding rule is known to operate on rune-only `TokenSet`s (no multi-rune entries) and when the input position is guaranteed to be on a token boundary, since otherwise inlining a rune decode could split a multi-rune token (ZWJ emoji, decomposed accents) the lexer would have grouped together.

The fused `Not(SimpleMatch)` opcodes (`PeekRejectOneOfRune`, `PeekRejectLiteralRune`) replace the three-state `Not` shape with one peek-and-reject opcode that decodes one rune inline, returns failure if it matches the inner pattern, success otherwise. Zero-width on the lexer either way.

The rule-stoppered `ScanUntilStopperEligibleRune` walks runes inline and only enters the stopper's full subprogram when the next rune is in the stopper's `FirstConsumedTokens` set. This is what makes paragraph terminators, CDATA's `]]>`, and Python triple-quotes fast even though their stopper is itself a rule.

The `ScannerSkipAdvance` opcode handles the `ZeroOrMore(FirstOf(match..., AnyToken.Delete))` shape that any "scan a haystack for sparse matches" grammar reduces to. At the top of each iteration, instead of invoking the inner `FirstOf` at every rune (and falling through to the deleted `AnyToken` for non-matches), the opcode jumps the lexer straight to the next position where one of the candidate matches could plausibly start. For literal-only alternatives the prefilter is stronger still: the scanner walks straight to the next full-literal candidate via the BCL's optimized substring search. This is the same skip the recursive evaluator does in `BetweenInclusiveRule.TryCreateScannerSkip`, ported as one opcode plus a side table on the program.

These fused opcodes are not magic. They are pattern matches in the compiler that recognize a shape we measured and decided was worth a dedicated opcode. Adding more is an open-ended project.

## What Lives Where

A rough map of `src/InductorParser/StateMachine/`:

`StateMachineParser.cs` is the public entry point. It owns the two caches (one per `PreserveAllSymbols` value) and the per-thread lexer pool. `Parse` and `TryMatch` both live here. `TryMatch` is the matcher-only variant: it skips `TreeBuilder` and the result allocation when the answer just needs to be "yes or no."

`Lowerer.cs` is the compiler. `Lower(rule)` does the cycle pre-pass and the main compilation pass and returns a `CompiledProgram`. `LoweringContext` is the per-compile scratch space (state list, dedup tables, cyclic-rule registry).

`CompiledProgram.cs` is the immutable result of compilation. States, side tables, entry-state index, and the `HasOutputs` flag the runtime checks to decide whether to allocate the output list at all.

`State.cs`, `LoweredOpCode.cs`, `OutputOp.cs`, `BacktrackFrame.cs`, and `CallFrame.cs` define the small structs the runtime uses. They are read-mostly and intentionally small (sixteen-byte states, four-frame-per-cache-line stacks).

`Machine.cs` is the mutable run state. It owns the two stacks, the output list, the deepest-failure record, and the per-thread pool plumbing. Lives on the stack of `Stepper.Run` as a `ref Machine` so the inner loop reads and writes fields without going through a class indirection.

`Stepper.cs` is the interpreter. `Run` is the outer loop. `Step` is the dispatcher. The per-opcode `Step_*` helpers are aggressively inlined so the JIT folds them into the switch case bodies.

`TreeBuilder.cs` consumes the output list and produces Symbols. This is where FlattenType actually shapes the tree.

## What This Document Is Not

This is not the place to learn how to write a grammar (see `primer1.md`, `primer2.md`, and `Primer3.md`), or how the recursive evaluator works (see `CodeArchitecture.md` and the `Rule.cs` header comment), or what the test-coverage bar is (see `TestArchitecture.md`). It is a sketch of how the second evaluator is laid out so the next person reading the code knows which file to open first.

There are gaps. Budget enforcement (`RuleCountLimit`, `MaxDepth`, `Timeout`, `Cancellation`) only fires inside the recursive evaluator's `EnterRule` today, so the state machine path does not honor them outside the bridge. Tracing is not wired into the state machine the way it is in the recursive evaluator. Both are known. Neither has bitten anyone yet.
