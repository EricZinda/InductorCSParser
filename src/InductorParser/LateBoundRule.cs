using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Maintainer notes. The user-facing story is in the doc comment below.
//
// Transparency is implemented by forwarding: TryParseRule hands the
// parse straight to the target, and the FlattenType property reports
// the target's, resolved through any chain of LateBoundRules and unnamed
// aliases (the other transparent stand-in, see AliasRule) to the first
// concrete rule. That's what makes `Alias(lateBound)`, `And(x,
// lateBound, y)`, and every other composition behave exactly as if the
// target rule were written in the LateBoundRule's place, with no
// placeholder layer and no special case.
//
// LateBoundRule is the exception to universal requirement #5: it
// rejects .As / .Flatten / .WithError always, not just after Compile.

/// <summary>
/// A placeholder for a rule you can't reference yet. Use it when two rules
/// refer to each other, so that no declaration order lets both see the
/// other. Build the grammar with the placeholder in the recursive spot,
/// then attach the real rule with <see cref="Bind">LateBoundRule.Bind(Rule)</see>.
/// </summary>
/// <remarks>
/// The problem it solves shows up in any grammar with mutual recursion:
/// <code>
/// Term       = Integer | '(' Expression ')'
/// Expression = Term ('+' Term)*
/// </code>
/// Expression refers to Term and Term refers to Expression. C# initializes
/// static fields top to bottom, so whichever rule is declared first sees
/// the other as null. A LateBoundRule stands in for the missing rule while
/// the grammar is built and gets its target attached afterward:
/// <code>
/// static readonly LateBoundRule Expression = new LateBoundRule("expression");
/// static readonly Rule Term = Or(Integer(), And(Token('('), Expression, Token(')')));
/// static readonly Rule Sum  = And(Term, ZeroOrMore(And(Token('+'), Term)));
/// static readonly Rule _init = Expression.Bind(Sum);
/// </code>
/// Term sees Expression as a valid (but still unbound) rule at construction
/// time. The last field exists only to run the <see cref="LateBoundRule.Bind">LateBoundRule.Bind</see> call at type
/// initialization, after all three rules exist. <see cref="LateBoundRule.Bind">LateBoundRule.Bind</see> returns the
/// LateBoundRule, which is a Rule, so the field can be typed either way.
/// <para>
/// A LateBoundRule is transparent. At parse time it forwards to its target,
/// and the parse tree shows the target's Symbol, never the placeholder's.
/// Its <see cref="FlattenType">LateBoundRule.FlattenType</see> and <see cref="EmitsLeaf">LateBoundRule.EmitsLeaf</see> come from the
/// target too, so <c>Alias(lateBound)</c>, <c><see cref="Rules.And">Rules.And</see>(x, lateBound, y)</c>, and
/// every other composition behave exactly as if the target were written in
/// its place. For the same reason the fluent modifiers don't apply to it:
/// <see cref="As(string)">LateBoundRule.As(string)</see>, <see cref="Flatten(FlattenType)">LateBoundRule.Flatten(FlattenType)</see>, and
/// <see cref="WithError(string, bool)">LateBoundRule.WithError(string, bool)</see> throw, and you set those on the
/// target rule instead. The name passed to the constructor is only a label
/// for trace output and error messages.
/// </para>
/// <para>
/// <see cref="Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> (or the first <see cref="Rule.Parse(string)">Rule.Parse</see>, which compiles for you)
/// throws if a LateBoundRule was never bound, naming it by that label, so
/// a forgotten <see cref="LateBoundRule.Bind">LateBoundRule.Bind</see> fails at startup instead of as a null reference deep
/// inside a later parse.
/// </para>
/// </remarks>
public sealed class LateBoundRule : Rule
{
    private readonly string? _debugName;
    private Rule? _target;

    /// <summary>
    /// Create an unbound placeholder. <paramref name="debugName"/> is a label
    /// for trace output and error messages, such as "Rule 'expression' is a
    /// LateBoundRule that was never bound". It isn't a findable name: no
    /// Symbol in the parse tree ever refers to the placeholder. To find the
    /// bound shape by name, name the target rule with <c><see cref="Rule.As(string)">Rule.As</see>(name)</c>.
    /// </summary>
    // base(FlattenType.Flatten) only seeds the base class's backing
    // field. The FlattenType property below overrides the getter to
    // report the target's value, so this seed is never read.
    public LateBoundRule(string? debugName = null) : base(FlattenType.Flatten, emitsLeaf: false)
    {
        _debugName = debugName;
        // LateBoundRule delegates wholly to its target, which opens and
        // owns its own transaction. A transaction here would be redundant,
        // and skipping it keeps the recursion hot path (a LateBoundRule
        // sits at every recursive grammar reference) free of an unused
        // transaction.
        OpensTransaction = false;
    }

    /// <summary>
    /// Attach the real rule. Returns this LateBoundRule so the call can sit
    /// in a field initializer that runs at type initialization, after every
    /// rule it depends on exists (see the class remarks for the pattern).
    /// Set-once: a second call throws, the same way <c><see cref="Rule.As(string)">Rule.As</see></c> and
    /// <c><see cref="Rule.WithError">Rule.WithError</see></c> do on any rule.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// This placeholder is already bound, or the grammar containing it has
    /// already been compiled.
    /// </exception>
    public LateBoundRule Bind(Rule target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (_target != null)
            throw new InvalidOperationException(
                $".Bind(...) can't be applied to this rule: it was already " +
                $"bound to '{_target.Name ?? _target.GetType().Name}'. " +
                $".Bind is set-once. To reuse a LateBoundRule under a " +
                $"different target, build a factory function that returns " +
                $"a fresh LateBoundRule each call.");
        _target = target;
        SetChildren(target);
        return this;
    }

    // Resolved during Compile (see ValidateCompiled) by walking the
    // .Bind(...) chain to the first concrete rule. Null until then.
    private FlattenType? _resolvedFlattenType;

    /// <summary>
    /// The bound target's <see cref="InductorParser.SyntaxTree.FlattenType"/>, worked out when the
    /// grammar is compiled. A LateBoundRule has no flatten policy of its
    /// own. Reading this before <see cref="Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> throws rather than guess a value
    /// that could turn out wrong.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The grammar hasn't been compiled yet.
    /// </exception>
    // The value is resolved through any chain of LateBoundRules and
    // unnamed aliases to the first concrete rule, once, during Compile
    // while the graph walk is single-threaded. Afterwards this getter is
    // a plain field read, so concurrent parses of the compiled grammar
    // need no synchronization.
    public override FlattenType FlattenType =>
        _resolvedFlattenType
        ?? throw new InvalidOperationException(
            "A LateBoundRule has no FlattenType of its own. It takes the FlattenType " +
            "of the rule you Bind it to, worked out when the grammar is compiled. " +
            "Compile the grammar first, then read FlattenType (Parse compiles for you).");

    /// <summary>
    /// The bound target's <see cref="Rule.EmitsLeaf">Rule.EmitsLeaf</see>. Reading this before
    /// <see cref="Bind">LateBoundRule.Bind(Rule)</see> throws rather than return a default that could
    /// turn out wrong once the target is attached.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No target has been bound yet.
    /// </exception>
    // Forwarding EmitsLeaf the same way as FlattenType means a
    // LateBoundRule around a leaf-emitting rule reads as one, so
    // AliasRule's leaf-substitution check behaves identically whether you
    // alias the leaf rule directly or through a LateBoundRule around it.
    public override bool EmitsLeaf =>
        _target?.EmitsLeaf
        ?? throw new InvalidOperationException(
            "A LateBoundRule has no EmitsLeaf value of its own. It takes the value " +
            "from the rule you Bind it to. Bind it first, then read EmitsLeaf.");

    /// <summary>
    /// Not supported. A LateBoundRule is transparent at parse time, so no
    /// Symbol would ever have the name and <see cref="SyntaxTree.Symbol.Find(Rule)">Symbol.Find</see> could never match it.
    /// Name the target rule instead (<c><see cref="Rule.As(string)">Rule.As</see>("name")</c>). For a label
    /// in traces and error messages, pass the name to the constructor.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override Rule As(string name) => throw new InvalidOperationException(
        "LateBoundRule.As(string) isn't supported: a LateBoundRule is transparent at " +
        "parse time, so no Symbol carries its name and Find() can never match it. To " +
        "make the bound shape findable under a name, name the target rule instead " +
        "(target.As(\"name\")). The constructor's name is only a label for traces and " +
        "error messages, not a findable name: new LateBoundRule(\"name\").");

    /// <summary>
    /// Not supported. A LateBoundRule is transparent at parse time, so no
    /// Symbol would ever have the id. Set the explicit id on the target rule
    /// instead (<c><see cref="Rule.As(SyntaxTree.SymbolId)">Rule.As</see>(new SymbolId(...))</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override Rule As(SymbolId id) => throw new InvalidOperationException(
        "LateBoundRule.As(SymbolId) isn't supported: a LateBoundRule is transparent at " +
        "parse time, so no Symbol in the parse tree will carry this Id. To give the " +
        "bound shape an explicit id, set it on the target rule instead " +
        "(target.As(new SymbolId(...))).");

    /// <summary>
    /// Not supported. A LateBoundRule's <see cref="FlattenType">LateBoundRule.FlattenType</see> forwards to
    /// the bound target, so a value set here would never take effect. Set
    /// <c><see cref="Rule.Flatten(SyntaxTree.FlattenType)">Rule.Flatten</see>(...)</c> on the target rule instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override Rule Flatten(FlattenType type) => throw new InvalidOperationException(
        "LateBoundRule.Flatten(...) isn't supported: a LateBoundRule's FlattenType " +
        "forwards to its bound target, so a value set here would never take effect. " +
        "Set .Flatten(...) on the target rule instead.");

    /// <summary>
    /// Not supported, for the same reason as <see cref="Flatten(FlattenType)">LateBoundRule.Flatten(FlattenType)</see>:
    /// a flatten policy on a transparent forwarding rule is never consulted.
    /// Set it on the target rule instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override Rule FlattenByDefault(FlattenType type) => throw new InvalidOperationException(
        "LateBoundRule.FlattenByDefault(...) isn't supported: the rule is transparent at parse " +
        "time, so its FlattenType is never consulted. Set the flatten policy on the bound target instead.");

    /// <summary>
    /// Not supported. A LateBoundRule forwards the parse to its target, which
    /// records its own failures with its own message, so a message set here
    /// would never be consulted. Set <c><see cref="Rule.WithError">Rule.WithError</see>(...)</c> on the target
    /// rule instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override Rule WithError(string errorMessage, bool forced = false) => throw new InvalidOperationException(
        "LateBoundRule.WithError(...) isn't supported: the rule is transparent at parse " +
        "time, so its ErrorMessage is never consulted. Set .WithError(...) on the bound target instead.");

    /// <summary>
    /// Forwards the parse to the bound target and returns its result
    /// unchanged.
    /// </summary>
    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // _target is guaranteed non-null here: Compile's ValidateCompiled
        // throws on an unbound LateBoundRule before any parse can reach
        // this method. Because the LateBoundRule reports the target's
        // FlattenType, TryParse and the calling ParseChild have already
        // set `outputSymbols` up exactly as they would for the target
        // rule itself. Forwarding straight to the target is all that's
        // left: the target writes its children into the list or returns
        // its own Symbol, and that result flows back unchanged.
        return ParseChild(_target!, lexer, outputSymbols);
    }

    /// <summary>
    /// Throws if this placeholder was never bound, then works out the
    /// target's <see cref="FlattenType">LateBoundRule.FlattenType</see> for the compiled grammar.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Bind">LateBoundRule.Bind(Rule)</see> was never called, or the chain of bound rules
    /// never reaches a concrete rule.
    /// </exception>
    protected override void ValidateCompiled()
    {
        if (_target == null)
        {
            var label = _debugName ?? "<anonymous LateBoundRule>";
            throw new InvalidOperationException(
                $"Rule '{label}' is a LateBoundRule that was never bound. " +
                "Call .Bind(targetRule) before calling Parse or Compile.");
        }
        // Resolve the target's FlattenType now, while Compile is
        // single-threaded. The walk also surfaces a .Bind(...) chain that
        // loops through LateBoundRules without ever reaching a concrete
        // rule, before a later FlattenType read could recurse into it.
        _resolvedFlattenType = ResolveTargetFlattenType();
    }

    // Walk the .Bind(...) chain to the first concrete rule and return its
    // FlattenType. The walk follows _target and Inner fields rather than
    // reading FlattenType on each link, because a LateBoundRule that
    // Compile hasn't resolved yet throws on that read. Throws if the
    // chain reaches an unbound rule, or loops back on itself without
    // ever reaching a concrete rule (a grammar that can never match
    // anything).
    private FlattenType ResolveTargetFlattenType()
    {
        var visited = new HashSet<Rule> { this };
        Rule current = _target!;
        while (true)
        {
            if (current is LateBoundRule lateBound)
            {
                if (lateBound._target == null)
                {
                    var label = lateBound._debugName ?? "<anonymous LateBoundRule>";
                    throw new InvalidOperationException(
                        $"Rule '{label}' is a LateBoundRule that was never bound. " +
                        "Call .Bind(targetRule) before calling Parse or Compile.");
                }
                if (!visited.Add(lateBound))
                    throw ChainLoopsForever();
                current = lateBound._target;
            }
            else if (current is AliasRule alias && alias.IsTransparent)
            {
                if (!visited.Add(alias))
                    throw ChainLoopsForever();
                current = alias.Inner;
            }
            else
            {
                return current.FlattenType;
            }
        }
    }

    private static InvalidOperationException ChainLoopsForever() =>
        new InvalidOperationException(
            "A LateBoundRule's .Bind(...) chain loops through LateBoundRules " +
            "and unnamed aliases without ever reaching a concrete rule, so it " +
            "can never match input. Bind one rule in " +
            "the loop to a real (non-LateBound, non-alias) rule.");

}
