using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;
using static InductorParser.Tests.TestHelpers;

namespace InductorParser.Tests;

// Tests for the Rule.Compile lifecycle. Coverage of Compile is split
// across several files by design (docs/TestArchitecture.md):
//
//   - Sealing per concrete rule type: the per-rule tests in
//     src/InductorParser.Tests/Rules/<RuleName>Tests.cs verify that
//     Flatten / WithError / As all throw on each rule after Compile
//     (universal requirement #5).
//   - Id assignment (explicit / named-hash / anonymous): IdAssignmentTests.
//   - NameOf auto-compile: NameOfTests.NameOf_auto_compiles_when_called_before_compile.
//   - LateBoundRule auto-compile through Parse, plus a regression that
//     ValidateAll walks nested rules: LateBoundRuleTests
//     (the never-bound rule is two levels deep inside Or+And).
//   - Cycle handling in ComputeRuleStartAll: exercised indirectly by
//     every recursive grammar test (would hang otherwise).
//
// What's left for this file: the cross-cutting Compile behaviors that
// don't belong to any one rule. The internal RuleStartRequirements
// consistency check, idempotency of Compile itself, and Parse's
// auto-compile on the success path (the LateBoundRule tests cover the
// failure path).
[TestFixture]
public class CompileTests
{
    [Test]
    public void Compile_throws_when_a_rule_reports_Advance_Never_with_non_empty_FirstConsumedTokens()
    {
        // Advance.Never means "never consumes on success," which logically
        // forces FirstConsumedTokens to be Empty. If nothing is consumed,
        // there can't be a set of possible first-consumed runes. A subclass
        // that returns a non-empty set alongside Never is violating the
        // contract, and the check here catches it at Compile time rather
        // than letting the mismatch silently corrupt an enclosing AndRule's
        // FirstConsumedTokens union.
        var bad = new InconsistentRuleStartRule();

        var ex = Assert.Throws<InvalidOperationException>(() => bad.Compile());
        Assert.That(ex!.Message, Does.Contain("InconsistentRuleStartRule"));
        Assert.That(ex.Message, Does.Contain("Advance.Never"));
    }

    [Test]
    public void Compile_is_idempotent()
    {
        // Compile is documented to do nothing on the second call. The
        // seal flag is the implementation, but several pieces of state
        // would become wrong if it ever re-ran: named-hash ids depend on
        // linear-probe order and could shift if explicit ids were
        // re-collected against a clean usedIds set, FirstConsumedTokens
        // and Advance are computed bottom-up and could drift, and
        // ValidateAll could throw on a graph that's already settled.
        //
        // Snapshot every reachable rule's post-Compile state (type, id,
        // name, flatten policy, FirstConsumedTokens, Advance) and verify
        // the second Compile is a true no-op against the full grammar
        // shape, not just the few ids the test happened to remember.
        // A grammar with an explicit id, a named rule, and anonymous rules
        // exercises all three id-assignment passes plus the bottom-up
        // RuleStartRequirements walk.
        var explicitId = new SymbolId(SymbolRanges.CustomRangeStart + 9999);
        var named = OneOrMore(OneOf(TokenSet.Letters)).As("settingName");
        var explicitRule = OneOrMore(OneOf(TokenSet.Digits)).As(explicitId);
        var anonymous = ZeroOrMore(Token('!'));
        var root = And(named, explicitRule, anonymous);

        root.Compile();
        string firstSnapshot = SnapshotGrammar(root);

        Assert.DoesNotThrow(() => root.Compile());
        string secondSnapshot = SnapshotGrammar(root);

        Assert.That(secondSnapshot, Is.EqualTo(firstSnapshot));
    }

    [Test]
    public void Parse_auto_compiles_a_grammar_that_was_not_compiled_explicitly()
    {
        // Parse() auto-compiles on first call. The failure-path version
        // (Parse on an unbound LateBoundRule throws because Validate
        // runs during the auto-compile) lives in LateBoundRuleTests.
        // This is the success-path counterpart: lock in the before-state
        // as an unassigned id, call Parse, and verify the id moved into
        // the custom range. Only Compile's named-id assignment pass can
        // produce that transition, so the after-value alone wouldn't
        // prove anything if some other code path had already assigned
        // the id. The before-check rules that out.
        //
        // .As(string) only sets Name, not Id, so an as-yet-uncompiled
        // named rule has the default SymbolId (Value 0).
        var rule = OneOrMore(OneOf(TokenSet.Letters)).As("word");
        Assert.That(rule.Id.Value, Is.EqualTo(0),
            "named rule shouldn't have an id assigned before Compile / Parse runs");

        var result = rule.Parse("hello");

        Assert.That(result.Success, Is.True);
        Assert.That(rule.Id.Value, Is.GreaterThanOrEqualTo(SymbolRanges.CustomRangeStart),
            "named rule should have a custom-range id after Parse, proving auto-compile ran");
    }

    [Test]
    public void Concurrent_parsing_of_a_compiled_grammar_is_thread_safe()
    {
        // The documented guarantee (docs/InductorParserReference.md "Thread
        // Safety"): a grammar is built and compiled once on a single thread,
        // and after Compile the rule graph is sealed and immutable, so any
        // number of threads can Parse it at once with no synchronization.
        // Each Parse builds its own lexer, Symbol tree, and ParseResult and
        // never writes back to the grammar.
        //
        // This locks that guarantee in. The grammar is compiled up front, so
        // no thread races the auto-compile (compiling is the caller's
        // single-threaded responsibility, not something the library makes
        // safe). All threads then parse the same input string instance,
        // which also exercises the per-input GraphemeClusterIndex cache that
        // genuinely is shared and internally synchronized. The Barrier makes
        // the threads start together, and every result must match the
        // single-threaded baseline. A future change that introduced
        // parse-time mutation of shared rule state would diverge or throw
        // here.
        const string input = "(abc,123,d4e,_f)";

        var grammar = BuildGrammar();
        grammar.Compile();
        string baselineDump = grammar.Parse(input).ToDebugString();

        for (int trial = 0; trial < 50; trial++)
        {
            int threadCount = Math.Max(8, Environment.ProcessorCount * 2);
            var barrier = new Barrier(threadCount);
            var results = new ConcurrentBag<string>();
            var exceptions = new ConcurrentBag<Exception>();
            var tasks = new Task[threadCount];
            for (int taskIndex = 0; taskIndex < tasks.Length; taskIndex++)
            {
                tasks[taskIndex] = Task.Run(() =>
                {
                    try
                    {
                        barrier.SignalAndWait();
                        results.Add(grammar.Parse(input).ToDebugString());
                    }
                    catch (Exception exception)
                    {
                        exceptions.Add(exception);
                    }
                });
            }
            Task.WaitAll(tasks);

            Assert.That(exceptions, Is.Empty,
                $"Trial {trial}: concurrent Parse on a compiled grammar threw: " +
                $"{(exceptions.IsEmpty ? "" : exceptions.ToString())}");
            foreach (var dump in results)
                Assert.That(dump, Is.EqualTo(baselineDump),
                    $"Trial {trial}: a concurrent parse result diverged from the " +
                    $"single-threaded baseline, which means a parse mutated shared " +
                    $"grammar state.");
        }
    }

    // A grammar with named, anonymous, and multi-rune-TokenSet rules, used
    // by the concurrent-parse test.
    private static Rule BuildGrammar()
    {
        var identifier = OneOrMore(OneOf(TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_"))).As("identifier");
        var number = OneOrMore(OneOf(TokenSet.Digits)).As("number");
        var atom = Or(identifier, number);
        var list = And(Token('('), atom, ZeroOrMore(And(Token(','), atom)), Token(')'));
        return Or(list, atom).As("root");
    }

    // Subclass that deliberately violates the RuleStartRequirements invariant. Lives
    // here and not in the main InductorParser assembly because the check
    // is defensive against authoring mistakes, not behavior any in-tree
    // rule produces. InternalsVisibleTo makes the internal virtual
    // overridable from the test assembly.
    private sealed class InconsistentRuleStartRule : Rule
    {
        public InconsistentRuleStartRule() : base(FlattenType.Preserve) { }

        internal override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, System.Collections.Generic.List<Symbol>? outputSymbols) => null;

        // Return the set of tokens (grapheme clusters) this rule
        // might consume as its first token (can be a superset).
        // TokenSet.Empty when Advance.Never. TokenSet.Universe means
        // "I don't know". Then say whether the rule Always / Sometimes
        // / Never consumes at least that first token on success. See
        // RuleStartRequirements for the full shortcut story including
        // polarity composition.
        internal override RuleStartRequirements ComputeRuleStart()
            => new RuleStartRequirements(TokenSet.Single('x'), Advance.Never);
    }
}
