using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// A truthful user-defined Rule, shared by the optimization-transparency
// fuzzers and the scanner-skip tests. It matches one token whose first rune
// is in `set`, consumes that single token, and reports ComputeRuleStart
// metadata that tells the truth about that: it always advances by one token
// and only matches tokens whose first rune is in the set. The lookahead
// shortcut and scanner-skip both rest entirely on that metadata being
// truthful, so this is a faithful stand-in for every well-behaved custom rule
// a user could plug into a grammar.
internal sealed class TruthfulRuneRule : Rule
{
    private readonly TokenSet _set;

    public TruthfulRuneRule(TokenSet set) : base(FlattenType.Preserve) => _set = set;

    internal override Symbol? TryParseRule(
        InductorParser.Lexing.Lexer lexer, int startPosition,
        FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        var token = lexer.Read();
        if (token.IsEof || !_set.Contains(token.FirstRune))
        {
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        var leaf = new Symbol(ResolveLeafId(token.RuneValue), FlattenType, token.Memory, lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leaf);
            return Symbol.Discarded;
        }
        return leaf;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.FirstTokenMustBeInSet(_set);
}

// The parser has two parse-time optimizations that are supposed to be
// invisible: the LL(1) lookahead shortcut (skip an alternative the next
// token can't start) and the scanner-skip (jump over a deleted fallback
// run). "Invisible" means the parse result is identical whether or not the
// optimization fired. These fuzzers verify that directly by running the same
// random grammars two ways and comparing, instead of trying to reason out
// every shape where an optimization could go wrong.
[TestFixture]
[NonParallelizable] // toggles the process-wide Rule.DisableLookaheadShortcut
public class OptimizationTransparencyTests
{
    private const string Alphabet = "abc";

    private static Rule WithRandomFlatten(Random random, Rule rule)
    {
        switch (random.Next(4))
        {
            case 0: return rule.Preserve();
            case 1: return rule.Flatten();
            case 2: return rule.Delete();
            default: return rule; // leave the class default
        }
    }

    private static TokenSet RandomSet(Random random)
    {
        switch (random.Next(3))
        {
            case 0: return TokenSet.Runes("a");
            case 1: return TokenSet.Runes("ab");
            default: return TokenSet.Runes("bc");
        }
    }

    private static Rule RandomLeaf(Random random)
    {
        Rule leaf;
        switch (random.Next(6))
        {
            case 0: leaf = Token(Alphabet[random.Next(Alphabet.Length)]); break;
            case 1: leaf = OneOf(RandomSet(random)); break;
            case 2: leaf = NoneOf(RandomSet(random)); break;
            case 3: leaf = AnyToken(); break;
            case 4: leaf = ScanWhile(RandomSet(random), random.Next(2)); break;
            default: leaf = new TruthfulRuneRule(RandomSet(random)); break; // truthful custom rule
        }
        // Occasionally attach a .WithError so the shortcut's
        // HasErrorMessageInSubtree gate is exercised too.
        if (random.Next(5) == 0) leaf = leaf.WithError("e" + random.Next(1000));
        return WithRandomFlatten(random, leaf);
    }

    // Random grammar tree. No Alias and no LateBound: Alias rebadges a leaf
    // inner to its own identity, which legitimately makes the default tree
    // and the PreserveAll tree differ (the rebadge is success-only and
    // identity-changing), so it isn't tree-transparent and would be a false
    // positive for the round-trip oracle. Everything else is fair game.
    private static Rule RandomGrammar(Random random, int depth)
    {
        if (depth <= 0 || random.Next(100) < 40) return RandomLeaf(random);
        switch (random.Next(9))
        {
            case 0: return WithRandomFlatten(random, And(RandomGrammar(random, depth - 1), RandomGrammar(random, depth - 1)));
            case 1: return WithRandomFlatten(random, Or(RandomGrammar(random, depth - 1), RandomGrammar(random, depth - 1)));
            case 2: return WithRandomFlatten(random, OneOrMore(RandomGrammar(random, depth - 1)));
            case 3: return WithRandomFlatten(random, ZeroOrMore(RandomGrammar(random, depth - 1)));
            case 4: return WithRandomFlatten(random, Optional(RandomGrammar(random, depth - 1)));
            case 5: return WithRandomFlatten(random, BetweenInclusive(0, 2, RandomGrammar(random, depth - 1)));
            case 6: return WithRandomFlatten(random, Not(RandomLeaf(random)));
            case 7: return WithRandomFlatten(random, Peek(RandomLeaf(random)));
            // The scanner-skip shape, with a random flatten on the Or so the
            // Preserve-Or case (the one that the scanner-skip used to get
            // wrong) is in the mix.
            default: return WithRandomFlatten(random,
                ZeroOrMore(WithRandomFlatten(random, Or(RandomGrammar(random, depth - 1), AnyToken().Delete()))));
        }
    }

    private static string RandomInput(Random random)
    {
        var input = new StringBuilder();
        int length = random.Next(6);
        for (int i = 0; i < length; i++) input.Append(Alphabet[random.Next(Alphabet.Length)]);
        return input.ToString();
    }

    // Everything a caller can observe from a parse: success, where it failed
    // and why, and the tree it produced. Two parses with equal Describe
    // strings are indistinguishable to a user.
    private static string Describe(ParseResult result)
    {
        if (!result.Success)
            return $"FAIL outcome={result.Outcome} at={result.ErrorCharIndex} msg=[{result.ErrorMessage}]";
        return "OK " + TestHelpers.Fingerprint(result.Symbols);
    }

    // The lookahead shortcut decides whether to even try an alternative based
    // on the metadata that alternative publishes from ComputeRuleStart. This
    // runs the same grammar with the shortcut on and off and checks nothing a
    // caller can see changed.
    //
    // Why this covers custom rules: the shortcut's only input about an
    // alternative is its published (first-token set, advance, polarity). A
    // custom rule that publishes that truthfully behaves, from the shortcut's
    // view, exactly like a built-in rule with the same metadata, so
    // TruthfulRuneRule in the pool is a true stand-in for any well-behaved
    // custom rule. A custom rule that LIES about its metadata is its own bug
    // (the shortcut trusts every rule equally, built-in or not) and is out of
    // scope by design. The pessimistic default metadata makes a rule that
    // doesn't override ComputeRuleStart un-shortcuttable, so a naive custom
    // rule is safe automatically.
    [Test]
    public void Lookahead_shortcut_on_and_off_produce_identical_results()
    {
        bool original = Rule.DisableLookaheadShortcut;
        var random = new Random(424242);
        try
        {
            for (int iteration = 0; iteration < 4000; iteration++)
            {
                var grammar = RandomGrammar(random, 4);
                grammar.Compile();
                string input = RandomInput(random);

                Rule.DisableLookaheadShortcut = false; // shortcut on
                string withShortcut = Describe(grammar.Parse(input));
                Rule.DisableLookaheadShortcut = true;  // shortcut off
                string withoutShortcut = Describe(grammar.Parse(input));

                Assert.That(withShortcut, Is.EqualTo(withoutShortcut),
                    $"lookahead shortcut changed the parse for input \"{input}\"");
            }
        }
        finally
        {
            Rule.DisableLookaheadShortcut = original;
        }
    }

    // PreserveAllSymbols keeps every grammar node in the tree and, as a side
    // effect, turns off both parse-time optimizations (the lookahead shortcut
    // and the scanner-skip). Applying each node's real FlattenType to that
    // full tree afterward (Symbol.Flatten / FlattenInto) must reproduce the
    // tree the normal optimized parse builds directly. If it doesn't, either
    // an optimization changed the tree or parse-time flattening and post-hoc
    // flattening disagree. This is the broad net that caught the scanner-skip
    // bug. It stays as an ongoing regression check for the whole flatten
    // machinery.
    [Test]
    public void Default_parse_matches_PreserveAllSymbols_then_post_hoc_Flatten()
    {
        var random = new Random(13579);
        int comparedTrees = 0;
        for (int iteration = 0; iteration < 4000; iteration++)
        {
            var grammar = RandomGrammar(random, 4);
            grammar.Compile();
            string input = RandomInput(random);

            var direct = grammar.Parse(input);
            var preserveAll = grammar.Parse(input, new ParseOptions { PreserveAllSymbols = true });

            Assert.That(direct.Success, Is.EqualTo(preserveAll.Success),
                $"success differs for input \"{input}\"");
            if (!direct.Success) continue;

            var postHocFlattened = new List<Symbol>();
            foreach (var symbol in preserveAll.Symbols) symbol.FlattenInto(postHocFlattened);

            Assert.That(TestHelpers.Fingerprint(direct.Symbols),
                Is.EqualTo(TestHelpers.Fingerprint(postHocFlattened)),
                $"parse-time flatten and post-hoc flatten disagree for input \"{input}\"");
            comparedTrees++;
        }
        // Check that the fuzzer didn't silently degenerate into all-failures,
        // which would make the tree comparison above vacuous.
        Assert.That(comparedTrees, Is.GreaterThan(500),
            "expected a meaningful number of successful parses to compare");
    }
}
