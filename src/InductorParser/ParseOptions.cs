using System;
using System.IO;
using System.Text;
using InductorParser.Tracing;

namespace InductorParser;

/// <summary>
/// Options for a single Parse call: trace output, the resource budgets (rule
/// count, recursion depth, wall-clock timeout), external cancellation,
/// output-tree shape, and the templates for the parser's default error
/// messages.
/// </summary>
/// <remarks>
/// Unicode normalization form is a grammar-level decision committed at Compile
/// time, not a parse option. Call rule.Compile() with a Normalization form (or
/// null to disable normalization) before parsing if you want a form other 
/// than the FormC default. See Rule.Compile(NormalizationForm?).
/// <para>
/// Failure positions reported in <see cref="ParseResult"/> are always in the
/// caller's original-input coordinates, even when the grammar was compiled
/// against a normalization form that rewrote the input before matching.
/// </para>
/// </remarks>
public sealed class ParseOptions
{
    /// <summary>
    /// Where trace output goes when the parser is tracing. Null turns tracing
    /// off.
    /// </summary>
    public TextWriter? TraceSink { get; set; }

    /// <summary>How verbose the trace output is.</summary>
    public TraceLevel TraceLevel { get; set; } = TraceLevel.Diagnostic;

    /// <summary>
    /// Caps how many work units the parse may consume before giving up. The
    /// parse aborts with <see cref="ParseOutcome.RuleCountLimitExceeded"/> when
    /// the counter exceeds this limit. Set to 0 to disable.
    /// </summary>
    /// <remarks>
    /// Each rule invocation counts as one unit, and each iteration of a
    /// bulk-scan inner loop (ScanWhile, ScanUntil, the AdvanceWhile*
    /// primitives, or any rule that calls Lexer.TickBudget) counts as one
    /// too. Because it's a count and not a wall-clock measurement, the same input
    /// against the same grammar trips at the same point on every run. The
    /// default of 10,000,000 lets well-formed parses through (a 1 MB file
    /// often runs through low millions) and catches both
    /// catastrophic-backtracking shapes and bulk-scan denial of service.
    /// </remarks>
    public long RuleCountLimit { get; set; } = 10_000_000L;

    /// <summary>
    /// Maximum recursion depth (rule invocations currently on the call stack),
    /// to catch deeply nested but well-formed input (think 10,000 open parens)
    /// before it blows the .NET call stack and crashes the host process. Set to
    /// 0 to disable. Independent of <see cref="RuleCountLimit"/>: a deeply
    /// nested input may use few invocations total.
    /// </summary>
    /// <remarks>
    /// Be careful when disabling. With no depth limit, deeply
    /// nested input recurses until the .NET call stack runs out and the runtime
    /// throws StackOverflowException. That exception can't be caught. It never
    /// surfaces as a failed <see cref="ParseResult"/>. It terminates the whole
    /// process on the spot. A handful of kilobytes of nested brackets is  enough to
    /// trigger it (the same shape as CVE-2026-40324 and similar
    /// recursive-descent parser denial-of-service reports). Any parser that
    /// touches untrusted input should keep a non-zero limit. 
    /// </remarks>
    public int MaxDepth { get; set; } = 1000;

    /// <summary>
    /// Wall-clock limit. The parse loop polls Stopwatch.Elapsed synchronously
    /// from inside its own loop, so the deadline trips even on WebGL where
    /// there's no background timer thread. Set to TimeSpan.Zero to disable,
    /// matching the <see cref="RuleCountLimit"/> / <see cref="MaxDepth"/>
    /// convention.
    /// </summary>
    /// <remarks>
    /// Off by default because timeouts are inherently flaky (same input takes
    /// different time on different hardware) and would cause unpredictable test
    /// failures as a default.
    /// <para>
    /// Best-effort, not exact. The clock is polled on a periodic check that
    /// fires once every so many rule invocations, not on every step (checking
    /// the clock on every step would impact performance negatively). A parse that
    /// finishes in fewer invocations than that interval never reaches a check,
    /// so a very short parse can run past a very small Timeout and still return
    /// its normal result instead of aborting. The
    /// deadline is there to stop a long-running or runaway parse, and a parse
    /// long enough to matter runs long enough to hit a check. Don't rely on
    /// Timeout to trip on a tiny grammar against a tiny input. If you need a
    /// hard, count-based cap that trips deterministically regardless of input
    /// size, use <see cref="RuleCountLimit"/> instead.
    /// </para>
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// External cancellation signal. Null means no cancellation source. See
    /// <see cref="ParseCancellation"/> for what it does, why it's a custom type
    /// instead of System.Threading.CancellationToken, and how to bridge from an
    /// existing CancellationToken.
    /// </summary>
    public ParseCancellation? Cancellation { get; set; }

    /// <summary>
    /// When true, the parser ignores the flatten settings of the rules and
    /// Parse returns a tree whose shape matches the grammar
    /// one-to-one: every FlattenType.Flatten Symbol, every FlattenType.Delete
    /// node, and every individual leaf symbol is present exactly where the
    /// grammar placed it.
    /// </summary>
    /// <remarks>
    /// Turn this on to debug or inspect a grammar, to PrintTree the full
    /// structure, or to Find(rule) against Symbols that the default path would
    /// lift out. When off (the default) the parser applies each rule's FlattenType before
    /// returning so that FlattenType.Delete nodes are gone, FlattenType.Flatten
    /// Symbols have their children lifted into the parent, and
    /// FlattenType.Preserve Symbols remain as findable nodes. That's the shape
    /// most callers actually want to walk (i.e. what the grammar was designed for).
    /// This setting is for debugging.
    /// </remarks>
    public bool PreserveAllSymbols { get; set; } = false;

    /// <summary>
    /// When true, Parse succeeds as soon as the root rule matches, even if the
    /// lexer hasn't reached end of input.
    /// </summary>
    /// <remarks>
    /// The default (false) requires every token of the input to be consumed by
    /// the grammar before Parse returns success: trailing characters that the grammar
    /// didn't match turns the parse into a failure positioned at the first
    /// unconsumed token. The default is strict because silently accepting
    /// trailing input would mask the "grammar accepted something it
    /// shouldn't have" bugs grammar authors care most about catching.
    /// <para>
    /// Turn this on for prefix parsing: matching one record at the front of a
    /// longer stream, testing a sub-rule against an input longer than the rule
    /// was meant to consume, or recognising a command at the start of a line
    /// and handing the rest off to another parser. 
    /// </para>
    /// </remarks>
    public bool AllowTrailingInput { get; set; } = false;

    private string _withErrorTemplate =
        "{message} at line {lineNumber}, column {tokenColumnNumber}.";
    /// <summary>
    /// Template that wraps a rule's <c>.WithError("...")</c> message when that
    /// rule is the deepest failure. The author's text fills the {message}
    /// placeholder, and the position placeholders every template shares add the
    /// location, so a custom message carries its position the way the mechanical
    /// default does. Setting it to null throws.
    /// </summary>
    /// <remarks>
    /// The default appends " at line {lineNumber}, column {tokenColumnNumber}." to
    /// the author's text. Set it to "{message}" to get the raw <c>.WithError</c>
    /// string back with no position, or reshape it however you like (position
    /// first, localized, and so on). See <see cref="PositionalErrorTemplate"/> for
    /// the placeholder syntax; the per-template placeholder here is {message}, the
    /// author's text. Unlike the mechanical templates it carries no {character},
    /// since a WithError failure can sit at end of input where there's no
    /// character to name.
    /// </remarks>
    public string WithErrorTemplate
    {
        get => _withErrorTemplate;
        set => _withErrorTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _positionalErrorTemplate =
        "Unexpected '{character}' at line {lineNumber}, column {tokenColumnNumber}.";
    /// <summary>
    /// Template for the default message when the parser rejects a specific
    /// input character and no .WithError("...") was attached at the deepest
    /// failure position. Setting it to null throws.
    /// </summary>
    /// <remarks>
    /// Templates can include named placeholders the parser substitutes when it
    /// builds the message. Placeholders are written as {name}. Unknown
    /// placeholders pass through verbatim, so a typo shows up in the output
    /// rather than throwing.
    /// <para>
    /// Every template (this one and the abort templates below) supports the
    /// same position placeholders, named to match the ParseResult.ErrorXxx
    /// properties so a template author can mirror whatever unit the rest of
    /// their code already uses:
    /// <code>
    ///   {charIndex}          ParseResult.ErrorCharIndex   (UTF-16 code units)
    ///   {tokenIndex}         ParseResult.ErrorTokenIndex  (StringInfo text elements)
    ///   {line}               ParseResult.ErrorLine        (zero-based, Language Server Protocol convention)
    ///   {charColumn}         ParseResult.ErrorCharColumn  (zero-based char column)
    ///   {tokenColumn}        ParseResult.ErrorTokenColumn (zero-based grapheme column)
    ///   {lineNumber}         ErrorLine + 1                (one-based line)
    ///   {charColumnNumber}   ErrorCharColumn + 1          (one-based char column)
    ///   {tokenColumnNumber}  ErrorTokenColumn + 1         (one-based grapheme column)
    /// </code>
    /// The default templates use {lineNumber} and {tokenColumnNumber} so the
    /// out-of-the-box message reads the way a person counts lines and characters
    /// in an editor: an emoji or a combining sequence earlier on the line counts
    /// as one column, not as its several UTF-16 code units. Use {charColumnNumber}
    /// (or {charColumn}) instead for a Language Server Protocol client or editor,
    /// which count columns in chars. The ParseResult fields stay zero-based; only
    /// the *Number placeholders are shifted.
    /// plus a per-template placeholder for the unit-specific value:
    /// <code>
    ///   WithErrorTemplate            {message}    (the rule's .WithError text)
    ///   PositionalErrorTemplate      {character}  (the unexpected input character)
    ///   MalformedInputTemplate       {character}  (the offending element, e.g. U+D800)
    ///   TimeoutAbortTemplate         {timeout}    (options.Timeout as a TimeSpan string)
    ///   RuleCountLimitAbortTemplate  {limit}      (options.RuleCountLimit)
    ///   DepthLimitAbortTemplate      {limit}      (options.MaxDepth)
    /// </code>
    /// </para>
    /// <para>
    /// The token-index, line/column, and token-column conversions each walk the
    /// input once, so they're computed lazily and only paid for when the
    /// corresponding placeholder appears in the template. The default templates
    /// use {lineNumber} and {tokenColumnNumber}, so building a default failure
    /// message pays one line scan plus one grapheme-cluster count of the input.
    /// That runs only on the failure path (a successful parse builds no message).
    /// A caller who wants the message built with no scan at all can set the
    /// templates to a {charIndex}-only string.
    /// </para>
    /// </remarks>
    public string PositionalErrorTemplate
    {
        get => _positionalErrorTemplate;
        set => _positionalErrorTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _endOfInputErrorTemplate =
        "Unexpected end of input at line {lineNumber}, column {tokenColumnNumber}.";
    /// <summary>
    /// Template for the default message when the parser fails at end of input
    /// and no .WithError("...") was attached. See
    /// <see cref="PositionalErrorTemplate"/> for the placeholder syntax.
    /// Setting it to null throws.
    /// </summary>
    public string EndOfInputErrorTemplate
    {
        get => _endOfInputErrorTemplate;
        set => _endOfInputErrorTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _malformedInputTemplate =
        "Malformed input at line {lineNumber}, column {tokenColumnNumber}: '{character}' isn't valid Unicode and can't be normalized.";
    /// <summary>
    /// Template for the message when the input can't be normalized to the
    /// grammar's normalization form because it isn't well-formed Unicode (an
    /// unpaired UTF-16 surrogate, or U+FFFE). The parse returns
    /// <see cref="ParseOutcome.MalformedInput"/> carrying this message instead
    /// of letting .NET's string.Normalize throw an ArgumentException whose text
    /// the app can't control. See <see cref="PositionalErrorTemplate"/> for the
    /// placeholder syntax; the per-template {character} placeholder renders the
    /// offending element (a lone surrogate comes out as U+D800-style text).
    /// Setting it to null throws.
    /// </summary>
    public string MalformedInputTemplate
    {
        get => _malformedInputTemplate;
        set => _malformedInputTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _timeoutAbortTemplate = "Parse aborted: timeout exceeded.";
    /// <summary>
    /// Template for the default message when <see cref="Timeout"/> aborts the
    /// parse. See <see cref="PositionalErrorTemplate"/> for the placeholder
    /// syntax. Setting it to null throws.
    /// </summary>
    public string TimeoutAbortTemplate
    {
        get => _timeoutAbortTemplate;
        set => _timeoutAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _ruleCountLimitAbortTemplate = "Parse aborted: rule-count limit exceeded.";
    /// <summary>
    /// Template for the default message when <see cref="RuleCountLimit"/> aborts
    /// the parse. See <see cref="PositionalErrorTemplate"/> for the placeholder
    /// syntax. Setting it to null throws.
    /// </summary>
    public string RuleCountLimitAbortTemplate
    {
        get => _ruleCountLimitAbortTemplate;
        set => _ruleCountLimitAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _depthLimitAbortTemplate = "Parse aborted: maximum recursion depth exceeded.";
    /// <summary>
    /// Template for the default message when <see cref="MaxDepth"/> aborts the
    /// parse. See <see cref="PositionalErrorTemplate"/> for the placeholder
    /// syntax. Setting it to null throws.
    /// </summary>
    public string DepthLimitAbortTemplate
    {
        get => _depthLimitAbortTemplate;
        set => _depthLimitAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _cancellationAbortTemplate = "Parse aborted: cancellation requested.";
    /// <summary>
    /// Template for the default message when <see cref="Cancellation"/> aborts
    /// the parse. See <see cref="PositionalErrorTemplate"/> for the placeholder
    /// syntax. Setting it to null throws.
    /// </summary>
    public string CancellationAbortTemplate
    {
        get => _cancellationAbortTemplate;
        set => _cancellationAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    // The three members below are plumbing for an optional alternative
    // evaluator and are unused in a production build: nothing there registers
    // an evaluator hook (Rule.AlternativeEvaluator), so they have no effect
    // and every parse runs on the built-in recursive evaluator. They exist
    // for test and CI runs that do register a hook and route the suite
    // through it.

    // Test-plumbing knob (intentionally internal) that lets the test
    // suite flip every Rule.Parse call from the recursive evaluator over
    // to the alternative-evaluator hook without rewriting hundreds of test
    // sites. Null means "use whatever DefaultUseAlternativeEvaluator says".
    internal bool? UseAlternativeEvaluator { get; set; }

    // Process-wide default for UseAlternativeEvaluator. A test fixture flips
    // it via the INDUCTOR_DEFAULT_ENGINE env var so one CI run can put the
    // whole suite on the alternative engine without per-call options.
    // Defaults to false so production behavior is unchanged.
    internal static bool DefaultUseAlternativeEvaluator { get; set; }

    // Per-call override wins over the process-wide default.
    internal bool ResolveUseAlternativeEvaluator() => UseAlternativeEvaluator ?? DefaultUseAlternativeEvaluator;
}
