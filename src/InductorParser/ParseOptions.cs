using System;
using System.IO;
using System.Text;
using InductorParser.Tracing;

namespace InductorParser;

public sealed class ParseOptions
{
    // Atomic unit the lexer reads. Default is grapheme so user-typed text
    // behaves the way users expect, even though
    // the underlying StringInfo implementation has known gaps on pre-.NET 5
    // runtimes (see GraphemeLexer.cs and backlog/i001).
    public InputUnit InputUnit { get; set; } = InputUnit.Grapheme;

    // Normalization form applied to the input before parsing. Default is the
    // composed form (FormC), which is what almost every grammar wants and
    // what essentially all web, source, and typed input already is. A
    // grammar written against Char("café") (precomposed é, U+00E9) with this
    // default will also match decomposed "cafe\u0301" input, because the
    // normalizer rewrites the latter to the former before the lexer sees
    // it. Set to null to skip normalization entirely (byte-exact
    // round-trippability, at the cost of losing the safety net).
    //
    // Positions reported in ParseResult (ErrorCharIndex and its derived
    // line/column/rune/grapheme properties) are ALWAYS into the caller's
    // original input string, regardless of this setting. When normalization
    // actually rewrites the input, the parser translates failure offsets
    // back to original-string coordinates at the boundary, so callers never
    // have to think about which coordinate system a position lives in. The
    // common case where the input is already in the target form pays zero
    // extra cost: String.Normalize returns the same string reference and
    // the translation step is skipped.
    public NormalizationForm? NormalizeInput { get; set; } = NormalizationForm.FormC;

    // Where trace output goes when the parser is tracing. Null means
    // tracing is off
    public TextWriter? TraceSink { get; set; }

    // Gates how verbose the trace output is.
    public TraceLevel TraceLevel { get; set; } = TraceLevel.Diagnostic;

    // Deterministic work budget. Every rule invocation increments a
    // counter; when it exceeds this number, the parse aborts with
    // ParseOutcome.WorkLimitExceeded. The default of 10_000_000 lets
    // well-formed parses through (a 1 MB file runs through low millions
    // of invocations on a typical grammar) and cleanly catches the
    // catastrophic-backtracking shapes that produce tens of billions of
    // invocations on tiny inputs. Set to 0 to disable.
    public long MaxRuleInvocations { get; set; } = 10_000_000L;

    // Maximum recursion depth (rule invocations currently on the call
    // stack). Catches deeply nested but well-formed input (think 10,000
    // open parens) before it blows the .NET call stack and crashes the
    // host process. Independent of MaxRuleInvocations: a deeply nested
    // input may use few invocations total. Set to 0 to disable.
    public int MaxDepth { get; set; } = 1000;

    // Wall-clock limit. The parse loop polls Stopwatch.Elapsed
    // synchronously from inside its own loop, so the deadline trips
    // even on WebGL where there is no background timer thread. Set to
    // TimeSpan.Zero to disable, matching the MaxRuleInvocations / MaxDepth
    // convention. Off by default because timeouts are inherently flaky
    // (same input takes different time on different hardware) and would
    // cause unpredictable test failures as a default.
    public TimeSpan Timeout { get; set; } = TimeSpan.Zero;

    // External cancellation signal. The caller holds the
    // ParseCancellation and calls .Cancel() from wherever the cancel
    // decision is made (button click, request handler, test); the parser
    // polls IsCanceled inside its periodic budget check and aborts with
    // ParseOutcome.Canceled. Null means no cancellation source.
    //
    // Deliberately a custom type, not System.Threading.CancellationToken:
    // CancellationToken's CancelAfter shortcut silently fails on WebGL
    // because it depends on a background timer thread. ParseCancellation
    // exposes only manual Cancel(), so the broken-on-WebGL code path
    // cannot be expressed. For a wall-clock deadline use Timeout above,
    // which uses synchronous Stopwatch polling and works on every
    // target. See ParseCancellation.cs for the bridge pattern from an
    // existing CancellationToken.
    public ParseCancellation? Cancellation { get; set; }

    // When true, the parser disables every parse-time tree-shape
    // optimization and emits a tree whose structure matches the grammar
    // one-to-one. Turn this on to debug or inspect a grammar.
    //
    // Two optimizations are suppressed:
    //
    //   * Flatten-wrapper elision. An Or (or any FlattenType.Flatten
    //     rule) normally returns its single matching child straight up
    //     so the otherwise-collapsed wrapper never appears in the tree.
    //     With the flag on, the wrapper stays.
    //
    //   * Delete-node filtering. A rule whose effective FlattenType is
    //     Delete normally returns the shared Symbol.Discarded sentinel
    //     and contributes no Symbol to the parent. With the flag on,
    //     Delete-typed rules produce real Symbols and remain visible in
    //     the raw tree, so a grammar author can see every Char('"'),
    //     OptionalWhitespace, Not/Peek node exactly where the grammar
    //     placed it.
    //
    // The off-by-default path produces a tree whose post-hoc
    // Symbol.Flatten() output is identical to the pre-optimization
    // Flatten output. Turning this flag on recovers the pre-optimization
    // raw tree shape exactly — useful for PrintTree and Find(rule)
    // queries against wrappers that would otherwise be elided.
    public bool PreserveFlattenWrappers { get; set; } = false;
}
