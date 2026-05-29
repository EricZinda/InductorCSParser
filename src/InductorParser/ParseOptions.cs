using System;
using System.IO;
using System.Text;
using InductorParser.Tracing;

namespace InductorParser;

public sealed class ParseOptions
{
    // Note: Unicode normalization form is no longer set here. It's a
    // grammar-level decision committed at Compile time. Call
    // rule.Compile(NormalizationForm.FormC) (or null to disable) before
    // parsing if you want a form other than the FormC default. See
    // Rule.Compile(NormalizationForm?) for the rationale and the
    // compile-time validation that catches rules whose literal text
    // isn't already in the chosen form.
    //
    // Positions reported in ParseResult (ErrorCharIndex and its derived
    // line/column/token properties) are ALWAYS into the caller's
    // original input string, regardless of which form the grammar was
    // compiled against. When normalization rewrites the input, the parser
    // translates failure offsets back to original-string coordinates at
    // the boundary, so callers never have to think about which coordinate
    // system a position lives in. If normalization returns the original
    // string reference, translation is skipped; otherwise the mapping is
    // paid only on failure / abort paths.

    // Where trace output goes when the parser is tracing. Null means
    // tracing is off
    public TextWriter? TraceSink { get; set; }

    // Gates how verbose the trace output is
    public TraceLevel TraceLevel { get; set; } = TraceLevel.Diagnostic;

    // Caps how many work units the parse may consume before giving up.
    // Each rule invocation counts as one unit, and each iteration of a
    // bulk-scan inner loop (ScanWhile, ScanUntil, the AdvanceWhile*
    // primitives, any user rule that calls Lexer.TickBudget) counts as
    // one too. The parse aborts with ParseOutcome.RuleCountLimitExceeded
    // when the counter exceeds this limit. Because it's a count not a
    // wall-clock measurement, the same input against the same grammar
    // trips at the same point on every run. The default of 10,000,000
    // lets well-formed parses through (a 1 MB file usually runs through
    // low millions) and catches both catastrophic-backtracking shapes
    // and bulk-scan DoS. Set to 0 to disable.
    public long RuleCountLimit { get; set; } = 10_000_000L;

    // Maximum recursion depth (rule invocations currently on the call
    // stack). Catches deeply nested but well-formed input (think 10,000
    // open parens) before it blows the .NET call stack and crashes the
    // host process. Independent of RuleCountLimit: a deeply nested
    // input may use few invocations total.
    //
    // Set to 0 to disable, but be clear about what that costs. With no
    // depth limit, deeply nested input recurses until the .NET call
    // stack runs out and the runtime throws StackOverflowException. That
    // exception can't be caught. It never surfaces as a failed
    // ParseResult. It terminates the whole process on the spot, taking
    // every other in-flight request and thread down with it. A handful
    // of kilobytes of nested brackets is enough to trigger it (the same
    // shape as CVE-2026-40324 and similar recursive-descent parser DoS
    // reports). Any parser that touches untrusted input must keep a
    // non-zero limit. Only disable it when you fully control the input
    // and know its nesting stays shallow.
    public int MaxDepth { get; set; } = 1000;

    // Wall-clock limit. The parse loop polls Stopwatch.Elapsed
    // synchronously from inside its own loop, so the deadline trips
    // even on WebGL where there's no background timer thread. Set to
    // TimeSpan.Zero to disable, matching the RuleCountLimit / MaxDepth
    // convention. Off by default because timeouts are inherently flaky
    // (same input takes different time on different hardware) and would
    // cause unpredictable test failures as a default.
    //
    // Best-effort, by design. The clock is polled on a periodic check
    // that fires once every so many rule invocations, not on every
    // step (checking the clock on every step would cost more than it
    // saves). A parse that finishes in fewer invocations than that
    // interval never reaches a check, so a very short parse can run
    // past a very small Timeout and still return its normal result
    // instead of aborting. That's fine in practice: the deadline is
    // there to stop a long-running or runaway parse, and a parse long
    // enough to matter runs long enough to hit a check. A parse that
    // finishes before the first check is already fast, so letting it
    // complete is the right outcome. Don't rely on Timeout to trip on a
    // tiny grammar against a tiny input. If you need a hard, count-based
    // cap that trips deterministically regardless of input size, use
    // RuleCountLimit instead.
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
    // findable nodes. That's the shape most
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

    // Templates for the default error messages the parser produces when no
    // .WithError("...") was attached at the deepest failure position, or
    // when a budget abort (timeout, rule count, recursion depth,
    // cancellation) ends the parse. Each template can include named
    // placeholders that the parser substitutes when it builds the message.
    // Placeholders are written as {name}. Unknown placeholders pass through
    // verbatim, so a typo shows up in the output rather than throwing.
    //
    // Every template supports the same four position placeholders, named
    // and numbered to match the ParseResult.ErrorXxx properties so a
    // template author can mirror whatever unit the rest of their code
    // already uses:
    //   {charIndex}    ParseResult.ErrorCharIndex   (UTF-16 code units)
    //   {tokenIndex}   ParseResult.ErrorTokenIndex  (StringInfo text elements)
    //   {line}         ParseResult.ErrorLine        (zero-based, LSP convention)
    //   {column}       ParseResult.ErrorColumn      (zero-based, in chars)
    //
    // Templates that mention an additional unit-specific placeholder:
    //   PositionalErrorTemplate      {character}  (the unexpected input character)
    //   TimeoutAbortTemplate         {timeout}    (options.Timeout as a TimeSpan string)
    //   RuleCountLimitAbortTemplate  {limit}      (options.RuleCountLimit)
    //   DepthLimitAbortTemplate      {limit}      (options.MaxDepth)
    //
    // The token-index and line/column conversions each walk the
    // input once, so they're computed lazily and only paid for when
    // the corresponding placeholder appears in the template. The
    // default templates only mention {charIndex}, so by default the
    // O(n) scans never run.
    //
    // Defaults match the pre-template hardcoded strings exactly, so
    // grammars and tests that didn't customize anything see the same
    // output as before. Setting a template to null throws.
    private string _positionalErrorTemplate =
        "Parse failed at offset {charIndex}: unexpected '{character}'.";
    public string PositionalErrorTemplate
    {
        get => _positionalErrorTemplate;
        set => _positionalErrorTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _endOfInputErrorTemplate = "Unexpected end of input.";
    public string EndOfInputErrorTemplate
    {
        get => _endOfInputErrorTemplate;
        set => _endOfInputErrorTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _timeoutAbortTemplate = "Parse aborted: timeout exceeded.";
    public string TimeoutAbortTemplate
    {
        get => _timeoutAbortTemplate;
        set => _timeoutAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _ruleCountLimitAbortTemplate = "Parse aborted: rule-count limit exceeded.";
    public string RuleCountLimitAbortTemplate
    {
        get => _ruleCountLimitAbortTemplate;
        set => _ruleCountLimitAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _depthLimitAbortTemplate = "Parse aborted: maximum recursion depth exceeded.";
    public string DepthLimitAbortTemplate
    {
        get => _depthLimitAbortTemplate;
        set => _depthLimitAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _cancellationAbortTemplate = "Parse aborted: cancellation requested.";
    public string CancellationAbortTemplate
    {
        get => _cancellationAbortTemplate;
        set => _cancellationAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    // Test-plumbing knob (intentionally internal) that lets the test
    // suite flip every Rule.Parse call from the recursive evaluator
    // over to the alternative-evaluator hook (Rule.AlternativeEvaluator)
    // without rewriting hundreds of test sites. Null means "use
    // whatever DefaultUseAlternativeEvaluator says"; an explicit
    // true / false on a per-call ParseOptions wins over the default.
    // The flag is inert in a recursive-only build (no hook registered),
    // so it's safe to leave in place when ExperimentalSrc isn't
    // present.
    internal bool? UseAlternativeEvaluator { get; set; }

    // Process-wide default for UseAlternativeEvaluator. The
    // EngineSelectionFixture that ships with the alternative-evaluator
    // implementation flips this to true when the
    // INDUCTOR_DEFAULT_ENGINE environment variable is set, so a single
    // CI invocation can run the entire suite through the alternative
    // engine without touching individual ParseOptions instances.
    // Defaults to false so production behavior is unchanged. Without a
    // registered hook the flag has no effect.
    internal static bool DefaultUseAlternativeEvaluator { get; set; }

    // Combine the per-call override with the process-wide default.
    // Per-call wins; only consulted by the dispatcher in Rule.Parse.
    internal bool ResolveUseAlternativeEvaluator() => UseAlternativeEvaluator ?? DefaultUseAlternativeEvaluator;
}
