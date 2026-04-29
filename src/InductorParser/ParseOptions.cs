using System;
using System.IO;
using System.Text;
using InductorParser.Tracing;

namespace InductorParser;

public sealed class ParseOptions
{
    // Atomic unit the lexer reads. Default is grapheme so user-typed text
    // behaves the way users expect: one character for a user is one token.
    public InputUnit InputUnit { get; set; } = InputUnit.Grapheme;

    // Normalization form applied to the input before parsing. Default is the
    // composed form (FormC), which is what almost every grammar wants and
    // what essentially all web, source, and typed input already is. A
    // grammar written against Literal("café") (precomposed é, U+00E9) with this
    // default will also match decomposed "cafe\u0301" input, because the
    // normalizer rewrites the latter to the former before the lexer sees
    // it. Set to null to skip normalization entirely.
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

    // Gates how verbose the trace output is
    public TraceLevel TraceLevel { get; set; } = TraceLevel.Diagnostic;

    // Caps how many rules the parse is allowed to invoke before giving
    // up. Every rule invocation increments a counter. When it exceeds
    // this number, the parse aborts with
    // ParseOutcome.RuleCountLimitExceeded. Because it's a count and not
    // a wall-clock measurement, the same input against the same grammar
    // trips at exactly the same point on every run regardless of
    // hardware speed. The default of 10,000,000 lets well-formed parses
    // through (a 1 MB file runs through low millions of invocations on
    // a typical grammar) and cleanly catches the catastrophic-
    // backtracking shapes that produce tens of billions of invocations
    // on tiny inputs. Set to 0 to disable.
    public long RuleCountLimit { get; set; } = 10_000_000L;

    // Maximum recursion depth (rule invocations currently on the call
    // stack). Catches deeply nested but well-formed input (think 10,000
    // open parens) before it blows the .NET call stack and crashes the
    // host process. Independent of RuleCountLimit: a deeply nested
    // input may use few invocations total. Set to 0 to disable.
    public int MaxDepth { get; set; } = 1000;

    // Wall-clock limit. The parse loop polls Stopwatch.Elapsed
    // synchronously from inside its own loop, so the deadline trips
    // even on WebGL where there is no background timer thread. Set to
    // TimeSpan.Zero to disable, matching the RuleCountLimit / MaxDepth
    // convention. Off by default because timeouts are inherently flaky
    // (same input takes different time on different hardware) and would
    // cause unpredictable test failures as a default.
    public TimeSpan Timeout { get; set; } = TimeSpan.Zero;

    // External cancellation signal. Null means no cancellation source.
    // See ParseCancellation for what it does, why it's a custom type
    // instead of System.Threading.CancellationToken, and how to bridge
    // from an existing CancellationToken.
    public ParseCancellation? Cancellation { get; set; }

    // When true, Parse returns a tree whose shape matches the grammar
    // one-to-one: every FlattenType.Flatten wrapper, every
    // FlattenType.Delete node, and every individual leaf symbol is
    // present exactly where the grammar placed it. Turn this on to
    // debug or inspect a grammar, to PrintTree the full structure, or
    // to Find(rule) against wrappers that the default path would lift
    // out.
    //
    // The off-by-default path applies each rule's FlattenType before
    // returning so that FlattenType.Delete nodes are gone,
    // FlattenType.Flatten wrappers have their children lifted into
    // the parent, and FlattenType.Preserve wrappers remain as
    // findable nodes. That is the shape most
    // callers actually want to walk: the syntactic noise (delimiters,
    // whitespace, anonymous grouping wrappers) is already out of the
    // way. The consequence is that Tree.Find(rule) only hits rules
    // whose FlattenType is Preserve. Set .Flatten(FlattenType.Preserve)
    // on any rule whose wrapper you need to locate after parsing.
    public bool PreserveAllSymbols { get; set; } = false;

    // When true, Parse succeeds as soon as the root rule matches, even
    // if the lexer hasn't reached end of input. The default (false)
    // requires every token of the input to be consumed by the grammar
    // before Parse returns success: a trailing tail the grammar didn't
    // claim turns the parse into a failure positioned at the first
    // unconsumed token. See docs/InductorParserDesignDecisions.md "Parse Requires
    // Consuming All Input" for why the default is strict.
    //
    // Turn this on for prefix parsing: matching one record at the
    // front of a longer stream, testing a sub-rule against an input
    // longer than the rule was meant to consume, or recognising a
    // command at the start of a line and handing the rest off to
    // another parser. The deepest-failure / error-position machinery
    // is unaffected: a failure inside the rule still reports its own
    // position. The only behavior that changes is whether trailing
    // unconsumed input is treated as a parse failure.
    public bool AllowTrailingInput { get; set; } = false;
}
