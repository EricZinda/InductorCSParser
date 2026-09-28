using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using InductorParser.Lexing;
using InductorParser.Lexing.Unicode;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;

namespace InductorParser;

/// <summary>
/// The base of everything in a grammar. A grammar is a tree of Rule objects:
/// composites like And/Or/<see cref="InductorParser.Rules.OneOrMore(InductorParser.Rule)">Rules.OneOrMore</see> hold other Rules, leaves like Token/<see cref="InductorParser.Rules.OneOf(System.String)">Rules.OneOf</see>
/// sit at the bottom, and the root is whatever Rule you hand to Parse().
/// Calling Parse on the root walks the tree and tries to match the input.
/// </summary>
/// <remarks>
/// Rules are instances, not types. In C# you build a Rule by calling factory
/// functions (And, Or, Token, etc.) on the Rules class that return Rule instances. The tree is
/// built at runtime, compiled once, and reused for every parse after that. A
/// grammar can live anywhere a reference can live: a local variable, a static
/// field, an entry in a dictionary, an argument passed around.
/// <para>
/// Rule construction is fluent. Modifier methods like .As(name) and
/// .Flatten(type) return the same Rule so it can read as a chain:
/// <code>
///     var settingName = OneOrMore(OneOf(TokenSet.Letters))
///         .As(nameof(settingName));
/// </code>
/// .As(name) silently flips the rule's FlattenType to <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> if it hasn't
/// been set explicitly, so a named rule is findable by <see cref="InductorParser.SyntaxTree.Symbol.Find(InductorParser.SyntaxTree.SymbolId)">Symbol.Find</see> without the
/// caller adding .Preserve() by hand. .Flatten(non-<see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>) after .As (or .As
/// after .Flatten(non-<see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>)) throws, since the two requests contradict each
/// other: a non-<see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> rule's Symbol doesn't reach the tree, so naming it for
/// Find is meaningless.
/// </para>
/// <para>
/// Rules are effectively immutable. Before <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> runs, you can call the
/// modifier methods. After <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> runs (either explicitly via .Compile() or
/// automatically on the first .Parse() call) the Rule is sealed and any further
/// modification throws InvalidOperationException. A compiled rule is immutable,
/// so any number of threads can Parse it at once with no synchronization.
/// Compilation itself is single-threaded: build and compile a grammar on one
/// thread, then share the compiled grammar. See
/// <a href="../docs/InductorParserReference.md#thread-safety">Thread Safety</a>.
/// </para>
/// <para>
/// Rule is abstract. The library's composite and leaf rules (AndRule, OrRule,
/// GraphemeRule, etc.) subclass it. User code can subclass Rule too if it needs
/// matching logic the built-in rules can't express. See <see cref="InductorParser.Rule.TryParseRule(InductorParser.Lexing.Lexer,System.Int32,InductorParser.SyntaxTree.FlattenType,System.Collections.Generic.List{InductorParser.SyntaxTree.Symbol})">Rule.TryParseRule</see> for the
/// full subclass rules.
/// </para>
/// </remarks>
public abstract class Rule
{
    // See below for description
    private bool _sealed;
    private bool _idAssigned;
    // True iff the user explicitly chose this rule's SymbolId via .As(SymbolId).
    // Narrower than _idAssigned, which is also true for ids assigned automatically
    // (GraphemeRule's single-rune Token code point, Compile's name/anonymous
    // passes). .As(string) doesn't set this: it only writes Name and lets Compile
    // derive an Id from the name hash.
    private bool _idUserExplicit;
    // Both set by .WithError(message, forced). _errorMessage is the user's exact
    // text (null means no .WithError, a "mechanical" failure). _errorForced marks
    // the message as one that beats every non-forced failure at any depth.
    // See docs/ErrorArchitecture.md for how these feed error selection.
    private string? _errorMessage;
    private bool _errorForced;

    // Has this rule been compiled yet? 
    internal bool IsCompiled => _sealed;

    // Set when a Compile attempt fails after entering the mutating phase.
    // At that point ids, literal projections, cached renderings, or other
    // compile-time state may have been partially written, so the rule graph
    // must be rebuilt rather than retried.
    private string? _invalidCompileReason;

    // The Unicode normalization form this grammar was compiled against. Set
    // by Compile(form) on every reachable rule, but only the root's value
    // matters at parse time. Defaults to NormalizationForm.FormC.
    // null means "skip normalization." The form is
    // committed at first compile. A subsequent Compile call with a different
    // form throws (see Compile for the conflict check).
    //
    // It belongs to the grammar, not the parse.
    private System.Text.NormalizationForm? _normalizationForm = System.Text.NormalizationForm.FormC;

    /// <summary>
    /// The Unicode normalization form this grammar was compiled against, or
    /// null if normalization is disabled. Set during <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> and read by Parse
    /// to normalize the input string before lexing. Exposed so callers and
    /// tests can introspect a compiled grammar.
    /// </summary>
    public System.Text.NormalizationForm? NormalizationForm => _normalizationForm;

    // Lazily-built reverse index from SymbolId to human-readable name
    // for every rule reachable from this root. Populated on the first
    // NameOf call. Grammars that never ask never pay for building it.
    // The IsUserSupplied flag distinguishes entries set by the user via
    // .As("name") from the class-derived trace-name fallback (And,
    // OneOrMore, Token, etc.). NameOf uses the flag to decide whether
    // the entry should win over the rune-string default for ids that
    // happen to land in the Unicode scalar range.
    private Dictionary<SymbolId, (string Name, bool IsUserSupplied)>? _nameIndex;

    // Lazily-built forward index from a user-supplied .As(name) string
    // back to the SymbolId the engine assigned to it during Compile.
    // Used by IdOf for callers who want to look up a rule's id at
    // grammar-construction time without holding a reference to the
    // Rule object. Populated on the first IdOf call. Only user-named
    // entries are indexed. The class-derived trace-name fallbacks
    // (And, OneOrMore, Token, ...) aren't, because they're not unique.
    private Dictionary<string, SymbolId>? _idByNameIndex;

    /// <summary>
    /// The rule's <see cref="SymbolId"/>, the integer identity parse-tree
    /// Symbols store. Assigned at <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> time unless set explicitly with
    /// <see cref="As(SymbolId)">Rule.As(SymbolId)</see>. A single-rune Token's id is its code point.
    /// </summary>
    public SymbolId Id { get; private set; }

    /// <summary>
    /// The name set with <see cref="As(string)">Rule.As(string)</see>, or null if the rule is
    /// anonymous. Used by <see cref="InductorParser.SyntaxTree.Symbol.Find(InductorParser.SyntaxTree.SymbolId)">Symbol.Find</see>, <see cref="NameOf">Rule.NameOf(SymbolId)</see>, and trace output.
    /// </summary>
    public string? Name { get; private set; }

    /// <summary>
    /// How this rule's successful match contributes to the parse tree
    /// (<see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> / <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see> / Flatten). See the
    /// <see cref="SyntaxTree.FlattenType"/> enum for what each value means.
    /// </summary>
    /// <remarks>
    /// Virtual so a rule that stands in for another rule can report that
    /// rule's value: <see cref="LateBoundRule"/> reports its bound
    /// target's, and an unnamed AliasRule reports its inner's. Neither has
    /// a flatten policy of its own in that state.
    /// </remarks>
    public virtual FlattenType FlattenType
    {
        get => _declaredFlattenType;
        private set => _declaredFlattenType = value;
    }

    private FlattenType _declaredFlattenType;

    /// <summary>
    /// The FlattenType stored on this rule itself, bypassing a getter
    /// override that forwards another rule's value. Rules that don't
    /// override <see cref="FlattenType">Rule.FlattenType</see> can ignore this: for them the
    /// two are the same value.
    /// </summary>
    /// <remarks>
    /// A rule that stands in for another rule (an unnamed AliasRule
    /// forwarding its inner) overrides the FlattenType getter, and then
    /// needs this to read what was set on the rule itself. .As(...) reads
    /// it (via ApplyIdentificationFlattenPolicy) to decide whether it
    /// still needs to flip the policy to <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>: on an unnamed alias
    /// over a <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> inner the virtual getter already reports <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>
    /// while the alias's own policy is still Flatten, and skipping the
    /// flip there would leave the alias transparent with a dead name.
    /// </remarks>
    protected FlattenType DeclaredFlattenType => _declaredFlattenType;

    /// <summary>
    /// The static error message set via .WithError("..."), or null if none.
    /// </summary>
    /// <remarks>
    /// Subclasses pass this to lexer.RecordFailure on the failure path so
    /// the depth-primary resolution can surface it: a failure with a message
    /// is "named", one without is "mechanical". See <a href="../docs/ErrorArchitecture.md">Error Reporting Architecture</a>.
    /// </remarks>
    protected internal string? ErrorMessage => _errorMessage;

    /// <summary>
    /// True when the message was set via .WithError("...", forced: true). A
    /// forced failure is a hard override: it beats every non-forced failure at
    /// any depth (and loses only to a deeper forced failure).
    /// </summary>
    protected internal bool ErrorForced => _errorForced;

    /// <summary>
    /// emitsLeaf is required (no default overload) so a new rule can't
    /// silently get the wrong shape. See the <see cref="InductorParser.Rule.EmitsLeaf">Rule.EmitsLeaf</see> property for what
    /// it means and how to choose it.
    /// </summary>
    protected Rule(FlattenType defaultFlatten, bool emitsLeaf, params Rule[]? children)
    {
        FlattenType = defaultFlatten;
        _emitsLeaf = emitsLeaf;
        Children = ValidateChildren(children);
        _ruleTraceName = DeriveRuleTraceName(GetType());
    }

    private readonly bool _emitsLeaf;

    /// <summary>
    /// True when <see cref="InductorParser.Rule.TryParseRule(InductorParser.Lexing.Lexer,System.Int32,InductorParser.SyntaxTree.FlattenType,System.Collections.Generic.List{InductorParser.SyntaxTree.Symbol})">Rule.TryParseRule</see> emits a single leaf Symbol with the matched
    /// text (leaves: <see cref="InductorParser.Rules.OneOf(System.String)">Rules.OneOf</see>, Literal, <see cref="InductorParser.Rules.AnyToken">Rules.AnyToken</see>, <see cref="InductorParser.Rules.ScanWhile(InductorParser.TokenSet,System.Int32)">Rules.ScanWhile</see>, <see cref="InductorParser.Rules.WithinToken(InductorParser.Rule)">Rules.WithinToken</see>, ...).
    /// False when it emits a composite Symbol with children (And, Or,
    /// <see cref="InductorParser.Rules.BetweenInclusive(System.Int32,System.Int32,InductorParser.Rule)">Rules.BetweenInclusive</see>) or no Symbol at all because it's zero-width (Not, Peek,
    /// Eof).
    /// </summary>
    /// <remarks>
    /// This is the output Symbol's shape, not the count of child rules:
    /// <see cref="InductorParser.Rules.WithinToken(InductorParser.Rule)">Rules.WithinToken</see> holds an inner rule but still emits one leaf. If the shape
    /// isn't fixed for the subclass, pass false to the constructor and override
    /// this to compute it (AliasRule and LateBoundRule do).
    /// </remarks>
    public virtual bool EmitsLeaf => _emitsLeaf;

    // Cached rule class name for trace output, derived from GetType().Name
    // in the constructor. The "Rule" suffix is stripped so "AndRule"
    // becomes "And", "WithinTokenRule" becomes "WithinToken", matching the
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

    // The trace label: "{Name}:{ruleName}" when the rule has a .As(name),
    // else just "{ruleName}". .WithError()'s message 
    // goes in the trace line's body (see AppendErrorMessage), not here.
    private string BuildTraceLabel() =>
        Name != null ? $"{Name}:{_ruleTraceName}" : _ruleTraceName;

    // Internal accessor so an alternative evaluator can label its trace
    // lines with the same "{Name}:{ruleClassName}" string the recursive
    // engine uses. An evaluator that emits its own trace lines rather
    // than going through TryParseRule needs the label without going
    // through the protected TraceSuccess / TraceFailure helpers.
    internal string TraceLabel => BuildTraceLabel();

    // If the rule has .WithError(msg) set, append it in quotes after
    // the trace body so a reader sees both what the rule actually
    // tried ("found 'x', wanted 'a'") and the friendly message that
    // would have surfaced to the user on a real parse failure
    // ("expected an A"). Only used on failure lines. On success
    // there's no error to report so the WithError message is
    // omitted.
    //
    // DisplayEscape.Escape rewrites control characters and line/paragraph
    // separators to U+XXXX so a WithError("line1\nline2") can't split the
    // FAIL line. The trace's interpolated `body` was already escaped by
    // TraceInterpolatedStringHandler, but that auto-escape only covers the
    // $"..." holes, not this WithError text appended after `body` is built,
    // hence the explicit escape here. _errorMessage keeps the user's exact
    // text, so ParseResult.ErrorMessage still surfaces it verbatim.
    private string AppendErrorMessage(string body)
    {
        if (_errorMessage == null) return body;
        string escapedMessage = DisplayEscape.Escape(_errorMessage, 0, _errorMessage.Length);
        return body.Length > 0
            ? $"{body} \"{escapedMessage}\""
            : $"\"{escapedMessage}\"";
    }

    /// <summary>
    /// Short-form trace helpers called from a rule's TryParse on the
    /// success or failure path.
    /// </summary>
    /// <remarks>
    /// [AggressiveInlining] + the
    /// <see cref="InductorParser.Tracing.TraceInterpolatedStringHandler">TraceInterpolatedStringHandler</see> parameter together make trace
    /// calls cost nothing when tracing is off. See
    /// <see cref="InductorParser.Tracing.TraceInterpolatedStringHandler">TraceInterpolatedStringHandler</see> for the full story.
    /// </remarks>
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

    /// <summary>
    /// Explicit-level overloads. Use when a trace should fire at a
    /// level other than Diagnostic (e.g. a summary line at Normal).
    /// </summary>
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

    /// <summary>
    /// The child rules this rule is built from. Composites (And, Or, <see cref="InductorParser.Rules.OneOrMore(InductorParser.Rule)">Rules.OneOrMore</see>,
    /// etc.) pass their children to the base constructor and access them here.
    /// Leaf rules (Token, <see cref="InductorParser.Rules.OneOf(System.String)">Rules.OneOf</see>, Eof) have none and get a shared empty list.
    /// </summary>
    /// <remarks>
    /// <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> walks this list to assign ids and seal every reachable rule.
    /// Exposed so external tools (visualizers, doc generators, tests) can walk
    /// the rule graph the same way <see cref="InductorParser.SyntaxTree.Symbol.Children">Symbol.Children</see> lets them walk the parse
    /// tree. The <see cref="System.Collections.Generic.IReadOnlyList{T}">IReadOnlyList&lt;Rule&gt;</see> return type lets external
    /// callers read the graph but not mutate it.
    /// </remarks>
    public IReadOnlyList<Rule> Children { get; private set; }

    /// <summary>
    /// Replace this rule's children. The only production use is LateBoundRule,
    /// which needs to install its target after construction.
    /// </summary>
    /// <remarks>
    /// Throws if the
    /// rule has already been compiled, so late-binding is a grammar-build-time
    /// operation and no rule can sprout new children mid-parse.
    /// </remarks>
    protected void SetChildren(params Rule[] children)
    {
        ThrowIfSealed();
        Children = ValidateChildren(children);
    }

    private static IReadOnlyList<Rule> ValidateChildren(Rule[]? children)
    {
        if (children == null)
            throw new ArgumentException("Rule children array must not be null.", nameof(children));

        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] == null)
                throw new ArgumentException($"Rule child at index {i} is null.", nameof(children));
        }

        if (children.Length == 0)
            return NoChildren;

        var copy = new Rule[children.Length];
        Array.Copy(children, copy, children.Length);
        return Array.AsReadOnly(copy);
    }

    /// <summary>
    /// Replace this rule's trace label. Intended for use only from subclass
    /// constructors that need to bake construction-time parameters (like
    /// BetweenInclusiveRule's bounds) into the label.
    /// </summary>
    /// <remarks>
    /// ThrowIfSealed keeps the trace name a grammar-construction-time setting:
    /// once the grammar is compiled and sealed, a rename throws rather than
    /// silently changing the trace label, the <see cref="InductorParser.Rule.NameOf(InductorParser.SyntaxTree.SymbolId)">Rule.NameOf</see> fallback for an unnamed
    /// rule, and diagnostic text under a live grammar. The constructor callers
    /// run before <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> seals the rule, so they pass the check. Trace output
    /// reads _ruleTraceName as a plain field load, so setting it stays a
    /// one-time cost.
    /// </remarks>
    protected void SetTraceName(string name)
    {
        ThrowIfSealed();
        _ruleTraceName = name;
    }

    // Strip the "Rule" suffix so the trace label reads "And" instead
    // of "AndRule". Called once per rule instance in the ctor.
    private static string DeriveRuleTraceName(Type t)
    {
        string name = t.Name;
        return name.EndsWith("Rule", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - 4)
            : name;
    }

    /// <summary>
    /// Hook for subclass-specific grammar-validity checks. Called once per
    /// rule during Compile. Default is no-op. LateBoundRule uses it to fail
    /// when a forward-reference was never bound.
    /// </summary>
    protected virtual void ValidateCompiled() { }

    /// <summary>
    /// Attach a debug/trace name and make the rule findable by that name
    /// (<see cref="InductorParser.SyntaxTree.Symbol.Find(InductorParser.SyntaxTree.SymbolId)">Symbol.Find</see>, <see cref="InductorParser.ParseResult">ParseResult</see> lookups). Returns the same Rule for fluent
    /// chaining. Throws InvalidOperationException if the rule has already been
    /// compiled, or if it was already named (<see cref="As(string)">Rule.As(string)</see> is
    /// set-once).
    /// </summary>
    /// <remarks>
    /// Naming a rule only works if its Symbol reaches the parse tree, which
    /// happens under <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>. So, if the flatten policy is anything else, .As
    /// tries to change it to Preserve. However, if the caller already set a non-<see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>
    /// policy explicitly via .Flatten(...) / .Delete() / .Flatten(), .As throws
    /// rather than silently overriding that choice.
    /// <para>
    /// Set-once for the string Name overload: a rule that already has a name can't be renamed. A second call throws instead.
    /// <see cref="As(SymbolId)">Rule.As(SymbolId)</see> writes a different field (Id, not Name) and
    /// composes with this: a rule with an explicit id can still pick up a name,
    /// and a named rule can still pick up an explicit id. Only same-overload
    /// repeats are bugs.
    /// </para>
    /// </remarks>
    public virtual Rule As(string name)
    {
        // Reject a null name first, before anything below runs. Otherwise
        // .As(null!) on a still-unnamed rule would pass the Name != null
        // set-once check (Name is null), flip FlattenType to Preserve in
        // ApplyIdentificationFlattenPolicy, then store null back into Name.
        // That leaves the rule half-changed: its flatten policy is already
        // Preserve but Name is still null, so a later real .As(...) wrongly
        // succeeds instead of throwing "already named".
        if (name == null) throw new ArgumentNullException(nameof(name));
        ThrowIfSealed();
        if (Name != null)
            throw new InvalidOperationException(
                $".As(\"{name}\") can't be applied to this rule: it was already " +
                $"named \"{Name}\". .As(string) is set-once. To reuse this rule " +
                $"shape under a different name, call .AliasedAs(\"{name}\") to get " +
                $"an alias with its own identity, or build a factory function " +
                $"that returns a fresh rule each call (e.g. `static Rule " +
                $"NumericCore(string name) => OneOrMore(OneOf(TokenSet.Digits))" +
                $".As(name);`).");
        ApplyIdentificationFlattenPolicy(nameof(As), name);
        Name = name;
        // Clear an auto-assigned id so Compile's AssignNamedIds pass gives
        // this rule a fresh custom-range id derived from the name hash.
        // The only auto-assignment path is GraphemeRule giving a
        // single-rune Token its code point in the constructor. Two
        // distinct Token('a').As(...) rules would otherwise silently share
        // the rune id and Tree.Find / NameOf couldn't distinguish them.
        // A user's explicit id via .As(SymbolId) stays put: that's what
        // _idUserExplicit checks for.
        if (_idAssigned && !_idUserExplicit)
            _idAssigned = false;
        return this;
    }

    /// <summary>
    /// Set an explicit <see cref="SymbolId"/> on this rule, for stable numbering
    /// across versions (useful when serializing parse trees). Returns the same
    /// Rule for fluent chaining. Throws if already compiled, or if an explicit
    /// id was already set.
    /// </summary>
    /// <remarks>
    /// Setting an id for a rule only works if its Symbol reaches the parse tree, which
    /// happens under <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>. So, if the flatten policy is anything else, .As
    /// tries to change it to Preserve. However, if the caller already set a non-<see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>
    /// policy explicitly via .Flatten(...) / .Flatten() / .Delete(), .As throws
    /// rather than silently overriding that choice.
    /// <para>
    /// The value must land in the custom range (&gt;= <see cref="InductorParser.SyntaxTree.SymbolRanges.CustomRangeStart">SymbolRanges.CustomRangeStart</see>). A lower
    /// value throws. The ranges below it would collide with auto-assigned ids
    /// if a rule reused one: 0..0x10FFFF go to Unicode rune
    /// leaves (e.g. Token('a')), and 0x110000..0x1FFFFF is reserved for future
    /// built-in ids and is unused today (anonymous rules get custom-range ids
    /// at <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see>).
    /// </para>
    /// </remarks>
    public virtual Rule As(SymbolId id)
    {
        ThrowIfSealed();
        if (id.Value < SymbolRanges.CustomRangeStart)
            throw new ArgumentOutOfRangeException(
                nameof(id),
                id.Value,
                $"An explicit SymbolId must land in the custom range " +
                $"(>= 0x{SymbolRanges.CustomRangeStart:X} / " +
                $"{SymbolRanges.CustomRangeStart}). Lower values are reserved " +
                $"for Unicode runes (0..0x10FFFF) and built-in expression " +
                $"ids (0x110000..0x1FFFFF). See SymbolRanges.");
        if (IsUserSymbolIdExplicit)
            throw new InvalidOperationException(
                $".As(SymbolId {id.Value}) can't be applied to this rule: it " +
                $"was already set to the explicit SymbolId {Id.Value}. .As(SymbolId) is " +
                $"set-once. To reuse this rule shape under a different explicit id, " +
                $"call .AliasedAs(new SymbolId(...)) to get an alias wrapper with " +
                $"its own identity, or build a factory function that returns a " +
                $"fresh rule each call.");
        ApplyIdentificationFlattenPolicy(nameof(As), id.ToString());
        Id = id;
        _idAssigned = true;
        _idUserExplicit = true;
        return this;
    }

    /// <summary>
    /// Wrap this rule in an alias that will have the name specified. This allows for reusing one
    /// rule shape without a factory function for each.
    /// </summary>
    /// <remarks>
    /// <code>
    ///     var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
    ///     var year  = digitSequence.AliasedAs("year");
    ///     var month = digitSequence.AliasedAs("month");
    /// </code>
    /// The alias's Symbol replaces its Symbol rather than nesting it. 
    /// <para>
    /// The substitution only happens on a successful match, so it doesn't affect
    /// error reporting. A .WithError on this rule (the one being aliased) records
    /// its message at the point inside it where the match broke. A .WithError on
    /// the alias records when this rule fails as a whole. If both are set, the
    /// usual deepest-failure rule decides which message the parse reports.
    /// </para>
    /// </remarks>
    public Rule AliasedAs(string name) => new AliasRule(this).As(name);

    /// <summary>
    /// Explicit-<see cref="SymbolId"/> variant of
    /// <see cref="AliasedAs(string)">Rule.AliasedAs(string)</see>: create a fresh alias around this rule, with the
    /// explicit id on the alias.
    /// </summary>
    public Rule AliasedAs(SymbolId id) => new AliasRule(this).As(id);

    // Shared path for both .As(string) and .As(SymbolId): the caller is
    // identifying this rule so it can be found later. Either auto-flip a
    // default flatten policy to Preserve, or fail if the caller already
    // set a contradicting non-Preserve policy.
    private void ApplyIdentificationFlattenPolicy(string callerMethod, string identifier)
    {
        // The virtual FlattenType getter won't work here because an unnamed alias
        // forwards it from its inner, so Alias(preserveRule).As("name")
        // would read Preserve and skip the flip the alias itself needs.
        if (DeclaredFlattenType == FlattenType.Preserve) return;
        if (_flattenPolicyExplicitlySet)
        {
            throw new InvalidOperationException(
                $".{callerMethod}(\"{identifier}\") can't be applied to this rule: " +
                $"its flatten policy was explicitly set to FlattenType.{DeclaredFlattenType}, " +
                $"so its wrapper Symbol won't appear in the parse tree and Tree.Find " +
                $"can't reach it. Set the flatten policy to FlattenType.Preserve, or " +
                $"remove the .{callerMethod}(...) call.");
        }
        FlattenType = FlattenType.Preserve;
    }

    // Set only by .As(SymbolId): "did the user explicitly set this rule's id?"
    // Two consumers check it:
    //   * Auto-assignment sites (e.g.
    //     GraphemeRule.ValidateNormalization) skip auto-assignment so a
    //     user's explicit id survives normalization.
    //   * Leaf rules that set a rune as the ID automatically (OneOfRule /
    //     NoneOfRule / AnyTokenRule / WithinTokenRule) 
    //     skip the optimization so leaves get the explicitly set id.
    internal bool IsUserSymbolIdExplicit => _idUserExplicit;

    /// <summary>
    /// Picks the leaf id for rules that can match a single rune (e.g. OneOfRule / NoneOfRule / AnyTokenRule /
    /// WithinTokenRule). An anonymous single-rune match uses the rune's code
    /// point as its id, so consumers can dispatch on `leaf.Id == 'a'`.
    /// </summary>
    /// <remarks>
    /// If the
    /// user named the rule (.As(string)) or gave it an explicit id
    /// (.As(SymbolId)), returns the chosen Id instead. A
    /// multi-rune token passes runeValue == -1 and thus also falls back to the set Id.
    /// </remarks>
    protected SymbolId ResolveLeafId(int runeValue) =>
        (Name == null && !IsUserSymbolIdExplicit && runeValue >= 0)
            ? new SymbolId(runeValue)
            : Id;

    /// <summary>
    /// Set the flatten policy (<see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> / <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see> / Flatten) that controls how
    /// this rule contributes to the parse tree on a successful match. Returns
    /// the same Rule for fluent chaining. Throws if already compiled. See the
    /// <see cref="SyntaxTree.FlattenType"/> enum for what each value means.
    /// </summary>
    /// <remarks>
    /// Setting to anything but <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> on a rule already identified with
    /// .As(name) or .As(SymbolId) throws since the whole point of
    /// them is to make the Symbol findable. 
    /// </remarks>
    public virtual Rule Flatten(FlattenType type)
    {
        CheckFlattenChangeAllowed(type, nameof(Flatten));
        FlattenType = type;
        _flattenPolicyExplicitlySet = true;
        return this;
    }

    // Shared test for Flatten and FlattenByDefault: a rule
    // identified with .As(name) / .As(SymbolId) can only be Preserve.
    private void CheckFlattenChangeAllowed(FlattenType type, string callerMethod)
    {
        ThrowIfSealed();
        if (type != FlattenType.Preserve && (Name != null || IsUserSymbolIdExplicit))
        {
            string identifier = Name != null
                ? $".As(\"{Name}\")"
                : $".As(SymbolId {Id})";
            throw new InvalidOperationException(
                $".{callerMethod}(FlattenType.{type}) can't be applied to this rule: " +
                $"it was already identified with {identifier}, so its wrapper Symbol " +
                $"must appear in the parse tree (Preserve) for Tree.Find to reach it. " +
                $"Keep the flatten policy at FlattenType.Preserve, or remove the .As(...) call.");
        }
    }

    // True once the caller has explicitly set a flatten policy via .Flatten(...),
    // .Preserve(), .Delete(), or .Flatten(). Read by ApplyIdentificationFlattenPolicy.
    private bool _flattenPolicyExplicitlySet;

    /// <summary>Shortcut for <see cref="Flatten(FlattenType)">Rule.Flatten(FlattenType)</see> with <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>.</summary>
    public Rule Preserve() => Flatten(FlattenType.Preserve);

    /// <summary>Shortcut for <see cref="Flatten(FlattenType)">Rule.Flatten(FlattenType)</see> with <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see>.</summary>
    public Rule Delete() => Flatten(FlattenType.Delete);

    /// <summary>Shortcut for <see cref="Flatten(FlattenType)">Rule.Flatten(FlattenType)</see> with <see cref="InductorParser.SyntaxTree.FlattenType.Flatten">FlattenType.Flatten</see>.</summary>
    public Rule Flatten() => Flatten(FlattenType.Flatten);

    /// <summary>
    /// Set the FlattenType as an overridable default, the way a rule's class
    /// default behaves, rather than the locked-in choice
    /// <see cref="Flatten(FlattenType)">Rule.Flatten(FlattenType)</see> records. Meant for composing factories.
    /// </summary>
    /// <remarks>
    /// The difference is for the factory's caller. Flatten(...) marks the policy
    /// user-explicit, so a later .As(name) on the returned rule throws, which the
    /// caller (who never wrote .Flatten / .Delete) can't anticipate.
    /// <see cref="InductorParser.Rule.FlattenByDefault(InductorParser.SyntaxTree.FlattenType)">Rule.FlattenByDefault</see> sets the same value but stays overridable, so .As(name)
    /// and a later .Flatten(...) work as they would on a rule at its class
    /// default. The built-in composing factories (<see cref="InductorParser.Rules.EndOfLine(System.Boolean)">Rules.EndOfLine</see>,
    /// <see cref="InductorParser.Rules.InlineWhitespace">Rules.InlineWhitespace</see>, <see cref="InductorParser.Rules.AnyWhitespace">Rules.AnyWhitespace</see>) use it.
    /// </remarks>
    public virtual Rule FlattenByDefault(FlattenType type)
    {
        CheckFlattenChangeAllowed(type, nameof(FlattenByDefault));
        FlattenType = type;
        return this;
    }

    /// <summary>
    /// Attach a static error message. If this rule has the deepest failure when a parse fails,
    /// <see cref="ParseResult.ErrorMessage">ParseResult.ErrorMessage</see> is this string instead of the
    /// generic "unexpected 'x'" fallback. Returns the same Rule for fluent
    /// chaining. Throws if already compiled.
    /// </summary>
    /// <remarks>
    /// Useful for user-friendly messages like "Expected a setting name" at the
    /// spots most likely to be where the author went wrong.
    /// <para>
    /// Only used if this rule is the deepest failure. To make a shallow failure win
    /// over a deeper one, set forced = true.
    /// See <a href="../docs/ErrorArchitecture.md">Error Reporting Architecture</a>.
    /// </para>
    /// </remarks>
    /// <param name="errorMessage">The message to surface. </param>
    /// <param name="forced">When true, this failure always wins, losing only to a deeper forced failure.</param>
    public virtual Rule WithError(string errorMessage, bool forced = false)
    {
        // Null check runs before every state mutation: ThrowIfSealed,
        // the _errorMessage != null set-once check, and the field writes.
        // Without it, .WithError(null!, forced: true) would skip the
        // set-once check, write _errorForced without writing a message,
        // and let a later legitimate .WithError(...) still succeed,
        // silently overwriting the forced flag the first call asked for.
        if (errorMessage == null) throw new ArgumentNullException(nameof(errorMessage));
        ThrowIfSealed();
        if (_errorMessage != null)
            throw new InvalidOperationException(
                $".WithError(\"{errorMessage}\") can't be applied to this rule: " +
                $"it already has the error message \"{_errorMessage}\". " +
                $".WithError(...) is set-once. To override the existing message " +
                $"at one use of this rule, wrap that use with a forced alias: " +
                $"Alias(rule).WithError(\"{errorMessage}\", forced: true). To " +
                $"give each use its own message, remove the message from the " +
                $"shared rule and wrap every use: Alias(rule).WithError(...). " +
                $"Or build a factory function that returns a fresh rule each " +
                $"call. docs/ErrorArchitecture.md explains which message " +
                $"surfaces when several apply.");
        _errorMessage = errorMessage;
        _errorForced = forced;
        return this;
    }

    /// <summary>
    /// Finalize the grammar using the default Unicode FormC normalization, shorthand for
    /// <see cref="Compile(NormalizationForm?)">Rule.Compile(NormalizationForm?)</see> with FormC.
    /// </summary>
    public Rule Compile() => Compile(System.Text.NormalizationForm.FormC);

    /// <summary>
    /// Finalize the grammar with an explicit Unicode normalization form: walk the
    /// rule graph from this Rule, assign a <see cref="SymbolId"/> to every rule
    /// that doesn't have one yet, validate each rule, convert its literal text to
    /// the chosen form, and seal the graph against further modification. Pass null
    /// to opt out of normalization entirely. Idempotent, and auto-invoked on the
    /// first <see cref="Parse(string)">Rule.Parse(string)</see>. Returns the same Rule for chaining.
    /// </summary>
    /// <remarks>
    /// Call it explicitly when you want grammar-construction errors to surface at
    /// a particular location rather than at first parse, or if you are parsing on 
    /// multiple threads. The normalization form is committed at
    /// first compile and applies to every parse afterward. A subsequent <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see>
    /// call with a different form throws InvalidOperationException: the form is
    /// part of the grammar's identity, not a per-parse option. Compilation is
    /// single-threaded, parsing is multi-threaded.
    /// <para>
    /// Id assignment runs in three passes. First, explicit ids: rules that called
    /// .As(SymbolId) keep the id they were given. Second, named rules get a
    /// hash-of-name id in the custom range, linear-probing upward if the slot is
    /// taken. The same name produces the same slot every run, so a named rule's id
    /// is stable across program executions absent grammar changes. Third, anonymous
    /// rules get sequential ids from <see cref="InductorParser.SyntaxTree.SymbolRanges.CustomRangeStart">SymbolRanges.CustomRangeStart</see>, probing past anything passes
    /// one and two claimed.
    /// </para>
    /// <para>
    /// When normalization form is non-null, every rule's expected text is converted into the
    /// chosen form in place. An author can type a literal in whatever form is
    /// convenient and it will be converted to the form chosen here for matching. 
    /// A precomposed 'é' written into the grammar will match a decomposed 'é' in
    /// the input if FormD is chosen, because <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> rewrites the literal to match. This
    /// removes the silent "rule never matches" failure mode where the literal's
    /// form and the input's form disagree.
    /// </para>
    /// <para>
    /// The normalization pass throws when a literal can't be represented in the
    /// chosen form: text the rejection scan flags (an unpaired surrogate, or U+FFFE), or a
    /// single-grapheme slot (a <see cref="InductorParser.Rules.OneOf(System.String)">Rules.OneOf</see> / <see cref="InductorParser.Rules.NoneOf(System.String)">Rules.NoneOf</see> set member or a single-rune Token)
    /// whose conversion produces more than one grapheme (the single grapheme ligature 'ﬁ' becomes
    /// a two grapheme "fi" under FormKC). The exception lists every offender and how to fix it.
    /// </para>
    /// </remarks>
    public Rule Compile(NormalizationForm? normalizeInput)
    {
        // Compilation is single-threaded by design: the caller builds and
        // compiles a grammar on one thread, then shares the compiled
        // (sealed, immutable) graph across threads for concurrent parses.
        // See docs/InductorParserReference.md "Thread Safety". Auto-compile
        // on the first Parse is a convenience for the single-threaded build
        // path, not a license to share an uncompiled grammar across
        // threads.
        if (_sealed)
        {
            if (_normalizationForm != normalizeInput)
                throw new InvalidOperationException(
                    $"Rule has already been compiled against " +
                    $"{FormatNormalizationForm(_normalizationForm)}. Re-compiling " +
                    $"with {FormatNormalizationForm(normalizeInput)} isn't allowed: " +
                    $"the normalization form is part of the grammar's identity and " +
                    $"is committed at first compile.");
            return this;
        }

        // Walk the whole graph first and refuse the Compile if any reachable
        // rule is already sealed. This is the only place sealedness is
        // checked, so it has to cover every rule before we touch anything.
        // The rest of Compile is a chain of passes (assign ids, normalize
        // literal text, stamp the form, then seal) that blindly mutate each
        // rule they visit and never look at `_sealed`. 
        // 
        // A static sub-rule shared across grammars makes the second grammar's
        // Compile (or Parse, which Compiles) throw once the first grammar has
        // sealed it. Expose shared shapes via factory functions that return a
        // fresh instance per call.
        var freshTreeVisited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CheckNoSealedReachableRules(this, freshTreeVisited);

        var usedIds = new HashSet<int>();
        var explicitRules = new Dictionary<int, Rule>();
        var namedRules = new Dictionary<string, Rule>();

        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CollectExplicitIds(this, visited, usedIds, explicitRules);

        visited.Clear();
        CheckNameUniqueness(this, visited, namedRules);

        var compileTouched = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CollectReachableRules(this, compileTouched);

        try
        {
            visited.Clear();
            AssignNamedIds(this, visited, usedIds);

            visited.Clear();
            int nextAnon = SymbolRanges.CustomRangeStart;
            AssignAnonymousIds(this, visited, usedIds, ref nextAnon);

            visited.Clear();
            ValidateAll(this, visited);

            // Convert every literal-bearing rule's stored text into the chosen
            // form in place: Token / Literal / LiteralIgnoreAsciiCase text and
            // OneOf / NoneOf sets are rewritten when they aren't already in the
            // form. Skipped when normalizeInput is null (the author opted out).
            // The only literals that can't be converted are collected as
            // offenders, and Compile then throws one InvalidOperationException
            // listing all of them so grammar authors fix every one in a single
            // pass. A literal is un-convertible when
            // string.Normalize rejects it (an unpaired surrogate) or when its
            // conversion produces more than one grapheme for a slot that holds
            // exactly one (a OneOf / NoneOf member or a single-rune Token).
            if (normalizeInput.HasValue)
            {
                var offenders = new List<(Rule rule, string original, string normalized)>();
                // Captures any ArgumentException string.Normalize throws for
                // literals it can't normalize.
                // We aggregate these as InnerException on the thrown
                // InvalidOperationException so a programmatic caller can walk
                // the runtime causes. The user-facing message stays the
                // multi-rule offender list BuildNormalizationErrorMessage emits.
                var normalizeFailures = new List<ArgumentException>();
                var reporter = new CompileNormalizationReporter(offenders, normalizeFailures);
                visited.Clear();
                ValidateNormalizationAll(this, visited, normalizeInput.Value, reporter);
                if (offenders.Count > 0)
                {
                    Exception? inner = normalizeFailures.Count > 0
                        ? new AggregateException(normalizeFailures)
                        : null;
                    throw new InvalidOperationException(
                        BuildNormalizationErrorMessage(normalizeInput.Value, offenders),
                        inner);
                }

                // A Token('é') starts as one rune (U+00E9) and gets that rune's
                // code point as its id: the character-range id that means "match
                // exactly this rune." Under FormD the normalization pass splits it
                // into two runes ("e + U+0301"). A two-rune leaf can't keep a
                // character-range id, so ValidateNormalization cleared it. Re-run
                // the anonymous-id pass to give such a rule a fresh custom-range
                // id, the same kind a Token that was multi-rune from the start
                // gets. The pass only re-ids rules whose id was cleared
                // (!_idAssigned). Any rule that kept its id is left alone.
                visited.Clear();
                AssignAnonymousIds(this, visited, usedIds, ref nextAnon);
            }

            visited.Clear();
            SealAll(this, visited);

            // Stamp the form onto every reachable rule so any subsequent
            // Compile call (which can land on any rule, not just the original
            // root) sees the form for its conflict check. Only the root's
            // value is read at parse time.
            visited.Clear();
            StampNormalizationForm(this, visited, normalizeInput);
        }
        catch (Exception ex)
        {
            MarkInvalidAfterFailedCompile(compileTouched, ex.Message);
            throw;
        }

        return this;
    }

    /// <summary>
    /// Return the human-readable name for a <see cref="SymbolId"/> in this
    /// grammar, or null if the id isn't known. Auto-compiles if the grammar
    /// hasn't been compiled yet, since ids aren't stable until <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> runs.
    /// </summary>
    /// <remarks>
    /// <see cref="InductorParser.Rule.NameOf(InductorParser.SyntaxTree.SymbolId)">Rule.NameOf</see> tries three things in order. A name from .As("foo") wins, whatever
    /// the id is. Otherwise an id in the rune range (0..0x10FFFF) is a Unicode
    /// code point and returns that one character (0x41 returns "A", 0x1F3B8
    /// returns "🎸", a lone surrogate half returns null). Otherwise it's an
    /// unnamed rule and returns the class-derived trace name ("And", "OneOrMore",
    /// "BetweenInclusive[1..3]").
    /// <para>
    /// Intended for parse-tree walkers (which see Symbols with SymbolIds,
    /// not Rule references) and for error-message rendering that wants to quote
    /// a rule's name. Tracing has direct Rule access and doesn't need this path.
    /// </para>
    /// </remarks>
    public string? NameOf(SymbolId id)
    {
        if (!_sealed) Compile();
        _nameIndex ??= BuildNameIndex();

        // A user-supplied .As("name") wins over every default. Returns
        // "aChar" for Token('a').As("aChar"), "letter" for
        // OneOf(...).As("letter"), and so on, regardless of where the
        // id lands in the SymbolRanges layout.
        if (_nameIndex.TryGetValue(id, out var entry) && entry.IsUserSupplied)
            return entry.Name;

        // Unicode scalar range with no user-supplied name: the rune's
        // own text is the natural label (single-rune Tokens render as
        // 'c' rather than Token: "c"). Returns null on invalid scalars
        // (surrogate halves) since they aren't representable as a Rune.
        int value = id.Value;
        if (value >= 0 && value < SymbolRanges.CharacterRangeEnd)
            return Rune.IsValid(value) ? new Rune(value).ToString() : null;

        // Custom-range or built-in id with no user-supplied name: the
        // class-derived trace name (And, OneOrMore,
        // BetweenInclusive[1..3]).
        return entry.Name;
    }

    /// <summary>
    /// Return the user-supplied .As("name") name for <paramref name="id"/>, or
    /// null if the id has no user name. 
    /// Auto-compiles, since ids aren't stable until <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> runs.
    /// </summary>
    /// <remarks>
    /// Lets a tree walker distinguish "the user named this rule" from "<see cref="InductorParser.Rule.NameOf(InductorParser.SyntaxTree.SymbolId)">Rule.NameOf</see>
    /// returned something because it always returns something". <see cref="InductorParser.Rule.NameOf(InductorParser.SyntaxTree.SymbolId)">Rule.NameOf</see>'s
    /// string-compare can't tell apart a user who happened to .As(...) the rule
    /// to the same string the default would have produced.
    /// </remarks>
    public string? UserNameOf(SymbolId id)
    {
        if (!_sealed) Compile();
        _nameIndex ??= BuildNameIndex();
        return _nameIndex.TryGetValue(id, out var entry) && entry.IsUserSupplied
            ? entry.Name
            : null;
    }

    private Dictionary<SymbolId, (string Name, bool IsUserSupplied)> BuildNameIndex()
    {
        var map = new Dictionary<SymbolId, (string Name, bool IsUserSupplied)>();
        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CollectNames(this, visited, map);
        return map;
    }

    /// <summary>
    /// Inverse of <see cref="NameOf">Rule.NameOf(SymbolId)</see>: takes the string the rule was
    /// constructed with via .As("name") and returns the <see cref="SymbolId"/>
    /// the engine assigned, or null if no reachable rule has that name.
    /// Auto-compiles, so callers can cache the id at static-init time without
    /// worrying about ordering relative to the first Parse.
    /// </summary>
    /// <remarks>
    /// Resolve a name to its id once, then compare ids while walking the tree.
    /// An id compare is an int compare, so it's faster than <see cref="InductorParser.SyntaxTree.Symbol.Is(string)">Symbol.Is(string)</see>,
    /// which interns and looks up a string for every node. Use <see cref="InductorParser.Rule.IdOf(System.String)">Rule.IdOf</see> when you'll
    /// branch on the same name across many nodes. Use <see cref="InductorParser.SyntaxTree.Symbol.Is(string)">Symbol.Is(string)</see> when
    /// readability matters more than speed.
    /// <code>
    ///     private static readonly SymbolId NumberId =
    ///         MyGrammar.Root.IdOf("number")!.Value;
    ///     ...
    ///     if (child.Id == NumberId) ProjectNumber(...);
    /// </code>
    /// Looking the id up once also means you don't need a public Rule field for
    /// every named rule just to recognize its nodes.
    /// </remarks>
    public SymbolId? IdOf(string ruleName)
    {
        if (ruleName == null) return null;
        if (!_sealed) Compile();
        _idByNameIndex ??= BuildIdByNameIndex();
        return _idByNameIndex.TryGetValue(ruleName, out var id) ? id : null;
    }

    private Dictionary<string, SymbolId> BuildIdByNameIndex()
    {
        var map = new Dictionary<string, SymbolId>();
        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CollectIdsByName(this, visited, map);
        return map;
    }

    // Forward-index counterpart to CollectNames. Only user-supplied
    // names go in. Class-derived trace names are skipped because they
    // aren't unique (many rules surface as just "And" / "OneOrMore"
    // and we'd lose the round-trip property). The duplicate-name check
    // that runs during Compile guarantees user names are unique per
    // grammar, so a single map slot per name is sufficient.
    private static void CollectIdsByName(Rule r, HashSet<Rule> visited, Dictionary<string, SymbolId> map)
    {
        if (!visited.Add(r)) return;
        if (r.Name != null)
            map[r.Name] = r.Id;
        foreach (var child in r.Children)
            CollectIdsByName(child, visited, map);
    }

    // Build the id -> name reverse index by walking the graph once.
    // Several rules can share one Id (single-rune Tokens use the rune's
    // code point as their Id), so when ids collide the second half of the
    // if lets a user-supplied .As name win over a trace-name fallback.
    private static void CollectNames(Rule r, HashSet<Rule> visited, Dictionary<SymbolId, (string Name, bool IsUserSupplied)> map)
    {
        if (!visited.Add(r)) return;
        bool isUser = r.Name != null;
        string name = r.Name ?? r._ruleTraceName;
        if (!map.TryGetValue(r.Id, out var existing) || (isUser && !existing.IsUserSupplied))
            map[r.Id] = (name, isUser);
        foreach (var child in r.Children)
            CollectNames(child, visited, map);
    }

    /// <summary>
    /// Run the grammar against an input string with default options.
    /// Auto-compiles on the first call. See
    /// <see cref="Parse(string, ParseOptions)">Rule.Parse(string, ParseOptions)</see> to pass options.
    /// </summary>
    public ParseResult Parse(string input) => Parse(input, new ParseOptions());

    /// <summary>
    /// Run the grammar against an input string with the given options.
    /// Auto-compiles on the first call.
    /// </summary>
    public ParseResult Parse(string input, ParseOptions options)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (options == null) throw new ArgumentNullException(nameof(options));

        // The in-loop budget check fires every 1024 rule invocations,
        // so a parse smaller than that would silently drop a
        // pre-canceled signal. Pre-flight it here.
        if (options.Cancellation != null && options.Cancellation.IsCanceled)
        {
            string message = BuildBudgetMessage(ParseOutcome.Canceled, abortPosition: 0, input, options);
            return ParseResult.Aborted(ParseOutcome.Canceled, errorCharIndex: 0, message, input, this);
        }
        return options.ResolveUseAlternativeEvaluator() && AlternativeEvaluator is { } hook
            ? hook(this, input, options)
            : ParseRecursive(input, options);
    }

    // Module-wide hook for an alternative parse engine. InductorParser's
    // default engine is the recursive-descent evaluator in ParseRecursive.
    // An alternative engine (for example an in-development state-machine
    // evaluator) is a separate implementation of the same parsing behavior: given
    // the same rule, input, and ParseOptions it returns the same ParseResult
    // the recursive engine would. Keeping it behind a hook lets that engine
    // run the existing grammars and test fixtures and have its output checked
    // against the recursive baseline, without rewriting any tests.
    //
    // An alternative engine installs itself here at startup (typically from a
    // test fixture's OneTimeSetUp). The field is null in a normal build, where
    // the default is recursive-only. When it's set and a parse opts in through
    // ParseOptions (see Parse), Parse routes through this hook instead of
    // calling ParseRecursive directly.
    internal static Func<Rule, string, ParseOptions, ParseResult>? AlternativeEvaluator;

    // The main parser body. Parse routes to whichever engine the options
    // and the global hook select, so under the alternative engine it returns
    // that engine's output, not a recursive result. A test that
    // needs to know what the recursive engine would have done to compare 
    // against can call this directly.
    internal ParseResult ParseRecursive(string input, ParseOptions options)
    {
        if (!_sealed) Compile();

        // Normalize the input to the grammar's form so a literal matches the
        // input even when the two were typed in different Unicode forms (a
        // precomposed 'é' in the grammar against a decomposed 'e + accent' in
        // the input).
        NormalizationForm? normalizeInput = _normalizationForm;
        string parseInput;
        if (normalizeInput.HasValue)
        {
            // Input the normalizers reject can't proceed: an unpaired
            // UTF-16 surrogate (ill-formed UTF-16), or U+FFFE (a
            // noncharacter .NET's string.Normalize refuses, matched here
            // so every runtime agrees). The parser scans
            // for it directly rather than relying on the runtime's
            // string.Normalize to throw, because not every runtime throws:
            // .NET's does, but Unity's Mono returns the string unchanged,
            // which would let ill-formed input flow on into the lexer. The
            // scan turns it into a MalformedInput result positioned at the
            // offending character, with a message the author can localize
            // via ParseOptions.MalformedInputTemplate, so a non-English app
            // reports it the same way it reports every other failure.
            // Callers that deliberately want surrogate-bearing input as
            // tokens opt out with Compile(null), which skips normalization
            // and never runs the scan.
            int scanBadIndex = UnicodeNormalization.FindFirstUnnormalizableIndex(input);
            if (scanBadIndex >= 0)
            {
                string scanMessage = BuildMalformedInputMessage(scanBadIndex, input, options);
                return ParseResult.MalformedInput(scanBadIndex, scanMessage, input, this);
            }
            try
            {
                parseInput = UnicodeNormalization.Normalize(input, normalizeInput.Value);
            }
            catch (ArgumentException)
            {
                // Backstop: the runtime rejected something the scan doesn't
                // know about. The scan covers everything .NET's Normalize
                // is known to throw on, so no input reaches this path on a
                // known runtime. If one ever does, report MalformedInput at
                // offset 0 rather than letting the exception escape the
                // ParseResult model.
                string malformedMessage = BuildMalformedInputMessage(0, input, options);
                return ParseResult.MalformedInput(0, malformedMessage, input, this);
            }
        }
        else
        {
            parseInput = input;
        }

        // Per-parse context every Symbol the engine builds will hold
        // a reference to. Lets Symbol.SourceRange / Symbol.SourceText
        // translate parseInput offsets back to original-input
        // coordinates without the consumer having to pass the
        // ParseResult.
        var parseContext = new ParseContext(input, parseInput, normalizeInput, this);
        Lexer lexer = new Lexer(parseInput, parseContext, options.TraceSink, options.TraceLevel);
        lexer.ConfigureOptions(options);
        
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
            // By now the throw has unwound through every rule's transaction,
            // rolling the lexer's position and failure tracker back, so the
            // lexer no longer knows how far the parse got. ThrowBudgetExceeded
            // captured that distance at throw time and parked it on the
            // exception, so read it from there instead of off the lexer.
            int abortPositionInParseInput = budget.DeepestPositionAtAbort;
            int abortPosition = NormalizedPositionMap.TranslateToOriginal(input, parseInput, abortPositionInParseInput, normalizeInput);
            return ParseResult.Aborted(budget.Outcome, abortPosition, BuildBudgetMessage(budget.Outcome, abortPosition, input, options), input, this);
        }
        if (result == null && rootList.Count == 0)
        {
            var positionInParseInput = Math.Max(lexer.DeepestFailurePosition, lexer.Position);
            int failurePosition = NormalizedPositionMap.TranslateToOriginal(input, parseInput, positionInParseInput, normalizeInput);
            return ParseResult.Failed(failurePosition, BuildErrorMessage(lexer.DeepestFailureMessage, positionInParseInput, parseInput, failurePosition, input, options), input, this);
        }
        if (!options.AllowTrailingInput && !lexer.IsEof)
        {
            // The parse succeeded but left input unconsumed. Report at
            // lexer.Position, the start of the leftover, not the
            // DeepestFailurePosition the two branches above use: that
            // high-water mark belongs to an alternative the parse abandoned
            // and has nothing to do with the trailing tail. For the same
            // reason pass customMessage: null and let the default template
            // describe the tail, rather than a WithError from a rolled-back
            // rule.
            int positionInParseInput = lexer.Position;
            int failurePosition = NormalizedPositionMap.TranslateToOriginal(input, parseInput, positionInParseInput, normalizeInput);
            return ParseResult.Failed(failurePosition, BuildErrorMessage(customMessage: null, positionInParseInput, parseInput, failurePosition, input, options), input, this);
        }
        // A real (non-Discarded) result is a Preserve root, returned as a
        // single-element list. The else covers both a Flatten root (rootList
        // holds the flattened children) and a Delete root (rootList empty).
        IReadOnlyList<Symbol> symbols;
        if (!ReferenceEquals(result, Symbol.Discarded) && result != null)
            symbols = new[] { result };
        else
            symbols = rootList;
        return ParseResult.Succeeded(symbols, input, this);
    }

    internal static string BuildBudgetMessage(ParseOutcome outcome, int abortPosition, string input, ParseOptions options)
    {
        switch (outcome)
        {
            case ParseOutcome.Timeout:
                return FormatTemplate(options.TimeoutAbortTemplate,
                    PositionPlaceholders(abortPosition, input),
                    ("timeout", () => options.Timeout.ToString()));
            case ParseOutcome.RuleCountLimitExceeded:
                return FormatTemplate(options.RuleCountLimitAbortTemplate,
                    PositionPlaceholders(abortPosition, input),
                    ("limit", () => options.RuleCountLimit.ToString()));
            case ParseOutcome.DepthLimitExceeded:
                return FormatTemplate(options.DepthLimitAbortTemplate,
                    PositionPlaceholders(abortPosition, input),
                    ("limit", () => options.MaxDepth.ToString()));
            case ParseOutcome.Canceled:
                return FormatTemplate(options.CancellationAbortTemplate,
                    PositionPlaceholders(abortPosition, input));
            default:
                // The four cases above are the only outcomes that abort a parse,
                // and ParseBudgetExceeded is only ever thrown with one of them, so
                // this branch is unreachable. Throw if a new ParseOutcome is
                // ever wired into the abort path without a case here, rather than
                // returning a vague string that hides the omission.
                throw Invariant.Fail(
                    $"BuildBudgetMessage reached its default branch with outcome '{outcome}'.");
        }
    }

    // The generic-failure path both engines funnel into. Its two int
    // parameters are different coordinate systems: positionInParseInput
    // indexes into parseInput (post-normalization) for the EOF check and the
    // {character} lookup. failurePosition is that position mapped back to the
    // original input for the user-facing placeholders.
    internal static string BuildErrorMessage(string? customMessage, int positionInParseInput, string parseInput, int failurePosition, string input, ParseOptions options)
    {
        // customMessage is a rule's .WithError("...") text for the deepest
        // failure. When set it wins over the generic message, but it still goes
        // through WithErrorTemplate so the author's text picks up the failure
        // position ("... at line L, column C.") the same way the mechanical
        // messages do. A caller who wants the raw text back sets
        // WithErrorTemplate to "{message}". This branch is checked before the
        // EOF one below, so a WithError at end of input is wrapped too. The
        // position placeholders resolve to the end position.
        if (customMessage != null)
            return FormatTemplate(options.WithErrorTemplate,
                PositionPlaceholders(failurePosition, input),
                ("message", () => customMessage));

        // At EOF (positionInParseInput == parseInput.Length) there's no
        // character to point at, so give an "end of input" message instead of
        // indexing past the end of parseInput on the default path below.
        if (positionInParseInput >= parseInput.Length)
            return FormatTemplate(options.EndOfInputErrorTemplate,
                PositionPlaceholders(failurePosition, input));
        // {character} reads from the original input, not parseInput, so the
        // message quotes what the user actually typed even when normalization
        // rewrote it (FormKC converts fullwidth, ligatures, and math letters to
        // ASCII). DisplayEscape renders a control or line-separator char (a bare
        // LF, U+2028) as U+XXXX rather than inserting a raw newline into the
        // one-line message. Printable characters still render verbatim.
        return FormatTemplate(options.PositionalErrorTemplate,
            PositionPlaceholders(failurePosition, input),
            ("character", () =>
            {
                int elementLength = GraphemeSegmentation
                    .GetLengthOfFirstExtendedGraphemeCluster(input.AsSpan(failurePosition));
                return DisplayEscape.Escape(input, failurePosition, elementLength);
            }));
    }

    // Build the message for a MalformedInput result. Same template
    // machinery as BuildErrorMessage: the shared position placeholders
    // ({charIndex} / {tokenIndex} / {line} / {charColumn} / {tokenColumn} /
    // {lineNumber} / {charColumnNumber} / {tokenColumnNumber}) plus a {character}
    // placeholder that renders the offending element through DisplayEscape, so a
    // lone surrogate comes out as "U+D800" rather than a raw, unrenderable code
    // unit. The default MalformedInputTemplate mentions {lineNumber} /
    // {tokenColumnNumber} and {character}, so building it pays one line scan plus
    // one grapheme-cluster count on the failure path.
    internal static string BuildMalformedInputMessage(int badIndex, string input, ParseOptions options)
    {
        return FormatTemplate(options.MalformedInputTemplate,
            PositionPlaceholders(badIndex, input),
            ("character", () =>
            {
                int elementLength = GraphemeSegmentation
                    .GetLengthOfFirstExtendedGraphemeCluster(input.AsSpan(badIndex));
                return DisplayEscape.Escape(input, badIndex, elementLength);
            }));
    }

    // The position placeholders shared by every default template:
    //   {charIndex}          - failure position in chars (UTF-16 code units),
    //                          matching ParseResult.ErrorCharIndex.
    //   {tokenIndex}         - matching ParseResult.ErrorTokenIndex.
    //   {line}               - zero-based, matching ParseResult.ErrorLine.
    //   {charColumn}         - zero-based char column, matching ParseResult.ErrorCharColumn.
    //   {tokenColumn}        - zero-based token (grapheme) column, matching
    //                          ParseResult.ErrorTokenColumn.
    //   {lineNumber}         - one-based ({line} + 1), for human-facing messages.
    //   {charColumnNumber}   - one-based char column ({charColumn} + 1).
    //   {tokenColumnNumber}  - one-based token column ({tokenColumn} + 1).
    // The zero-based placeholders match the ParseResult fields. The *Number
    // variants are the same positions counted from 1, which is what a person
    // reading an editor expects. The char columns ({charColumn} / {charColumnNumber})
    // follow the Language Server Protocol's UTF-16 code-unit convention. The
    // token columns ({tokenColumn} / {tokenColumnNumber}) count grapheme
    // clusters, so an emoji or combining sequence earlier on the line counts as
    // one, matching the character a person sees. The default messages use
    // {lineNumber} and {tokenColumnNumber} for that reason. The fields stay as
    // they are. Only the *Number placeholders shift by one.
    //
    // The Func<string> delegates exist because each token-index / line-column /
    // token-column conversion walks the input once, so we only want to pay for
    // the ones whose placeholder actually appears in the template the caller
    // chose. The default templates use {lineNumber} and {tokenColumnNumber}, so a
    // failed parse pays one line scan plus one grapheme-cluster count when its
    // message is built.
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
            ("charColumn", () =>
            {
                SourcePositionConverter.ToLineColumn(input, charIndex, out _, out int column);
                return column.ToString();
            }),
            ("tokenColumn", () => SourcePositionConverter.ToTokenColumn(input, charIndex).ToString()),
            ("lineNumber", () =>
            {
                SourcePositionConverter.ToLineColumn(input, charIndex, out int line, out _);
                return (line + 1).ToString();
            }),
            ("charColumnNumber", () =>
            {
                SourcePositionConverter.ToLineColumn(input, charIndex, out _, out int column);
                return (column + 1).ToString();
            }),
            ("tokenColumnNumber", () => (SourcePositionConverter.ToTokenColumn(input, charIndex) + 1).ToString()),
        };
    }

    // Substitute {name}-style placeholders in a user-supplied template
    // string. positionPlaceholders are the ones every template shares
    // ({charIndex}, {tokenIndex}, {line}, {charColumn}, {tokenColumn}, and the
    // one-based {lineNumber} / {charColumnNumber} / {tokenColumnNumber}).
    // extraPlaceholders are the
    // ones specific to one template ({timeout}, {limit}, {character}), which is
    // why it's params: the templates that need none pass nothing. Each
    // placeholder's value is computed lazily via its Func<string> only when the
    // placeholder actually appears in the template, so callers don't pay for an
    // O(n) rune-index walk if their template only mentions {charIndex}. Unknown
    // placeholder names pass through verbatim.
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
    // TryParse also normalizes what the subclass deals with: it computes
    // effectiveFlattenType (collapsing PreserveAllSymbols), and makes
    // outputSymbols non-null only in Flatten mode. Subclasses don't have
    // to re-check FlattenType or PreserveAllSymbols, they implement
    // the three modes per the TryParseRule rules below.
    internal Symbol? TryParse(Lexer lexer, List<Symbol>? outputSymbols)
    {
        lexer.Budget.EnterRule();
        try
        {
            // effectiveFlattenType collapses FlattenType and
            // PreserveAllSymbols. PreserveAllSymbols treats every rule
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
                // for it to write children into. When the caller passed null
                // because it has nowhere to put the children (Not/Peek throwing
                // away inner's output, a Flatten root that didn't get a rootList,
                // etc.), we allocate a throwaway list here. Inner writes into it
                // but nobody reads it, and the list goes out of scope when this
                // call returns. Keeps subclasses simple: they always get a
                // list to write into.
                outputSymbols = new List<Symbol>();
            }
            int savedCount = outputSymbols?.Count ?? 0;

            Symbol? result;
            if (OpensTransaction)
            {
                // Opening the outer transaction here, instead of in every
                // TryParseRule, means a subclass can't forget to commit
                using var transaction = lexer.BeginTransaction();
                result = TryParseRule(lexer, transaction.StartPosition, effectiveFlattenType, outputSymbols);
                if (result != null)
                    transaction.Commit();
            }
            else
            {
                // EofRule and LateBoundRule open no transaction: Eof never
                // moves the cursor, and LateBound delegates wholly to its
                // target, which owns its own transaction. They take
                // startPosition straight from the current cursor.
                result = TryParseRule(lexer, lexer.Position, effectiveFlattenType, outputSymbols);
            }

            if (result == null)
            {
                // Roll back any partial writes to outputSymbols. The
                // transaction's `using` already rolled back the lexer
                // position. This rolls back the caller's list.
                if (outputSymbols != null && outputSymbols.Count > savedCount)
                    outputSymbols.RemoveRange(savedCount, outputSymbols.Count - savedCount);
                return null;
            }

            // Safety net: subclasses that ignored effectiveFlattenType
            // and handed back a real Symbol in Delete / Flatten mode
            // get normalized to Discarded so callers can rely on only
            // FlattenType.Preserve returning a Symbol.
            if (effectiveFlattenType != FlattenType.Preserve)
                return Symbol.Discarded;
            return result;
        }
        finally
        {
            lexer.Budget.ExitRule();
        }
    }

    /// <summary>
    /// The matching method every subclass implements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rule.TryParse owns the outer transaction. It opens one before calling
    /// this method and commits it only when this method returns a non-null
    /// Symbol, so a subclass never calls <see cref="InductorParser.Lexing.Lexer.BeginTransaction">Lexer.BeginTransaction</see> or Commit for its own
    /// outer scope and can't forget the commit. `startPosition` is the lexer
    /// position captured the moment that transaction opened. Use it for failure
    /// error positions and for the ReadOnlyMemory span of any Symbol you build.
    /// </para>
    /// <para>
    /// On failure, return null. Rule.TryParse's transaction rolls the lexer back
    /// automatically and truncates any partial writes to outputSymbols for you.
    /// Call lexer.RecordFailure() or lexer.RecordCompositeFailure() so the "deepest failure wins"
    /// error-reporting can surface your rule's message.
    /// </para>
    /// <para>
    /// If your rule matches stored expected text, record failure positions only
    /// at whole-grapheme boundaries of that text: a partially matched grapheme
    /// isn't progress. Canonical normalization changes a grapheme's rune count
    /// but not its boundaries, so whole-grapheme positions come out the same
    /// whichever form the grammar was compiled with, while rune-level
    /// positions don't. LiteralRule's FailurePosition helper shows the pattern.
    /// See <a href="../docs/ErrorArchitecture.md#where-each-rule-records-its-failure">Where each rule records its failure</a>.
    /// </para>
    /// <para>
    /// On success, return a non-null Symbol whose form depends on
    /// `effectiveFlattenType`. For <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see>, emit nothing and return
    /// Symbol.Discarded. For Flatten, append each Symbol you would have
    /// collected to `outputSymbols` (the caller's list, guaranteed non-null) and
    /// return Symbol.Discarded. For a composite that would be each child's
    /// Symbol, and for a leaf it's the leaf Symbol itself. For <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see>, build a
    /// Symbol that wraps your matched child Symbols (or, for a leaf, the span of
    /// input you consumed) and return it.
    /// </para>
    /// <para>
    /// To look at what comes next without consuming it,
    /// open a lexer.BeginProbe() and read inside it. 
    /// The Probe rolls the position back when disposed without a commit, but it
    /// also throws away any failures recorded inside it, so a peek that fails
    /// doesn't show up in the final error message. 
    /// </para>
    /// <para>
    /// `outputSymbols` is non-null only in Flatten mode, where it's the
    /// caller's list for you to append your child Symbols to. In <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see> and
    /// <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> mode it's null.
    /// </para>
    /// <para>
    /// If your rule wraps child rules, pass them to the base constructor as
    /// `base(flattenType, children)`. The base stores them in the <see cref="InductorParser.Rule.Children">Rule.Children</see>
    /// property, which is how <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> discovers the grammar: it walks <see cref="InductorParser.Rule.Children">Rule.Children</see>
    /// to assign ids and seal every reachable rule. A child you don't pass to
    /// base is invisible to Compile.
    /// </para>
    /// </remarks>
    protected abstract Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols);

    /// <summary>
    /// Whether Rule.TryParse opens an automatic outer transaction around
    /// this rule's TryParseRule. True for every rule that speculatively
    /// reads input, which is almost all of them: a rule that reads tokens
    /// and then fails must be able to roll back. EofRule and LateBoundRule
    /// set this false in their constructors. Eof never moves the cursor,
    /// and LateBound delegates wholly to its target rule, which owns its
    /// own transaction.
    /// </summary>
    /// <remarks>
    /// A plain field, not a virtual property: Rule.TryParse reads it on
    /// every rule invocation, so a virtual dispatch there would be
    /// hot-path overhead. A field read plus a well-predicted branch is
    /// effectively free.
    /// <para>
    /// `protected internal` so external Rule subclasses can also opt out
    /// of the auto-managed transaction when they own their own probe /
    /// transaction scope. The default (true) is what almost every shape
    /// wants.
    /// </para>
    /// </remarks>
    protected internal bool OpensTransaction = true;

    /// <summary>
    /// Helper for a rule to run an inner rule with the transaction and budget
    /// wrapping every rule gets, forwarding `outputSymbols` to it. Used both by
    /// composites assembling their children (And, Or, Alias, ...) and by rules
    /// that drive an inner rule as a lookahead or subroutine (Not, Peek,
    /// <see cref="InductorParser.Rules.ScanUntil(InductorParser.Rule,System.Boolean)">Rules.ScanUntil</see>, <see cref="InductorParser.Rules.WithinToken(InductorParser.Rule)">Rules.WithinToken</see>).
    /// </summary>
    /// <remarks>
    /// Pass the list the inner should append its Flatten-mode children to, or
    /// null to discard them (lookahead and probes pass null). You don't have to
    /// check the inner's FlattenType first: TryParse ignores the list unless the
    /// inner is Flatten, so passing one to a <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> or <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see> inner is harmless.
    /// </remarks>
    protected Symbol? ParseChild(Rule child, Lexer lexer, List<Symbol>? outputSymbols)
        => child.TryParse(lexer, outputSymbols);

    /// <summary>
    /// Builds the composite Symbol a rule returns from <see cref="InductorParser.Rule.TryParseRule(InductorParser.Lexing.Lexer,System.Int32,InductorParser.SyntaxTree.FlattenType,System.Collections.Generic.List{InductorParser.SyntaxTree.Symbol})">Rule.TryParseRule</see>, taking
    /// ownership of the children collection instead of copying it. Prefer this
    /// over `new Symbol(Id, FlattenType, children, ...)` on the parse hot path.
    /// That public constructor defensively copies the collection because it can't
    /// trust an arbitrary caller to stop touching it, but a rule that built
    /// `children` fresh for this one match and hands it straight off here skips
    /// that array copy, paying only to publish the existing collection read-only.
    /// </summary>
    /// <remarks>
    /// `children` must be a collection this rule
    /// built for this match and will never read or mutate again. The returned
    /// Symbol wraps it directly (a List or array goes through AsReadOnly, so
    /// <see cref="InductorParser.SyntaxTree.Symbol.Children">Symbol.Children</see> still can't be cast back to a mutable type). When you can't make that promise, use
    /// the public Symbol constructor, which copies.
    /// </remarks>
    protected Symbol CreateCompositeFromOwnedChildren(IReadOnlyList<Symbol>? children, ReadOnlyMemory<char> consumedSpan, ParseContext? context)
        => Symbol.FromOwnedChildren(Id, FlattenType, children, consumedSpan, context);

    /// <summary>
    /// Subclass hook for <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see>-time normalization-form validation. Each
    /// rule that holds user-supplied text the parser will compare against
    /// normalized input overrides this to walk its own data: it reports any
    /// text whose normalization can't be represented under the chosen form
    /// via reporter.ReportOffender, and may convert its own stored text or
    /// set to the normalized form in place (those mutations are the rule
    /// writing its own private state, so they don't go through the
    /// reporter).
    /// </summary>
    /// <remarks>
    /// Default no-op: a rule with no fixed text of its own has nothing to
    /// validate.
    /// <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see>'s static walker (below) calls this on every reachable rule
    /// and recurses into Children. 
    /// </remarks>
    protected virtual void ValidateNormalization(
        NormalizationForm form,
        INormalizationReporter reporter)
    {
        // default no-op
    }

    /// <summary>
    /// Helper for rules that have one fixed expected string. Tries to
    /// convert `text` to `form`. Returns the normalized text on success.
    /// </summary>
    /// <remarks>
    /// Text that can't be normalized (an unpaired surrogate, or U+FFFE,
    /// the two things the rejection scan flags) is reported to the
    /// reporter (which surfaces
    /// it as the thrown exception's InnerException and records a matching
    /// offender), and the method returns null. The scan runs before the
    /// conversion because not every runtime's string.Normalize throws on
    /// such text, and the catch stays as a backstop for anything a runtime
    /// rejects beyond the scan. Callers that get a non-null result should
    /// replace their stored expected text with it. This behavior makes
    /// the rule's match-time view canonically equivalent to the user's
    /// typed text under any form.
    /// </remarks>
    protected static string? TryConvertToForm(
        Rule rule, string text,
        NormalizationForm form,
        INormalizationReporter reporter)
    {
        int badIndex = UnicodeNormalization.FindFirstUnnormalizableIndex(text);
        if (badIndex >= 0)
        {
            reporter.ReportNormalizeFailure(rule, text,
                UnicodeNormalization.CreateUnnormalizableTextException(text, badIndex));
            return null;
        }
        try
        {
            return UnicodeNormalization.Normalize(text, form);
        }
        catch (ArgumentException exception)
        {
            reporter.ReportNormalizeFailure(rule, text, exception);
            return null;
        }
    }

    internal void SetIdInternal(SymbolId id)
    {
        Id = id;
        _idAssigned = true;
    }

    /// <summary>
    /// Protected hook for a single-rune leaf rule that wants its id to be
    /// the rune's code point (the "id == rune" shape that gives
    /// Symbol.Is(Token('x')) its meaning).
    /// </summary>
    /// <remarks>
    /// Encapsulates the logic so a user-defined rule doesn't have to know about the internal
    /// id machinery: the assignment is skipped when the user already set
    /// an explicit SymbolId via .As(SymbolId) or named the rule via
    /// .As(string). GraphemeRule uses this, and a
    /// third-party single-rune leaf can use it too.
    /// </remarks>
    protected void SetLeafRuneId(int runeValue)
    {
        if (!IsUserSymbolIdExplicit && Name == null)
            SetIdInternal(new SymbolId(runeValue));
    }

    /// <summary>
    /// Counterpart to <see cref="InductorParser.Rule.SetLeafRuneId(System.Int32)">Rule.SetLeafRuneId</see> for a single-rune leaf rule whose stored
    /// text turned multi-rune during the normalization pass (Token('é'),
    /// U+00E9, decomposing to "e + U+0301" under FormD).
    /// </summary>
    /// <remarks>
    /// The constructor gave this rule the rune's code point as its Id which is the
    /// character-range id (0..0x10FFFF) that means "match exactly this one
    /// rune." A two-rune leaf can't use that kind of id, so clear it here.
    /// </remarks>
    protected void ClearLeafRuneId()
    {
        if (!IsUserSymbolIdExplicit && Name == null)
            _idAssigned = false;
    }

    // Pass 1. Walk the graph and stash any user-set explicit ids so the
    // later passes know which slots are off limits, and reject duplicate .As(SymbolId).
    //
    // The conflict check is gated on `_idUserExplicit`, set only by
    // `.As(SymbolId)`, not on `_idAssigned`. Non-user ids are allowed to
    // collide and just reserve their slot in `usedIds`. A grammar that mentions
    // Token('a') twice has two rules sharing rune id 97 by design. That's
    // harmless: a rune-range id identifies a character, not a rule (NameOf
    // returns "a" for 97 without consulting the rule set), so duplicates can't
    // make any id lookup ambiguous the way two equal .As(SymbolId) ids would.
    private static void CollectExplicitIds(Rule r, HashSet<Rule> visited, HashSet<int> usedIds, Dictionary<int, Rule> explicitRules)
    {
        if (!visited.Add(r)) return;
        if (r._idAssigned)
        {
            int idValue = r.Id.Value;
            if (r._idUserExplicit && explicitRules.TryGetValue(idValue, out var existing))
            {
                throw new InvalidOperationException(
                    $"Two rules use the explicit SymbolId({idValue}): " +
                    $"'{DescribeRule(existing)}' and '{DescribeRule(r)}'. " +
                    $"Each .As(new SymbolId(...)) explicit id must be unique within a grammar.");
            }
            if (r._idUserExplicit)
                explicitRules[idValue] = r;
            usedIds.Add(idValue);
        }
        foreach (var child in r.Children)
            CollectExplicitIds(child, visited, usedIds, explicitRules);
    }

    private static string DescribeRule(Rule r) => r.Name ?? r._ruleTraceName;

    // Reject grammars where two distinct reachable rules share an .As(string)
    // name. A name is meant to identify a single rule in NameOf, parse-tree
    // lookups, and trace output, so duplicates would silently make those
    // resolutions ambiguous. This is the parallel of the explicit-id
    // collision check in CollectExplicitIds, just for names instead of SymbolIds.
    private static void CheckNameUniqueness(Rule r, HashSet<Rule> visited, Dictionary<string, Rule> namedRules)
    {
        if (!visited.Add(r)) return;
        if (r.Name != null)
        {
            if (namedRules.ContainsKey(r.Name))
            {
                throw new InvalidOperationException(
                    $"Two rules share the name '{r.Name}'. " +
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
            // The probe only stops on overflow if every slot from the hash up
            // through int.MaxValue is taken (~2 billion ids), where slot++ wraps
            // to a negative value below the custom range. Unreachable in any real
            // grammar, but fail rather than hand out a corrupt id.
            Invariant.That(slot >= SymbolRanges.CustomRangeStart,
                $"Named-id probe for rule '{r.Name}' overflowed the custom range.");
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
            // Same overflow case as AssignNamedIds: nextAnon wraps negative only
            // after ~2 billion ids are claimed. Unreachable in practice, but
            // fail rather than hand out a corrupt id.
            Invariant.That(nextAnon >= SymbolRanges.CustomRangeStart,
                "Anonymous-id probe overflowed the custom range.");
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
    // string.GetHashCode is randomized per .NET process (on every .NET
    // Core version) and varies across runtimes. FNV-1a is a fixed byte-level
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

    private static void SealAll(Rule r, HashSet<Rule> visited)
    {
        if (!visited.Add(r)) return;
        r._sealed = true;
        foreach (var child in r.Children)
            SealAll(child, visited);
    }

    // Walk the rule graph and ask each rule to validate its own user-
    // supplied text against the chosen normalization form. Each rule
    // overrides the instance-level ValidateNormalization to do
    // the right thing for its own data shape: literal-bearing rules
    // normalize their one fixed string, set-bearing rules walk their entries,
    // composites no-op (this walker recurses into Children separately).
    // Reports every offender through the reporter and the caller throws one
    // combined exception.
    private static void ValidateNormalizationAll(
        Rule r,
        HashSet<Rule> visited,
        NormalizationForm form,
        INormalizationReporter reporter)
    {
        if (!visited.Add(r)) return;
        r.ValidateNormalization(form, reporter);
        foreach (var child in r.Children)
            ValidateNormalizationAll(child, visited, form, reporter);
    }

    // The only INormalizationReporter implementation. Compile creates it for the
    // normalization pass, where it collects two lists, offenders and failures.
    // After the pass, Compile builds the thrown InvalidOperationException from
    // them: offenders become its message text, failures become an
    // AggregateException InnerException. A normalize failure goes into both
    // lists, so its rule still shows up in the user-facing message, not only the
    // InnerException.
    private sealed class CompileNormalizationReporter : INormalizationReporter
    {
        private readonly List<(Rule rule, string original, string normalized)> _offenders;
        private readonly List<ArgumentException> _failures;

        public CompileNormalizationReporter(
            List<(Rule rule, string original, string normalized)> offenders,
            List<ArgumentException> failures)
        {
            _offenders = offenders;
            _failures = failures;
        }

        public void ReportOffender(Rule rule, string original, string suggestedReplacement)
            => _offenders.Add((rule, original, suggestedReplacement));

        public void ReportNormalizeFailure(Rule rule, string original, ArgumentException failure)
        {
            _failures.Add(failure);
            _offenders.Add((rule, original,
                $"<this literal can't be normalized: {failure.Message} " +
                $"This is usually an unpaired surrogate. Use Compile(null) to keep " +
                $"surrogate-bearing literals as-is.>"));
        }
    }

    // Build the multi-rule error message, e.g.:
    //
    //   Compile failed: 2 rules hold text that can't be converted to FormKC.
    //   Compile normalizes each rule's text to the chosen form, so it will
    //   match the form the input gets converted to. These rules can't be
    //   converted that way, either because the text isn't valid Unicode or
    //   because converting it splits one grapheme into several where the
    //   rule matches exactly one. Fix each rule as noted below, or pass a
    //   different normalization form to Compile (or null to turn
    //   normalization off):
    //     - Token (GraphemeRule): 'U+FB01' <Token converts to multi-grapheme
    //       sequence "fi" under FormKC. Token matches exactly one grapheme.
    //       Use Literal("fi") or And(Token-per-grapheme) instead.>
    //     - letter (OneOfRule): 'U+FB01' <converts under FormKC to the
    //       multi-grapheme sequence "fi", but a TokenSet member has to be
    //       exactly one grapheme. ...>
    //
    // (U+FB01 is the "ﬁ" ligature. FormatLiteralForError renders non-ASCII
    // runes as U+XXXX, since the difference is often invisible. The detail
    // after each literal is the offender description the reporting rule
    // supplied, not a drop-in replacement value.)
    private static string BuildNormalizationErrorMessage(
        NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders)
    {
        // Count distinct rules, not offender lines. A single TokenSet-bearing
        // rule (OneOf / NoneOf / ScanWhile / ScanUntil) reports one offender
        // per offending set entry, so OneOf(TokenSet.Letters) under FormKC is
        // one rule with hundreds of convertible members, not hundreds of
        // rules. The header sentence is about rules ("a rule whose expected
        // text is in a different form will never match"), so it has to count
        // rules. The per-entry detail still gets one line each below.
        var distinctRules = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        foreach (var (offendingRule, _, _) in offenders)
            distinctRules.Add(offendingRule);
        int ruleCount = distinctRules.Count;

        var builder = new StringBuilder();
        builder.Append("Compile failed: ")
               .Append(ruleCount)
               .Append(ruleCount == 1 ? " rule holds" : " rules hold")
               .Append(" text that can't be converted to ")
               .Append(FormatNormalizationForm(form))
               .Append(". Compile normalizes each rule's text to the chosen form, so it ")
               .Append("will match the form the input gets converted to. ")
               .Append(ruleCount == 1 ? "This rule" : "These rules")
               .Append(" can't be converted that way, either because the text isn't valid ")
               .Append("Unicode or because converting it splits one grapheme into several ")
               .Append("where the rule matches exactly one. Fix each rule as noted below, or ")
               .AppendLine("pass a different normalization form to Compile (or null to turn normalization off):");
        foreach (var (rule, original, detail) in offenders)
        {
            string displayName = rule.Name ?? rule._ruleTraceName;
            string ruleType = rule.GetType().Name;
            builder.Append("  - ")
                   .Append(displayName)
                   .Append(" (")
                   .Append(ruleType)
                   .Append("): ")
                   .Append(FormatLiteralForError(original))
                   .Append(' ')
                   .Append(detail)
                   .AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    // Render a literal for the error message. Wraps in single quotes and
    // escapes embedded quotes / backslashes. Bare runes outside the
    // printable range get a U+XXXX form so the user can tell what
    // changed even when the difference is invisible (combining marks,
    // ZWJ, variation selectors, etc.).
    private static string FormatLiteralForError(string literal)
    {
        var builder = new StringBuilder();
        builder.Append('\'');
        for (int index = 0; index < literal.Length;)
        {
            int runeValue;
            int runeLength;
            if (RuneHelpers.IsSurrogatePairAt(literal, index))
            {
                runeValue = char.ConvertToUtf32(literal[index], literal[index + 1]);
                runeLength = 2;
            }
            else
            {
                runeValue = literal[index];
                runeLength = 1;
            }

            if (runeValue >= 0x20 && runeValue < 0x7F && runeValue != '\'' && runeValue != '\\')
                builder.Append((char)runeValue);
            else if (runeValue == '\'' || runeValue == '\\')
                builder.Append('\\').Append((char)runeValue);
            else
                builder.Append("U+").Append(runeValue.ToString("X4"));

            index += runeLength;
        }
        builder.Append('\'');
        return builder.ToString();
    }

    private static string FormatNormalizationForm(NormalizationForm? form) =>
        form.HasValue ? form.Value.ToString() : "no normalization (null)";

    // Walk the graph and stamp the form on every rule, so a later
    // Compile call landing on any rule in the graph can make sure it doesn't change. 
    // Only the root's value is read at parse time.
    private static void StampNormalizationForm(Rule r, HashSet<Rule> visited, NormalizationForm? form)
    {
        if (!visited.Add(r)) return;
        r._normalizationForm = form;
        foreach (var child in r.Children)
            StampNormalizationForm(child, visited, form);
    }

    // Collect every rule reachable from this root into `visited` (the caller's
    // `compileTouched` set). This builds a roster only, it sets no per-rule
    // flag. Compile gathers it before mutating anything so that if a later pass
    // throws, the catch can invalidate the whole reachable graph through
    // MarkInvalidAfterFailedCompile, not just the rules reached before the throw.
    private static void CollectReachableRules(Rule r, HashSet<Rule> visited)
    {
        if (!visited.Add(r)) return;
        foreach (var child in r.Children)
            CollectReachableRules(child, visited);
    }

    private static void MarkInvalidAfterFailedCompile(HashSet<Rule> rules, string reason)
    {
        foreach (var rule in rules)
            rule._invalidCompileReason ??= reason;
    }

    // Walk the graph and reject this Compile if any reachable rule has
    // already been compiled or was invalidated by a failed compile attempt.
    // See the caller in Compile for the full rationale. The short version is
    // that a rule's compile bakes in its identity (ids, literal text,
    // normalization form), and the per-pass mutators
    // inside Compile don't re-check `_sealed`. This walk is the one check
    // that makes those passes safe. Without it, a second Compile from a new
    // root would silently corrupt a sealed or invalidated sub-rule's state.
    private static void CheckNoSealedReachableRules(Rule r, HashSet<Rule> visited)
    {
        if (!visited.Add(r)) return;
        if (r._invalidCompileReason != null)
        {
            string ruleLabel = r.Name ?? r._ruleTraceName;
            throw new InvalidOperationException(
                $"Rule '{ruleLabel}' can't be compiled because a previous Compile " +
                $"attempt failed after it had already changed these rules. Build a fresh " +
                $"grammar instance before compiling again. Previous failure: " +
                $"{r._invalidCompileReason}");
        }
        if (r._sealed)
        {
            string ruleLabel = r.Name ?? r._ruleTraceName;
            throw new InvalidOperationException(
                $"Rule '{ruleLabel}' has already been compiled and can't be reused in " +
                $"another grammar. Compile the parent first, or use factory functions for " +
                $"shared rule shapes.");
        }
        foreach (var child in r.Children)
            CheckNoSealedReachableRules(child, visited);
    }

    private void ThrowIfSealed()
    {
        if (_sealed)
            throw new InvalidOperationException("Rule has been compiled and is now sealed.");
    }
}
