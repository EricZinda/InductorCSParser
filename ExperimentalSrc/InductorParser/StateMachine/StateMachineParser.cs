using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser.StateMachine;

// Public entry point for the state-machine evaluator. Mirrors the
// shape of Rule.Parse(string, ParseOptions) but routes through the
// flat lowered program instead of the recursive virtual TryParseRule
// path. The first call for a given rule lowers and caches the
// program. Subsequent calls reuse it.
//
// Iteration 1 scope: Literal, Grapheme, OneOf, Eof, And, Or,
// BetweenInclusive (covers Optional / OneOrMore / ZeroOrMore /
// AtLeast / AtMost / Exactly), Not, Peek, LateBound. Error position.
// FlattenType handling. PreserveAllSymbols. No budgets, no
// normalization, no trace.
public static class StateMachineParser
{
    // Two caches, one per PreserveAllSymbols. Fast / debug splits
    // because PreserveAllSymbols changes which output states the
    // lowerer skips. Most users only ever hit one of the two caches.
    private static readonly ConditionalWeakTable<Rule, CompiledProgram> _cacheFast = new();
    private static readonly ConditionalWeakTable<Rule, CompiledProgram> _cacheDebug = new();

    // Per-thread Lexer pool. Each Parse call would otherwise heap-
    // allocate a fresh Lexer; pooling reuses one instance per thread.
    // Combined with the existing backtrack/call/output buffer pools, a
    // steady-state Parse on a pooled grammar allocates nothing for the
    // parse infrastructure (only the result Symbols themselves).
    [ThreadStatic]
    private static Lexer? _pooledLexer;

    public static ParseResult Parse(Rule rootRule, string input) =>
        Parse(rootRule, input, new ParseOptions());

    // Slim matcher-only entry point. Returns true iff the grammar
    // accepts the entire input (same accept/reject answer as Parse,
    // just bool instead of ParseResult). Skips TreeBuilder, the
    // top-level Symbols list allocation, and the ParseResult struct
    // construction; the parse infrastructure is otherwise identical
    // to Parse. Useful when the question is "does this match" and
    // the parse tree isn't needed — the equivalent of Regex.IsMatch
    // for grammars.
    //
    // When the grammar's effective shape produces no outputs
    // (everything Delete-flattened, no Preserve composites), the
    // CompiledProgram.HasOutputs flag is false and the Machine
    // skips the output-list pool fetch entirely.
    public static bool TryMatch(Rule rootRule, string input) =>
        TryMatch(rootRule, input, new ParseOptions());

    public static bool TryMatch(Rule rootRule, string input, ParseOptions options)
    {
        // A pre-canceled signal on a parse short enough to finish before the
        // periodic budget check fires would otherwise be silently dropped:
        // TryMatch's only abort surface is the ParseBudgetExceeded catch
        // below, and the throw is gated on the 1024-invocation periodic
        // check. Pre-flighting matches Rule.Parse's same-day fix for the
        // recursive engine. Done before NormalizeIfRequested so a huge
        // pre-canceled input doesn't pay the normalize cost either.
        if (options.Cancellation != null && options.Cancellation.IsCanceled)
            return false;

        CompiledProgram program = GetOrLower(rootRule, options.PreserveAllSymbols);
        string parseInput = NormalizeIfRequested(input, rootRule.NormalizationForm);
        Lexer lexer = RentLexer(parseInput, options);
        lexer.ConfigureOptions(options);
        Machine machine = default;
        try
        {
            try
            {
                bool succeeded = Stepper.Run(program, lexer, out machine);
                return succeeded && lexer.IsEof;
            }
            catch (ParseBudgetExceeded)
            {
                // Budget abort is "did not match" for matcher-mode
                // callers. The Aborted outcome only surfaces on the
                // ParseResult-returning Parse path.
                return false;
            }
        }
        finally
        {
            machine.Release();
            ReturnLexerToPool(lexer);
        }
    }

    public static ParseResult Parse(Rule rootRule, string input, ParseOptions options)
    {
        // A pre-canceled signal on a parse short enough to finish before the
        // periodic budget check fires would otherwise be silently dropped
        // and the parse would return Success. The recursive engine has the
        // same pre-flight at Rule.Parse for tiny parses; the SM engine
        // needs its own because callers invoking StateMachineParser.Parse
        // directly bypass Rule.Parse. Done before NormalizeIfRequested so
        // a huge pre-canceled input doesn't pay the normalize cost either.
        if (options.Cancellation != null && options.Cancellation.IsCanceled)
        {
            string canceledMessage = Rule.BuildBudgetMessage(ParseOutcome.Canceled, abortPos: 0, input, options);
            return ParseResult.Aborted(ParseOutcome.Canceled, errorCharIndex: 0, canceledMessage, input, rootRule);
        }

        CompiledProgram program = GetOrLower(rootRule, options.PreserveAllSymbols);

        // Normalize before the lexer sees the input so grammars written
        // against one composition form also match the other. Mirrors the
        // recursive evaluator at Rule.Parse. When the input is already in
        // the target form (the common case for typed and web-sourced
        // text), String.Normalize short-circuits and returns the same
        // reference, which makes the downstream position translation a
        // pass-through. NormalizeInput = null skips the step entirely.
        string parseInput = NormalizeIfRequested(input, rootRule.NormalizationForm);

        Lexer lexer = RentLexer(parseInput, options);

        // Configure budgets / debug flags on the lexer so
        // Budget.EnterRuleAtDepth (called from Step_Call /
        // Step_CallSuppressOutputs) and BridgeToRecursive (which
        // delegates back to Rule.TryParse and calls Budget.EnterRule
        // itself) see the same options the recursive
        // evaluator would. RuleCountLimit / MaxDepth / Timeout /
        // Cancellation trip the same ParseBudgetExceeded the recursive
        // engine throws and we translate it into ParseResult.Aborted
        // below, mirroring Rule.Parse's catch.
        lexer.ConfigureOptions(options);

        Machine machine = default;
        try
        {
            bool succeeded;
            try
            {
                succeeded = Stepper.Run(program, lexer, out machine);
            }
            catch (ParseBudgetExceeded budget)
            {
                // The throw rolled the SM's call stack and lexer position
                // back through whatever frames were active at the point of
                // the abort. Use the same Math.Max idiom Rule.Parse uses
                // so the abort position prefers the deepest recorded
                // failure (a high-water mark not affected by rollback)
                // and falls back to the rolled-back lexer.Position when
                // no failure has been recorded yet. Both engines build
                // the same shape of ParseResult.Aborted from the same
                // ParseOutcome, so a side-by-side compare on the
                // recursive vs SM run agrees on outcome and position.
                int abortRaw = System.Math.Max(System.Math.Max(machine.DeepestFailure, lexer.DeepestFailurePosition), lexer.Position);
                int abortPos = NormalizedPositionMap.TranslateToOriginal(input, parseInput, abortRaw, rootRule.NormalizationForm);
                return ParseResult.Aborted(budget.Outcome, abortPos, Rule.BuildBudgetMessage(budget.Outcome, abortPos, input, options), input, rootRule);
            }

            // The root rule didn't match, a genuine grammar mismatch. Report
            // the deepest position any rule reached (a high-water mark not
            // affected by rollback), falling back to the rolled-back
            // lexer.Position when nothing was recorded, and surface the
            // deepest failure's WithError message. Mirrors the first failure
            // branch in Rule.ParseRecursive.
            if (!succeeded)
            {
                int failurePosition = System.Math.Max(machine.DeepestFailure, lexer.Position);
                int reportedPosition = NormalizedPositionMap.TranslateToOriginal(input, parseInput, failurePosition, rootRule.NormalizationForm);
                string message = Rule.BuildErrorMessage(machine.DeepestFailureMessage, failurePosition, parseInput, reportedPosition, input, options);
                return ParseResult.Failed(reportedPosition, message, input, rootRule);
            }

            // AllowTrailingInput relaxes the post-rule EOF check: the
            // grammar's own success condition was met (succeeded == true),
            // but unconsumed input remains past where the rule stopped.
            // This is a separate branch from the genuine-mismatch one above,
            // matching Rule.ParseRecursive: the trailing-input failure points
            // at lexer.Position (the first leftover character, the start of
            // the unconsumed tail), not Math.Max(DeepestFailure, Position).
            // A sibling alternative the success path rolled back can have
            // explored deeper than where the root stopped consuming, and that
            // abandoned position isn't the leftover character the user needs
            // to look at. The message is the generic positional one
            // (customMessage: null), not a WithError from a rolled-back rule
            // that isn't on the success path. See
            // docs/InductorParserDesignDecisions.md "Parse Requires Consuming
            // All Input" and the 2026-05-05 recursive-engine fix (backlog h4tn)
            // that split these two branches there.
            if (!options.AllowTrailingInput && !lexer.IsEof)
            {
                int trailingPosition = lexer.Position;
                int reportedPosition = NormalizedPositionMap.TranslateToOriginal(input, parseInput, trailingPosition, rootRule.NormalizationForm);
                string message = Rule.BuildErrorMessage(customMessage: null, trailingPosition, parseInput, reportedPosition, input, options);
                return ParseResult.Failed(reportedPosition, message, input, rootRule);
            }

            // Lowering bakes the effective shape into the program's
            // structure (Delete leaves and Flatten composites are
            // skipped on the fast path). Output ops still carry each
            // rule's DECLARED FlattenType so TreeBuilder can put the
            // declared value on Symbols, matching the recursive engine.
            // Pass the PreserveAllSymbols flag through so the builder
            // promotes Delete-declared composites and leaves into the
            // tree under debug mode rather than dropping them. Leaf
            // memory slices into parseInput, the same string the lexer
            // was reading, mirroring how the recursive engine builds
            // Symbols against the normalized text.
            // The ParseContext lets every Symbol TreeBuilder builds
            // resolve its rule name (Symbol.DisplayName / Is(string))
            // and translate parseInput offsets back to original-input
            // coordinates (Symbol.SourceRange / SourceText) under
            // normalization. Rule.ParseRecursive forwards the same
            // context to the recursive engine's Lexer. Without it here
            // every state-machine-built Symbol reported a null
            // DisplayName and leaked parseInput coordinates.
            var parseContext = new ParseContext(input, parseInput, rootRule.NormalizationForm, rootRule);
            IReadOnlyList<Symbol> symbols = TreeBuilder.Build(machine.OutputOps, parseInput, options.PreserveAllSymbols, parseContext);
            return ParseResult.Succeeded(symbols, input, rootRule);
        }
        finally
        {
            // Return the machine's heap-allocated buffers and the
            // lexer to the per-thread pool so the next Parse on this
            // thread can reuse them. TreeBuilder copied data out of
            // OutputOps into the Symbols above, so clearing the
            // list here is safe. Stepper.Run assigns machineOut up
            // front, so this still reaches a valid Machine even when
            // the budget-exceeded path took over.
            machine.Release();
            ReturnLexerToPool(lexer);
        }
    }

    // Tree-free counting entry points. They run the same Stepper as
    // Parse but skip TreeBuilder and the resulting Symbol allocation,
    // which is most of the per-parse cost on grep-style line-by-line
    // workloads. Each method walks Machine.OutputOps directly with
    // a small purpose-built reducer and returns a count (or 0 on a
    // failed parse). Use these when the caller would otherwise build
    // a parse tree and immediately fold it down to a number — the
    // rebar runner's count / count-spans / grep / grep-captures
    // models all fit that shape.
    //
    // Returns the number of times <paramref name="matchRule"/> opens
    // a composite in the produced output stream. Equivalent to
    // <c>Parse(...).Tree.FindAll(matchRule).LongCount()</c> but
    // without allocating any Symbols.
    public static long CountMatches(Rule rootRule, Rule matchRule, string input, ParseOptions options)
    {
        return RunAndReduce(rootRule, input, options, matchRule.Id, ReduceCountMatches, 0L);
    }

    // Returns the (offset, length) span of every <paramref name="matchRule"/>
    // firing in source order. For a leaf-shaped match rule (Literal,
    // LiteralIgnoreAsciiCase, ScanWhile, ScanUntil), the span is the
    // leaf's own offset/length. For a composite match rule, the span
    // covers the leftmost leaf's offset through the rightmost leaf's
    // offset+length within the match's Open/Close pair. Returns an
    // empty list on parse failure or when the rule never fires.
    //
    // This is the engine-level "where did matches happen" primitive.
    // Use it when the caller wants positions, slice substrings, or
    // build their own aggregation. The rebar runner's count-spans
    // model uses it like this:
    // <code>
    // long bytes = 0;
    // foreach (var span in StateMachineParser.EnumerateMatchSpans(rule, matchRule, input, opts))
    //     bytes += Encoding.UTF8.GetByteCount(input.AsSpan(span.Offset, span.Length));
    // </code>
    public static IReadOnlyList<MatchSpan> EnumerateMatchSpans(Rule rootRule, Rule matchRule, string input, ParseOptions options)
    {
        return RunAndReduce(rootRule, input, options, matchRule.Id, ReduceEnumerateMatchSpans, (IReadOnlyList<MatchSpan>)Array.Empty<MatchSpan>());
    }

    // Returns the total of (match count) plus (sum of distinct
    // capture-rule firings inside each match), counting each capture
    // rule once per match regardless of whether its captured span
    // is empty. Equivalent to the rebar grep-captures reducer in
    // BenchmarkPlan.CountGrepCaptures (which iterates the match's
    // sub-tree and tests `match.Find(capture) != null`), but skips
    // tree construction.
    public static long CountMatchesAndCaptures(
        Rule rootRule,
        Rule matchRule,
        IReadOnlyList<Rule> captureRules,
        string input,
        ParseOptions options)
    {
        var captureIds = new SymbolId[captureRules.Count];
        for (int index = 0; index < captureRules.Count; index++)
            captureIds[index] = captureRules[index].Id;
        return RunWithCaptureReducer(rootRule, input, options, matchRule.Id, captureIds);
    }

    // Returns true iff <paramref name="matchRule"/> opens a composite
    // at least once in the output stream. Bails out of the OutputOps
    // walk on the first hit. Equivalent to
    // <c>Parse(...).Tree.Find(matchRule) != null</c>.
    public static bool HasAnyMatch(Rule rootRule, Rule matchRule, string input, ParseOptions options)
    {
        return RunAndReduce(rootRule, input, options, matchRule.Id, ReduceHasAnyMatch, false);
    }

    private delegate T OutputReducer<T>(List<OutputOp> ops, string input, SymbolId matchId, T seed);

    private static T RunAndReduce<T>(
        Rule rootRule,
        string input,
        ParseOptions options,
        SymbolId matchId,
        OutputReducer<T> reducer,
        T failureValue)
    {
        // Pre-canceled signal pre-flight: a tiny parse (counter / matcher
        // entry points fire on grep-style inputs that can finish below the
        // 1024-invocation periodic-check boundary) would otherwise drop the
        // cancellation. See TryMatch / Parse for the same fix.
        if (options.Cancellation != null && options.Cancellation.IsCanceled)
            return failureValue;

        CompiledProgram program = GetOrLower(rootRule, options.PreserveAllSymbols);
        string parseInput = NormalizeIfRequested(input, rootRule.NormalizationForm);
        Lexer lexer = RentLexer(parseInput, options);
        lexer.ConfigureOptions(options);
        Machine machine = default;
        try
        {
            bool succeeded;
            try
            {
                succeeded = Stepper.Run(program, lexer, out machine);
            }
            catch (ParseBudgetExceeded)
            {
                // Counter / matcher entry points return a single value
                // (count, span list, bool); collapse the abort to the
                // same "did not match anything" answer a clean failure
                // would produce.
                return failureValue;
            }
            if (!succeeded || !lexer.IsEof)
                return failureValue;
            return reducer(machine.OutputOps, parseInput, matchId, default!);
        }
        finally
        {
            machine.Release();
            ReturnLexerToPool(lexer);
        }
    }

    private static long RunWithCaptureReducer(
        Rule rootRule,
        string input,
        ParseOptions options,
        SymbolId matchId,
        SymbolId[] captureIds)
    {
        // Pre-canceled signal pre-flight: see RunAndReduce / TryMatch /
        // Parse for the same fix. CountMatchesAndCaptures' single
        // caller-visible return value collapses every abort path (and
        // the pre-cancellation path) to zero.
        if (options.Cancellation != null && options.Cancellation.IsCanceled)
            return 0;

        CompiledProgram program = GetOrLower(rootRule, options.PreserveAllSymbols);
        string parseInput = NormalizeIfRequested(input, rootRule.NormalizationForm);
        Lexer lexer = RentLexer(parseInput, options);
        lexer.ConfigureOptions(options);
        Machine machine = default;
        try
        {
            bool succeeded;
            try
            {
                succeeded = Stepper.Run(program, lexer, out machine);
            }
            catch (ParseBudgetExceeded)
            {
                return 0;
            }
            if (!succeeded || !lexer.IsEof) return 0;
            return CountMatchesAndCapturesCore(machine.OutputOps, matchId, captureIds);
        }
        finally
        {
            machine.Release();
            ReturnLexerToPool(lexer);
        }
    }

    // Normalize the caller's input string into the form the lexer should
    // see. When the input is already in the target form, String.Normalize
    // returns the same reference and the downstream position-translation
    // step is a pointer-equality pass-through. A null form skips
    // normalization entirely. The form is read from the compiled rule
    // (Rule.NormalizationForm), where it's committed at Compile time.
    private static string NormalizeIfRequested(string input, NormalizationForm? form) =>
        form.HasValue ? input.Normalize(form.Value) : input;

    private static long ReduceCountMatches(List<OutputOp> ops, string input, SymbolId matchId, long _)
    {
        // The match rule may be either a composite (And, Or,
        // BetweenInclusive, ...) emitted as Open/Close framing or a
        // leaf (Literal, LiteralIgnoreAsciiCase, ScanWhile) emitted as
        // a single EmitLeaf. Both shapes count toward "this rule
        // fired"; mirror the recursive Symbol.FindAll behavior which
        // matches by Id regardless of leaf/composite. Prebuilt entries
        // come from BridgeToRecursive and carry the original Symbol;
        // count those whose Id matches too.
        long count = 0;
        for (int index = 0; index < ops.Count; index++)
        {
            var operation = ops[index];
            if (operation.Kind == OutputKind.CloseComposite) continue;
            SymbolId opId = operation.Kind == OutputKind.Prebuilt
                ? (operation.PrebuiltSymbol?.Id ?? default)
                : operation.SymbolId;
            if (opId == matchId) count++;
        }
        return count;
    }

    private static bool ReduceHasAnyMatch(List<OutputOp> ops, string input, SymbolId matchId, bool _)
    {
        for (int index = 0; index < ops.Count; index++)
        {
            var operation = ops[index];
            if (operation.Kind == OutputKind.CloseComposite) continue;
            SymbolId opId = operation.Kind == OutputKind.Prebuilt
                ? (operation.PrebuiltSymbol?.Id ?? default)
                : operation.SymbolId;
            if (opId == matchId) return true;
        }
        return false;
    }

    private static IReadOnlyList<MatchSpan> ReduceEnumerateMatchSpans(List<OutputOp> ops, string input, SymbolId matchId, IReadOnlyList<MatchSpan> _)
    {
        // Walk the OutputOps once. Top-level EmitLeaf with matchId is
        // a leaf match (its offset/length is the span). OpenComposite
        // with matchId enters a composite-match window; collect every
        // EmitLeaf inside, take min(offset) and max(offset+length) as
        // the span. Nested composites count as inside the window via
        // the depth counter. Empty composite matches (no leaves) get
        // a (0, 0) span — none of the supported rebar rules can
        // produce one in practice, but the fallback keeps the
        // contract honest.
        List<MatchSpan>? spans = null;
        int depth = -1;
        int matchStart = -1;
        int matchEnd = -1;
        for (int index = 0; index < ops.Count; index++)
        {
            var operation = ops[index];
            switch (operation.Kind)
            {
                case OutputKind.OpenComposite:
                    if (depth >= 0) { depth++; break; }
                    if (operation.SymbolId == matchId)
                    {
                        depth = 0;
                        matchStart = -1;
                        matchEnd = -1;
                    }
                    break;
                case OutputKind.CloseComposite:
                    if (depth < 0) break;
                    if (depth == 0)
                    {
                        spans ??= new List<MatchSpan>();
                        spans.Add(matchStart < 0
                            ? new MatchSpan(0, 0)
                            : new MatchSpan(matchStart, matchEnd - matchStart));
                        depth = -1;
                        break;
                    }
                    depth--;
                    break;
                case OutputKind.EmitLeaf:
                    if (depth >= 0)
                    {
                        if (matchStart < 0) matchStart = operation.Offset;
                        matchEnd = operation.Offset + operation.Length;
                        break;
                    }
                    if (operation.SymbolId == matchId)
                    {
                        spans ??= new List<MatchSpan>();
                        spans.Add(new MatchSpan(operation.Offset, operation.Length));
                    }
                    break;
                case OutputKind.Prebuilt:
                    if (operation.PrebuiltSymbol == null) break;
                    if (depth >= 0)
                    {
                        // The Prebuilt symbol's leaves carry their own
                        // (input, offset, length) via ReadOnlyMemory<char>;
                        // contribute every leaf's bounds to the
                        // enclosing match span. ScanWhile is the common
                        // shape that lands here today: it bridges to
                        // the recursive evaluator and emits one leaf
                        // Symbol over the matched run.
                        UpdateBoundsFromSymbol(operation.PrebuiltSymbol, ref matchStart, ref matchEnd);
                        break;
                    }
                    if (operation.PrebuiltSymbol.Id == matchId &&
                        TryGetSymbolSpan(operation.PrebuiltSymbol, out int offset, out int length))
                    {
                        spans ??= new List<MatchSpan>();
                        spans.Add(new MatchSpan(offset, length));
                    }
                    break;
            }
        }
        return spans ?? (IReadOnlyList<MatchSpan>)Array.Empty<MatchSpan>();
    }

    private static void UpdateBoundsFromSymbol(Symbol symbol, ref int matchStart, ref int matchEnd)
    {
        // "Has children", not "is a composite": recurse into the children
        // when there are any, otherwise this node's bounds come from its
        // own recorded span. A leaf keeps its text in LeafMemory; a
        // childless composite keeps its consumedSpan there too, so the
        // LeafMemory read below covers both childless shapes. Switching
        // this to IsLeaf would skip that read for a childless composite
        // and lose its span.
        if (symbol.Children.Count > 0)
        {
            foreach (var child in symbol.Children)
                UpdateBoundsFromSymbol(child, ref matchStart, ref matchEnd);
            return;
        }
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetString(symbol.LeafMemory, out _, out int start, out int length) && length > 0)
        {
            if (matchStart < 0 || start < matchStart) matchStart = start;
            if (start + length > matchEnd) matchEnd = start + length;
        }
    }

    private static bool TryGetSymbolSpan(Symbol symbol, out int offset, out int length)
    {
        int spanStart = -1;
        int spanEnd = -1;
        UpdateBoundsFromSymbol(symbol, ref spanStart, ref spanEnd);
        if (spanStart < 0)
        {
            offset = 0;
            length = 0;
            return false;
        }
        offset = spanStart;
        length = spanEnd - spanStart;
        return true;
    }

    private static long CountMatchesAndCapturesCore(List<OutputOp> ops, SymbolId matchId, SymbolId[] captureIds)
    {
        // For each match (Open/Close pair with matchId), count the
        // match itself plus the number of distinct capture rules
        // that fire anywhere within its sub-range. Capture rules can
        // appear as composites (Capture(And(...)) emits Open/Close)
        // or as leaves (Capture(ScanUntil(...)) emits a single
        // EmitLeaf), depending on what the wrapped rule lowers to.
        // The check matches by SymbolId regardless of op kind so
        // both shapes count the same way the recursive
        // Symbol.Find(capture) walk would.
        long total = 0;
        int matchDepth = -1;
        var seenInMatch = new bool[captureIds.Length];
        for (int index = 0; index < ops.Count; index++)
        {
            var operation = ops[index];
            SymbolId opId = operation.Kind == OutputKind.Prebuilt
                ? (operation.PrebuiltSymbol?.Id ?? default)
                : operation.SymbolId;
            switch (operation.Kind)
            {
                case OutputKind.OpenComposite:
                    if (matchDepth < 0)
                    {
                        if (opId == matchId)
                        {
                            matchDepth = 0;
                            total++;
                            for (int captureIndex = 0; captureIndex < seenInMatch.Length; captureIndex++)
                                seenInMatch[captureIndex] = false;
                        }
                    }
                    else
                    {
                        matchDepth++;
                        TryCountCapture(opId, captureIds, seenInMatch, ref total);
                    }
                    break;
                case OutputKind.CloseComposite:
                    if (matchDepth < 0) break;
                    if (matchDepth == 0) { matchDepth = -1; break; }
                    matchDepth--;
                    break;
                case OutputKind.EmitLeaf:
                case OutputKind.Prebuilt:
                    if (matchDepth < 0) break;
                    TryCountCapture(opId, captureIds, seenInMatch, ref total);
                    break;
            }
        }
        return total;
    }

    private static void TryCountCapture(SymbolId opId, SymbolId[] captureIds, bool[] seenInMatch, ref long total)
    {
        for (int captureIndex = 0; captureIndex < captureIds.Length; captureIndex++)
        {
            if (!seenInMatch[captureIndex] && opId == captureIds[captureIndex])
            {
                seenInMatch[captureIndex] = true;
                total++;
                return;
            }
        }
    }

    // Rent a Lexer from the per-thread pool, or allocate one if the
    // pool is empty. ResetForReuse clears all per-parse state and
    // re-binds the lexer to the new input string. The matching
    // ReturnLexerToPool puts it back when the parse finishes.
    //
    // Pool slot is set to null while the lexer is in use so a parser
    // that re-enters during a bridge call (synchronously) sees an
    // empty pool and allocates fresh, rather than corrupting the
    // outer parse's state.
    private static Lexer RentLexer(string input, ParseOptions options)
    {
        var pooled = _pooledLexer;
        if (pooled != null)
        {
            _pooledLexer = null;
            pooled.ResetForReuse(input, options.TraceSink, options.TraceLevel);
            return pooled;
        }
        return new Lexer(input, traceSink: options.TraceSink, traceLevel: options.TraceLevel);
    }

    private static void ReturnLexerToPool(Lexer lexer)
    {
        _pooledLexer = lexer;
    }

    private static CompiledProgram GetOrLower(Rule rootRule, bool preserveAllSymbols)
    {
        var cache = preserveAllSymbols ? _cacheDebug : _cacheFast;
        if (cache.TryGetValue(rootRule, out var existing)) return existing;
        var lowered = Lowerer.Lower(rootRule, preserveAllSymbols);
        cache.AddOrUpdate(rootRule, lowered);
        return lowered;
    }

}
