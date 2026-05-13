using System;
using NUnit.Framework;
using InductorParser;
using InductorParser.Prefilter;
using static InductorParser.Rules;

namespace InductorParser.Tests.Prefilter;

[TestFixture]
public class OneOfRulePrefilterTests
{
    [Test]
    public void OneOf_required_literal_skips_sets_with_multi_rune_entries()
    {
        // OneOfRulePrefilter.ComputeRequiredLiteral consults
        // TryGetBmpChars on the rune-only part of the set and ignores
        // multi-rune entries. For a mixed set (single rune 'a' plus
        // the CRLF two-rune cluster as a multi-rune entry) it would
        // advertise "a" as a required literal, but the rule also
        // matches "\r\n" via the multi-rune entry, and that match's
        // consumed text contains no 'a'. The TryGetRequiredLiteral
        // contract says every successful match contains the returned
        // literal as a substring, so an "a"-bearing answer would
        // break the contract.
        var set = TokenSet.Single('a') | TokenSet.Graphemes("\r\n");
        var rule = OneOf(set);
        rule.Compile(null);

        bool hasLiteral = rule.TryGetRequiredLiteral(out string literal, out _);
        if (hasLiteral)
        {
            var crlfMatch = rule.Parse("\r\n");
            Assert.That(crlfMatch.Success, Is.True,
                "OneOf does match CRLF via the multi-rune entry: " + crlfMatch.ErrorMessage);
            string matched = crlfMatch.Tree!.ToString();
            Assert.That(matched.Contains(literal, StringComparison.Ordinal), Is.True,
                $"TryGetRequiredLiteral contract: every match must contain the literal '{literal}', " +
                $"but matched text {Display(matched)} does not.");
        }

        static string Display(string s) =>
            "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }
}
