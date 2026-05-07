using System;
using System.Collections.Generic;

namespace InductorParser.Prefilter;

// Required-literal prefilter analysis. Lives in this separate assembly
// (rather than as overrides on Rule and its subclasses inside core) so
// the core parser stays free of an optimization that neither parsing
// engine uses. The rebar grep benchmark is the only in-tree consumer.
// If this whole project is removed, core builds and tests are unaffected.
//
// The analyzer is a switch on rule type. User-defined rule subclasses
// fall into the default branch (return null), matching what the old
// virtual default returned. The per-rule analysis bodies live in sibling
// files named for the rule they cover (OneOfRule.cs, LiteralRule.cs,
// etc.) so this file's git history mirrors the override locations the
// methods were copied out of.
public static class RuleExtensions
{
    /// <summary>
    /// Returns true when every successful match of this rule is
    /// guaranteed to consume text containing the returned literal as a
    /// substring (case-invariant if <paramref name="ignoreAsciiCase"/>
    /// is true). Pure static analysis — neither parsing engine consults
    /// this; it exists so external callers (regex-style grep runners,
    /// etc.) can pre-filter input before invoking the parser. The rebar
    /// grep runner uses it to skip past lines that can't possibly match
    /// via one BCL substring search across the whole haystack before
    /// line-by-line parsing kicks in. Mirrors the literal-prefilter
    /// analysis every serious regex engine does internally (rust/regex's
    /// 'literal' module, .NET's compiled regex, PCRE2's "studied"
    /// patterns).
    /// <para>
    /// The derived literal is the longest contiguous run of fixed-text
    /// children at any position in the rule tree (LiteralRule,
    /// LiteralIgnoreAsciiCaseRule, TokenRule, OneOfRule with a single
    /// BMP char or a single ASCII letter pair like 'Nn'). Concatenated
    /// through And and propagated through BetweenInclusive[atLeast&gt;=1]
    /// and FlattenType wrappers. Returns false (literal == "") when no
    /// such required substring can be derived from the rule.
    /// </para>
    /// </summary>
    public static bool TryGetRequiredLiteral(this Rule rule, out string literal, out bool ignoreAsciiCase)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        var result = ComputeRequiredLiteral(rule);
        if (result == null)
        {
            literal = "";
            ignoreAsciiCase = false;
            return false;
        }
        literal = result.Value.Text;
        ignoreAsciiCase = result.Value.IgnoreCase;
        return literal.Length > 0;
    }

    /// <summary>
    /// Returns true when every successful match of this rule is
    /// guaranteed to contain at least one of the returned literals as a
    /// substring. Use when no single shared literal can be derived
    /// (<see cref="TryGetRequiredLiteral"/> returns false) but the rule
    /// has a small fixed set of literal-prefix alternatives. The
    /// AWS-keys grammar's <c>Or("ASIA"|"AKIA"|"AROA"|"AIDA")</c>
    /// is the motivating shape: every match contains exactly one of
    /// those four literals, so a multi-substring pre-scan still skips
    /// lines that can't possibly match.
    /// <para>
    /// Pure static analysis like <see cref="TryGetRequiredLiteral"/> —
    /// neither parsing engine consults this.
    /// </para>
    /// <para>
    /// <paramref name="maxAlternatives"/> caps the returned set size.
    /// The caller picks a cap that makes a multi-substring scan worth
    /// it (8-16 is reasonable for the rebar grep runner; a
    /// 2000-literal dictionary would have selectivity at most 1 in 26
    /// from the first-rune set and isn't worth pre-filtering with this
    /// analysis). Returns false when no analyzable set exists or when
    /// it exceeds the cap.
    /// </para>
    /// </summary>
    public static bool TryGetRequiredLiteralAlternatives(
        this Rule rule,
        int maxAlternatives,
        out IReadOnlyList<(string Text, bool IgnoreCase)> alternatives)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        var result = ComputeRequiredLiteralAlternatives(rule);
        if (result == null || result.Count == 0 || result.Count > maxAlternatives)
        {
            alternatives = Array.Empty<(string Text, bool IgnoreCase)>();
            return false;
        }
        alternatives = result;
        return true;
    }

    // Internal dispatchers. The per-rule helpers call back through these
    // to recurse into children, so the recursion lives in one place
    // instead of being duplicated in every helper.
    //
    // User-defined Rule subclasses (and any built-in rule type that
    // doesn't surface a fixed text — Eof, Peek, Not, AnyToken,
    // ScanUntil, ScanWhile, WithinToken, LateBound) fall into the
    // default branch and return null, matching the pre-refactor
    // behavior of the virtual default on Rule.
    internal static (string Text, bool IgnoreCase)? ComputeRequiredLiteral(Rule rule) => rule switch
    {
        LiteralRule literal => LiteralRulePrefilter.ComputeRequiredLiteral(literal),
        LiteralIgnoreAsciiCaseRule literal => LiteralIgnoreAsciiCaseRulePrefilter.ComputeRequiredLiteral(literal),
        GraphemeRule grapheme => GraphemeRulePrefilter.ComputeRequiredLiteral(grapheme),
        OneOfRule oneOf => OneOfRulePrefilter.ComputeRequiredLiteral(oneOf),
        AndRule andRule => AndRulePrefilter.ComputeRequiredLiteral(andRule),
        BetweenInclusiveRule between => BetweenInclusiveRulePrefilter.ComputeRequiredLiteral(between),
        _ => null,
    };

    internal static (string Text, bool IgnoreCase)? ComputeConcatenableText(Rule rule) => rule switch
    {
        LiteralRule literal => LiteralRulePrefilter.ComputeConcatenableText(literal),
        LiteralIgnoreAsciiCaseRule literal => LiteralIgnoreAsciiCaseRulePrefilter.ComputeConcatenableText(literal),
        GraphemeRule grapheme => GraphemeRulePrefilter.ComputeConcatenableText(grapheme),
        OneOfRule oneOf => OneOfRulePrefilter.ComputeConcatenableText(oneOf),
        AndRule andRule => AndRulePrefilter.ComputeConcatenableText(andRule),
        BetweenInclusiveRule between => BetweenInclusiveRulePrefilter.ComputeConcatenableText(between),
        _ => null,
    };

    internal static IReadOnlyList<(string Text, bool IgnoreCase)>? ComputeRequiredLiteralAlternatives(Rule rule) => rule switch
    {
        OrRule orRule => OrRulePrefilter.ComputeRequiredLiteralAlternatives(orRule),
        AndRule andRule => AndRulePrefilter.ComputeRequiredLiteralAlternatives(andRule),
        _ => null,
    };
}
