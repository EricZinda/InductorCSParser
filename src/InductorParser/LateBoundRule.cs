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
//     static readonly Rule Term = FirstOf(Integer(), AllOf(Grapheme('('), Expression, Grapheme(')')));
//     static readonly Rule Sum  = AllOf(Term, ZeroOrMore(AllOf(Grapheme('+'), Term)));
//     static readonly Rule _init = Expression.Bind(Sum);
//
// Term sees Expression as a valid (but unbound) rule at construction
// time. Sum references Term. The final `_init` field exists only to
// fire the Bind call at type-init time, after all three rules are
// already constructed.
//
// At parse time, a bound LateBoundRule just forwards TryParse to its
// target. The produced Symbol carries the target rule's Id, not the
// LateBoundRule's, because LateBoundRule is a structural placeholder,
// not a meaningful grammar node.
//
// Tests live in src/InductorParser.Tests/Rules/LateBoundRuleTests.cs.
// See docs/TestArchitecture.md for the per-rule test conventions.
// LateBoundRule is the exception to universal requirement #5: it
// rejects .As / .Flatten / .WithError always, not just after Compile,
// so its tests verify the always-rejecting form.
public sealed class LateBoundRule : Rule
{
    private readonly string? _debugName;
    private Rule? _target;

    public LateBoundRule(string? debugName = null) : base(FlattenType.Flatten)
    {
        _debugName = debugName;
    }

    // Attach the real target. Returns this LateBoundRule so callers can
    // write `static readonly Rule _init = Expression.Bind(Sum);` as a
    // one-liner that fires at type-init time. Throws if the graph has
    // already been compiled.
    public LateBoundRule Bind(Rule target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        _target = target;
        SetChildren(target);
        return this;
    }

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

    // Flatten on LateBoundRule would set a FlattenType that's never
    // consulted: TryParse returns the target's Symbol directly, which
    // carries the target's FlattenType. Set .Flatten(...) on the target
    // rule instead.
    public override Rule Flatten(FlattenType type) => throw new InvalidOperationException(
        "LateBoundRule.Flatten(...) isn't supported: the rule is transparent at parse " +
        "time, so its FlattenType is never consulted. Set .Flatten(...) on the bound target instead.");

    // WithError on LateBoundRule would set an error message that's never
    // consulted: TryParse just forwards to the target, which runs its own
    // RecordFailure on failure using the target's ErrorMessage. Set
    // .WithError(...) on the target rule instead.
    public override Rule WithError(string errorMessage) => throw new InvalidOperationException(
        "LateBoundRule.WithError(...) isn't supported: the rule is transparent at parse " +
        "time, so its ErrorMessage is never consulted. Set .WithError(...) on the bound target instead.");

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // _target is guaranteed non-null here: Compile's validation pass
        // throws on an unbound LateBoundRule before any parse can reach
        // this method. LateBoundRule is transparent at parse time, so
        // discard is ignored (target computes its own) and the
        // accumulator forwards straight through.
        var targetSymbol = ParseChild(_target!, lexer, outputSymbols);
        // When the target is FlattenType.Preserve it returns a real
        // wrapper Symbol. LateBoundRule is FlattenType.Flatten, so our
        // own TryParse shim is about to normalize that wrapper to
        // Symbol.Discarded. Push the target's wrapper into outputSymbols
        // ourselves so the Preserve tree node reaches the parent list
        // (AllOfRule / FirstOfRule / BetweenInclusiveRule only add children they
        // see returned, not ones lost inside a transparent proxy).
        if (targetSymbol != null
            && !ReferenceEquals(targetSymbol, Symbol.Discarded)
            && outputSymbols != null
            && effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols.Add(targetSymbol);
        }
        return targetSymbol;
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
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (RuneSet.Empty when Advance.Never. RuneSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // LateBoundRule is transparent at parse time, so its RuleStartRequirements is
        // just the target's. Compile's depth-first walk visits the target
        // as our one child, so in the acyclic case the target's values are
        // already populated by the time we land here. If the target graph
        // forms a cycle back through this LateBoundRule, the cycle-detection
        // path leaves whichever node it hit during recursion at the
        // pessimistic default (Universe, Advance.Sometimes). That keeps
        // FirstOfRule conservative. A future pass could refine by re-walking
        // until no FirstConsumedRunes changes if a grammar shows up where it
        // matters.
        return new RuleStartRequirements(_target!.FirstConsumedRunes, _target.Advance);
    }
}
