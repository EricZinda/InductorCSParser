using System;
using System.IO;
using System.Text;
using InductorParser.Tracing;

namespace InductorParser;

/// <summary>
/// This is how you set options for a single <see cref="Rule.Parse(string)">Rule.Parse</see> call, things like: trace output, the resource budgets (rule
/// count, recursion depth, wall-clock timeout), external cancellation,
/// output-tree shape, and alternative templates for the parser's default error
/// messages.
/// </summary>
/// <remarks>
/// <a href="../docs/Primer3.md#compatibility-vs-canonical">Unicode normalization form</a>
/// is a grammar-level decision committed at <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see>
/// time, not a parse option. Call <see cref="Rule.Compile()">Rule.Compile</see>() with a Normalization form (or
/// null to disable normalization) before parsing if you want a form other 
/// than the FormC default. See <see cref="Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile(NormalizationForm?)</see>.
/// </remarks>
public sealed class ParseOptions
{
    /// <summary>
    /// Where trace output goes when the parser is tracing. Null turns tracing
    /// off.
    /// </summary>
    public TextWriter? TraceSink { get; set; }

    /// <summary>
    /// How verbose the trace output is. See <see cref="InductorParser.Tracing.TraceLevel"/>
    /// for a description of each level.
    /// </summary>
    public TraceLevel TraceLevel { get; set; } = TraceLevel.Diagnostic;

    /// <summary>
    /// Caps how many work units the parse may consume before giving up. The
    /// parse aborts with <see cref="ParseOutcome.RuleCountLimitExceeded">ParseOutcome.RuleCountLimitExceeded</see> when
    /// a periodic check (see remarks) finds the counter has exceeded this
    /// limit. Set to 0 to disable.
    /// </summary>
    /// <remarks>
    /// Each rule invocation counts as one unit, as does each iteration of a
    /// bulk-scan inner loop (<see cref="InductorParser.Rules.ScanWhile(InductorParser.TokenSet,System.Int32)">Rules.ScanWhile</see>, <see cref="InductorParser.Rules.ScanUntil(InductorParser.Rule,System.Boolean)">Rules.ScanUntil</see>, the AdvanceWhile*
    /// primitives, or any rule that calls <see cref="InductorParser.Lexing.Lexer.TickBudget">Lexer.TickBudget</see>). Because it's a count and not a wall-clock measurement, the same input
    /// against the same grammar trips at the same point on every run. The
    /// default of 10,000,000 lets well-formed parses through (a 1 MB file
    /// often runs through low millions) and catches both
    /// catastrophic-backtracking shapes and bulk-scan denial of service.
    /// <para>
    /// Treat this as a backstop
    /// against runaway parses, not a precise budget. Here's why: the limit is checked at periodic checkpoints (every 1024 work units),
    /// not on every unit, which keeps the per-unit cost at one mask and one
    /// compare. The abort happens at the first checkpoint after
    /// the counter crosses the limit, so the parse can run up to 1023 units
    /// past it. A parse that finishes before the first checkpoint never
    /// aborts at all, no matter how small the limit, so a limit below 1024
    /// can't make a small parse fail.
    /// </para>
    /// </remarks>
    public long RuleCountLimit { get; set; } = 10_000_000L;

    /// <summary>
    /// Maximum recursion depth (rule invocations currently on the call stack),
    /// to catch deeply nested but well-formed input (think 10,000 open parens)
    /// before it blows the .NET call stack and crashes the host process. Set to
    /// 0 to disable. Independent of <see cref="RuleCountLimit">ParseOptions.RuleCountLimit</see>: a deeply
    /// nested input may use few invocations total.
    /// </summary>
    /// <remarks>
    /// Be careful when disabling. With no depth limit, deeply
    /// nested input recurses until the .NET call stack runs out and the runtime
    /// throws StackOverflowException. That exception can't be caught and thus it never
    /// surfaces as a failed <see cref="ParseResult"/>. It terminates the whole
    /// process on the spot. A handful of kilobytes of nested brackets is  enough to
    /// trigger it (the same shape as CVE-2026-40324 and similar
    /// recursive-descent parser denial-of-service reports). Any parser that
    /// touches untrusted input should keep a non-zero limit. 
    /// </remarks>
    public int MaxDepth { get; set; } = 1000;

    /// <summary>
    /// Wall-clock limit. The parse loop polls <see cref="System.Diagnostics.Stopwatch.Elapsed">Stopwatch.Elapsed</see> synchronously
    /// from inside its own loop, so the deadline trips even on WebGL where
    /// there's no background timer thread. Set to <see cref="System.TimeSpan.Zero">TimeSpan.Zero</see> to disable,
    /// matching the <see cref="RuleCountLimit">ParseOptions.RuleCountLimit</see> / <see cref="MaxDepth">ParseOptions.MaxDepth</see>
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
    /// so a very short parse can run past a very small <see cref="ParseOptions.Timeout">ParseOptions.Timeout</see> and still return
    /// its normal result instead of aborting. The
    /// deadline is there to stop a long-running or runaway parse, and a parse
    /// long enough to matter runs long enough to hit a check. Don't rely on
    /// <see cref="ParseOptions.Timeout">ParseOptions.Timeout</see> to trip on a tiny grammar against a tiny input. For a cap that
    /// trips at the same point on every run regardless of hardware, use
    /// <see cref="RuleCountLimit">ParseOptions.RuleCountLimit</see> instead. It's polled at the same periodic
    /// checkpoints, so it shares the granularity caveat described in its
    /// remarks, but a count is repeatable where a clock isn't.
    /// </para>
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// The external cancellation signal this parse watches. Null means no
    /// cancellation source. See
    /// <see cref="ParseCancellation"/> for what it does, why it's a custom type
    /// instead of <see cref="System.Threading.CancellationToken">System.Threading.CancellationToken</see>, and how to bridge from an
    /// existing <see cref="System.Threading.CancellationToken">CancellationToken</see>.
    /// </summary>
    public ParseCancellation? Cancellation { get; set; }

    /// <summary>
    /// When true, the parser ignores the flatten settings of the rules and
    /// <see cref="Rule.Parse(string)">Rule.Parse</see> returns a tree that matches the grammar
    /// one-to-one: every <see cref="InductorParser.SyntaxTree.FlattenType.Flatten">FlattenType.Flatten</see> Symbol, every <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see>
    /// node, and every individual leaf symbol is present exactly where the
    /// grammar placed it. Alias subtrees are the one exception, described
    /// in the remarks.
    /// </summary>
    /// <remarks>
    /// Turn this on to debug or inspect a grammar, to <see cref="InductorParser.ParseResult.PrintTree">ParseResult.PrintTree</see> the full
    /// structure, or to <see cref="SyntaxTree.Symbol.Find(Rule)">Symbol.Find</see>(rule) against Symbols that the default path would
    /// remove. When off (the default) the parser applies each rule's <see cref="Rule.FlattenType">Rule.FlattenType</see> before
    /// returning so that <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see> nodes are gone, <see cref="InductorParser.SyntaxTree.FlattenType.Flatten">FlattenType.Flatten</see>
    /// Symbols have their children lifted into the parent, and
    /// <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> Symbols remain as findable nodes. That's the shape
    /// most callers actually want to walk (i.e. what the grammar was designed for).
    /// This setting is for debugging.
    /// <para>
    /// Named aliases usually replace the node for the rule they wrap, even
    /// when this option is enabled. For example, if a rule named "digits"
    /// is wrapped in an <see cref="Rules.Alias">Rules.Alias(Rule)</see> named "year", the tree
    /// contains a "year" node, with no separate "digits" node underneath it.
    /// A wrapped rule with <see cref="SyntaxTree.FlattenType.Delete">FlattenType.Delete</see> is
    /// different: this option keeps its node and content underneath the alias,
    /// marked for deletion. Calling <see cref="SyntaxTree.Symbol.Flatten">Symbol.Flatten()</see>
    /// on the debug tree then removes that node and its content, leaving the
    /// empty alias node that a normal parse would produce.
    /// </para>
    /// </remarks>
    public bool PreserveAllSymbols { get; set; } = false;

    /// <summary>
    /// When true, <see cref="Rule.Parse(string)">Rule.Parse</see> succeeds as soon as the root rule matches, even if the
    /// lexer hasn't reached end of input.
    /// </summary>
    /// <remarks>
    /// The default (false) requires every token of the input to be consumed by
    /// the grammar before <see cref="Rule.Parse(string)">Rule.Parse</see> returns success: trailing characters that the grammar
    /// didn't match turn the parse into a failure positioned at the first
    /// unconsumed token. The default is strict because silently accepting
    /// trailing input is usually a bug that grammar authors want to catch.
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
    /// Template that wraps a rule's <c><see cref="Rule.WithError">Rule.WithError("...")</see></c> message when that
    /// rule is the deepest failure. The parser replaces <c>{message}</c> with
    /// the text passed to <see cref="Rule.WithError">Rule.WithError(string, bool)</see>. Setting it to null throws.
    /// A built-in English default adds the error location to the rule's message.
    /// Change this template to localize that added text, rearrange the location,
    /// or show only the rule's message.
    /// </summary>
    /// <remarks>
    /// For example, <c><see cref="Rule.WithError">Rule.WithError("Expected a number")</see></c>
    /// with the default template produces "Expected a number at line 2, column 5."
    /// if the error is at that location.
    /// <para>
    /// The default appends " at line {lineNumber}, column {tokenColumnNumber}." to
    /// the author's text. Set it to "{message}" to get the raw <see cref="InductorParser.Rule.WithError(System.String,System.Boolean)">Rule.WithError</see>
    /// string back with no position, or reshape it however you like (position
    /// first, localized, and so on). See <see cref="PositionalErrorTemplate">ParseOptions.PositionalErrorTemplate</see> for
    /// the placeholder syntax. The per-template placeholder here is {message}, the
    /// author's text. Unlike the mechanical templates it has no {character},
    /// since a <see cref="InductorParser.Rule.WithError(System.String,System.Boolean)">Rule.WithError</see> failure can sit at end of input where there's no
    /// character to name.
    /// </para>
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
    /// input character and no <see cref="Rule.WithError">Rule.WithError</see>("...") was attached at the deepest
    /// failure position. Setting it to null throws.
    /// A built-in English default reports the unexpected character and its location.
    /// Change this template to localize the message or use your app's preferred position format.
    /// </summary>
    /// <remarks>
    /// Templates can include named placeholders that get replaced with values when the parser
    /// builds the message. Placeholders are written as {name}. Unknown
    /// placeholders pass through verbatim, so a typo shows up in the output
    /// rather than throwing.
    /// <para>
    /// Every template (this one and the abort templates below) supports the
    /// same position placeholders, named to match the ParseResult.ErrorXxx
    /// properties so a template author can use the unit they choose:
    /// <code>
    ///   {charIndex}          ParseResult.ErrorCharIndex   (UTF-16 code units)
    ///   {tokenIndex}         ParseResult.ErrorTokenIndex  (grapheme clusters)
    ///   {line}               ParseResult.ErrorLine        (zero-based line, Language Server Protocol convention)
    ///   {lineNumber}         ErrorLine + 1                (one-based line)
    ///   {charColumn}         ParseResult.ErrorCharColumn  (zero-based char column)
    ///   {tokenColumn}        ParseResult.ErrorTokenColumn (zero-based grapheme column)
    ///   {charColumnNumber}   ErrorCharColumn + 1          (one-based char column)
    ///   {tokenColumnNumber}  ErrorTokenColumn + 1         (one-based grapheme column)
    /// </code>
    /// The default templates use {lineNumber} and {tokenColumnNumber} so the
    /// out-of-the-box message reads the way a person counts lines and characters
    /// in an editor: an emoji or a combining sequence earlier on the line counts
    /// as one column, not as its several UTF-16 code units. Use {charColumnNumber}
    /// (or {charColumn}) instead for a Language Server Protocol client or editor,
    /// which count columns in chars.
    /// </para>
    /// <para>
    /// Each template also has one placeholder of its own for the
    /// template-specific value:
    /// <code>
    ///   WithErrorTemplate            {message}    (the rule's .WithError text)
    ///   PositionalErrorTemplate      {character}  (the unexpected input character)
    ///   MalformedInputTemplate       {character}  (the offending element, e.g. U+D800)
    ///   TimeoutAbortTemplate         {timeout}    (options.Timeout as a TimeSpan string)
    ///   RuleCountLimitAbortTemplate  {limit}      (options.RuleCountLimit)
    ///   DepthLimitAbortTemplate      {limit}      (options.MaxDepth)
    /// </code>
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
    /// and no <see cref="Rule.WithError">Rule.WithError</see>("...") was attached. See
    /// <see cref="PositionalErrorTemplate">ParseOptions.PositionalErrorTemplate</see> for the placeholder syntax.
    /// Setting it to null throws.
    /// A built-in English default reports unexpected end of input and its location.
    /// Change this template to localize the message or explain that more input was expected.
    /// </summary>
    public string EndOfInputErrorTemplate
    {
        get => _endOfInputErrorTemplate;
        set => _endOfInputErrorTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _malformedInputTemplate =
        "Malformed input at line {lineNumber}, column {tokenColumnNumber}: '{character}' can't be normalized.";
    /// <summary>
    /// Template for the message when the input can't be normalized to the
    /// grammar's normalization form. Setting it to null throws.
    /// A built-in English default identifies the offending character and its location.
    /// Change this template to localize the message or explain invalid text in terms your users understand.
    /// </summary>
    /// <remarks>
    /// Two kinds of input trigger it: an unpaired UTF-16 surrogate (which is
    /// ill-formed UTF-16), and U+FFFE (which is a noncharacter that .NET's <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see>
    /// throws upon finding. Note that this parser rejects it the same way on every runtime). The
    /// parse returns <see cref="ParseOutcome.MalformedInput">ParseOutcome.MalformedInput</see> with this
    /// message instead of letting <see cref="string.Normalize(System.Text.NormalizationForm)">string.Normalize</see> throw an ArgumentException
    /// whose text the app can't control.
    /// <para>
    /// See <see cref="PositionalErrorTemplate">ParseOptions.PositionalErrorTemplate</see> for the placeholder syntax. The
    /// per-template placeholder here is {character}, the offending element. A
    /// lone surrogate renders as U+D800-style text, since there's no character
    /// to show.
    /// </para>
    /// </remarks>
    public string MalformedInputTemplate
    {
        get => _malformedInputTemplate;
        set => _malformedInputTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _timeoutAbortTemplate = "Parse aborted: timeout exceeded.";
    /// <summary>
    /// Template for the default message when <see cref="Timeout">ParseOptions.Timeout</see> aborts the
    /// parse. See <see cref="PositionalErrorTemplate">ParseOptions.PositionalErrorTemplate</see> for the placeholder
    /// syntax. Setting it to null throws.
    /// Defaults to "Parse aborted: timeout exceeded." Change this template to
    /// localize the message or include the configured duration with {timeout}.
    /// </summary>
    public string TimeoutAbortTemplate
    {
        get => _timeoutAbortTemplate;
        set => _timeoutAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _ruleCountLimitAbortTemplate = "Parse aborted: rule-count limit exceeded.";
    /// <summary>
    /// Template for the default message when <see cref="RuleCountLimit">ParseOptions.RuleCountLimit</see> aborts
    /// the parse. See <see cref="PositionalErrorTemplate">ParseOptions.PositionalErrorTemplate</see> for the placeholder
    /// syntax. Setting it to null throws.
    /// Defaults to "Parse aborted: rule-count limit exceeded." Change this template
    /// to explain the processing limit to your users or include its value with {limit}.
    /// </summary>
    public string RuleCountLimitAbortTemplate
    {
        get => _ruleCountLimitAbortTemplate;
        set => _ruleCountLimitAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _depthLimitAbortTemplate = "Parse aborted: maximum recursion depth exceeded.";
    /// <summary>
    /// Template for the default message when <see cref="MaxDepth">ParseOptions.MaxDepth</see> aborts the
    /// parse. See <see cref="PositionalErrorTemplate">ParseOptions.PositionalErrorTemplate</see> for the placeholder
    /// syntax. Setting it to null throws.
    /// Defaults to "Parse aborted: maximum recursion depth exceeded." Change this
    /// template to describe excessive nesting in your grammar's terms or include {limit}.
    /// </summary>
    public string DepthLimitAbortTemplate
    {
        get => _depthLimitAbortTemplate;
        set => _depthLimitAbortTemplate = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _cancellationAbortTemplate = "Parse aborted: cancellation requested.";
    /// <summary>
    /// Template for the default message when <see cref="Cancellation">ParseOptions.Cancellation</see> aborts
    /// the parse. See <see cref="PositionalErrorTemplate">ParseOptions.PositionalErrorTemplate</see> for the placeholder
    /// syntax. Setting it to null throws.
    /// Defaults to "Parse aborted: cancellation requested." Change this template
    /// to localize the message or use your app's usual cancellation wording.
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
