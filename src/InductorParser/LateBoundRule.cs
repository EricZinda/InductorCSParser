using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Forward-reference placeholder for mutually recursive grammars. In a
// grammar like
//
//     Term = Integer | '(' Expression ')'
//     Expression = Term ('+' Term)*
//
// Expression references Term and Term references Expression, which is a
// cycle no declaration order can resolve. C# initializes static fields
// top-to-bottom, so whichever one is declared first sees the other as
// null.
//
// The fix is a LateBoundRule: a Rule that stands in for the real target
// during construction and gets its target attached later via .Bind(...).
// The canonical pattern:
//
//     static readonly LateBoundRule Expression = new LateBoundRule("expression");
//     static readonly Rule Term = Or(Integer(), And(Token('('), Expression, Token(')')));
//     static readonly Rule Sum  = And(Term, ZeroOrMore(And(Token('+'), Term)));
//     static readonly Rule _init = Expression.Bind(Sum);
//
// Term sees Expression as a valid (but unbound) rule at construction
// time. Sum references Term. The final `_init` field exists only to
// fire the Bind call at type-init time, after all three rules are
// already constructed.
//
// A LateBoundRule is transparent. At parse time it just forwards
// TryParse to its target, and the Symbol that flows up carries the
// target's Id, not the LateBoundRule's. Transparency also covers
// FlattenType: a LateBoundRule has no flatten policy of its own, so the
// FlattenType property reports the target's, resolved through any chain
// of LateBoundRules to the first concrete rule. That's what makes
// `Alias(lateBound)`, `And(x, lateBound, y)`, and every other
// composition behave exactly as if the target rule were written in the
// LateBoundRule's place, with no placeholder layer and no special case.
//
// LateBoundRule is the exception to universal requirement #5: it
// rejects .As / .Flatten / .WithError always, not just after Compile.
public sealed class LateBoundRule : Rule
{
    private readonly string? _debugName;
    private Rule? _target;

    // base(FlattenType.Flatten) only seeds the base class's backing
    // field. The FlattenType property below overrides the getter to
    // report the target's value, so this seed is never read.
    public LateBoundRule(string? debugName = null) : base(FlattenType.Flatten)
    {
        _debugName = debugName;
    }

    // Attach the real target. Returns this LateBoundRule so callers can
    // write `static readonly Rule _init = Expression.Bind(Sum);` as a
    // one-liner that fires at type-init time. Throws if the graph has
    // already been compiled, and throws on a second call against the
    // same instance: .Bind is set-once, matching .As(string) /
    // .As(SymbolId) / .WithError. A double-Bind silently swaps the
    // target with no error pointing at the duplicate call, and the
    // grammar then runs against whichever target initialized last.
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

    // A LateBoundRule has no FlattenType of its own: it reports the
    // bound target's, resolved through any chain of LateBoundRules to
    // the first concrete rule. The value is computed once during Compile
    // while the graph walk is single-threaded; afterwards this getter is
    // a plain field read, so concurrent parses of the compiled grammar
    // need no synchronization. Reading it before Compile throws rather
    // than guessing a value that would later turn out wrong.
    public override FlattenType FlattenType =>
        _resolvedFlattenType
        ?? throw new InvalidOperationException(
            "LateBoundRule.FlattenType was read before Compile resolved it from " +
            "the bound target. A LateBoundRule has no FlattenType of its own. " +
            "Compile the grammar first (Parse compiles automatically).");

    // Naming a LateBoundRule is a bug: the name would derive a Name
    // and (via hashing) an Id, but neither is ever visible at parse time.
    // Fail instead of letting users build a rule whose Find
    // silently returns null. Pass the debug name to the constructor.
    public override Rule As(string name) => throw new InvalidOperationException(
        "LateBoundRule.As(string) isn't supported: the rule is transparent at parse " +
        "time, so Find() would never match it. Pass a debug name to the constructor: " +
        "new LateBoundRule(\"name\").");

    public override Rule As(SymbolId id) => throw new InvalidOperationException(
        "LateBoundRule.As(SymbolId) isn't supported: the rule is transparent at parse " +
        "time, so no Symbol in the parse tree will carry this Id.");

    // Flatten on LateBoundRule is rejected because its FlattenType is not
    // its own: the property forwards to the bound target. A value set
    // here would be shadowed by that forward and never take effect. Set
    // .Flatten(...) on the target rule instead.
    public override Rule Flatten(FlattenType type) => throw new InvalidOperationException(
        "LateBoundRule.Flatten(...) isn't supported: a LateBoundRule's FlattenType " +
        "forwards to its bound target, so a value set here would never take effect. " +
        "Set .Flatten(...) on the target rule instead.");

    // WithError on LateBoundRule would set an error message that's never
    // consulted: TryParse just forwards to the target, which runs its own
    // RecordFailure on failure using the target's ErrorMessage. Set
    // .WithError(...) on the target rule instead.
    public override Rule WithError(string errorMessage, bool forced = false) => throw new InvalidOperationException(
        "LateBoundRule.WithError(...) isn't supported: the rule is transparent at parse " +
        "time, so its ErrorMessage is never consulted. Set .WithError(...) on the bound target instead.");

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // _target is guaranteed non-null here: Compile's ValidateCompiled
        // throws on an unbound LateBoundRule before any parse can reach
        // this method. Because the LateBoundRule reports the target's
        // FlattenType, TryParse and the calling ParseChild have already
        // set `outputSymbols` up exactly as they would for the target
        // rule itself. Forwarding straight to the target is all that's
        // left: the target writes its children into the list or returns
        // its own wrapper Symbol, and that result flows back unchanged.
        return ParseChild(_target!, lexer, outputSymbols);
    }

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

    // Walk the .Bind(...) chain to the first concrete (non-LateBound)
    // rule and return its FlattenType. Throws if the chain reaches an
    // unbound rule, or loops back on itself without ever reaching a
    // concrete rule (a grammar that can never match anything).
    private FlattenType ResolveTargetFlattenType()
    {
        var visited = new HashSet<LateBoundRule> { this };
        Rule current = _target!;
        while (current is LateBoundRule lateBound)
        {
            if (lateBound._target == null)
            {
                var label = lateBound._debugName ?? "<anonymous LateBoundRule>";
                throw new InvalidOperationException(
                    $"Rule '{label}' is a LateBoundRule that was never bound. " +
                    "Call .Bind(targetRule) before calling Parse or Compile.");
            }
            if (!visited.Add(lateBound))
                throw new InvalidOperationException(
                    "A LateBoundRule's .Bind(...) chain loops through LateBoundRules " +
                    "without ever reaching a concrete rule, so it has no FlattenType " +
                    "and can never match input. Bind one rule in the loop to a real " +
                    "(non-LateBound) rule.");
            current = lateBound._target;
        }
        return current.FlattenType;
    }

    // LateBoundRule is transparent at parse time. Compile's depth-first
    // walk visits the target as our one child, so the target's values
    // are populated by the time we land here. If the target graph
    // forms a cycle back through this LateBoundRule, the cycle-detection
    // path leaves whichever node it hit at the pessimistic default
    // (Universe, Advance.Sometimes, MustBeIn). That keeps OrRule
    // conservative.
    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.PassesThroughTo(_target!);
}
