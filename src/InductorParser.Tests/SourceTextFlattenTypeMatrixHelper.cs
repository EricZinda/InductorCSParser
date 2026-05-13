using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Scaffold for the per-rule SourceText FlattenType-matrix tests in
// `Rules/*Tests.cs`. Each per-rule test parameterizes
// AssertSourceTextUnderEveryFlattenType below over the three
// FlattenType values — Delete, Flatten, Preserve — and supplies a
// rule factory plus the expected matched text. The helper builds a
// `And(target).Preserve()` grammar around the rule-with-FlattenType,
// parses under ParseOptions.PreserveAllSymbols so the target Symbol
// stays in the tree regardless of FlattenType, and asserts
// `target.SourceText` returns the expected text.
//
// The invariant being pinned: a Symbol's SourceText is the section of
// the user's original input the rule matched, regardless of the
// rule's FlattenType. FlattenType affects whether and how the Symbol
// appears in the surrounding tree shape, not the matched-text content.
//
// We can't use `.As("target")` here because the framework rejects
// .As(...) on rules with FlattenType.Delete (its guardrail says the
// Symbol won't be findable). Under PreserveAllSymbols it WOULD be
// findable, but the rule doesn't know that at construction. So the
// helper finds the target positionally: it's the lone child of the
// And wrapper under PreserveAllSymbols, where every Symbol survives.
public static class SourceTextFlattenTypeMatrixHelper
{
    // Build a fresh rule via `ruleBuilder` for each FlattenType, wrap
    // it in `And(target).Preserve()`, parse under PreserveAllSymbols
    // so the target Symbol enters the tree regardless of FlattenType,
    // and verify SourceText. The wrapper And is Preserve so the parse
    // has a single Tree symbol to inspect; the target is its sole
    // child (the wrapper And consumed nothing else).
    //
    // ruleBuilder returns a fresh rule each call because compiled
    // rules can't be re-modified — each FlattenType iteration needs
    // its own unsealed rule to call .Flatten(...) on.
    //
    // For composite rules (And, Or, BetweenInclusive, Peek, Not, Eof),
    // the rule object IS the composite. For leaf rules (Token, OneOf,
    // Literal, etc.), the rule is the leaf factory result. Either way
    // it lands as the wrapper's first child under PreserveAllSymbols.
    public static void AssertSourceTextUnderEveryFlattenType(
        Func<Rule> ruleBuilder,
        string input,
        string expectedSourceText)
    {
        foreach (var flattenType in new[] { FlattenType.Delete, FlattenType.Flatten, FlattenType.Preserve })
        {
            var target = ruleBuilder().Flatten(flattenType);
            var grammar = And(target).Preserve();
            var result = grammar.Parse(input, new ParseOptions { PreserveAllSymbols = true });

            Assert.That(result.Success, Is.True,
                $"FlattenType.{flattenType}: parse failed at offset {result.ErrorCharIndex}: {result.ErrorMessage}");
            Assert.That(result.Tree!.Children.Count, Is.EqualTo(1),
                $"FlattenType.{flattenType}: expected exactly one child under the wrapper And.");
            var symbol = result.Tree.Children[0];
            Assert.That(symbol.FlattenType, Is.EqualTo(flattenType),
                $"FlattenType.{flattenType}: target Symbol should carry the declared FlattenType.");
            Assert.That(symbol.SourceText, Is.EqualTo(expectedSourceText),
                $"FlattenType.{flattenType}: SourceText should be \"{expectedSourceText}\" " +
                $"regardless of FlattenType — FlattenType controls visibility in the tree, " +
                $"not the matched-text content.");
        }
    }
}
