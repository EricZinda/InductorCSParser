using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;

namespace InductorParser;

// Rule is the base of everything in a grammar. A grammar is a tree of Rule
// objects: combinators like And/Or/OneOrMore wrap other Rules, primitives
// like Char/RuneIn sit at the leaves, and the root is whatever Rule you
// hand to Parse(). Calling Parse on the root walks the tree and tries to
// match the input.
//
// Rules are instances, not types. 
// In C# you build a Rule by calling factory functions (And, Or, Char, etc.)
// that return Rule instances. The tree is built at runtime, compiled once,
// and reused for every parse after that. A grammar can live anywhere a
// reference can live: a local variable, a static field, an entry in a
// dictionary, an argument passed around. The library doesn't care.
//
// Rule construction is fluent: Modifier methods like .As(name) and
// .Flatten(type) return the same Rule so the configuration reads as a
// chain:
//
//     var settingName = OneOrMore(RuneIn(RuneClass.Letters))
//         .As(nameof(settingName))
//         .Flatten(FlattenType.None);
//
// Rules are effectively immutable. Before Compile runs you can call the
// modifier methods. After Compile runs (either explicitly via .Compile()
// or automatically on the first .Parse() call) the Rule is sealed and any
// further modification throws InvalidOperationException. This makes the
// "build once, parse many times" model safe even when the same Rule is
// shared across threads.
//
// Rule is abstract. The library's combinator and primitive rules
// (AndRule, OrRule, CharRule, etc.) subclass it and implement the matching
// method. User code can subclass Rule too if it needs matching logic the
// built-in combinators can't express. The contract a subclass has to
// satisfy: implement TryParse to either return a Symbol subtree on success
// or return null on failure (and never consume input on failure), use the
// lexer's transactional API for backtracking, and expose its child rules
// via ChildRules so Compile can walk the graph.
public abstract class Rule
{
    private bool _sealed;
    private bool _idAssigned;
    private string? _errorMessage;

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

    // Cached rule class name for trace output, derived from GetType().Name
    // in the constructor. The "Rule" suffix is stripped so "AndRule"
    // becomes "And", "CharRule" becomes "Char", matching the trace
    // naming convention. Reading this is a field load — cheaper than
    // calling GetType().Name on every trace emission. Works under
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
    // here — .WithError() sets the user-facing error message, not a
    // rule identity, so it belongs in the trace line's body (see
    // AppendErrorMessage) rather than as a label prefix.
    private string BuildTraceLabel() =>
        Name != null ? $"{Name}:{_ruleTraceName}" : _ruleTraceName;

    // If the rule has .WithError(msg) set, append it in quotes after
    // the trace body so a reader sees both what the rule actually
    // tried ("found 'x', wanted 'a'") and the friendly message that
    // would have surfaced to the user on a real parse failure
    // ("expected an A"). Only used on failure lines; on success
    // there is no error to report so the WithError message is
    // omitted.
    private string AppendErrorMessage(string body) =>
        _errorMessage != null ? $"{body} \"{_errorMessage}\"" : body;

    // Short-form trace helpers called from a rule's TryParse on the
    // success or failure path. 
    //
    // [AggressiveInlining] lets the JIT fold the body into the caller
    // so the off-path is a handler-construct + early return.
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
    // level other than Diagnostic (e.g. a summary line at Normal).
    // The handler attribute threads nameof(level) through so the
    // compiler picks the 5-arg TraceInterpolatedStringHandler ctor.
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

    // Shared sentinel for leaf rules that have no children. Array.Empty<T>()
    // already returns a singleton, so this isn't saving an allocation, just
    // naming the case and sparing leaf-rule constructions a new zero-length
    // Rule[] allocation from the params machinery.
    private static readonly IReadOnlyList<Rule> NoChildren = Array.Empty<Rule>();

    // The child rules this rule is built from. Composites (And, Or, OneOrMore,
    // etc.) pass their children to the base constructor and access them via
    // this property; leaf rules (Char, RuneIn, Eof) pass nothing and get the
    // shared empty list. Compile walks this list to assign ids and seal every
    // reachable rule. Subclasses never override storage; there is one place
    // children live, so there is nowhere to forget to wire them up.
    //
    // The private setter is what lets SetChildren (below) mutate children
    // for the LateBoundRule case. Every other rule fixes its children in
    // the constructor and never touches them again. After Compile seals
    // the rule, SetChildren throws and Children becomes truly immutable.
    protected internal IReadOnlyList<Rule> Children { get; private set; }

    protected Rule(FlattenType defaultFlatten, params Rule[] children)
    {
        FlattenType = defaultFlatten;
        Children = children.Length > 0 ? children : NoChildren;
        _ruleTraceName = DeriveRuleTraceName(GetType());
    }

    // Replace this rule's trace label. Intended for use only from subclass
    // constructors that need to bake construction-time parameters (like
    // BetweenInclusiveRule's bounds) into the label. No ThrowIfSealed check
    // here because at constructor time the rule isn't reachable from
    // grammar code yet, so it can't have been compiled and sealed.
    // Trace emission reads _ruleTraceName as a field load, so renaming
    // here stays a one-time cost.
    protected void SetTraceName(string name) => _ruleTraceName = name;

    // Strip the "Rule" suffix so the trace label reads "And" instead
    // of "AndRule". GetType() in a base constructor returns the
    // derived runtime type (C# guarantee), so this resolves correctly
    // for every subclass. Called once per rule instance in the ctor;
    // the result is cached in _ruleTraceName so trace emission just
    // reads a field.
    private static string DeriveRuleTraceName(Type t)
    {
        string name = t.Name;
        return name.EndsWith("Rule", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - 4)
            : name;
    }

    // Replace this rule's children. The only production use is LateBoundRule,
    // which needs to install its target after construction. Throws if the
    // rule has already been compiled, so late-binding is a grammar-build-time
    // operation and no rule can sprout new children mid-parse.
    protected void SetChildren(params Rule[] children)
    {
        ThrowIfSealed();
        Children = children.Length > 0 ? children : NoChildren;
    }

    // Hook for subclass-specific grammar-validity checks. Called once per
    // rule during Compile. Default is no-op. LateBoundRule uses it to fail
    // fast when a forward-reference was never bound.
    protected virtual void ValidateCompiled() { }

    // Attach a debug/trace name. Returns the same Rule for fluent chaining.
    // Throws InvalidOperationException if the Rule has already been compiled.
    // Virtual so subclasses (LateBoundRule) can forbid it where naming would
    // be a footgun.
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

    // Set the flatten policy (None / Delete / Flatten) that will apply when
    // FlattenInto walks the parse tree. Returns the same Rule for fluent
    // chaining. Throws if already compiled. Virtual so LateBoundRule can
    // forbid it (a FlattenType set on a transparent forwarding rule is
    // never consulted and would silently do nothing).
    public virtual Rule Flatten(FlattenType type)
    {
        ThrowIfSealed();
        FlattenType = type;
        return this;
    }

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
    // Auto-invoked on the first call to Parse(); call it explicitly when
    // you want grammar-construction errors to surface at program startup
    // rather than at first parse.
    //
    // Id assignment runs in three passes so the priority order matches what
    // the docs promise:
    //   1. Pinned ids first. Rules that called .As(SymbolId) keep the id
    //      they were given, and that id is reserved against later passes.
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

        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CollectPinnedIds(this, visited, usedIds);

        visited.Clear();
        AssignNamedIds(this, visited, usedIds);

        visited.Clear();
        int nextAnon = SymbolRanges.CustomRangeStart;
        AssignAnonymousIds(this, visited, usedIds, ref nextAnon);

        visited.Clear();
        ValidateAll(this, visited);

        visited.Clear();
        SealAll(this, visited);
        return this;
    }

    // Return the human-readable name for a SymbolId in this grammar, or
    // null if the id isn't known. Two sources, tried in order:
    //
    //   1. Character range (0..0x10FFFF): render the code point as a
    //      single-char string. A tree leaf with id 0x41 comes back as "A",
    //      0x1F3B8 comes back as "🎸". Surrogate halves (0xD800..0xDFFF)
    //      aren't valid scalar values and return null; no lexer produces
    //      them as ids, so this only matters if a caller hand-built a bad
    //      SymbolId.
    //
    //   2. Per-grammar rule index: a lazily-built Dictionary<SymbolId, Rule>
    //      keyed on every rule reachable from this root. For a rule created
    //      with .As("foo"), returns "foo". For an unnamed rule, returns the
    //      class-derived trace name ("And", "OneOrMore", "Char",
    //      "BetweenInclusive[1..3]"). Returns null if the id isn't in the
    //      grammar.
    //
    // Intended for parse-tree walkers (which only carry SymbolIds, not Rule
    // references) and for error-message rendering that wants to quote a
    // rule's name. Tracing already has direct Rule access and doesn't
    // need this path.
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
    // things like And / OneOrMore / BetweenInclusive[1..3].
    private static void CollectNames(Rule r, HashSet<Rule> visited, Dictionary<SymbolId, string> map)
    {
        if (!visited.Add(r)) return;
        map[r.Id] = r.Name ?? r._ruleTraceName;
        foreach (var child in r.Children)
            CollectNames(child, visited, map);
    }

    // Run the grammar against an input string. Auto-compiles on first call.
    // Default ParseOptions uses the GraphemeLexer; pass options explicitly
    // to switch to the RuneLexer or change other parse-time settings.
    public ParseResult Parse(string input) => Parse(input, new ParseOptions());

    public ParseResult Parse(string input, ParseOptions options)
    {
        Compile();
        Lexer lexer = options.InputUnit == InputUnit.Rune
            ? (Lexer)new RuneLexer(input, options.TraceSink, options.TraceLevel)
            : (Lexer)new GraphemeLexer(input, options.TraceSink, options.TraceLevel);
        lexer.ConfigureBudgets(options);
        Symbol? tree;
        try
        {
            tree = TryParse(lexer);
        }
        catch (ParseBudgetExceeded budget)
        {
            // The throw rode up through every active rule's `using var
            // transaction = lexer.BeginTransaction()`, which rolled the
            // lexer back frame by frame. lexer.Position now reflects
            // wherever the unwind settled. We carry that as the
            // ErrorCharIndex so callers get a coarse "how far did the
            // parser get" hint for diagnostics.
            return ParseResult.Aborted(budget.Outcome, lexer.Position, BuildBudgetMessage(budget.Outcome), lexer.Input, this);
        }
        if (tree != null && lexer.IsEof)
            return ParseResult.Succeeded(tree, lexer.Input, this);
        var pos = Math.Max(lexer.DeepestFailure, lexer.Position);
        return ParseResult.Failed(pos, BuildErrorMessage(lexer, pos), lexer.Input, this);
    }

    private static string BuildBudgetMessage(ParseOutcome outcome)
    {
        switch (outcome)
        {
            case ParseOutcome.Timeout:
                return "Parse aborted: timeout exceeded.";
            case ParseOutcome.WorkLimitExceeded:
                return "Parse aborted: maximum rule invocations exceeded.";
            case ParseOutcome.DepthLimitExceeded:
                return "Parse aborted: maximum recursion depth exceeded.";
            case ParseOutcome.Canceled:
                return "Parse aborted: cancellation requested.";
            default:
                return "Parse aborted.";
        }
    }

    private static string BuildErrorMessage(Lexer lexer, int pos)
    {
        // Prefer the error message the user attached to the rule that failed
        // at the deepest position (via .WithError("...")). That is the
        // "expected a setting name"-style message grammar authors write for
        // the spots most likely to be where a user goes wrong. Fall back to
        // the generic position-based message only when no rule at the
        // deepest failure had a WithError set.
        if (lexer.DeepestFailureMessage is { } custom)
            return custom;

        // pos == input.Length happens when the grammar wanted more characters
        // than the input had. Example: parsing "setting = 5" against a grammar
        // that requires a trailing ';'. The lexer reaches EOF, the rule for
        // ';' fails and records the failure at the end position. We have to
        // handle this both to give a useful error message ("end of input"
        // rather than "unexpected ';'" pointing at a character that isn't
        // there) and to avoid the Input[pos] indexing on the next line
        // throwing IndexOutOfRangeException.
        if (pos >= lexer.Input.Length)
            return "Unexpected end of input.";
        return $"Parse failed at offset {pos}: unexpected '{lexer.Input[pos]}'.";
    }

    // The entry point every Rule call (top-level Parse and child
    // Inner.TryParse) goes through. Bookkeeps the budget counters on the
    // lexer (rule depth, total invocations, periodic timeout / cancellation
    // poll) and then delegates to the subclass's TryParseRule. Wrapping
    // it here means user-defined Rule subclasses that just override
    // TryParseRule inherit catastrophic-backtracking protection with no
    // extra work.
    //
    // The try/finally is what keeps depth balanced when a budget trips:
    // ParseBudgetExceeded unwinds the stack, every frame's transaction
    // `using` rolls back the lexer, and ExitRule decrements the depth
    // counter on the way up. Rule.Parse catches the exception at the
    // boundary and produces a ParseResult.Aborted.
    internal Symbol? TryParse(Lexer lexer)
    {
        lexer.EnterRule();
        try
        {
            return TryParseRule(lexer);
        }
        finally
        {
            lexer.ExitRule();
        }
    }

    // The matching method every subclass implements. Contract:
    //   * Open a transaction with lexer.BeginTransaction() at the top.
    //   * On success: call transaction.Commit() and return a Symbol subtree
    //     describing what matched.
    //   * On failure: return null without committing. The `using` on the
    //     transaction will roll the lexer back automatically.
    //   * Never consume input on failure (the contract above guarantees
    //     this if you follow the transaction pattern).
    //   * Call lexer.RecordFailure() on the failure path so the
    //     "deepest failure wins" error-reporting heuristic works.
    internal abstract Symbol? TryParseRule(Lexer lexer);

    internal void SetIdInternal(SymbolId id)
    {
        Id = id;
        _idAssigned = true;
    }

    // Pass 1. Walk the graph and stash any explicitly-pinned ids so the
    // later passes know which slots are off limits.
    private static void CollectPinnedIds(Rule r, HashSet<Rule> visited, HashSet<int> usedIds)
    {
        if (!visited.Add(r)) return;
        if (r._idAssigned) usedIds.Add(r.Id.Value);
        foreach (var child in r.Children)
            CollectPinnedIds(child, visited, usedIds);
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
    // FNV-1a is not cryptographically strong, but we don't need that
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
    // message if it finds a problem; default implementation is no-op.
    private static void ValidateAll(Rule r, HashSet<Rule> visited)
    {
        if (!visited.Add(r)) return;
        r.ValidateCompiled();
        foreach (var child in r.Children)
            ValidateAll(child, visited);
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
