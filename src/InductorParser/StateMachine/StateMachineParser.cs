using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser.StateMachine;

// Public entry point for the state-machine evaluator. Mirrors the
// shape of Rule.Parse(string, ParseOptions) but routes through the
// flat lowered program instead of the recursive virtual TryParseRule
// path. The first call for a given rule lowers and caches the
// program. Subsequent calls reuse it.
//
// Iteration 1 scope: Literal, Token, OneOf, Eof, AllOf, FirstOf,
// BetweenInclusive (covers Optional / OneOrMore / ZeroOrMore /
// AtLeast / AtMost / Exactly), Not, Peek, LateBound. Both lexers.
// Error position. FlattenType handling. PreserveAllSymbols. No
// budgets, no normalization, no trace.
public static class StateMachineParser
{
    // Four caches, one per (PreserveAllSymbols, InputUnit) combo.
    // Fast / debug splits because PreserveAllSymbols changes which
    // output states the lowerer skips. Rune / grapheme splits
    // because the rune-only fused-scan opcodes (ScanOneOfRune /
    // ScanNoneOfRune) inline rune decode, which would split multi-
    // rune graphemes under InputUnit.Grapheme. Most users only ever
    // hit one or two of the four caches.
    private static readonly ConditionalWeakTable<Rule, CompiledProgram> _cacheFastRune = new();
    private static readonly ConditionalWeakTable<Rule, CompiledProgram> _cacheFastGrapheme = new();
    private static readonly ConditionalWeakTable<Rule, CompiledProgram> _cacheDebugRune = new();
    private static readonly ConditionalWeakTable<Rule, CompiledProgram> _cacheDebugGrapheme = new();

    // Per-thread Lexer pool. Each Parse call would otherwise heap-
    // allocate a fresh RuneLexer or GraphemeLexer; pooling reuses one
    // instance per thread per lexer type. Combined with the existing
    // backtrack/call/output buffer pools, a steady-state Parse on
    // a pooled grammar allocates nothing for the parse infrastructure
    // (only the result Symbols themselves).
    [ThreadStatic]
    private static RuneLexer? _pooledRuneLexer;
    [ThreadStatic]
    private static GraphemeLexer? _pooledGraphemeLexer;

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
        CompiledProgram program = GetOrLower(rootRule, options.PreserveAllSymbols, options.InputUnit);
        Lexer lexer = RentLexer(input, options);
        lexer.ConfigureBudgets(options);
        bool succeeded = Stepper.Run(program, lexer, out Machine machine);
        try
        {
            return succeeded && lexer.IsEof;
        }
        finally
        {
            machine.Release();
            ReturnLexerToPool(lexer);
        }
    }

    public static ParseResult Parse(Rule rootRule, string input, ParseOptions options)
    {
        CompiledProgram program = GetOrLower(rootRule, options.PreserveAllSymbols, options.InputUnit);

        Lexer lexer = RentLexer(input, options);

        // Configure budgets / debug flags on the lexer so the
        // BridgeToRecursive opcode (which delegates back to
        // Rule.TryParse) sees the same options the recursive evaluator
        // would. PreserveAllSymbols is the most important of these:
        // when on, bridged rules need to know to keep their FlattenType
        // promotions. Budget enforcement (RuleCountLimit, MaxDepth,
        // Timeout, Cancellation) only fires inside the recursive
        // evaluator's EnterRule, so the state-machine path doesn't
        // currently honor them; documenting that as a known gap.
        lexer.ConfigureBudgets(options);

        bool succeeded = Stepper.Run(program, lexer, out Machine machine);

        try
        {
            if (!succeeded)
            {
                int failurePosition = System.Math.Max(machine.DeepestFailure, lexer.Position);
                return ParseResult.Failed(failurePosition, BuildErrorMessage(machine, failurePosition, input), input, rootRule);
            }
            if (!lexer.IsEof)
            {
                int failurePosition = System.Math.Max(machine.DeepestFailure, lexer.Position);
                return ParseResult.Failed(failurePosition, BuildErrorMessage(machine, failurePosition, input), input, rootRule);
            }

            // The lowered program already baked the effective FlattenType
            // into its output states (Delete leaves and Flatten composites
            // were skipped on the fast path). TreeBuilder no longer needs
            // to apply any per-node override, so pass false here regardless.
            IReadOnlyList<Symbol> symbols = TreeBuilder.Build(machine.OutputOps, input, preserveAllSymbols: false);
            return ParseResult.Succeeded(symbols, input, rootRule);
        }
        finally
        {
            // Return the machine's heap-allocated buffers and the
            // lexer to the per-thread pool so the next Parse on this
            // thread can reuse them. TreeBuilder copied data out of
            // OutputOps into the Symbols above, so clearing the
            // list here is safe.
            machine.Release();
            ReturnLexerToPool(lexer);
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
        if (options.InputUnit == InputUnit.Rune)
        {
            var pooled = _pooledRuneLexer;
            if (pooled != null)
            {
                _pooledRuneLexer = null;
                pooled.ResetForReuse(input, options.TraceSink, options.TraceLevel);
                return pooled;
            }
            return new RuneLexer(input, options.TraceSink, options.TraceLevel);
        }
        else
        {
            var pooled = _pooledGraphemeLexer;
            if (pooled != null)
            {
                _pooledGraphemeLexer = null;
                pooled.ResetForReuse(input, options.TraceSink, options.TraceLevel);
                return pooled;
            }
            return new GraphemeLexer(input, options.TraceSink, options.TraceLevel);
        }
    }

    private static void ReturnLexerToPool(Lexer lexer)
    {
        if (lexer is RuneLexer rune)
            _pooledRuneLexer = rune;
        else if (lexer is GraphemeLexer grapheme)
            _pooledGraphemeLexer = grapheme;
    }

    private static CompiledProgram GetOrLower(Rule rootRule, bool preserveAllSymbols, InputUnit inputUnit)
    {
        var cache = (preserveAllSymbols, inputUnit) switch
        {
            (false, InputUnit.Rune) => _cacheFastRune,
            (false, _) => _cacheFastGrapheme,
            (true, InputUnit.Rune) => _cacheDebugRune,
            (true, _) => _cacheDebugGrapheme,
        };
        if (cache.TryGetValue(rootRule, out var existing)) return existing;
        var lowered = Lowerer.Lower(rootRule, preserveAllSymbols, inputUnit);
        cache.AddOrUpdate(rootRule, lowered);
        return lowered;
    }

    private static string BuildErrorMessage(Machine machine, int position, string input)
    {
        if (machine.DeepestFailureMessage is { } custom)
            return custom;
        if (position >= input.Length)
            return "Unexpected end of input.";
        return $"Parse failed at offset {position}: unexpected '{input[position]}'.";
    }
}
