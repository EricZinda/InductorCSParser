using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;

namespace InductorParser;

// Rule is the base of everything in a grammar. A grammar is a tree of Rule
// objects: composites like And/Or/OneOrMore wrap other Rules, leaves like
// Token/OneOf sit at the bottom, and the root is whatever Rule you
// hand to Parse(). Calling Parse on the root walks the tree and tries to
// match the input.
//
// Rules are instances, not types.
// In C# you build a Rule by calling factory functions (And, Or, Token, etc.)
// that return Rule instances. The tree is built at runtime, compiled once,
// and reused for every parse after that. A grammar can live anywhere a
// reference can live: a local variable, a static field, an entry in a
// dictionary, an argument passed around. The library doesn't care.
//
// Rule construction is fluent: Modifier methods like .As(name) and
// .Flatten(type) return the same Rule so it can read as a chain:
//
//     var settingName = OneOrMore(OneOf(TokenSet.Letters))
//         .As(nameof(settingName));
//
// .As(name) silently flips the rule's FlattenType to Preserve if it
// hasn't been set explicitly, so a named rule is findable by Tree.Find
// without the caller adding .Preserve() by hand. .Flatten(non-Preserve)
// after .As (or .As after .Flatten(non-Preserve)) throws, since the two
// requests contradict each other: a non-Preserve rule's wrapper Symbol
// doesn't reach the tree, so naming it for Find is meaningless.
//
// Rules are effectively immutable. Before Compile runs, you can call the
// modifier methods. After Compile runs (either explicitly via .Compile()
// or automatically on the first .Parse() call) the Rule is sealed and any
// further modification throws InvalidOperationException. A compiled rule
// is immutable, so any number of threads can Parse it at once with no
// synchronization. Compilation itself is single-threaded: build and
// compile a grammar on one thread, then share the compiled grammar. See
// docs/InductorParserReference.md "Thread Safety".
//
// Rule is abstract. The library's composite and leaf rules
// (AndRule, OrRule, GraphemeRule, etc.) subclass it. User code can subclass
// Rule too if it needs matching logic the built-in rules can't express.
// See TryParseRule below for the full subclass contract.
public abstract class Rule
{
    // See below for description
    private bool _sealed;
    private bool _idAssigned;
    // True iff the user explicitly chose this rule's SymbolId via .As(SymbolId).
    // Distinct from _idAssigned (also set by GraphemeRule's constructor when it
    // auto-assigns a single-rune Token its code point, and by Compile's named /
    // anonymous id passes). Gates the duplicate-id conflict check in
    // CollectExplicitIds, the leaf-id shortcut in ResolveLeafId, GraphemeRule's
    // post-normalization id re-assignment, and .As(string)'s auto-assigned-id
    // reset. Not set by .As(string), which only writes Name and lets Compile
    // derive an Id from the name hash.
    private bool _idUserExplicit;
    private string? _errorMessage;
    private bool _errorForced;

    // Has this rule been compiled yet? An alternative evaluator checks
    // this before calling Compile() so a caller who already compiled the
    // rule with a specific normalization form (or with null to opt out)
    // doesn't get an InvalidOperationException from the engine forcing
    // the FormC default.
    internal bool IsCompiled => _sealed;

    // The Unicode normalization form this grammar was compiled against. Set
    // by Compile(form) on every reachable rule, but only the root's value
    // matters at parse time. Default NormalizationForm.FormC matches the
    // historical default. null means "skip normalization." The form is
    // committed at first compile. A subsequent Compile call with a different
    // form throws (see Compile for the conflict check).
    //
    // NormalizationForm is a property of the grammar, not the parse, because
    // every literal-bearing rule (Token / Literal / LiteralIgnoreAsciiCase)
    // commits to a specific form the moment its expected text is written
    // into source. Switching forms between parses on the same compiled
    // grammar would silently break match behavior, so the form is locked in
    // at Compile time and validated against every literal in the graph.
    private System.Text.NormalizationForm? _normalizationForm = System.Text.NormalizationForm.FormC;

    // The Unicode normalization form this grammar was compiled against, or
    // null if normalization is disabled. Set during Compile and read by
    // Parse to normalize the input string before lexing. Public so callers
    // and tests can introspect a compiled grammar.
    public System.Text.NormalizationForm? NormalizationForm => _normalizationForm;

    // Lazily-built reverse index from SymbolId to human-readable name
    // for every rule reachable from this root. Populated on the first
    // NameOf call. Grammars that never ask never pay the allocation.
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
    // entries are indexed; the class-derived trace-name fallbacks
    // (And, OneOrMore, Token, ...) aren't, because they're not unique.
    private Dictionary<string, SymbolId>? _idByNameIndex;

    public SymbolId Id { get; private set; }
    public string? Name { get; private set; }

    // Virtual so LateBoundRule can override the getter to report its
    // bound target's FlattenType. A LateBoundRule has no flatten policy
    // of its own; see LateBoundRule.FlattenType. The private setter stays
    // non-virtual: only the base's own constructor / .As(...) / .Flatten(...)
    // use it, and none of those run on a LateBoundRule (it overrides .As
    // and .Flatten to throw).
    public virtual FlattenType FlattenType { get; private set; }

    // The static error message set via .WithError("..."), or null if none.
    // Subclasses pass this to lexer.RecordFailure on the failure path so
    // the depth-primary resolution can surface it: a failure with a message
    // is "named", one without is "mechanical".
    protected internal string? ErrorMessage => _errorMessage;

    // True when the message was set via .WithError("...", forced: true). A
    // forced failure is a hard override: it beats every non-forced failure at
    // any depth (and loses only to a deeper forced failure).
    protected internal bool ErrorForced => _errorForced;

    protected Rule(FlattenType defaultFlatten, params Rule[]? children)
    {
        FlattenType = defaultFlatten;
        Children = ValidateChildren(children);
        _ruleTraceName = DeriveRuleTraceName(GetType());
    }

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

    // Compose the full trace label: "{Name}:{ruleName}" when the rule
    // has a .As(name) set, else just "{ruleName}". Only .As() is used
    // here. .WithError() sets the user-facing error message, not a
    // rule identity, so it belongs in the trace line's body (see
    // AppendErrorMessage) rather than as a label prefix.
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
    // The body-length check drops the body-separator space when the
    // body is empty. OrRule's failure trace is the only caller that
    // hits this path (its body is $"" because there's no per-child
    // detail to surface once all alternatives failed); without the
    // check, WriteTraceLine's own ": " plus the leading space in the
    // format string would render "Or:  \"...\"" with a double space.
    private string AppendErrorMessage(string body)
    {
        if (_errorMessage == null) return body;
        return body.Length > 0
            ? $"{body} \"{_errorMessage}\""
            : $"\"{_errorMessage}\"";
    }

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

    // The child rules this rule is built from. Composites (And, Or, OneOrMore,
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
    //
    // `public` so external tools (visualizers, doc generators, tests) can
    // walk the rule graph the same way Symbol.Children lets them walk the
    // parse tree. The return type is IReadOnlyList<Rule>, so external
    // callers can read the graph but not mutate it.
    public IReadOnlyList<Rule> Children { get; private set; }

    // Replace this rule's children. The only production use is LateBoundRule,
    // which needs to install its target after construction. Throws if the
    // rule has already been compiled, so late-binding is a grammar-build-time
    // operation and no rule can sprout new children mid-parse.
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

        return children.Length > 0 ? children : NoChildren;
    }

    // Replace this rule's trace label. Intended for use only from subclass
    // constructors that need to bake construction-time parameters (like
    // BetweenInclusiveRule's bounds) into the label. No ThrowIfSealed check
    // here because at constructor time the rule isn't reachable from
    // grammar code yet, so it can't have been compiled and sealed.
    // Trace output reads _ruleTraceName as a field load, so renaming
    // here stays a one-time cost.
    protected void SetTraceName(string name) => _ruleTraceName = name;

    // Strip the "Rule" suffix so the trace label reads "And" instead
    // of "AndRule". GetType() in a base constructor returns the
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
    //
    // Identifying a rule with .As(name) means the caller wants to find it
    // later by name (Tree.Find, ParseResult lookups). For that to work the
    // rule's wrapper Symbol has to appear in the parse tree, which only
    // happens under FlattenType.Preserve. Two paths:
    //   * If the flatten policy is still the rule's class default
    //     (e.g. Token defaults to Delete, And defaults to Flatten), .As
    //     silently flips it to Preserve so the named rule is findable
    //     without the caller having to add .Preserve() by hand.
    //   * If the caller already set the policy explicitly to a non-Preserve
    //     value via .Flatten(...) / .Delete() / .Flatten(), .As throws.
    //     Honoring the explicit choice and silently overriding it would
    //     both be wrong: the caller's two requests contradict each other.
    // Virtual so subclasses (LateBoundRule) can forbid it where naming
    // would be a bug.
    //
    // Set-once on Name: a rule that already has a name from a prior
    // .As(string) call can't be renamed. The fluent API encourages
    // chaining (.As("foo").WithError("...")), and chaining looks
    // like it's building a new rule each time. But .As(name) mutates
    // the rule instance in place and returns it, so calling
    // .As(string) twice with different names against the same rule
    // instance silently makes the last call win. The bug surfaces only
    // when consumers try to dispatch by Tree.Find / Tree.Is on which
    // name matched, by which point the user has built more grammar on
    // top. A second .As(string) call throws instead.
    //
    // .As(SymbolId) writes a different field (Id, not Name) and composes
    // with .As(string): a rule with an explicit id can still pick up a
    // name and a named rule can still pick up an explicit id. Only
    // same-overload repeats are bugs, since those overwrite the field the
    // previous call set.
    public virtual Rule As(string name)
    {
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
        // single-rune Token its code point in the constructor; two
        // distinct Token('a').As(...) rules would otherwise silently share
        // the rune id and Tree.Find / NameOf couldn't distinguish them.
        // A user's explicit id via .As(SymbolId) stays put: that's what
        // _idUserExplicit checks for.
        if (_idAssigned && !_idUserExplicit)
            _idAssigned = false;
        return this;
    }

    // Set an explicit SymbolId on this Rule (for stable numbering across
    // versions, useful when serializing parse trees). Returns the same
    // Rule for fluent chaining. Throws if already compiled. Same
    // identify-implies-Preserve story as As(string): an explicit id is
    // only useful if the rule's Symbol reaches the tree to carry
    // it, so the flatten-policy auto-flip / contradiction-throw applies
    // here too. Virtual for the same reason As(string) is.
    //
    // The explicit value must land in the custom range (>= CustomRangeStart).
    // Values below that are reserved: 0..0x10FFFF for Unicode rune leaves
    // (Token('a') already carries id 0x61 by construction) and
    // 0x110000..0x1FFFFF for built-in expression ids. Giving a rule an id
    // in either reserved range produces silent identity collisions with
    // rune leaves or built-ins, since Tree.Find / Tree.Is / NameOf
    // disambiguate by integer id only.
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

    // Wrap this rule in an alias that can be given its own name. The
    // alias parses by forwarding to this rule and produces a Symbol
    // carrying the alias's own Id, with this rule's matched content as
    // that Symbol's children. Use to reuse one rule shape under several
    // names without building a factory function for each:
    //
    //     var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
    //     var year  = digitSequence.AliasedAs("year");
    //     var month = digitSequence.AliasedAs("month");
    //
    // If this rule is itself Preserve (the default for any named rule),
    // aliasing replaces this rule's Symbol with the alias's Symbol
    // rather than nesting them: this rule's identity is hidden when
    // accessed through this alias path. From other parts of the grammar
    // that use this rule directly, Tree.Find on its name still works.
    //
    // Errors: the rebadge is success-only, so it doesn't change error
    // attribution. A .WithError on this rule still fires from inside
    // it. A .WithError on the alias fires when this rule fails to match
    // anywhere within it. If both are set, the standard deepest-failure
    // tie-breaking picks the surfaced message.
    public Rule AliasedAs(string name) => new AliasRule(this).As(name);

    // Explicit-SymbolId variant of AliasedAs(string). Same semantic: a
    // fresh AliasRule wrapping this one, with the explicit id on the alias.
    public Rule AliasedAs(SymbolId id) => new AliasRule(this).As(id);

    // Shared path for both .As(string) and .As(SymbolId): the caller is
    // identifying this rule so it can be found later. Either auto-flip a
    // default flatten policy to Preserve, or fail if the caller already
    // set a contradicting non-Preserve policy.
    private void ApplyIdentificationFlattenPolicy(string callerMethod, string identifier)
    {
        if (FlattenType == FlattenType.Preserve) return;
        if (_flattenPolicyExplicitlySet)
        {
            throw new InvalidOperationException(
                $".{callerMethod}(\"{identifier}\") can't be applied to this rule: " +
                $"its flatten policy was explicitly set to FlattenType.{FlattenType}, " +
                $"so its wrapper Symbol won't appear in the parse tree and Tree.Find " +
                $"can't reach it. Set the flatten policy to FlattenType.Preserve, or " +
                $"remove the .{callerMethod}(...) call.");
        }
        FlattenType = FlattenType.Preserve;
    }

    // Set by .As(SymbolId) and only by .As(SymbolId). Two consumers,
    // both checking for "user explicitly identified this rule by id":
    //   * Auto-assignment sites (SetIdInternal callers like
    //     GraphemeRule.CollectNormalizationOffenders) skip the
    //     auto-assignment so a user's explicit id survives normalization.
    //   * Leaf-emitting rules with the rune-as-leaf-id optimization
    //     (OneOfRule / NoneOfRule / AnyTokenRule / WithinTokenRule, via
    //     ResolveLeafId below) skip the optimization so leaves carry the
    //     user's explicit id and Tree.Find / Tree.Is resolve through the
    //     user's reference.
    // Parallel to Name (set by .As(string)) for the second consumer:
    // either user-identification path disables the rune-as-leaf-id
    // shortcut.
    internal bool IsUserSymbolIdExplicit => _idUserExplicit;

    // The leaf-id rule for OneOfRule / NoneOfRule / AnyTokenRule /
    // WithinTokenRule. A truly anonymous single-rune match carries the
    // rune's code point as its leaf id, so tree consumers can dispatch
    // on `leaf.Id == 'a'` without going through a synthetic per-rule id.
    // A user-identified rule (`.As(string)` sets Name, `.As(SymbolId)`
    // sets IsUserSymbolIdExplicit) carries the rule's own Id so
    // Tree.Find / Tree.Is / NameOf resolve through the user's reference.
    // A multi-rune token has runeValue == -1 and falls through to Id
    // either way, since one int can't hold a multi-rune code point.
    // Centralized here so the four leaf-emitting rules can't drift on
    // the gate.
    protected SymbolId ResolveLeafId(int runeValue) =>
        (Name == null && !IsUserSymbolIdExplicit && runeValue >= 0)
            ? new SymbolId(runeValue)
            : Id;

    // Set the flatten policy (Preserve / Delete / Flatten) that controls
    // how this rule contributes to the parse tree on a successful match.
    // See the FlattenType enum for what each value means. Returns the same
    // Rule for fluent chaining. Throws if already compiled. Virtual so
    // LateBoundRule can forbid it (a FlattenType set on a transparent
    // forwarding rule is never consulted and would silently do nothing).
    //
    // Setting a non-Preserve policy on a rule that's already been
    // identified with .As(name) or .As(SymbolId) throws: identification
    // and "this rule's wrapper Symbol is absent from the tree" contradict
    // each other, since the whole point of .As is to make the wrapper
    // findable. Setting Preserve, or any policy on an unidentified rule,
    // is fine.
    public virtual Rule Flatten(FlattenType type)
    {
        CheckFlattenChangeAllowed(type, nameof(Flatten));
        FlattenType = type;
        _flattenPolicyExplicitlySet = true;
        return this;
    }

    // Shared precondition for Flatten and FlattenByDefault: a rule
    // identified with .As(name) / .As(SymbolId) can only be Preserve.
    // Flatten or Delete drops its node from the parse tree, leaving
    // nothing for Tree.Find to return.
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

    // True once the caller has called .Flatten(...), .Preserve(), .Delete(),
    // or .Flatten(). Stays false while the rule still carries its class
    // default. .As(name) checks this to decide whether to silently flip
    // FlattenType to Preserve (default still in place) or throw (caller
    // already set a contradicting non-Preserve policy).
    private bool _flattenPolicyExplicitlySet;

    // Convenience shortcuts for the three FlattenType values. These read
    // better than .Flatten(FlattenType.X) at calls that otherwise
    // chain several modifiers, e.g. Literal("abc").Preserve() vs
    // Literal("abc").Flatten(FlattenType.Preserve) for a literal whose
    // default policy is Delete. All three forward to Flatten(FlattenType),
    // so LateBoundRule's override that forbids setting a flatten policy
    // still fires here.
    public Rule Preserve() => Flatten(FlattenType.Preserve);
    public Rule Delete() => Flatten(FlattenType.Delete);
    public Rule Flatten() => Flatten(FlattenType.Flatten);

    // Set the FlattenType as an overridable default rather than an
    // explicit, locked-in choice. The method exists for writing
    // composing factories: a factory that wraps Or / And / OneOrMore
    // (whose class default is FlattenType.Flatten) but wants its result
    // to default to Delete should call FlattenByDefault(FlattenType.Delete)
    // rather than Flatten(FlattenType.Delete).
    //
    // The difference is what happens to the factory's caller. .Flatten(...)
    // records the policy as user-explicit, so a later .As(name) on the
    // returned rule throws ("flatten policy was explicitly set..."). The
    // factory's caller never wrote .Flatten / .Delete and can't anticipate
    // that. FlattenByDefault sets the same FlattenType value but leaves the
    // policy overridable, so .As(name) and a later .Flatten(...) behave
    // exactly as they would on a rule still carrying its class default.
    //
    // The built-in composing factories (Rules.EndOfLine, InlineWhitespace,
    // AnyWhitespace) use this; user-written factories that compose rules
    // and want a non-default flatten policy should use it for the same
    // reason. Virtual so LateBoundRule can forbid it, matching .Flatten.
    public virtual Rule FlattenByDefault(FlattenType type)
    {
        CheckFlattenChangeAllowed(type, nameof(FlattenByDefault));
        FlattenType = type;
        return this;
    }

    // Attach a static error message. If this rule's message wins the
    // depth-primary resolution when a parse fails, ParseResult.ErrorMessage
    // will be this string instead of the generic "unexpected 'x'"
    // fallback. Useful for giving user-friendly messages like "Expected a
    // setting name" at the spots most likely to be where the author went
    // wrong.
    //
    // A rule with no .WithError produces a "mechanical" failure (position
    // only). .WithError("msg") produces a "named" failure. .WithError("msg",
    // forced: true) produces a "forced" failure. At error-report time:
    //   1. A forced failure overrides everything, at any depth.
    //   2. Otherwise the deepest failure wins, named and mechanical alike.
    //   3. At an exact-depth tie a named failure beats a mechanical one;
    //      a same-kind tie goes to the first recorded.
    //
    // So .WithError chooses the words and tips an exact-depth tie, but it
    // never pulls the reported position off the parser's deepest failure.
    // See docs/ErrorArchitecture.md.
    //
    // Returns the same Rule for fluent chaining. Throws if already
    // compiled. Virtual so LateBoundRule can forbid it (a WithError set
    // on a transparent forwarding rule is never consulted and would
    // silently do nothing).
    public virtual Rule WithError(string errorMessage, bool forced = false)
    {
        if (errorMessage == null) throw new ArgumentNullException(nameof(errorMessage));
        ThrowIfSealed();
        if (_errorMessage != null)
            throw new InvalidOperationException(
                $".WithError(\"{errorMessage}\") can't be applied to this rule: " +
                $"it already has the error message \"{_errorMessage}\". " +
                $".WithError(...) is set-once. To attach a different error to the " +
                $"same rule shape at multiple call sites, wrap it with Alias(...) " +
                $"to get a fresh wrapper that can carry its own error: " +
                $"Alias(rule).WithError(\"{errorMessage}\"). Or build a factory " +
                $"function that returns a fresh rule each call.");
        _errorMessage = errorMessage;
        _errorForced = forced;
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
    //   1. Explicit ids first. Rules that called .As(SymbolId) keep the id
    //      they were given, and that id is reserved against later passes.
    //      Two reachable rules given the same explicit SymbolId are
    //      rejected here with a clear error, since downstream lookups by
    //      raw SymbolId (parse-tree walking, NameOf) can't disambiguate
    //      duplicate ids.
    //   2. Named rules get a hash-of-name id in the custom range. If the
    //      hash slot is already taken (by an explicit id or an earlier named
    //      rule), the id linear-probes upward until it finds an empty
    //      slot. Same name produces the same hash slot every run, so a
    //      rule's id is stable across program executions in the absence
    //      of grammar changes.
    //   3. Anonymous rules get sequential ids starting at CustomRangeStart,
    //      probing upward past anything already claimed by passes 1 and 2.
    //
    // Then a validation pass asks each rule whether it's well-formed, and
    // a final pass seals the graph against further mutation.
    public Rule Compile() => Compile(System.Text.NormalizationForm.FormC);

    // Compile with an explicit normalization form. Pass null to opt out of
    // normalization entirely. The form is committed at first compile and
    // applies to every parse afterward. A subsequent Compile call with a
    // different form throws InvalidOperationException; the form is part of
    // the grammar's identity, not a per-parse knob.
    //
    // Validation: when form is non-null, every reachable Token / Literal /
    // LiteralIgnoreAsciiCase rule's expected text is checked against its
    // normalization in the chosen form. If any literal isn't already in
    // that form, Compile throws with a message listing every offender and
    // showing the suggested normalized text. This catches the silent
    // "rule never matches" failure mode where an author wrote a decomposed
    // 'é' but the grammar will run against FormC-normalized input that
    // only ever produces the precomposed 'é' as a token.
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

        // Every rule reachable from this root has to be unsealed. Compile
        // commits each rule's id, FirstConsumedTokens, projected literal
        // text, and normalization form, and the per-pass mutators don't
        // re-check `_sealed`; walking into one already-sealed rule would
        // silently corrupt its previous compile's state. Trade-off: a
        // static sub-rule shared across grammars stops working once one
        // of them has been Parsed or Compiled. Expose shared shapes via
        // factory functions that return a fresh instance per call.
        var freshTreeVisited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CheckNoSealedReachableRules(this, freshTreeVisited);

        var usedIds = new HashSet<int>();
        var explicitRules = new Dictionary<int, Rule>();
        var namedRules = new Dictionary<string, Rule>();

        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CollectExplicitIds(this, visited, usedIds, explicitRules);

        visited.Clear();
        CheckNameUniqueness(this, visited, namedRules);

        visited.Clear();
        AssignNamedIds(this, visited, usedIds);

        visited.Clear();
        int nextAnon = SymbolRanges.CustomRangeStart;
        AssignAnonymousIds(this, visited, usedIds, ref nextAnon);

        visited.Clear();
        ValidateAll(this, visited);

        // Validate every literal-bearing rule against the chosen normalization
        // form. Skipped when normalizeInput is null (the author opted out).
        // Throws one InvalidOperationException listing every offender so
        // grammar authors fix all mismatches in one pass instead of one at
        // a time. The pass mutates literal-bearing rules' stored text
        // (Token / Literal / LiteralIgnoreAsciiCase) and OneOf / NoneOf
        // sets when the original entries aren't already in the chosen
        // form.
        if (normalizeInput.HasValue)
        {
            var offenders = new List<(Rule rule, string original, string normalized)>();
            // Captures any ArgumentException string.Normalize throws for
            // literals it can't normalize (in practice, unpaired surrogates).
            // We aggregate these as InnerException on the thrown
            // InvalidOperationException so a programmatic caller can walk
            // the runtime causes; the user-facing message stays the
            // multi-rule offender list BuildNormalizationErrorMessage emits.
            var normalizeFailures = new List<ArgumentException>();
            visited.Clear();
            CollectNormalizationOffendersAll(this, visited, normalizeInput.Value, offenders, normalizeFailures);
            if (offenders.Count > 0)
            {
                Exception? inner = normalizeFailures.Count > 0
                    ? new AggregateException(normalizeFailures)
                    : null;
                throw new InvalidOperationException(
                    BuildNormalizationErrorMessage(normalizeInput.Value, offenders),
                    inner);
            }
        }

        visited.Clear();
        SealAll(this, visited);

        // Stamp the form onto every reachable rule so any subsequent
        // Compile call (which can land on any rule, not just the original
        // root) sees the form for its conflict check. Only the root's
        // value is read at parse time.
        visited.Clear();
        StampNormalizationForm(this, visited, normalizeInput);

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
    //      class-derived trace name ("And", "OneOrMore", "Token",
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

    private Dictionary<SymbolId, (string Name, bool IsUserSupplied)> BuildNameIndex()
    {
        var map = new Dictionary<SymbolId, (string Name, bool IsUserSupplied)>();
        var visited = new HashSet<Rule>(ReferenceComparer<Rule>.Instance);
        CollectNames(this, visited, map);
        return map;
    }

    // Inverse of NameOf: takes the string the rule was constructed with
    // via .As("name") and returns the SymbolId the engine assigned.
    // Returns null if no reachable rule has that name. Auto-compiles
    // the grammar (ids aren't stable until Compile runs) so callers
    // can cache the id at static-init time without worrying about
    // ordering relative to the first Parse.
    //
    // Typed-AST projections use this to dispatch by name without
    // having to expose one public static Rule field per named
    // production on the grammar class:
    //
    //     private static readonly SymbolId NumberId =
    //         MyGrammar.Root.IdOf("number")!.Value;
    //     ...
    //     if (child.Id == NumberId) ProjectNumber(...);
    //
    // Equivalent to Symbol.Is(string), but faster on the hot path of
    // a tree walker (SymbolId compare is an int compare, no string
    // intern lookup per node). Use IdOf when you'll dispatch on the
    // same name in a loop; use Symbol.Is(string) when readability
    // matters more.
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
    // names go in; class-derived trace names are skipped because they
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

    // Populate the reverse index by walking the sealed rule graph once.
    // Each entry tracks both the resolved name (the user-supplied .As
    // name when set, otherwise the class-derived trace name) and whether
    // the user supplied it. NameOf reads the flag to decide whether the
    // entry should override the rune-string default for character-range
    // ids. A user-supplied name on one rule wins over a trace-name
    // fallback on a different rule that happens to share the same id
    // (single-rune Tokens use the rune's code point as their Id, so
    // multiple Token rules in the same grammar share an id).
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

    // Parse: the main entry point for running a grammar
    //
    // Run the grammar against an input string. Auto-compiles on first call.
    // Pass options explicitly to change parse-time settings.
    public ParseResult Parse(string input) => Parse(input, new ParseOptions());

    // Dispatcher. Routes to the recursive evaluator by default. An
    // alternative-evaluator implementation can register itself by
    // assigning AlternativeEvaluator at startup. The test suite can
    // flip the routing process-wide via
    // ParseOptions.DefaultUseAlternativeEvaluator, or per-call via
    // ParseOptions.UseAlternativeEvaluator, so the same fixtures can
    // run through either engine without per-test rewrites. The flag
    // without a registered hook is inert: a recursive-only build
    // always hits ParseRecursive regardless of the flag.
    public ParseResult Parse(string input, ParseOptions options)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (options == null) throw new ArgumentNullException(nameof(options));

        // The in-loop budget check fires every 1024 rule invocations,
        // so a parse smaller than that would silently drop a
        // pre-canceled signal. Pre-flight it here.
        if (options.Cancellation != null && options.Cancellation.IsCanceled)
        {
            string message = BuildBudgetMessage(ParseOutcome.Canceled, abortPos: 0, input, options);
            return ParseResult.Aborted(ParseOutcome.Canceled, errorCharIndex: 0, message, input, this);
        }
        return options.ResolveUseAlternativeEvaluator() && AlternativeEvaluator is { } hook
            ? hook(this, input, options)
            : ParseRecursive(input, options);
    }

    // Module-wide alternative-evaluator hook. Set by an alternative engine
    // implementation at startup (typically from a test fixture's
    // OneTimeSetUp). Null in a recursive-only build, which is the default.
    // The hook receives the rule, the input, and the same ParseOptions
    // the caller passed to Parse, and returns the same ParseResult shape
    // ParseRecursive would.
    internal static Func<Rule, string, ParseOptions, ParseResult>? AlternativeEvaluator;

    // The recursive evaluator's body. Compare fixtures that need a
    // guaranteed recursive-engine baseline (so an alternative evaluator's
    // run can compare its own output against a stable control) call this
    // directly instead of going through Parse.
    internal ParseResult ParseRecursive(string input, ParseOptions options)
    {
        // Auto-compile preserves whatever form the grammar is already
        // compiled with. If the caller hasn't compiled yet, fall back
        // to the FormC default that matches Compile()'s zero-argument
        // overload. This avoids a spurious form-conflict throw when a
        // grammar was explicitly compiled with a non-FormC form (or
        // null) and then parsed without re-specifying it.
        if (!_sealed) Compile();

        // Normalize before the lexer sees the input so grammars written
        // against one composition form also match the other. The common
        // case (input already in the target form, essentially all typed
        // and web-sourced text) is usually just a normalization scan. If
        // normalization returns the original string reference, downstream position
        // translation is skipped. Null means "skip normalization entirely,"
        // which trades the safety net for character-exact round-trippability.
        // The form was committed at Compile time and is part of the
        // grammar's identity; see Compile(NormalizationForm?) for the rationale.
        NormalizationForm? normalizeInput = _normalizationForm;
        string parseInput = normalizeInput.HasValue
            ? input.Normalize(normalizeInput.Value)
            : input;

        // Per-parse context every Symbol the engine builds will hold
        // a reference to. Lets Symbol.SourceRange / Symbol.SourceText
        // translate parseInput offsets back to original-input
        // coordinates without the consumer having to thread the
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
            // The throw rode up through every active rule's transaction
            // (the `using var transaction` in Rule.TryParse), which
            // rolled the lexer position back frame by frame, so
            // lexer.Position is now back at 0. An active lookahead Probe
            // also restores the failure tracker on its way out (see
            // Lexer.Probe), so lexer.DeepestFailurePosition would no longer
            // reflect how far the probe explored either. Lexer.ThrowBudgetExceeded
            // freezes the "how far did the parser get" reading at throw
            // time (Math.Max(DeepestFailurePosition, Position) before any
            // restoration runs) and parks it on the exception, so we
            // read it back unchanged here.
            int abortRaw = budget.DeepestPositionAtAbort;
            int abortPos = NormalizedPositionMap.TranslateToOriginal(input, parseInput, abortRaw, normalizeInput);
            return ParseResult.Aborted(budget.Outcome, abortPos, BuildBudgetMessage(budget.Outcome, abortPos, input, options), input, this);
        }
        if (result == null && rootList.Count == 0)
        {
            var pos = Math.Max(lexer.DeepestFailurePosition, lexer.Position);
            int failurePos = NormalizedPositionMap.TranslateToOriginal(input, parseInput, pos, normalizeInput);
            return ParseResult.Failed(failurePos, BuildErrorMessage(lexer.DeepestFailureMessage, pos, parseInput, failurePos, input, options), input, this);
        }
        if (!options.AllowTrailingInput && !lexer.IsEof)
        {
            // Trailing-input branch: the parse SUCCEEDED but the rule
            // didn't claim everything. Report at lexer.Position (the
            // start of the unconsumed tail), not the high-water
            // DeepestFailurePosition that the two branches above use.
            // Position is meaningful and DeepestFailurePosition is
            // from a sibling alternative the parser deliberately
            // abandoned. Same reason for passing customMessage: null
            // instead of DeepestFailureMessage. A WithError on a
            // rolled-back rule isn't relevant to "you have leftover
            // input"; let the standard PositionalErrorTemplate
            // describe the trailing tail.
            int pos = lexer.Position;
            int failurePos = NormalizedPositionMap.TranslateToOriginal(input, parseInput, pos, normalizeInput);
            return ParseResult.Failed(failurePos, BuildErrorMessage(customMessage: null, pos, parseInput, failurePos, input, options), input, this);
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

    // Every engine ends up here for the generic-failure path so default
    // error messages stay consistent and ParseOptions templates apply
    // uniformly. customMessage is the deepest-failure message the engine
    // recorded (lexer.DeepestFailureMessage on the recursive path).
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
        // Default path: the parse failed at a real character in the input.
        // Render the positional template with the offending character and
        // its position. {character} reads from the original input rather
        // than parseInput so the message quotes what the user typed, even
        // when normalization rewrote it (FormKC folds fullwidth, ligatures,
        // and math letters to ASCII; the user is still looking at the
        // unfolded form). The EOF guard above ensures failurePos is in
        // range for the GetNextTextElement call.
        return FormatTemplate(options.PositionalErrorTemplate,
            PositionPlaceholders(failurePos, input),
            ("character", () => StringInfo.GetNextTextElement(input, failurePos)));
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
        lexer.Budget.EnterRule();
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

            Symbol? result;
            if (OpensTransaction)
            {
                // The rule-owned outer transaction is automatic. Opening
                // it here instead of in every TryParseRule means a
                // subclass can't forget the Commit on its success path:
                // Rule.TryParse commits iff TryParseRule returns a
                // non-null Symbol, and every non-commit exit (failure
                // return, a thrown exception, a tripped budget) rolls
                // back through the `using`. The transaction opens inside
                // this `try`, after Budget.EnterRule, so a depth-limit
                // throw from EnterRule can't leak a transaction.
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
            // get normalized to Discarded so callers can rely on the
            // contract (only FlattenType.Preserve returns a wrapper Symbol).
            if (effectiveFlattenType != FlattenType.Preserve)
                return Symbol.Discarded;
            return result;
        }
        finally
        {
            lexer.Budget.ExitRule();
        }
    }

    // The matching method every subclass implements. Contract:
    //   * Rule.TryParse owns the outer transaction. It opens one before
    //     calling this method and commits it iff this method returns a
    //     non-null Symbol, so a subclass never calls BeginTransaction or
    //     Commit for its own outer scope and can't forget the commit.
    //     `startPosition` is the lexer position captured the moment that
    //     transaction opened. Use it for failure anchors and for the
    //     ReadOnlyMemory span of any Symbol you build.
    //   * On failure: return null. Rule.TryParse's transaction rolls the
    //     lexer back automatically, and Rule.TryParse truncates any
    //     partial writes to outputSymbols for you. Call
    //     lexer.RecordFailure() (or lexer.RecordCompositeFailure() for a
    //     composite's own .WithError) so the "deepest failure wins"
    //     error-reporting heuristic can surface your rule's message.
    //   * On success: return a non-null Symbol. What exactly you return
    //     depends on `effectiveFlattenType`:
    //       - Delete: emit nothing, return Symbol.Discarded.
    //       - Flatten: append each Symbol you would have collected to
    //         `outputSymbols` (the caller's list, guaranteed non-null)
    //         and return Symbol.Discarded. For a composite, that's each
    //         matched child's Symbol. For a leaf, that's the leaf Symbol
    //         itself.
    //       - Preserve: build a wrapper Symbol around your matched
    //         children (or leaf content) and return it.
    //   * For speculative lookahead inside the rule (positive or negative
    //     lookahead, probing a stopper), open a lexer.BeginProbe() rather
    //     than a transaction: a Probe restores the failure tracker and the
    //     subtree-extent mark on rollback as well as the position, so an
    //     off-path probe failure can't leak into error reporting.
    //   * `outputSymbols` is the caller's list in Flatten mode. It's
    //     non-null by contract (callers of Flatten rules are required to
    //     provide one), and null otherwise.
    //   * Subclass construction: pass child rules to the base constructor
    //     via `base(flattenType, children)`. The `Children` property is
    //     populated automatically and Compile walks it to assign ids and
    //     seal the graph.
    // `protected internal` so external Rule subclasses can override this. The
    // `internal` half preserves every existing caller (the built-in
    // composite rules and Rule.TryParse itself). The `protected` half is what
    // makes the abstract member visible to subclasses in other assemblies.
    // See src/InductorParser.ExternalContractTests for an external subclass
    // that exercises this.
    protected internal abstract Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols);

    // Whether Rule.TryParse opens an automatic outer transaction around
    // this rule's TryParseRule. True for every rule that speculatively
    // reads input, which is almost all of them: a rule that reads tokens
    // and then fails must be able to roll back. EofRule and LateBoundRule
    // set this false in their constructors. Eof never moves the cursor,
    // and LateBound delegates wholly to its target rule, which owns its
    // own transaction. Skipping the transaction for those two keeps the
    // recursion hot path (LateBound sits at every recursive grammar
    // reference) free of a transaction it would never use.
    //
    // A plain field, not a virtual property: Rule.TryParse reads it on
    // every rule invocation, so a virtual dispatch there would be
    // hot-path overhead. A field read plus a well-predicted branch is
    // effectively free.
    //
    // `protected internal` so external Rule subclasses can also opt out
    // of the auto-managed transaction when they own their own probe /
    // transaction scope. The default (true) is what almost every shape
    // wants.
    protected internal bool OpensTransaction = true;

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

    // The user-supplied literal text this rule matches against, exposed
    // on the base so every consumer that wants "the literal" reads from
    // one polymorphic spot instead of switching on rule type. Default
    // null means "this rule has no literal text" (composites, zero-width
    // predicates, OneOf / NoneOf which carry rune sets, etc.).
    // GraphemeRule, LiteralRule, and LiteralIgnoreAsciiCaseRule override
    // to return their expected text. Used by BetweenInclusiveRule's
    // scanner-skip optimization, which collects literal candidates for
    // the substring-search fast path.
    // Compile's normalization-form validation does NOT read this. It
    // calls CollectNormalizationOffenders below instead, which lets each
    // rule (including OneOf / NoneOf with set entries that aren't a
    // single string) validate its own data shape.
    internal virtual string? ExpectedText => null;

    // Subclass hook for Compile-time normalization-form validation. Each
    // rule that holds user-supplied text the parser will compare against
    // normalized input overrides this to walk its own data and add an
    // offender (or an ArgumentException to failures) for any text whose
    // normalization differs from the chosen form. Default no-op covers
    // composites, zero-width predicates, and any rule whose match doesn't
    // depend on stored fixed text. Compile's static walker (below) calls
    // this on every reachable rule and recurses into Children. See
    // backlog n4kp for the form-check shape and the user-visible error
    // message.
    internal virtual void CollectNormalizationOffenders(
        NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
    {
        // default no-op
    }

    // Helper for rules that have one fixed expected string. Tries to
    // convert `text` to `form`. Returns the normalized text on success.
    // On ArgumentException (in practice an unpaired surrogate, which
    // string.Normalize rejects regardless of which form was requested),
    // captures the exception in `failures` and adds a synthetic offender
    // entry that points at Compile(null), then returns null. Callers that
    // get a non-null result should replace their stored expected text
    // with it; the auto-convert behavior makes the rule's match-time view
    // canonically equivalent to the user's typed text under any form.
    protected static string? TryConvertToForm(
        Rule rule, string text,
        NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
    {
        try
        {
            return text.Normalize(form);
        }
        catch (ArgumentException exception)
        {
            failures.Add(exception);
            offenders.Add((rule, text,
                $"<string.Normalize rejected this literal: {exception.Message} " +
                $"This is usually an unpaired surrogate. Use Compile(null) to keep " +
                $"surrogate-bearing literals as-is.>"));
            return null;
        }
    }

    internal void SetIdInternal(SymbolId id)
    {
        Id = id;
        _idAssigned = true;
    }

    // Pass 1. Walk the graph and stash any user-set explicit ids so the
    // later passes know which slots are off limits, and reject two
    // reachable rules whose .As(SymbolId) explicit ids land on the same id
    // with a compile-time error that names both rules.
    //
    // The gate is `_idUserExplicit`, set only by `.As(SymbolId)` (and
    // persisting through sealing). `As(SymbolId)` itself enforces that
    // explicit ids land in the custom range, so reaching this method with
    // `_idUserExplicit=true` already implies a custom-range id. Two kinds
    // of assigned ids correctly fall through to just reserving the slot
    // in `usedIds`:
    //
    //   * Rune-range pre-assigned ids. Every single-rune Token has its
    //     code point assigned in its constructor, not by the user. A
    //     grammar that mentions Token('a') twice has two rules sharing id
    //     97 by design; NameOf short-circuits the rune range to the rune
    //     string and there's no name ambiguity to resolve.
    //
    //   * Custom-range ids stamped by the anonymous pass. A sub-rule
    //     that was compiled standalone and is now reached from a larger
    //     grammar has `_idAssigned=true` but `_idUserExplicit=false`, so
    //     it doesn't compete for the conflict slot.
    private static void CollectExplicitIds(Rule r, HashSet<Rule> visited, HashSet<int> usedIds, Dictionary<int, Rule> explicitRules)
    {
        if (!visited.Add(r)) return;
        if (r._idAssigned)
        {
            int idValue = r.Id.Value;
            if (r._idUserExplicit && explicitRules.TryGetValue(idValue, out var existing))
            {
                throw new InvalidOperationException(
                    $"Two reachable rules use the explicit SymbolId({idValue}): " +
                    $"'{DescribeExplicitRule(existing)}' and '{DescribeExplicitRule(r)}'. " +
                    $"Each .As(new SymbolId(...)) explicit id must be unique within a grammar.");
            }
            if (r._idUserExplicit)
                explicitRules[idValue] = r;
            usedIds.Add(idValue);
        }
        foreach (var child in r.Children)
            CollectExplicitIds(child, visited, usedIds, explicitRules);
    }

    private static string DescribeExplicitRule(Rule r) => r.Name ?? r._ruleTraceName;

    // Reject grammars where two distinct reachable rules share an .As(string)
    // name. A name is meant to identify a single rule in NameOf, parse-tree
    // lookups, and trace output, so duplicates would silently make those
    // resolutions ambiguous. This is the parallel of the explicit-id
    // collision check in CollectExplicitIds, just for names instead of SymbolIds.
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

    private static void SealAll(Rule r, HashSet<Rule> visited)
    {
        if (!visited.Add(r)) return;
        r._sealed = true;
        foreach (var child in r.Children)
            SealAll(child, visited);
    }

    // Walk the rule graph and ask each rule to validate its own user-
    // supplied text against the chosen normalization form. Each rule
    // overrides the instance-level CollectNormalizationOffenders to do
    // the right thing for its own data shape: literal-bearing rules
    // normalize one fixed string, set-bearing rules walk their entries,
    // composites no-op (this walker recurses into Children separately).
    // Records every offender; the caller throws one combined exception.
    private static void CollectNormalizationOffendersAll(
        Rule r,
        HashSet<Rule> visited,
        NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> normalizeFailures)
    {
        if (!visited.Add(r)) return;
        r.CollectNormalizationOffenders(form, offenders, normalizeFailures);
        foreach (var child in r.Children)
            CollectNormalizationOffendersAll(child, visited, form, offenders, normalizeFailures);
    }

    // Build the multi-rule error message. One header line names the form,
    // then one line per offender with the rule's display name, the
    // original literal, and the suggested normalized form. Authors copy
    // the suggested text back into source to fix every offender in one
    // edit pass.
    private static string BuildNormalizationErrorMessage(
        NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders)
    {
        var builder = new StringBuilder();
        builder.Append("Compile failed: ")
               .Append(offenders.Count)
               .Append(offenders.Count == 1 ? " rule has" : " rules have")
               .Append(" expected text that isn't in ")
               .Append(FormatNormalizationForm(form))
               .AppendLine(". The parser normalizes input to this form before")
               .AppendLine("matching, so a rule whose expected text is in a different form will")
               .AppendLine("never match. Use the suggested form below or pass a different");
        builder.AppendLine("normalization form to Compile (or null to disable normalization):");
        foreach (var (rule, original, normalized) in offenders)
        {
            string displayName = rule.Name ?? rule._ruleTraceName;
            string ruleType = rule.GetType().Name;
            builder.Append("  - ")
                   .Append(displayName)
                   .Append(" (")
                   .Append(ruleType)
                   .Append("): ")
                   .Append(FormatLiteralForError(original))
                   .Append(" should be ")
                   .Append(FormatLiteralForError(normalized))
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
            if (SurrogateHelpers.IsSurrogatePairAt(literal, index))
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
    // Compile call landing on any rule in the graph can run its
    // conflict check. Only the root's value is read at parse time.
    private static void StampNormalizationForm(Rule r, HashSet<Rule> visited, NormalizationForm? form)
    {
        if (!visited.Add(r)) return;
        r._normalizationForm = form;
        foreach (var child in r.Children)
            StampNormalizationForm(child, visited, form);
    }

    // Walk the graph and reject this Compile if any reachable rule has
    // already been compiled. See the caller in Compile for the full
    // rationale. The short version is that a rule's compile bakes in
    // its identity (ids, FirstConsumedTokens, projected literal text,
    // normalization form), and the per-pass mutators inside Compile
    // don't re-check `_sealed`. This walk is the one check that makes
    // those passes safe. Without it, a second Compile from a new root
    // would silently corrupt the sealed sub-rule's state.
    private static void CheckNoSealedReachableRules(Rule r, HashSet<Rule> visited)
    {
        if (!visited.Add(r)) return;
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
