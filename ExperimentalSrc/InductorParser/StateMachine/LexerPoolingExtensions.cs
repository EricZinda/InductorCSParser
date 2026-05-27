using System.IO;
using InductorParser.Lexing;
using InductorParser.Tracing;

namespace InductorParser.StateMachine;

// Lexer-pooling support the StateMachine uses to reuse one Lexer
// instance across back-to-back parses on the same thread instead of
// allocating a fresh one per parse. The recursive evaluator constructs
// a new Lexer per parse and doesn't call any of these.
internal static class LexerPoolingExtensions
{
    // Re-bind a previously-used Lexer to a new input string and reset
    // all per-parse state. Pooled lexers are always full-input grapheme-
    // mode (no sub-lexer state to roll over), so the rune-per-token
    // mode can't leak between parses.
    //
    // After ResetForReuse, the caller is expected to invoke
    // ConfigureBudgets to set the budget limits and PreserveAllSymbols
    // for the new parse. The reset clears the budget counters via
    // Budget.Reset so a pooled lexer can't leak rule-invocation count
    // from the previous parse.
    public static void ResetForReuse(this Lexer lexer, string input, TextWriter? traceSink, TraceLevel traceLevel)
    {
        if (input == null) throw new System.ArgumentNullException(nameof(input));
        lexer.BindInput(input, startPosition: 0, endPosition: input.Length, traceSink, traceLevel);
        lexer.ResetParseState();
        lexer.Budget.Reset();
    }
}
