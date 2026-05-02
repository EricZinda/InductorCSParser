using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;

namespace InductorParser;

// Rule is the base of everything in a grammar. A grammar is a tree of Rule
// objects: composites like AllOf/FirstOf/OneOrMore wrap other Rules, leaves like
// Token/OneOf sit at the bottom, and the root is whatever Rule you
// hand to Parse(). Calling Parse on the root walks the tree and tries to
// match the input.
//
// Rules are instances, not types.
// In C# you build a Rule by calling factory functions (AllOf, FirstOf, Token, etc.)
// that return Rule instances. The tree is built at runtime, compiled once,
// and reused for every parse after that. A grammar can live anywhere a
// reference can live: a local variable, a static field, an entry in a
// dictionary, an argument passed around. The library doesn't care.
//
// Rule construction is fluent: Modifier methods like .As(name) and
// .Flatten(type) return the same Rule so it can read as a chain:
//
//     var settingName = OneOrMore(OneOf(TokenSet.Letters))
//         .As(nameof(settingName))
//         .Flatten(FlattenType.Preserve);
//
// Rules are effectively immutable. Before Compile runs, you can call the
// modifier methods. After Compile runs (either explicitly via .Compile()
// or automatically on the first .Parse() call) the Rule is sealed and any
// further modification throws InvalidOperationException. This makes the
// "build once, parse many times" model safe even when the same Rule is
// shared across threads.
//
// Rule is abstract. The library's composite and leaf rules
// (AllOfRule, FirstOfRule, GraphemeRule, etc.) subclass it. User code can subclass
// Rule too if it needs matching logic the built-in rules can't express.
// See TryParseRule below for the full subclass contract.
public abstract class Rule
{
    // See below for description
    private bool _sealed;
    private bool _idAssigned;
    private string? _errorMessage;

    // FirstConsumedTokens and Advance drive the "can I skip this rule?"
    // shortcut. See RuleStartRequirements for the full story. The type
    // returned by ComputeRuleStart encapsulates these two. Populated at
    // Compile time. The pessimistic defaults
    // below (Universe, Sometimes) mean any user-defined Rule subclass that
    // doesn't override ComputeRuleStart is safe and never gets shortcutted.
    internal TokenSet FirstConsumedTokens { get; private set; } = TokenSet.Universe;
    internal Advance Advance { get; private set; } = Advance.Sometimes;

    // The "can I skip this rule?" shortcut's consumer-facing API.
    // See RuleStartRequirements for the full story.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool CannotMatchLookahead(int peekRune) =>
        Advance == Advance.Always && !FirstConsumedTokens.Contains(peekRune);

    // Returns true when every successful match of this rule is guaranteed
    // to consume text that contains the returned literal as a substring
    // (case-invariant if ignoreAsciiCase is true). Useful for callers that
    // want to pre-filter input before invoking the parser: the rebar
    // grep runner uses this to skip past lines that can't possibly match
    // via one BCL substring search across the whole haystack, before
    // line-by-line parsing kicks in. Mirrors the literal-prefilter
    // analysis every serious regex engine does internally (rust/regex's
    // 'literal' module, .NET's compiled regex, PCRE2's "studied"
    // patterns).
    //
    // The derived literal is the longest contiguous run of fixed-text
    // children at any position in the rule tree (LiteralRule,
    // LiteralIgnoreAsciiCaseRule, TokenRule, OneOfRule with a single
    // BMP char or a single ASCII letter pair like 'Nn'). Concatenated
    // through AllOf and propagated through BetweenInclusive[atLeast>=1]
    // and FlattenType wrappers. Returns false (literal == "") when no
    // such required substring can be derived from the rule.
    public bool TryGetRequiredLiteral(out string literal, out bool ignoreAsciiCase)
    {
        var result = ComputeRequiredLiteral();
        if (result == null)
        {
            literal = "";
            ignoreAsciiCase = false;
            return false;
        }
        literal = result.Value.Text;
        ignoreAsciiCase = result.Value.IgnoreCase;
        return literal.Length > 0;
    }

    // Returns true when every successful match of this rule is guaranteed
    // to contain at least one of the returned literals as a substring. Use
    // when no single shared literal can be derived (TryGetRequiredLiteral
    // returns false) but the rule has a small fixed set of literal-prefix
    // alternatives. The AWS-keys grammar's FirstOf("ASIA"|"AKIA"|"AROA"|"AIDA")
    // is the motivating shape: every match contains exactly one of those
    // four literals, so a multi-substring pre-scan still skips lines that
    // can't possibly match.
    //
    // <paramref name="maxAlternatives"/> caps the returned set size. The
    // caller picks a cap that makes a multi-substring scan worth it
    // (8-16 is reasonable for the rebar grep runner; a 2000-literal
    // dictionary would have selectivity at most 1 in 26 from the
    // first-rune set and isn't worth pre-filtering with this analysis).
    // Returns false when no analyzable set exists or when it exceeds the
    // cap.
    public bool TryGetRequiredLiteralAlternatives(
        int maxAlternatives,
        out IReadOnlyList<(string Text, bool IgnoreCase)> alternatives)
    {
        var result = ComputeRequiredLiteralAlternatives();
        if (result == null || result.Count == 0 || result.Count > maxAlternatives)
        {
            alternatives = Array.Empty<(string Text, bool IgnoreCase)>();
            return false;
        }
        alternatives = result;
        return true;
    }

    // Subclasses override to declare what fixed text every successful
    // match consumes. Default is "no required literal." See the
    // matching override on each composite / leaf rule for specifics.
    internal virtual (string Text, bool IgnoreCase)? ComputeRequiredLiteral() => null;

    // Like ComputeRequiredLiteral, but returns a set of literals when the
    // rule's structure guarantees every match contains at least one. The
    // canonical shape this captures is FirstOf(literal-branches): every
    // branch must succeed via its own literal, so the union across branches
    // is required. AllOf surfaces a multi-literal child if it has one.
    internal virtual IReadOnlyList<(string Text, bool IgnoreCase)>? ComputeRequiredLiteralAlternatives() => null;

    // Subclasses override when they always consume a fixed-length run
    // of text. AllOf uses this to concatenate consecutive fixed-text
    // children into one required literal. A null return means "this
    // rule's match length isn't fixed at compile time" — the AllOf
    // walker breaks the concatenation run there and recurses for a
    // standalone candidate instead.
    internal virtual (string Text, bool IgnoreCase)? ComputeConcatenableText() => null;

    // Lazily-built reverse index from SymbolId to human-readable name
    // for every rule reachable from this root. Populated on the first
    // NameOf call. Grammars that never ask never pay the allocation.
    private Dictionary<SymbolId, string>? _nameIndex;

    public SymbolId Id { get; private set; }
    public string? Name { get; private set; }
    public FlattenType FlattenType { get; private set; }

    // The static error message set via .WithError("..."), or null if none.
    // Subclasses pass this to lexer.RecordFailure on the failure path so
    // the "deepest failure wins" heuristic can surface it.
    protected internal string? ErrorMessage => _errorMessage;

    protected Rule(FlattenType defaultFlatten, params Rule[] children)
    {
        FlattenType = defaultFlatten;
        Children = children.Length > 0 ? children : NoChildren;
        _ruleTraceName = DeriveRuleTraceName(GetType());
    }

    // Cached rule class name for trace output, derived from GetType().Name
    // in the constructor. The "Rule" suffix is stripped so "AllOfRule"
    // becomes "AllOf", "WithinTokenRule" becomes "WithinToken", matching the
    // trace naming convention. Reading this is a field load which is cheaper
    // than calling GetType().Name on every trace output. Works under
    // IL2CPP because it's baked in at construction time, not looked
    // up via name-based reflection.
    //
    // Subclasses whose trace name needs construction-time parameterization
    // (e.g. BetweenInclusiveRule rendering its bounds as "BetweenInclusive[1..3]")
    // call SetTraceName from their own constructor to overwrite the
    // type-derived default.
    private string _ruleTraceName;

    // Compose the full trace label: "{Name}:{ruleName}" when the rule
    // has a .As(name) set, else just "{ruleName}". Only .As() is used
    // here. .WithError() sets the user-facing error message, not a
    // rule identity, so it belongs in the trace line's body (see
    // AppendErrorMessage) rather than as a label prefix.
    private string BuildTraceLabel() =>
        Name != null ? $"{Name}:{_ruleTraceName}" : _ruleTraceName;

    // Internal accessor so the state-machine evaluator can label its
    // Call / Return trace lines with the same "{Name}:{ruleClassName}"
    // string the recursive engine uses. The SM emits its own trace
    // lines from Stepper.Step_Call / Step_ReturnSuccess /
    // Step_ReturnFailure rather than going through TryParseRule, so it
    // needs the label without going through the protected TraceSuccess
    // / TraceFailure helpers.
    internal string TraceLabel => BuildTraceLabel();

    // If the rule has .WithError(msg) set, append it in quotes after
    // the trace body so a reader sees both what the rule actually
    // tried ("found 'x', wanted 'a'") and the friendly message that
    // would have surfaced to the user on a real parse failure
    // ("expected an A"). Only used on failure lines. On success
    // there's no error to report so the WithError message is
    // omitted.
    private string AppendErrorMessage(string body) =>
        _errorMessage != null ? $"{body} \"{_errorMessage}\"" : body;

    // Short-form trace helpers called from a rule's TryParse on the
    // success or failure path. [AggressiveInlining] + the
    // TraceInterpolatedStringHandler parameter together make trace
    // calls cost nothing when tracing is off. See
    // TraceInterpolatedStringHandler for the full story.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void TraceSuccess(
        Lexer lexer,
        [InterpolatedStringHandlerArgument(nameof(lexer))]
        TraceInterpolatedStringHandler message)
    {
        string? formatted = message.GetFormattedOrNull();
        if (formatted == null) return;
        lexer.WriteTraceLine(BuildTraceLabel(), TraceOutcome.Success, formatted);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void TraceFailure(
        Lexer lexer,
        [InterpolatedStringHandlerArgument(nameof(lexer))]
        TraceInterpolatedStringHandler message)
    {
        string? formatted = message.GetFormattedOrNull();
        if (formatted == null) return;
        lexer.WriteTraceLine(BuildTraceLabel(), TraceOutcome.Failure, AppendErrorMessage(formatted));
    }

    // Explicit-level overloads. Use when a trace should fire at a
    // level other than Diagnostic (e.g. a summary line at Normal). The
    // InterpolatedStringHandlerArgument on the message parameter is
    // what routes `level` to the handler when the compiler rewrites
    // the call (see TraceInterpolatedStringHandler for how that works).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void TraceSuccess(
        Lexer lexer,
        TraceLevel level,
        [InterpolatedStringHandlerArgument(nameof(lexer), nameof(level))]
        TraceInterpolatedStringHandler message)
    {
        string? formatted = message.GetFormattedOrNull();
        if (formatted == null) return;
        lexer.WriteTraceLine(BuildTraceLabel(), TraceOutcome.Success, formatted);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void TraceFailure(
        Lexer lexer,
        TraceLevel level,
        [InterpolatedStringHandlerArgument(nameof(lexer), nameof(level))]
        TraceInterpolatedStringHandler message)
    {
        string? formatted = message.GetFormattedOrNull();
        if (formatted == null) return;
        lexer.WriteTraceLine(BuildTraceLabel(), TraceOutcome.Failure, AppendErrorMessage(formatted));
    }

    // Shared empty array used by leaf rules for their Children list. Without
    // it, every leaf-rule constructor call would allocate a brand-new
    // zero-length Rule[] to satisfy the `params Rule[] children` parameter.
    // Reusing this single pre-built instance skips that allocation.
    private static readonly IReadOnlyList<Rule> NoChildren = Array.Empty<Rule>();

    // The child rules this rule is built from. Composites (AllOf, FirstOf, OneOrMore,
    // etc.) pass their children to the base constructor and access them via
    // this property. Leaf rules (Token, OneOf, Eof) don't pass any children,
    // and the constructor below swaps in the shared empty list (NoChildren)
    // when that happens. Compile walks this list to assign ids and seal every
    // reachable rule.
    //
    // The private setter is what lets SetChildren (below) mutate children
    // for the LateBoundRule case. Every other rule fixes its children in
    // the constructor and never touches them again. After Compile seals
    // the rule, SetChildren throws and Children becomes truly immutable.
    protected internal IReadOnlyList<Rule> Children { get; private set; }

    // Replace this rule's children. The only production use is LateBoundRule,
    // which needs to install its target after construction. Throws if the
    // rule has already been compiled, so late-binding is a grammar-build-time
    // operation and no rule can sprout new children mid-parse.
    protected void SetChildren(params Rule[] children)
    {
        ThrowIfSealed();
        Children = children.Length > 0 ? children : NoChildren;
    }

    // Replace this rule's trace label. Intended for use only from subclass
    // constructors that need to bake construction-time parameters (like
    // BetweenInclusiveRule's bounds) into the label. No ThrowIfSealed check
    // here because at constructor time the rule isn't reachable from
    // grammar code yet, so it can't have been compiled and sealed.
    // Trace output reads _ruleTraceName as a field load, so renaming
    // here stays a one-time cost.
    protected void SetTraceName(string name) => _ruleTraceName = name;

    // Strip the "Rule" suffix so the trace label reads "AllOf" instead
    // of "AllOfRule". GetType() in a base constructor returns the
    // derived runtime type (C# guarantee), so this resolves correctly
    // for every subclass. Called once per rule instance in the ctor.
    // The result is cached in _ruleTraceName so trace output just
    // reads a field.
    private static string DeriveRuleTraceName(Type t)
    {
        string name = t.Name;
        return name.EndsWith("Rule", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - 4)
            : name;
    }

    // Hook for subclass-specific grammar-validity checks. Called once per
    // rule during Compile. Default is no-op. LateBoundRule uses it to fail
    // when a forward-reference was never bound.
    protected virtual void ValidateCompiled() { }

    // Attach a debug/trace name. Returns the same Rule for fluent chaining.
    // Throws InvalidOperationException if the Rule has already been compiled.
    // Virtual so subclasses (LateBoundRule) can forbid it where naming would
    // be a bug.
    public virtual Rule As(string name)
    {
        ThrowIfSealed();
        Name = name;
        return this;
    }

    // Pin an explicit SymbolId on this Rule (for stable numbering across
    // versions, useful when serializing parse trees). Returns the same
    // Rule for fluent chaining. Throws if already compiled. Virtual for
    // the same reason As(string) is.
    public virtual Rule As(SymbolId id)
    {
        ThrowIfSealed();
        Id = id;
        _idAssigned = true;
        return this;
    }

    // Set the flatten policy (Preserve / Delete / Flatten) that controls
    // how this rule contributes to the parse tree on a successful match.
    // See the FlattenType enum for what each value means. Returns the same
    // Rule for fluent chaining. Throws if already compiled. Virtual so
    // LateBoundRule can forbid it (a FlattenType set on a transparent
    // forwarding rule is never consulted and would silently do nothing).
    public virtual Rule Flatten(FlattenType type)
    {
        ThrowIfSealed();
        FlattenType = type;
        return this;
    }

    // Convenience shortcuts for the three FlattenType values. These read
    // better than .Flatten(FlattenType.X) at calls that otherwise
    // chain several modifiers, e.g. .As("number").Preserve() vs
    // .As("number").Flatten(FlattenType.Preserve). All three forward to
    // Flatten(FlattenType), so LateBoundRule's override that forbids
    // setting a flatten policy still fires here.
    public Rule Preserve() => Flatten(FlattenType.Preserve);
    public Rule Delete() => Flatten(FlattenType.Delete);
    public Rule Flatten() => Flatten(FlattenType.Flatten);

    // Attach a static error message. If this rule is the "deepest failure"
    // when a parse fails, ParseResult.ErrorMessage will be this string
    // instead of the generic "unexpected 'x'" fallback. Useful for giving
    // user-friendly messages like "Expected a setting name" at the spots
    // most likely to be where the author went wrong. Returns the same Rule
    // for fluent chaining. Throws if already compiled. Virtual so
    // LateBoundRule can forbid it (a WithError set on a transparent
    // forwarding rule is never consulted and would silently do nothing).
    public virtual Rule WithError(string errorMessage)
    {
        ThrowIfSealed();
        _errorMessage = errorMessage;
        return this;
    }

    // Finalize the grammar. Walks the rule graph rooted at this Rule and:
    // assigns SymbolIds to every Rule that doesn't have one yet, seals
    // every Rule against further modification. Idempotent: calling Compile
    // twice does nothing the second time. Returns the same Rule for chaining.
    //
    // Auto-invoked on the first call to Parse(). Call it explicitly when
    // you want grammar-construction errors to surface at program startup
    // rather than at first parse.
    //
    // Id assignment runs in three passes:
    //   1. Pinned ids first. Rules that called .As(SymbolId) keep the id
    //      they were given, and that id is reserved against later passes.
    //      Two reachable rules pinned to the same SymbolId are rejected
    //      here with a clear error, since downstream lookups by raw
    //      SymbolId (parse-tree walking, NameOf) can't disambiguate
    //      duplicate ids.
    //   2. Named rules get a hash-of-name id in the custom range. If the
    //      hash slot is already taken (by a pinned id or an earlier named
    //      rule), the id linear-probes upward until it finds an empty
    //      slot. Same name produces the same hash slot every run, so a
    //      rule's id is stable across program executions in the absence
    //      of grammar changes.
    //   3. Anonymous rules get sequential ids starting at CustomRangeStart,
    //      probing upward past anything already claimed by passes 1 and 2.
    //
    // Then a validation pass asks each rule whether it's well-formed, and
    // a final pass seals the graph against further mutation.
    public Rule Compile()
    {
        if (_sealed) return this;

        var usedIds = new HashSet<int>();
        var pinnedRules = new Dictionary<int, Rule>();
        var namedRules = new Dictionary<string, Rule>();

        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CollectPinnedIds(this, visited, usedIds, pinnedRules);

        visited.Clear();
        CheckNameUniqueness(this, visited, namedRules);

        visited.Clear();
        AssignNamedIds(this, visited, usedIds);

        visited.Clear();
        int nextAnon = SymbolRanges.CustomRangeStart;
        AssignAnonymousIds(this, visited, usedIds, ref nextAnon);

        visited.Clear();
        ValidateAll(this, visited);

        // Compute FirstConsumedTokens / Advance for every reachable rule (see
        // RuleStartRequirements for the shortcut docs). Done after Validate so
        // LateBoundRule's _target is guaranteed non-null by the time we walk
        // its child.
        var computing = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        visited.Clear();
        ComputeRuleStartAll(this, visited, computing);

        visited.Clear();
        SealAll(this, visited);
        return this;
    }

    // Return the human-readable name for a SymbolId in this grammar, or
    // null if the id isn't known. Two sources, tried in order:
    //
    //   1. Rune range (0..0x10FFFF): render the code point as a
    //      single-rune string. A tree leaf with id 0x41 comes back as "A",
    //      0x1F3B8 comes back as "🎸". Surrogate halves (0xD800..0xDFFF)
    //      aren't valid scalar values and return null. No lexer produces
    //      them as ids, so this only matters if a caller hand-built a bad
    //      SymbolId.
    //
    //   2. Per-grammar rule index: a lazily-built Dictionary<SymbolId, Rule>
    //      keyed on every rule reachable from this root. For a rule created
    //      with .As("foo"), returns "foo". For an unnamed rule, returns the
    //      class-derived trace name ("AllOf", "OneOrMore", "Token",
    //      "BetweenInclusive[1..3]"). Returns null if the id isn't in the
    //      grammar.
    //
    // Intended for parse-tree walkers (which see Symbols carrying SymbolIds,
    // not Rule references) and for error-message rendering that wants to
    // quote a rule's name. Tracing already has direct Rule access and
    // doesn't need this path.
    //
    // Auto-compiles if the grammar hasn't been compiled yet, since ids
    // aren't stable until Compile runs.
    public string? NameOf(SymbolId id)
    {
        int value = id.Value;
        if (value >= 0 && value < SymbolRanges.CharacterRangeEnd)
        {
            return Rune.IsValid(value) ? new Rune(value).ToString() : null;
        }

        Compile();
        _nameIndex ??= BuildNameIndex();
        return _nameIndex.TryGetValue(id, out var name) ? name : null;
    }

    private Dictionary<SymbolId, string> BuildNameIndex()
    {
        var map = new Dictionary<SymbolId, string>();
        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CollectNames(this, visited, map);
        return map;
    }

    // Populate the reverse index by walking the sealed rule graph once.
    // For each rule, prefer the user-supplied Name (from .As("foo")) and
    // fall back to the class-derived trace name, which is what tracing
    // shows for unnamed rules and what a tree-walker expects to see for
    // things like AllOf / OneOrMore / BetweenInclusive[1..3].
    private static void CollectNames(Rule r, HashSet<Rule> visited, Dictionary<SymbolId, string> map)
    {
        if (!visited.Add(r)) return;
        map[r.Id] = r.Name ?? r._ruleTraceName;
        foreach (var child in r.Children)
            CollectNames(child, visited, map);
    }

    // Parse: the main entry point for running a grammar
    //
    // Run the grammar against an input string. Auto-compiles on first call.
    // Pass options explicitly to change parse-time settings.
    public ParseResult Parse(string input) => Parse(input, new ParseOptions());

    // Dispatcher. Routes to the recursive evaluator by default. An
    // alternative-evaluator implementation (e.g. the state-machine
    // engine that ships in ExperimentalSrc) can register itself by
    // assigning AlternativeEvaluator at startup. The test suite can
    // flip the routing process-wide via
    // ParseOptions.DefaultUseAlternativeEvaluator, or per-call via
    // ParseOptions.UseAlternativeEvaluator, so the same fixtures can
    // run through either engine without per-test rewrites. The flag
    // without a registered hook is inert: a recursive-only build
    // always hits ParseRecursive regardless of the flag.
    public ParseResult Parse(string input, ParseOptions options) =>
        options.ResolveUseAlternativeEvaluator() && AlternativeEvaluator is { } hook
            ? hook(this, input, options)
            : ParseRecursive(input, options);

    // Module-wide alternative-evaluator hook. Set by an alternative engine
    // implementation at startup (typically from a test fixture's
    // OneTimeSetUp). Null in a recursive-only build, which is the default.
    // The hook receives the rule, the input, and the same ParseOptions
    // the caller passed to Parse, and returns the same ParseResult shape
    // ParseRecursive would.
    internal static Func<Rule, string, ParseOptions, ParseResult>? AlternativeEvaluator;

    // The recursive evaluator's body. Compare fixtures that need a
    // guaranteed recursive-engine baseline (so the SM run can compare
    // its own output against a stable control) call this directly
    // instead of going through Parse.
    internal ParseResult ParseRecursive(string input, ParseOptions options)
    {
        Compile();

        // Normalize before the lexer sees the input so grammars written
        // against one composition form also match the other. The common
        // case (input already in the target form, essentially all typed
        // and web-sourced text) is usually just a normalization scan. If
        // normalization returns the original string reference, downstream position
        // translation is skipped. Null means "skip normalization entirely,"
        // which trades the safety net for character-exact round-trippability.
        string parseInput = options.NormalizeInput.HasValue
            ? input.Normalize(options.NormalizeInput.Value)
            : input;

        Lexer lexer = new Lexer(parseInput, options.TraceSink, options.TraceLevel);
        lexer.ConfigureBudgets(options);
        Symbol? result;
        // Pre-allocate a root list so a root with FlattenType.Flatten
        // has somewhere to merge into. If root is FlattenType.Preserve,
        // TryParse nulls this out and rootList stays empty. If root is
        // FlattenType.Delete, same. Only a FlattenType.Flatten root
        // populates it.
        var rootList = new List<Symbol>();
        try
        {
            result = TryParse(lexer, rootList);
        }
        catch (ParseBudgetExceeded budget)
        {
            // The throw rode up through every active rule's `using var
            // transaction = lexer.BeginTransaction()`, which rolled the
            // lexer position back frame by frame, so lexer.Position is now
            // back at 0. lexer.DeepestFailure isn't rolled back (it's a
            // high-water mark of failure positions), so it's the best
            // "how far did the parser get" hint we can give. Use the
            // same Math.Max idiom as the normal failure path below for
            // consistency.
            int abortRaw = Math.Max(lexer.DeepestFailure, lexer.Position);
            int abortPos = NormalizedPositionMap.TranslateToOriginal(input, parseInput, abortRaw, options.NormalizeInput);
            return ParseResult.Aborted(budget.Outcome, abortPos, BuildBudgetMessage(budget.Outcome, abortPos, input, options), input, this);
        }
        if (result == null && rootList.Count == 0)
        {
            var pos = Math.Max(lexer.DeepestFailure, lexer.Position);
            int failurePos = NormalizedPositionMap.TranslateToOriginal(input, parseInput, pos, options.NormalizeInput);
            return ParseResult.Failed(failurePos, BuildErrorMessage(lexer.DeepestFailureMessage, pos, parseInput, failurePos, input, options), input, this);
        }
        if (!options.AllowTrailingInput && !lexer.IsEof)
        {
            var pos = Math.Max(lexer.DeepestFailure, lexer.Position);
            int failurePos = NormalizedPositionMap.TranslateToOriginal(input, parseInput, pos, options.NormalizeInput);
            return ParseResult.Failed(failurePos, BuildErrorMessage(lexer.DeepestFailureMessage, pos, parseInput, failurePos, input, options), input, this);
        }
        // Three success shapes:
        //   * Preserve root: result is its wrapper Symbol, rootList is empty.
        //     Symbols = [result].
        //   * Flatten root: result is Discarded, rootList has content.
        //     Symbols = rootList.
        //   * Delete root: result is Discarded, rootList is empty.
        //     Symbols = [] (unusual but consistent).
        IReadOnlyList<Symbol> symbols;
        if (!ReferenceEquals(result, Symbol.Discarded) && result != null)
            symbols = new[] { result };
        else
            symbols = rootList;
        return ParseResult.Succeeded(symbols, input, this);
    }

    internal static string BuildBudgetMessage(ParseOutcome outcome, int abortPos, string input, ParseOptions options)
    {
        switch (outcome)
        {
            case ParseOutcome.Timeout:
                return FormatTemplate(options.TimeoutAbortTemplate,
                    PositionPlaceholders(abortPos, input),
                    ("timeout", () => options.Timeout.ToString()));
            case ParseOutcome.RuleCountLimitExceeded:
                return FormatTemplate(options.RuleCountLimitAbortTemplate,
                    PositionPlaceholders(abortPos, input),
                    ("limit", () => options.RuleCountLimit.ToString()));
            case ParseOutcome.DepthLimitExceeded:
                return FormatTemplate(options.DepthLimitAbortTemplate,
                    PositionPlaceholders(abortPos, input),
                    ("limit", () => options.MaxDepth.ToString()));
            case ParseOutcome.Canceled:
                return FormatTemplate(options.CancellationAbortTemplate,
                    PositionPlaceholders(abortPos, input));
            default:
                // ParseOutcome values outside the four budget kinds shouldn't
                // reach the abort path. The "Parse aborted." fallback stays
                // hardcoded because there's no template to consult and no
                // placeholders that would matter.
                return "Parse aborted.";
        }
    }

    // Both engines (recursive and state-machine) end up here for the
    // generic-failure path so default error messages stay consistent
    // and ParseOptions templates apply uniformly. customMessage is the
    // deepest-failure message recorded by the engine (lexer.DeepestFailureMessage
    // for the recursive engine, machine.DeepestFailureMessage for the SM);
    // posInParseInput indexes into parseInput (the post-normalization input)
    // for the EOF check and the {character} substitution; failurePos is
    // the same position translated back to the original input for the
    // user-facing placeholders.
    internal static string BuildErrorMessage(string? customMessage, int posInParseInput, string parseInput, int failurePos, string input, ParseOptions options)
    {
        // Prefer the error message the user attached to the rule that failed
        // at the deepest position (via .WithError("...")). That's the
        // "expected a setting name"-style message grammar authors write for
        // the spots most likely to be where a user goes wrong. Fall back to
        // the generic position-based message only when no rule at the
        // deepest failure had a WithError set.
        if (customMessage != null)
            return customMessage;

        // posInParseInput == parseInput.Length happens when the grammar
        // wanted more characters than the input had. Example: parsing
        // "setting = 5" against a grammar that requires a trailing ';'.
        // The lexer reaches EOF, the rule for ';' fails and records the
        // failure at the end position. We have to handle this both to
        // give a useful error message ("end of input" rather than
        // "unexpected ';'" pointing at a character that isn't there) and
        // to avoid the parseInput[pos] indexing on the next line throwing
        // IndexOutOfRangeException.
        if (posInParseInput >= parseInput.Length)
            return FormatTemplate(options.EndOfInputErrorTemplate,
                PositionPlaceholders(failurePos, input));
        return FormatTemplate(options.PositionalErrorTemplate,
            PositionPlaceholders(failurePos, input),
            ("character", () => parseInput[posInParseInput].ToString()));
    }

    // The four position placeholders shared by every default template.
    // {charIndex} is the failure position in chars (UTF-16 code units),
    // matching ParseResult.ErrorCharIndex. {tokenIndex} mirrors
    // ErrorTokenIndex. {line} and {column} are zero-based, matching
    // ErrorLine and ErrorColumn (LSP convention).
    //
    // The Func<string> wrappers are deliberate: each token-index /
    // line-column conversion walks the input once, so we only want to pay
    // for the ones whose placeholder actually appears in the template the
    // caller chose. The default templates only use {charIndex}, so by
    // default we never run the O(n) scans.
    private static (string Key, Func<string> ValueProvider)[] PositionPlaceholders(int charIndex, string input)
    {
        return new (string, Func<string>)[]
        {
            ("charIndex", () => charIndex.ToString()),
            ("tokenIndex", () => SourcePositionConverter.ToTokenIndex(input, charIndex).ToString()),
            ("line", () =>
            {
                SourcePositionConverter.ToLineColumn(input, charIndex, out int line, out _);
                return line.ToString();
            }),
            ("column", () =>
            {
                SourcePositionConverter.ToLineColumn(input, charIndex, out _, out int column);
                return column.ToString();
            }),
        };
    }

    // Substitute {name}-style placeholders in a user-supplied template
    // string. Each placeholder's value is computed lazily via its
    // Func<string> only when the placeholder actually appears in the
    // template, so callers don't pay for an O(n) rune-index walk if their
    // template only mentions {charIndex}. Unknown placeholder names pass
    // through verbatim, so a typo in a custom template is visible in the
    // resulting message rather than throwing on every parse failure.
    private static string FormatTemplate(
        string template,
        (string Key, Func<string> ValueProvider)[] positionPlaceholders,
        params (string Key, Func<string> ValueProvider)[] extraPlaceholders)
    {
        string formatted = template;
        formatted = ApplyPlaceholders(formatted, positionPlaceholders);
        formatted = ApplyPlaceholders(formatted, extraPlaceholders);
        return formatted;
    }

    private static string ApplyPlaceholders(string template, (string Key, Func<string> ValueProvider)[] placeholders)
    {
        string formatted = template;
        foreach (var (key, valueProvider) in placeholders)
        {
            string token = "{" + key + "}";
            if (formatted.IndexOf(token, StringComparison.Ordinal) >= 0)
                formatted = formatted.Replace(token, valueProvider());
        }
        return formatted;
    }

    // The entry point every Rule call (top-level Parse and child
    // Inner.TryParse) goes through. Bookkeeps the budget counters on the
    // lexer (rule depth, total invocations, periodic timeout / cancellation
    // poll) and then delegates to the subclass's TryParseRule. Wrapping
    // it here means user-defined Rule subclasses that just override
    // TryParseRule inherit catastrophic-backtracking protection with no
    // extra work.
    //
    // TryParse also normalizes the subclass's view: it computes
    // effectiveFlattenType (collapsing PreserveAllSymbols), and makes
    // outputSymbols non-null only in Flatten mode. Subclasses don't have
    // to re-check FlattenType or PreserveAllSymbols, they implement
    // the three modes per the TryParseRule rules below.
    internal Symbol? TryParse(Lexer lexer, List<Symbol>? outputSymbols)
    {
        lexer.EnterRule();
        try
        {
            // effectiveFlattenType collapses FlattenType +
            // PreserveAllSymbols. Debug mode treats every rule
            // as Preserve.
            FlattenType effectiveFlattenType =
                lexer.PreserveAllSymbols ? FlattenType.Preserve : FlattenType;
            if (effectiveFlattenType != FlattenType.Flatten)
            {
                outputSymbols = null;
            }
            else if (outputSymbols == null)
            {
                // A rule with FlattenType.Flatten expects its caller to pass a list
                // for it to write children into. When the caller passed
                // null (Not/Peek throwing away inner's output, a Flatten
                // root that didn't get a rootList, etc.), we allocate a
                // throwaway list here. Inner writes into it but nobody
                // reads it, and the list goes out of scope when this call
                // returns. Keeps subclasses simple: they always get a
                // list to write into.
                outputSymbols = new List<Symbol>();
            }
            int savedCount = outputSymbols?.Count ?? 0;
            var result = TryParseRule(lexer, effectiveFlattenType, outputSymbols);
            if (result == null)
            {
                // Roll back any partial writes to outputSymbols. The
                // transaction's `using` rolled back the lexer. This
                // rolls back the caller's list.
                if (outputSymbols != null && outputSymbols.Count > savedCount)
                    outputSymbols.RemoveRange(savedCount, outputSymbols.Count - savedCount);
                return null;
            }

            // Safety net: subclasses that ignored effectiveFlattenType
            // and handed back a real Symbol in Delete / Flatten mode
            // get normalized to Discarded so callers can rely on the
            // contract (only FlattenType.Preserve returns a wrapper Symbol).
            if (effectiveFlattenType != FlattenType.Preserve)
                return Symbol.Discarded;
            return result;
        }
        finally
        {
            lexer.ExitRule();
        }
    }

    // The matching method every subclass implements. Contract:
    //   * Open a transaction with lexer.BeginTransaction() at the top.
    //   * On failure: return null without committing. The `using` on the
    //     transaction rolls the lexer back automatically. Never consume
    //     input on failure (following the transaction pattern guarantees
    //     this). Call lexer.RecordFailure() so the "deepest failure wins"
    //     error-reporting heuristic can surface your rule's error message.
    //     Rule.TryParse rolls back any partial writes to outputSymbols
    //     for you, so subclasses don't need to truncate on the failure
    //     path.
    //   * On success: call transaction.Commit() and return a non-null
    //     Symbol. What exactly you return depends on `effectiveFlattenType`:
    //       - Delete: emit nothing, return Symbol.Discarded.
    //       - Flatten: append each Symbol you would have collected to
    //         `outputSymbols` (the caller's list, guaranteed non-null)
    //         and return Symbol.Discarded. For a composite, that's each
    //         matched child's Symbol. For a leaf, that's the leaf Symbol
    //         itself.
    //       - Preserve: build a wrapper Symbol around your matched
    //         children (or leaf content) and return it.
    //   * `outputSymbols` is the caller's list in Flatten mode. It's
    //     non-null by contract (callers of Flatten rules are required to
    //     provide one), and null otherwise.
    //   * Subclass construction: pass child rules to the base constructor
    //     via `base(flattenType, children)`. The `Children` property is
    //     populated automatically and Compile walks it to assign ids and
    //     seal the graph.
    //   * Optionally override ComputeRuleStart to publish this rule's
    //     FirstConsumedTokens and Advance (see RuleStartRequirements for
    //     the docs). Without an override the pessimistic defaults apply
    //     and enclosing rules never shortcut this rule (correct but
    //     slower).
    internal abstract Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols);

    // Helper for composite rules to call a child rule with the right
    // "write-here" list.
    //
    // `outputSymbols` is the list the caller is currently
    // collecting its own matched children into: either the caller's
    // caller's list (when the caller is writing into it), the caller's
    // own wrap-mode list, or null when the caller has no list yet.
    //
    // ParseChild forwards that list only when the child would actually
    // write into it (FlattenType.Flatten, normal mode). Otherwise it
    // passes null so a FlattenType.Preserve or FlattenType.Delete
    // child wraps or discards normally. Rule.TryParse performs the same check as a safety net,
    // so a custom composite that forgets this helper still gets correct
    // behavior. This just makes the intent visible at the caller.
    protected Symbol? ParseChild(Rule child, Lexer lexer, List<Symbol>? outputSymbols)
    {
        var listForChild = child.FlattenType == FlattenType.Flatten && !lexer.PreserveAllSymbols
            ? outputSymbols
            : null;
        return child.TryParse(lexer, listForChild);
    }

    // Subclass hook that publishes this rule's FirstConsumedTokens and
    // Advance. Called once per rule during Compile, in depth-first post-
    // order so children's values are already populated when a composite's
    // ComputeRuleStart runs. See RuleStartRequirements for what to produce
    // and why. The pessimistic default below (Universe, Sometimes) is the
    // fully-safe "I don't know" answer that never gets shortcutted.
    internal virtual RuleStartRequirements ComputeRuleStart()
    {
        return new RuleStartRequirements(TokenSet.Universe, Advance.Sometimes);
    }

    internal void SetIdInternal(SymbolId id)
    {
        Id = id;
        _idAssigned = true;
    }

    // Pass 1. Walk the graph and stash any explicitly-pinned ids so the
    // later passes know which slots are off limits. Reject two reachable
    // rules whose user-supplied .As(SymbolId) pins land on the same
    // custom-range id with a compile-time error that names both rules.
    // Allowing custom-range duplicate user pins would break parse-tree
    // lookups by raw SymbolId and let NameOf return whichever rule the
    // graph walk happened to visit second.
    //
    // Two ids are out of scope for this check:
    //
    //   * Pre-pinned ids in the rune range (every single-rune Token has
    //     its code point pinned at construction time). A grammar that
    //     mentions Token('a') twice has two rules sharing id 97 by design,
    //     NameOf short-circuits the rune range to the rune string, and
    //     there's no rule-name ambiguity to resolve.
    //
    //   * Ids stamped by a prior Compile on a sub-rule. If the caller
    //     compiled a sub-grammar and is now compiling a larger grammar
    //     that reaches it, those ids look pinned but weren't chosen by
    //     the user. A user pin via .As(SymbolId) always happens before
    //     Compile (As throws on a sealed rule), so a rule whose id is
    //     assigned but isn't yet sealed is the user-pinned shape we
    //     care about here.
    private static void CollectPinnedIds(Rule r, HashSet<Rule> visited, HashSet<int> usedIds, Dictionary<int, Rule> pinnedRules)
    {
        if (!visited.Add(r)) return;
        if (r._idAssigned)
        {
            int idValue = r.Id.Value;
            bool isUserPinnedCustom = !r._sealed && idValue >= SymbolRanges.CustomRangeStart;
            if (isUserPinnedCustom && pinnedRules.TryGetValue(idValue, out var existing))
            {
                throw new InvalidOperationException(
                    $"Two reachable rules pin SymbolId({idValue}): " +
                    $"'{DescribePinnedRule(existing)}' and '{DescribePinnedRule(r)}'. " +
                    $"Each .As(new SymbolId(...)) pin must be unique within a grammar.");
            }
            if (isUserPinnedCustom)
                pinnedRules[idValue] = r;
            usedIds.Add(idValue);
        }
        foreach (var child in r.Children)
            CollectPinnedIds(child, visited, usedIds, pinnedRules);
    }

    private static string DescribePinnedRule(Rule r) => r.Name ?? r._ruleTraceName;

    // Reject grammars where two distinct reachable rules share an .As(string)
    // name. A name is meant to identify a single rule in NameOf, parse-tree
    // lookups, and trace output, so duplicates would silently make those
    // resolutions ambiguous. This is the parallel of the pin-collision check
    // in CollectPinnedIds, just for names instead of SymbolIds.
    //
    // The visited set guarantees we walk each rule once, so the dictionary
    // only ever sees the second instance under a given name.
    private static void CheckNameUniqueness(Rule r, HashSet<Rule> visited, Dictionary<string, Rule> namedRules)
    {
        if (!visited.Add(r)) return;
        if (r.Name != null)
        {
            if (namedRules.ContainsKey(r.Name))
            {
                throw new InvalidOperationException(
                    $"Two reachable rules share the name '{r.Name}'. " +
                    $"Each .As(string) name must be unique within a grammar.");
            }
            namedRules[r.Name] = r;
        }
        foreach (var child in r.Children)
            CheckNameUniqueness(child, visited, namedRules);
    }

    // Pass 2. For every Rule that has a Name but no id yet, hash the name
    // into the custom range and probe upward from the hash slot to find
    // an empty one. Same name produces the same hash slot every time, so
    // a named rule's id is stable run-to-run as long as the grammar around
    // it doesn't change.
    private static void AssignNamedIds(Rule r, HashSet<Rule> visited, HashSet<int> usedIds)
    {
        if (!visited.Add(r)) return;
        if (!r._idAssigned && r.Name != null)
        {
            int slot = HashNameToCustomRange(r.Name);
            while (!usedIds.Add(slot)) slot++;
            r.Id = new SymbolId(slot);
            r._idAssigned = true;
        }
        foreach (var child in r.Children)
            AssignNamedIds(child, visited, usedIds);
    }

    // Pass 3. Anonymous rules get sequential ids starting at the bottom of
    // the custom range. Probe upward past anything passes 1 and 2 already
    // claimed.
    private static void AssignAnonymousIds(Rule r, HashSet<Rule> visited, HashSet<int> usedIds, ref int nextAnon)
    {
        if (!visited.Add(r)) return;
        if (!r._idAssigned)
        {
            while (!usedIds.Add(nextAnon)) nextAnon++;
            r.Id = new SymbolId(nextAnon);
            nextAnon++;
            r._idAssigned = true;
        }
        foreach (var child in r.Children)
            AssignAnonymousIds(child, visited, usedIds, ref nextAnon);
    }

    // Map a name to a stable starting slot in the custom range. Hash
    // collisions are fine because pass 2 probes upward.
    //
    // Uses FNV-1a 32-bit rather than string.GetHashCode because
    // string.GetHashCode is randomized per .NET process (since .NET Core
    // 3.0) and varies across runtimes. FNV-1a is a fixed byte-level
    // algorithm: same name produces the same hash on every process, every
    // .NET runtime (CoreCLR, Mono, IL2CPP), every version. That means a
    // grammar's named-rule ids are stable run-to-run, which is what
    // callers who serialize parse trees or match traces across runs want.
    //
    // FNV-1a isn't cryptographically strong, but we don't need that
    // here. We need deterministic, well-distributed, and cheap. FNV-1a
    // is all three.
    internal static int HashNameToCustomRange(string name)
    {
        const uint OffsetBasis = 2166136261u;
        const uint Prime = 16777619u;
        uint hash = OffsetBasis;
        foreach (char c in name)
        {
            // .NET strings are UTF-16. Feed the low byte then the high
            // byte so the hash is tied to the bytes of the UTF-16
            // representation, same on every runtime.
            hash = (hash ^ (byte)(c & 0xFF)) * Prime;
            hash = (hash ^ (byte)((c >> 8) & 0xFF)) * Prime;
        }
        int slot = (int)(hash & 0x7FFFFFFF); // strip sign bit
        int span = int.MaxValue - SymbolRanges.CustomRangeStart;
        return SymbolRanges.CustomRangeStart + (slot % span);
    }

    // Ask every reachable rule whether it's valid (all forward-refs bound,
    // etc.). Each subclass's ValidateCompiled throws with a helpful
    // message if it finds a problem. Default implementation is no-op.
    private static void ValidateAll(Rule r, HashSet<Rule> visited)
    {
        if (!visited.Add(r)) return;
        r.ValidateCompiled();
        foreach (var child in r.Children)
            ValidateAll(child, visited);
    }

    // Depth-first, post-order walk with cycle detection. A rule's
    // ComputeRuleStart reads its children's FirstConsumedTokens/Advance, so
    // children have to be computed first. When a cycle is found
    // (LateBoundRule pointing back into a FirstOf that contains it, for
    // instance), the in-progress rule is left at its pessimistic default
    // (Universe, Advance.Sometimes) so the loop terminates.
    // That's safe: FirstOfRule will always try
    // it, which is exactly the behavior before required-runes dispatch
    // existed.
    //
    // A smarter algorithm could repeat the walk until no
    // FirstConsumedTokens changes (each pass can only grow a FirstConsumedTokens, so this
    // terminates), which would tighten the result for self-referential
    // grammars and let FirstOfRule skip more branches inside them. But the
    // common case (LateBoundRule target is reachable via a non-cyclic
    // path) converges correctly on the first visit, so the pessimistic
    // fallback is enough for now.
    private static void ComputeRuleStartAll(Rule r, HashSet<Rule> visited, HashSet<Rule> computing)
    {
        if (visited.Contains(r)) return;
        if (!computing.Add(r)) return; // cycle: leave at pessimistic default
        foreach (var child in r.Children)
            ComputeRuleStartAll(child, visited, computing);
        var start = r.ComputeRuleStart();
        // Advance.Never means the rule never consumes on success, so
        // FirstConsumedTokens must be Empty. Anything else is dead data
        // that would mislead a reader. Fail at Compile time so subclass
        // authors find out immediately instead of debugging a wrong
        // AllOfRule union somewhere else.
        if (start.Advance == Advance.Never && !start.FirstConsumedTokens.IsEmpty)
            throw new InvalidOperationException(
                $"Rule '{r.GetType().Name}' returned Advance.Never with non-empty " +
                $"FirstConsumedTokens. A rule that never advances can't have a set " +
                $"of possible first-consumed runes. Use TokenSet.Empty for " +
                $"FirstConsumedTokens when Advance is Never.");
        r.FirstConsumedTokens = start.FirstConsumedTokens;
        r.Advance = start.Advance;
        computing.Remove(r);
        visited.Add(r);
    }

    private static void SealAll(Rule r, HashSet<Rule> visited)
    {
        if (!visited.Add(r)) return;
        r._sealed = true;
        foreach (var child in r.Children)
            SealAll(child, visited);
    }

    private void ThrowIfSealed()
    {
        if (_sealed)
            throw new InvalidOperationException("Rule has been compiled and is now sealed.");
    }
}
