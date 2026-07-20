using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using InductorParser;
using InductorParser.SyntaxTree;
using InductorParser.Tracing;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Differential grammar fuzzer. Generates random small grammar blueprints
// over the public rule factories, generates inputs biased toward each
// grammar's own terminals, and checks every (grammar, input) pair against
// these oracles that need no expected-output files:
//
//   1. Determinism: parsing the same input twice on one compiled grammar
//      gives identical results.
//   2. Compile(FormC) and Compile(null) agree completely, when
//      normalization is a no-op for both the grammar text and the input.
//   3. Compile(FormC) and Compile(FormD) agree on outcome, error report,
//      and SourceText tree for any input. The parser tokenizes grapheme
//      clusters, and canonical normalization rewrites the runes inside a
//      cluster without moving the cluster boundaries, so both compiles
//      compare the same sequence of user-perceived characters and
//      matching can't depend on the form. Skipped for grammars
//      containing WithinToken or Identifier, which deliberately step
//      below grapheme tokenization to the runes inside a token, the
//      layer where the two forms genuinely differ.
//   4. Wrapping one random subrule in an unnamed Alias, or binding it as
//      the target of a LateBoundRule, changes nothing: same outcome,
//      same error report, same tree compared by name. Those are the two
//      transparent stand-in rules, and both forward everything to the
//      rule they stand in for.
//   5. A PreserveAllSymbols parse never changes the match outcome or the
//      error report, and its post-hoc Symbol.Flatten() tree reproduces
//      the production tree exactly.
//   6. On success, every node's SourceText equals the input text at its
//      SourceRange, and every range is in bounds.
//   7. Wrapping the whole grammar in one extra And changes nothing: the
//      extra And matches exactly what its child matches, and its own
//      mechanical failure record lands at the subtree's deepest failure,
//      a position the tracker already holds.
//   8. Exactly(n, x) and an And of n references to one shared x instance
//      agree completely, checked when x can't succeed at zero width
//      (for a consuming x the count rule's zero-width break never fires,
//      so the two shapes run x the same way the same number of times).
//   9. Reversing the alternatives of an Or whose branches provably can't
//      compete changes nothing. Eligibility (see FuzzRewriter): every
//      branch consumes at least one token on success, every branch has a
//      known exact first-token set, the sets are pairwise disjoint, and
//      no branch subtree has a .WithError. Under those rules at most one
//      branch can match. A branch whose set doesn't hold the input's
//      first token fails mechanically at the Or's start, the one branch
//      whose set does hold it records the same mechanical failures in
//      either order, and mechanical ranking is a pure max, so trial
//      order can't show through. Oracles 7-9 are the neutral-rewrite
//      oracles from the retired backlog item 0a02 (closed 2026-07-10,
//      see the neutral-rewrite entry in docs/BugSearchLog/).
//
// Everything is deterministic per seed: grammars and inputs come from
// seeded Random instances and the corpus enumeration is ordered, so a
// reported divergence reproduces from its seed alone. Each divergence
// message includes the seed, the input, and the grammar expression, which
// together are the failing repro.
//
// Grammar text and input fragments come from UnicodeExamples via
// AllStringConstants(), so a constant added to the corpus flows into
// fuzzed grammars with no edit here.
//
// Not everything is generated yet, and a green run says nothing about
// what isn't: .As(SymbolId) explicit ids, FlattenByDefault, built-in
// TokenSets (Letters, Digits, the Xid sets) as set sources,
// Identifier extras beyond the underscore profile,
// Compile(FormKC / FormKD), and non-default ParseOptions. Backlog aaen
// tracks growing the coverage.
internal sealed class GrammarFuzzHarness
{
    // Stop collecting after this many divergences: one root cause tends
    // to fire across many seeds, and twenty examples identify it.
    public const int DivergenceLimit = 20;

    private readonly List<string> _divergences = new();

    public IReadOnlyList<string> Divergences => _divergences;
    public long CasesChecked { get; private set; }
    public long SuccessfulParses { get; private set; }
    public long CrossFormComparisons { get; private set; }
    public long ExactlyRewriteComparisons { get; private set; }
    public long OrReorderComparisons { get; private set; }

    public void RunSeeds(int firstSeed, int seedCount)
    {
        for (int seed = firstSeed; seed < firstSeed + seedCount && _divergences.Count < DivergenceLimit; seed++)
        {
            try
            {
                RunSeed(seed);
            }
            catch (Exception exception)
            {
                _divergences.Add($"HARNESS seed {seed}: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private void RunSeed(int seed)
    {
        var blueprint = FuzzGenerator.Generate(seed, wrapAt: -1, "Alias", out int nodeCount);
        int wrapTarget = new Random(seed * 31 + 7).Next(nodeCount);
        var blueprintWithAlias = FuzzGenerator.Generate(seed, wrapTarget, "Alias", out _);
        var blueprintWithLateBound = FuzzGenerator.Generate(seed, wrapTarget, "LateBound", out _);

        bool grammarIsNfc = FuzzGenerator.AllGrammarText(blueprint).All(SafeIsNfc);
        bool formDComparable = !FuzzGenerator.ContainsRuneLevelMatching(blueprint);

        // The neutral rewrites from retired backlog item 0a02. The And wrap always
        // applies. The other two apply only where their eligibility rules
        // hold (see FuzzRewriter), so they come back null when the
        // blueprint has no rewritable spot.
        var blueprintAndWrap = new FuzzListNode("And", new[] { blueprint });
        var blueprintExactlyRewrite = FuzzRewriter.RewriteExactlyToAnd(blueprint);
        var blueprintOrReorder = FuzzRewriter.ReverseEligibleOrAlternatives(blueprint);

        Rule grammarFormC, grammarNull, grammarAlias, grammarLateBound, grammarFormD;
        try
        {
            grammarFormC = blueprint.Build().Compile(NormalizationForm.FormC);
            grammarNull = blueprint.Build().Compile(null);
            grammarAlias = blueprintWithAlias.Build().Compile(NormalizationForm.FormC);
            grammarLateBound = blueprintWithLateBound.Build().Compile(NormalizationForm.FormC);
            grammarFormD = blueprint.Build().Compile(NormalizationForm.FormD);
        }
        catch (Exception exception)
        {
            _divergences.Add($"COMPILE seed {seed}: {exception.GetType().Name}: {exception.Message}\n  grammar: {blueprint.Show()}");
            return;
        }

        // The plain grammar compiled, so a rewrite variant that fails to
        // compile is itself a divergence: a neutral rewrite must stay
        // compilable. Compiled one variant per try block so the
        // divergence names the variant and shows its expression, which
        // together with the seed is the actual repro.
        Rule grammarAndWrap;
        Rule? grammarExactlyRewrite, grammarOrReorder;
        try
        {
            grammarAndWrap = blueprintAndWrap.Build().Compile(NormalizationForm.FormC);
        }
        catch (Exception exception)
        {
            _divergences.Add(RewriteCompileDivergence("ANDWRAP", seed, exception, blueprint, blueprintAndWrap));
            return;
        }
        try
        {
            grammarExactlyRewrite = blueprintExactlyRewrite?.Build().Compile(NormalizationForm.FormC);
        }
        catch (Exception exception)
        {
            _divergences.Add(RewriteCompileDivergence("EXACTLY-TO-AND", seed, exception, blueprint, blueprintExactlyRewrite!));
            return;
        }
        try
        {
            grammarOrReorder = blueprintOrReorder?.Build().Compile(NormalizationForm.FormC);
        }
        catch (Exception exception)
        {
            _divergences.Add(RewriteCompileDivergence("OR-REORDER", seed, exception, blueprint, blueprintOrReorder!));
            return;
        }

        foreach (string input in FuzzGenerator.MakeInputs(blueprint, seed))
        {
            CasesChecked++;
            string context = $"seed {seed} input {FuzzText.Quote(input)}\n  grammar: {blueprint.Show()}";

            var first = grammarFormC.Parse(input);
            if (first.Success) SuccessfulParses++;
            var again = grammarFormC.Parse(input);
            var withAlias = grammarAlias.Parse(input);
            var withLateBound = grammarLateBound.Parse(input);
            var debug = grammarFormC.Parse(input, new ParseOptions { PreserveAllSymbols = true });

            string? why = DiffResults(first, again);
            if (why != null) { _divergences.Add($"DETERMINISM {why}\n  {context}"); continue; }

            if (grammarIsNfc && SafeIsNfc(input))
            {
                var underNull = grammarNull.Parse(input);
                why = DiffResults(first, underNull);
                if (why != null) { _divergences.Add($"FORMC-VS-NULL {why}\n  {context}"); continue; }
            }

            if (formDComparable)
            {
                CrossFormComparisons++;
                var underFormD = grammarFormD.Parse(input);
                if (first.Outcome != underFormD.Outcome)
                    _divergences.Add($"FORMC-VS-FORMD outcome {first.Outcome} vs {underFormD.Outcome}\n  {context}");
                else if (!first.Success)
                {
                    if (first.ErrorCharIndex != underFormD.ErrorCharIndex || first.ErrorMessage != underFormD.ErrorMessage)
                        _divergences.Add($"FORMC-VS-FORMD error ({first.ErrorCharIndex} \"{first.ErrorMessage}\") vs ({underFormD.ErrorCharIndex} \"{underFormD.ErrorMessage}\")\n  {context}");
                }
                else
                {
                    string textTreeC = DumpBySourceText(first.Symbols, grammarFormC);
                    string textTreeD = DumpBySourceText(underFormD.Symbols, grammarFormD);
                    if (textTreeC != textTreeD)
                        _divergences.Add($"FORMC-VS-FORMD tree\n  C: {textTreeC}\n  D: {textTreeD}\n  {context}");
                    else
                        CheckRanges(underFormD, input, context + " [FormD]");
                }
            }

            CompareNeutralVariant("ALIAS", first, withAlias, grammarFormC, grammarAlias, blueprintWithAlias, context);
            CompareNeutralVariant("LATEBOUND", first, withLateBound, grammarFormC, grammarLateBound, blueprintWithLateBound, context);

            var withAndWrap = grammarAndWrap.Parse(input);
            CompareNeutralVariant("ANDWRAP", first, withAndWrap, grammarFormC, grammarAndWrap, blueprintAndWrap, context);
            if (withAndWrap.Success) CheckRanges(withAndWrap, input, context + " [andWrap variant]");

            if (grammarExactlyRewrite != null)
            {
                ExactlyRewriteComparisons++;
                var withExactlyRewrite = grammarExactlyRewrite.Parse(input);
                // The rewrite renames the rewritten node's class-derived
                // fallback label (Exactly becomes And), so its tree
                // comparison uses user-assigned names only.
                CompareNeutralVariant("EXACTLY-TO-AND", first, withExactlyRewrite, grammarFormC, grammarExactlyRewrite,
                    blueprintExactlyRewrite!, context, compareTreesByUserNamesOnly: true);
                if (withExactlyRewrite.Success) CheckRanges(withExactlyRewrite, input, context + " [exactlyRewrite variant]");
            }

            if (grammarOrReorder != null)
            {
                OrReorderComparisons++;
                var withOrReorder = grammarOrReorder.Parse(input);
                CompareNeutralVariant("OR-REORDER", first, withOrReorder, grammarFormC, grammarOrReorder,
                    blueprintOrReorder!, context);
                if (withOrReorder.Success) CheckRanges(withOrReorder, input, context + " [orReorder variant]");
            }

            if (first.Outcome != debug.Outcome)
                _divergences.Add($"DEBUG outcome {first.Outcome} vs {debug.Outcome}\n  {context}");
            else if (!first.Success)
            {
                if (first.ErrorCharIndex != debug.ErrorCharIndex || first.ErrorMessage != debug.ErrorMessage)
                    _divergences.Add($"DEBUG error ({first.ErrorCharIndex} \"{first.ErrorMessage}\") vs ({debug.ErrorCharIndex} \"{debug.ErrorMessage}\")\n  {context}");
            }
            else
            {
                string flattened = DumpIds(debug.Symbols.SelectMany(s => s.Flatten()).ToList());
                string production = DumpIds(first.Symbols);
                if (flattened != production)
                    _divergences.Add($"ROUNDTRIP\n  flattened: {flattened}\n  production: {production}\n  {context}");
            }

            if (first.Success) CheckRanges(first, input, context);
            if (withAlias.Success) CheckRanges(withAlias, input, context + " [alias variant]");
            if (withLateBound.Success) CheckRanges(withLateBound, input, context + " [lateBound variant]");

            if (_divergences.Count >= DivergenceLimit) return;
        }
    }

    // A neutral variant (transparent stand-in wrap, whole-grammar And
    // wrap, Exactly-to-And with a shared inner instance, eligible-Or
    // reversal) shouldn't change the outcome, the error report, or the
    // tree. Trees compare by name because the variant's extra or altered
    // rules shift anonymous id assignment, and NameOf labels are stable
    // across the two variants. The one exception is a variant that
    // changes a rule's class (Exactly-to-And): its class-derived fallback
    // label changes too, so that comparison passes
    // compareTreesByUserNamesOnly to use user-assigned names only.
    private void CompareNeutralVariant(
        string label, ParseResult plain, ParseResult variant,
        Rule plainGrammar, Rule variantGrammar, FuzzNode variantBlueprint, string context,
        bool compareTreesByUserNamesOnly = false)
    {
        if (plain.Outcome != variant.Outcome)
            _divergences.Add($"{label} outcome {plain.Outcome} vs {variant.Outcome}\n  {context}\n  variant grammar: {variantBlueprint.Show()}");
        else if (!plain.Success)
        {
            if (plain.ErrorCharIndex != variant.ErrorCharIndex || plain.ErrorMessage != variant.ErrorMessage)
                _divergences.Add($"{label} error ({plain.ErrorCharIndex} \"{plain.ErrorMessage}\") vs ({variant.ErrorCharIndex} \"{variant.ErrorMessage}\")\n  {context}\n  variant grammar: {variantBlueprint.Show()}");
        }
        else
        {
            string namedPlain = compareTreesByUserNamesOnly
                ? DumpBySourceText(plain.Symbols, plainGrammar)
                : DumpNamed(plain.Symbols, plainGrammar);
            string namedVariant = compareTreesByUserNamesOnly
                ? DumpBySourceText(variant.Symbols, variantGrammar)
                : DumpNamed(variant.Symbols, variantGrammar);
            if (namedPlain != namedVariant)
                _divergences.Add($"{label} tree\n  plain: {namedPlain}\n  variant: {namedVariant}\n  {context}\n  variant grammar: {variantBlueprint.Show()}");
        }
    }

    private static string RewriteCompileDivergence(
        string label, int seed, Exception exception, FuzzNode plainBlueprint, FuzzNode variantBlueprint) =>
        $"REWRITE-COMPILE {label} seed {seed}: {exception.GetType().Name}: {exception.Message}" +
        $"\n  grammar: {plainBlueprint.Show()}\n  variant grammar: {variantBlueprint.Show()}";

    // Full comparison for variants that assign identical ids (same
    // blueprint, same Compile walk order): outcome, error report on
    // failure, id-level tree on success. Returns null when they agree.
    private static string? DiffResults(ParseResult a, ParseResult b)
    {
        if (a.Outcome != b.Outcome) return $"outcome {a.Outcome} vs {b.Outcome}";
        if (!a.Success)
        {
            if (a.ErrorCharIndex != b.ErrorCharIndex) return $"error index {a.ErrorCharIndex} vs {b.ErrorCharIndex}";
            if (a.ErrorMessage != b.ErrorMessage) return $"error message \"{a.ErrorMessage}\" vs \"{b.ErrorMessage}\"";
            return null;
        }
        string left = DumpIds(a.Symbols);
        string right = DumpIds(b.Symbols);
        return left == right ? null : $"tree\n  a: {left}\n  b: {right}";
    }

    private void CheckRanges(ParseResult result, string input, string context)
    {
        var stack = new Stack<Symbol>(result.Symbols);
        while (stack.Count > 0)
        {
            var symbol = stack.Pop();
            foreach (var child in symbol.Children) stack.Push(child);
            var range = symbol.SourceRange;
            if (range == null) continue;
            int start = range.Value.Start.CharIndex;
            int end = range.Value.End.CharIndex;
            if (start < 0 || end > input.Length || end < start)
            {
                _divergences.Add($"RANGE out of bounds [{start},{end}) for input length {input.Length}\n  {context}");
                continue;
            }
            string viaRange = input.Substring(start, end - start);
            if (viaRange != symbol.SourceText)
                _divergences.Add($"RANGE/TEXT range gives {FuzzText.Quote(viaRange)}, SourceText gives {FuzzText.Quote(symbol.SourceText)}\n  {context}");
        }
    }

    internal static bool SafeIsNfc(string s)
    {
        try { return s.IsNormalized(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }
    }

    private static string DumpIds(IReadOnlyList<Symbol> symbols)
    {
        var builder = new StringBuilder();
        void Walk(Symbol s)
        {
            builder.Append(s.Id.Value).Append(s.IsLeaf ? "L" : "C").Append('[').Append(s).Append(']');
            if (!s.IsLeaf)
            {
                builder.Append('(');
                foreach (var child in s.Children) Walk(child);
                builder.Append(')');
            }
        }
        foreach (var s in symbols) Walk(s);
        return builder.ToString();
    }

    // Name-level dump for the neutral-variant comparisons (ALIAS,
    // LATEBOUND, ANDWRAP, OR-REORDER): a variant's inserted rule or
    // altered Compile walk order shifts anonymous id assignment, so
    // labels come from NameOf, which is stable across the two variants.
    private static string DumpNamed(IReadOnlyList<Symbol> symbols, Rule grammarRoot)
    {
        var builder = new StringBuilder();
        void Walk(Symbol s)
        {
            builder.Append(grammarRoot.NameOf(s.Id) ?? "?").Append(s.IsLeaf ? "L" : "C").Append('[').Append(s).Append(']');
            if (!s.IsLeaf)
            {
                builder.Append('(');
                foreach (var child in s.Children) Walk(child);
                builder.Append(')');
            }
        }
        foreach (var s in symbols) Walk(s);
        return builder.ToString();
    }

    // Dump keyed on user names and SourceText only. Two users: the
    // cross-form comparison, where class fallbacks and rune labels are
    // form-sensitive when FormD splits a single-rune token and SourceText
    // (original coordinates) sidesteps ToString (normalized text), and
    // the Exactly-to-And comparison, where the rewrite changes the
    // rewritten node's class-derived fallback label (Exactly becomes And).
    private static string DumpBySourceText(IReadOnlyList<Symbol> symbols, Rule grammarRoot)
    {
        var builder = new StringBuilder();
        void Walk(Symbol s)
        {
            builder.Append(grammarRoot.UserNameOf(s.Id) ?? "_").Append(s.IsLeaf ? "L" : "C").Append('[').Append(FuzzText.Quote(s.SourceText)).Append(']');
            if (!s.IsLeaf)
            {
                builder.Append('(');
                foreach (var child in s.Children) Walk(child);
                builder.Append(')');
            }
        }
        foreach (var s in symbols) Walk(s);
        return builder.ToString();
    }
}

// Quoting for divergence messages and blueprint Show() renderings.
// DisplayEscape is the library's own one-line-safe rendering: control
// and line-separator characters come out as U+XXXX forms, and printable
// text (non-ASCII included) stays verbatim.
internal static class FuzzText
{
    public static string Quote(string s) => $"\"{DisplayEscape.Escape(s, 0, s.Length)}\"";
}

// The Unicode pools the generator draws from, derived from
// UnicodeExamples.AllStringConstants() so a constant added to the corpus
// flows into fuzzed grammars automatically.
internal static class FuzzCorpus
{
    public static readonly IReadOnlyList<string> SingleClusters;
    public static readonly IReadOnlyList<string> SingleRuneClusters;
    public static readonly IReadOnlyList<string> WellFormedTexts;
    public static readonly IReadOnlyList<string> IllFormedTexts;
    public static readonly IReadOnlyList<string> IdentifierTexts;

    static FuzzCorpus()
    {
        var pairs = UnicodeExamples.AllStringConstants().ToList();
        var all = pairs.Select(pair => pair.Value).ToList();

        // Grammar-text pools exclude strings string.Normalize rejects
        // (lone surrogates, U+FFFE): a Literal or Token holding one is a
        // documented Compile-time offender under a normalizing Compile,
        // so it would only add noise here. Those strings stay available
        // as inputs, where MalformedInput is the expected outcome.
        SingleClusters = all
            .Where(value => value.Length > 0 && IsWellFormed(value) && CanNormalize(value)
                && StringInfo.GetNextTextElement(value, 0).Length == value.Length)
            .Distinct()
            .ToList();
        WellFormedTexts = all
            .Where(value => value.Length > 0 && IsWellFormed(value) && CanNormalize(value))
            .Distinct()
            .ToList();
        IllFormedTexts = all
            .Where(value => value.Length > 0 && !IsWellFormed(value))
            .Distinct()
            .ToList();
        // Clusters that are exactly one rune (one char, or one surrogate
        // pair). WithinToken inner sets draw from this pool: the
        // one-rune-per-token sub-lexer can never produce a token equal
        // to a multi-rune member, so a multi-rune member there would be
        // unmatchable.
        SingleRuneClusters = SingleClusters
            .Where(value => value.Length == 1
                || (value.Length == 2 && char.IsHighSurrogate(value[0])))
            .ToList();
        // Corpus entries whose constant name marks them as identifiers
        // (GreekKalimeraIdentifier and friends). Sample inputs for
        // generated Identifier rules, keyed off the naming convention so
        // new corpus identifiers flow in automatically.
        var identifierTexts = pairs
            .Where(pair => pair.Name.EndsWith("Identifier", StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .Where(value => value.Length > 0 && IsWellFormed(value) && CanNormalize(value))
            .Distinct()
            .ToList();
        if (identifierTexts.Count == 0) identifierTexts.Add("abc");
        IdentifierTexts = identifierTexts;
    }

    public static bool CanNormalize(string s)
    {
        try
        {
            s.Normalize(NormalizationForm.FormC);
            s.Normalize(NormalizationForm.FormD);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public static bool IsWellFormed(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(s[i])) return false;
        }
        return true;
    }
}

// A blueprint is a buildable description of a grammar. Build() constructs
// fresh Rule instances every call, so one blueprint can produce the
// FormC, FormD, and null-normalization variants of the same grammar
// (compiled Rules are single-use). Show() renders the grammar expression
// for divergence messages.

internal abstract class FuzzNode
{
    public string? AsName;
    public FlattenType? ExplicitFlatten;
    public string? ErrorText;
    public bool ErrorForced;

    // True for nodes whose Build() result rejects .As / .Flatten /
    // .WithError (a LateBoundRule root). The generator still rolls the
    // modifier dice for these so seed alignment holds, and discards the
    // result.
    public virtual bool SkipModifiers => false;

    public abstract Rule BuildCore();
    public abstract string ShowCore();
    public virtual IEnumerable<FuzzNode> ChildNodes => Array.Empty<FuzzNode>();
    public virtual IEnumerable<string> GrammarText => Array.Empty<string>();

    public Rule Build()
    {
        var rule = BuildCore();
        if (ExplicitFlatten != null) rule = rule.Flatten(ExplicitFlatten.Value);
        if (AsName != null) rule = rule.As(AsName);
        if (ErrorText != null) rule = rule.WithError(ErrorText, ErrorForced);
        return rule;
    }

    public string Show()
    {
        var text = ShowCore();
        if (ExplicitFlatten != null) text += $".Flatten({ExplicitFlatten})";
        if (AsName != null) text += $".As(\"{AsName}\")";
        if (ErrorText != null) text += ErrorForced ? $".WithError(\"{ErrorText}\", forced: true)" : $".WithError(\"{ErrorText}\")";
        return text;
    }
}

internal sealed class FuzzTokenNode : FuzzNode
{
    public readonly char C;
    public FuzzTokenNode(char c) { C = c; }
    public override Rule BuildCore() => Token(C);
    public override string ShowCore() => $"Token('{(C == '\\' ? "\\\\" : C.ToString())}')";
    public override IEnumerable<string> GrammarText => new[] { C.ToString() };
}

internal sealed class FuzzGraphemeTokenNode : FuzzNode
{
    public readonly string Grapheme;
    public FuzzGraphemeTokenNode(string grapheme) { Grapheme = grapheme; }
    public override Rule BuildCore() => Token(Grapheme);
    public override string ShowCore() => $"Token({FuzzText.Quote(Grapheme)})";
    public override IEnumerable<string> GrammarText => new[] { Grapheme };
}

internal sealed class FuzzLiteralNode : FuzzNode
{
    public readonly string S;
    public readonly bool IgnoreCase;
    public FuzzLiteralNode(string s, bool ignoreCase) { S = s; IgnoreCase = ignoreCase; }
    public override Rule BuildCore() => IgnoreCase ? LiteralIgnoreAsciiCase(S) : Literal(S);
    public override string ShowCore() => $"{(IgnoreCase ? "LiteralIgnoreAsciiCase" : "Literal")}({FuzzText.Quote(S)})";
    public override IEnumerable<string> GrammarText => new[] { S };
}

internal sealed class FuzzSetLeafNode : FuzzNode
{
    public readonly string Kind;
    public readonly char[] Chars;
    public readonly string[] GraphemeMembers;
    public readonly int ScanWhileMinimum;

    public FuzzSetLeafNode(string kind, char[] chars, string[] graphemeMembers, int scanWhileMinimum = 1)
    {
        Kind = kind;
        Chars = chars;
        GraphemeMembers = graphemeMembers;
        ScanWhileMinimum = scanWhileMinimum;
    }

    public static TokenSet BuildSetFrom(char[] chars, string[] graphemeMembers)
    {
        TokenSet set = TokenSet.Empty;
        foreach (char c in chars) set |= TokenSet.Single(c);
        foreach (string grapheme in graphemeMembers) set |= TokenSet.Graphemes(grapheme);
        return set;
    }

    public TokenSet BuildSet() => BuildSetFrom(Chars, GraphemeMembers);

    public override Rule BuildCore() => Kind switch
    {
        "OneOf" => OneOf(BuildSet()),
        "NoneOf" => NoneOf(BuildSet()),
        "ScanWhile" => ScanWhile(BuildSet(), ScanWhileMinimum),
        "ScanUntil" => ScanUntil(BuildSet()),
        "ScanUntilEof" => ScanUntil(BuildSet(), eofIsTerminator: true),
        "ScanUntilEscape" => ScanUntil(BuildSet(), new Rune('\\'), AnyToken()),
        _ => throw new InvalidOperationException(Kind),
    };

    public override string ShowCore()
    {
        var members = new StringBuilder();
        members.Append(FuzzText.Quote(new string(Chars)));
        foreach (var grapheme in GraphemeMembers) members.Append('+').Append(FuzzText.Quote(grapheme));
        string set = $"set({members})";
        return Kind switch
        {
            "ScanWhile" => ScanWhileMinimum == 1 ? $"ScanWhile({set})" : $"ScanWhile({set}, minimumCount: {ScanWhileMinimum})",
            "ScanUntilEof" => $"ScanUntil({set}, eofIsTerminator: true)",
            "ScanUntilEscape" => $"ScanUntil({set}, '\\\\', AnyToken())",
            _ => $"{Kind}({set})",
        };
    }

    // The escape-start rune is part of the grammar's text for the form
    // gates: it's hardcoded ASCII today, but reporting it here keeps the
    // FormC-vs-null and FormC-vs-FormD eligibility checks correct if it
    // ever becomes a corpus draw.
    public override IEnumerable<string> GrammarText =>
        Chars.Select(c => c.ToString())
            .Concat(GraphemeMembers)
            .Concat(Kind == "ScanUntilEscape" ? new[] { "\\" } : Array.Empty<string>());
}

// No-argument factory calls. "Leaf" describes the blueprint shape:
// Integer, Float, and the whitespace factories construct composite rule
// graphs underneath, the blueprint just has nothing to configure on them.
internal sealed class FuzzSimpleLeafNode : FuzzNode
{
    public readonly string Kind;
    public FuzzSimpleLeafNode(string kind) { Kind = kind; }
    public override Rule BuildCore() => Kind switch
    {
        "AnyToken" => AnyToken(),
        "Eof" => Eof(),
        "EndOfLine" => EndOfLine(),
        "EndOfLineEofIsEol" => EndOfLine(eofIsEol: true),
        "ScanUntilEof" => ScanUntilEof(),
        "InlineWhitespace" => InlineWhitespace(),
        "AnyWhitespace" => AnyWhitespace(),
        "Integer" => Integer(),
        "Float" => Float(),
        _ => throw new InvalidOperationException(Kind),
    };
    public override string ShowCore() =>
        Kind == "EndOfLineEofIsEol" ? "EndOfLine(eofIsEol: true)" : $"{Kind}()";
}

internal sealed class FuzzListNode : FuzzNode
{
    public readonly string Kind;
    public readonly FuzzNode[] Items;
    public FuzzListNode(string kind, FuzzNode[] items) { Kind = kind; Items = items; }
    public override IEnumerable<FuzzNode> ChildNodes => Items;
    public override Rule BuildCore()
    {
        var built = Items.Select(i => i.Build()).ToArray();
        // Throws on an unknown Kind like every other node's switch, so a
        // typo'd Kind can't silently build as an Or while FuzzRewriter's
        // analyses treat it as something else.
        return Kind switch
        {
            "And" => And(built),
            "Or" => Or(built),
            _ => throw new InvalidOperationException(Kind),
        };
    }
    public override string ShowCore() => $"{Kind}({string.Join(", ", Items.Select(i => i.Show()))})";
}

internal sealed class FuzzRepetitionNode : FuzzNode
{
    public readonly string Kind;
    public readonly FuzzNode Inner;
    public readonly int Count;
    public readonly int UpperCount;
    public FuzzRepetitionNode(string kind, FuzzNode inner, int count, int upperCount = 0)
    {
        Kind = kind;
        Inner = inner;
        Count = count;
        UpperCount = upperCount;
    }
    public override IEnumerable<FuzzNode> ChildNodes => new[] { Inner };
    public override Rule BuildCore() => Kind switch
    {
        "Optional" => Optional(Inner.Build()),
        "ZeroOrMore" => ZeroOrMore(Inner.Build()),
        "OneOrMore" => OneOrMore(Inner.Build()),
        "Exactly" => Exactly(Count, Inner.Build()),
        "AtLeast" => AtLeast(Count, Inner.Build()),
        "AtMost" => AtMost(Count, Inner.Build()),
        "Between" => BetweenInclusive(Count, UpperCount, Inner.Build()),
        _ => throw new InvalidOperationException(Kind),
    };
    public override string ShowCore() => Kind switch
    {
        "Exactly" => $"Exactly({Count}, {Inner.Show()})",
        "AtLeast" => $"AtLeast({Count}, {Inner.Show()})",
        "AtMost" => $"AtMost({Count}, {Inner.Show()})",
        "Between" => $"BetweenInclusive({Count}, {UpperCount}, {Inner.Show()})",
        _ => $"{Kind}({Inner.Show()})",
    };
}

// The Exactly(n, x) neutral rewrite target: an And of n references to
// one shared x instance. Build() constructs the inner once and repeats
// the reference, because building x twice would duplicate any .As name
// inside it, and two reachable rules sharing a name is a Compile-time
// user error. Repeating one instance is the documented shared-rule
// shape. Only FuzzRewriter creates these, the generator never does.
internal sealed class FuzzSharedSequenceNode : FuzzNode
{
    public readonly FuzzNode Inner;
    public readonly int RepeatCount;

    public FuzzSharedSequenceNode(FuzzNode inner, int repeatCount)
    {
        Inner = inner;
        RepeatCount = repeatCount;
    }

    public override IEnumerable<FuzzNode> ChildNodes => new[] { Inner };

    public override Rule BuildCore()
    {
        var shared = Inner.Build();
        return And(Enumerable.Repeat(shared, RepeatCount).ToArray());
    }

    public override string ShowCore() =>
        $"AndSharedRepeat({RepeatCount}, {Inner.Show()})";
}

internal sealed class FuzzWrapNode : FuzzNode
{
    public readonly string Kind;
    public readonly FuzzNode Inner;
    public FuzzWrapNode(string kind, FuzzNode inner) { Kind = kind; Inner = inner; }
    public override IEnumerable<FuzzNode> ChildNodes => new[] { Inner };
    public override Rule BuildCore() => Kind switch
    {
        "Not" => Not(Inner.Build()),
        "Peek" => Peek(Inner.Build()),
        "Alias" => Alias(Inner.Build()),
        "LateBound" => new LateBoundRule("fuzzWrap").Bind(Inner.Build()),
        "WithinToken" => WithinToken(Inner.Build()),
        _ => throw new InvalidOperationException(Kind),
    };
    public override string ShowCore() => $"{Kind}({Inner.Show()})";
}

internal sealed class FuzzScanUntilRuleStopNode : FuzzNode
{
    public readonly FuzzNode Stopper;
    public readonly bool EofIsTerminator;
    public readonly bool WithRuneEscape;

    public FuzzScanUntilRuleStopNode(FuzzNode stopper, bool eofIsTerminator, bool withRuneEscape = false)
    {
        Stopper = stopper;
        EofIsTerminator = eofIsTerminator;
        WithRuneEscape = withRuneEscape;
    }

    public override IEnumerable<FuzzNode> ChildNodes => new[] { Stopper };

    public override Rule BuildCore() => WithRuneEscape
        ? ScanUntil(Stopper.Build(), new Rune('\\'), AnyToken(), EofIsTerminator)
        : ScanUntil(Stopper.Build(), EofIsTerminator);

    // Same reasoning as FuzzSetLeafNode: the escape rune is grammar text
    // for the form gates.
    public override IEnumerable<string> GrammarText =>
        WithRuneEscape ? new[] { "\\" } : Array.Empty<string>();

    public override string ShowCore()
    {
        string escape = WithRuneEscape ? ", '\\\\', AnyToken()" : "";
        string eof = EofIsTerminator ? ", eofIsTerminator: true" : "";
        return $"ScanUntil({Stopper.Show()}{escape}{eof})";
    }
}

// The rule-valued escape-start ScanUntil overload: a TokenSet stopper
// with a sub-rule that recognizes the escape opener (the "${" shape).
// The escape end is AnyToken, matching the set-based escape node.
internal sealed class FuzzScanUntilEscapeStartRuleNode : FuzzNode
{
    public readonly char[] Chars;
    public readonly string[] GraphemeMembers;
    public readonly FuzzNode EscapeStart;
    public readonly bool EofIsTerminator;

    public FuzzScanUntilEscapeStartRuleNode(char[] chars, string[] graphemeMembers, FuzzNode escapeStart, bool eofIsTerminator)
    {
        Chars = chars;
        GraphemeMembers = graphemeMembers;
        EscapeStart = escapeStart;
        EofIsTerminator = eofIsTerminator;
    }

    public override IEnumerable<FuzzNode> ChildNodes => new[] { EscapeStart };

    public override Rule BuildCore() =>
        ScanUntil(FuzzSetLeafNode.BuildSetFrom(Chars, GraphemeMembers), EscapeStart.Build(), AnyToken(), EofIsTerminator);

    public override IEnumerable<string> GrammarText =>
        Chars.Select(c => c.ToString()).Concat(GraphemeMembers);

    public override string ShowCore()
    {
        var members = new StringBuilder();
        members.Append(FuzzText.Quote(new string(Chars)));
        foreach (var grapheme in GraphemeMembers) members.Append('+').Append(FuzzText.Quote(grapheme));
        string eof = EofIsTerminator ? ", eofIsTerminator: true" : "";
        return $"ScanUntil(set({members}), {EscapeStart.Show()}, AnyToken(){eof})";
    }
}

// Identifier with the default UAX #31 profile, or the underscore
// profile a programming-language grammar typically uses. SampleText is
// a corpus identifier the input generator plants so generated
// Identifier rules actually get matching input.
internal sealed class FuzzIdentifierNode : FuzzNode
{
    public readonly string SampleText;
    public readonly bool UnderscoreExtras;

    public FuzzIdentifierNode(string sampleText, bool underscoreExtras)
    {
        SampleText = sampleText;
        UnderscoreExtras = underscoreExtras;
    }

    public override Rule BuildCore() => UnderscoreExtras
        ? Identifier(TokenSet.Runes("_"), TokenSet.Runes("_"))
        : Identifier();

    public override IEnumerable<string> GrammarText =>
        UnderscoreExtras ? new[] { "_" } : Array.Empty<string>();

    public override string ShowCore() =>
        UnderscoreExtras ? "Identifier(\"_\", \"_\")" : "Identifier()";
}

// A bounded recursive grammar: lateBound = Or(baseCase, And(open,
// lateBound-or-alias, close)). The opener always consumes at least one
// token, so recursion depth is bounded by the input length.
internal sealed class FuzzRecursionNode : FuzzNode
{
    public readonly FuzzNode Open;
    public readonly FuzzNode BaseCase;
    public readonly FuzzNode Close;
    public readonly bool ViaAlias;

    public FuzzRecursionNode(FuzzNode open, FuzzNode baseCase, FuzzNode close, bool viaAlias)
    {
        Open = open;
        BaseCase = baseCase;
        Close = close;
        ViaAlias = viaAlias;
    }

    public override bool SkipModifiers => true;
    public override IEnumerable<FuzzNode> ChildNodes => new[] { Open, BaseCase, Close };

    public override Rule BuildCore()
    {
        var lateBound = new LateBoundRule("recursion");
        Rule reference = ViaAlias ? Alias(lateBound) : lateBound;
        var body = Or(BaseCase.Build(), And(Open.Build(), reference, Close.Build()));
        lateBound.Bind(body);
        return lateBound;
    }

    public override string ShowCore() =>
        $"Recursion[open: {Open.Show()}, base: {BaseCase.Show()}, close: {Close.Show()}{(ViaAlias ? ", viaAlias" : "")}]";
}

internal static class FuzzGenerator
{
    private const string CharPool = "abc01=,[]\" \\";

    // Generates the blueprint for `seed`. Pass wrapAt = -1 for the plain
    // grammar, or a node index to wrap that node in a transparent
    // stand-in (wrapKind "Alias" or "LateBound"). The wrap consumes no
    // random draws, so the same seed produces the same structure with or
    // without it, which is what makes the wrap-neutrality comparisons
    // valid.
    public static FuzzNode Generate(int seed, int wrapAt, string wrapKind, out int nodeCount)
    {
        var state = new GeneratorState(new Random(seed), wrapAt, wrapKind);
        var inner = state.GenerateNode(0);
        nodeCount = state.Counter;
        return new FuzzListNode("And", new[] { inner }) { ExplicitFlatten = FlattenType.Preserve };
    }

    public static IEnumerable<string> AllGrammarText(FuzzNode blueprint)
    {
        foreach (var text in blueprint.GrammarText) yield return text;
        foreach (var child in blueprint.ChildNodes)
            foreach (var text in AllGrammarText(child)) yield return text;
    }

    // True when the blueprint holds a rule that matches runes inside a
    // token (WithinToken, Identifier). Those are form-sensitive by
    // design, so the FormC-vs-FormD oracle skips grammars containing one.
    public static bool ContainsRuneLevelMatching(FuzzNode blueprint)
    {
        if (blueprint is FuzzWrapNode wrap && wrap.Kind == "WithinToken") return true;
        if (blueprint is FuzzIdentifierNode) return true;
        return blueprint.ChildNodes.Any(ContainsRuneLevelMatching);
    }

    public static IEnumerable<string> MakeInputs(FuzzNode blueprint, int seed)
    {
        var random = new Random(seed * 17 + 3);
        var fragments = new List<string>();
        Collect(blueprint, fragments);

        // Canonically recoded variants of the grammar's own terminals, so
        // a FormC grammar meets FormD-coded input and the other way around.
        foreach (var fragment in fragments.Take(12).ToList())
        {
            string formC = SafeNormalize(fragment, NormalizationForm.FormC);
            string formD = SafeNormalize(fragment, NormalizationForm.FormD);
            if (formC != fragment) fragments.Add(formC);
            if (formD != fragment) fragments.Add(formD);
        }

        fragments.AddRange(new[] { "x", UnicodeExamples.LatinEAcutePrecomposedGrapheme, " ", "\n", "\r\n", "zz" });
        for (int i = 0; i < 3; i++)
            fragments.Add(FuzzCorpus.WellFormedTexts[random.Next(FuzzCorpus.WellFormedTexts.Count)]);
        if (FuzzCorpus.IllFormedTexts.Count > 0 && random.Next(100) < 30)
            fragments.Add(FuzzCorpus.IllFormedTexts[random.Next(FuzzCorpus.IllFormedTexts.Count)]);
        fragments = fragments.Where(f => f.Length > 0).Distinct().ToList();

        var inputs = new List<string> { "" };

        // A traversal-ordered concatenation approximates a matching input
        // for sequence-heavy grammars.
        var ordered = new List<string>();
        Collect(blueprint, ordered);
        inputs.Add(Truncate(string.Concat(ordered)));

        // Nested inputs for recursive grammars: open, repeated, then the
        // base case, then close repeated the same number of times.
        var recursion = FindRecursion(blueprint);
        if (recursion != null)
        {
            string open = string.Concat(CollectOne(recursion.Open));
            string baseCase = string.Concat(CollectOne(recursion.BaseCase));
            string close = string.Concat(CollectOne(recursion.Close));
            for (int nesting = 1; nesting <= 3; nesting++)
                inputs.Add(Truncate(string.Concat(Enumerable.Repeat(open, nesting)) + baseCase + string.Concat(Enumerable.Repeat(close, nesting))));
        }

        for (int i = 0; i < 11; i++)
        {
            var builder = new StringBuilder();
            int pieces = random.Next(0, 9);
            for (int pieceIndex = 0; pieceIndex < pieces && builder.Length < 30; pieceIndex++)
                builder.Append(fragments[random.Next(fragments.Count)]);
            inputs.Add(Truncate(builder.ToString()));
        }
        return inputs.Distinct();
    }

    // Inputs may legitimately be ill-formed, so a cut that splits a
    // surrogate pair is fine here: it just produces one more malformed
    // input, and the oracles that need well-formed input gate on it.
    private static string Truncate(string s) => s.Length <= 30 ? s : s.Substring(0, 30);

    private static string SafeNormalize(string s, NormalizationForm form)
    {
        try { return s.Normalize(form); }
        catch (ArgumentException) { return s; }
    }

    private static FuzzRecursionNode? FindRecursion(FuzzNode node)
    {
        if (node is FuzzRecursionNode recursion) return recursion;
        foreach (var child in node.ChildNodes)
        {
            var found = FindRecursion(child);
            if (found != null) return found;
        }
        return null;
    }

    private static List<string> CollectOne(FuzzNode node)
    {
        var list = new List<string>();
        Collect(node, list);
        return list;
    }

    private static void Collect(FuzzNode node, List<string> fragments)
    {
        switch (node)
        {
            case FuzzTokenNode token: fragments.Add(token.C.ToString()); break;
            case FuzzGraphemeTokenNode grapheme: fragments.Add(grapheme.Grapheme); break;
            case FuzzLiteralNode literal: fragments.Add(literal.S); break;
            case FuzzSetLeafNode set:
                foreach (char c in set.Chars.Take(2)) fragments.Add(c.ToString());
                foreach (string member in set.GraphemeMembers.Take(1)) fragments.Add(member);
                break;
            case FuzzScanUntilEscapeStartRuleNode escapeSet:
                foreach (char c in escapeSet.Chars.Take(2)) fragments.Add(c.ToString());
                foreach (string member in escapeSet.GraphemeMembers.Take(1)) fragments.Add(member);
                break;
            case FuzzScanUntilRuleStopNode ruleStop when ruleStop.WithRuneEscape:
                fragments.Add("\\");
                break;
            case FuzzIdentifierNode identifier:
                fragments.Add("abc");
                fragments.Add(identifier.SampleText);
                if (identifier.UnderscoreExtras) fragments.Add("_x");
                break;
            case FuzzSimpleLeafNode simple:
                switch (simple.Kind)
                {
                    case "EndOfLine": case "EndOfLineEofIsEol": fragments.Add("\n"); break;
                    case "InlineWhitespace": fragments.Add(" "); fragments.Add("\t"); break;
                    case "AnyWhitespace": fragments.Add(" "); fragments.Add("\n"); break;
                    case "Integer": fragments.Add("42"); fragments.Add("-7"); break;
                    case "Float": fragments.Add("3.25"); fragments.Add("-8.5"); fragments.Add("6e4"); break;
                }
                break;
        }
        foreach (var child in node.ChildNodes) Collect(child, fragments);
    }

    private sealed class GeneratorState
    {
        private readonly Random _random;
        private readonly int _wrapAt;
        private readonly string _wrapKind;
        public int Counter;
        private int _nameCounter;
        private int _errorCounter;
        private bool _recursionUsed;
        private char[]? _alphabet;

        public GeneratorState(Random random, int wrapAt, string wrapKind)
        {
            _random = random;
            _wrapAt = wrapAt;
            _wrapKind = wrapKind;
        }

        private char[] Alphabet => _alphabet ??= PickAlphabet();

        private char[] PickAlphabet()
        {
            int size = _random.Next(4, 7);
            var picked = new List<char>();
            for (int i = 0; i < size; i++)
            {
                char c = CharPool[_random.Next(CharPool.Length)];
                if (!picked.Contains(c)) picked.Add(c);
            }
            if (picked.Count == 0) picked.Add('a');
            return picked.ToArray();
        }

        public FuzzNode GenerateNode(int depth)
        {
            int index = Counter++;
            FuzzNode node = depth >= 4 || _random.Next(100) < 30 + depth * 15
                ? GenerateLeaf()
                : GenerateComposite(depth);
            ApplyModifiers(node);
            if (index == _wrapAt) node = new FuzzWrapNode(_wrapKind, node);
            return node;
        }

        private FuzzNode GenerateLeaf()
        {
            int roll = _random.Next(33);
            if (roll <= 2) return new FuzzTokenNode(Alphabet[_random.Next(Alphabet.Length)]);
            if (roll <= 4) return new FuzzGraphemeTokenNode(PickCluster());
            if (roll <= 6) return new FuzzLiteralNode(RandomText(1, 3), ignoreCase: false);
            if (roll == 7) return new FuzzLiteralNode(PickText(), ignoreCase: false);
            if (roll == 8) return new FuzzLiteralNode(RandomLetters(1, 2), ignoreCase: true);
            if (roll <= 10) return new FuzzSetLeafNode("OneOf", RandomSetChars(), RandomSetGraphemes());
            if (roll == 11) return new FuzzSetLeafNode("NoneOf", RandomSetChars(), RandomSetGraphemes());
            if (roll == 12) return new FuzzSetLeafNode("ScanWhile", RandomSetChars(), RandomSetGraphemes(), _random.Next(4));
            if (roll == 13) return new FuzzSetLeafNode("ScanUntil", RandomSetChars(), RandomSetGraphemes());
            if (roll == 14) return new FuzzSetLeafNode("ScanUntilEof", RandomSetChars(), RandomSetGraphemes());
            if (roll == 15) return new FuzzSetLeafNode("ScanUntilEscape", RandomSetChars(), RandomSetGraphemes());
            if (roll == 16) return new FuzzScanUntilRuleStopNode(GenerateStopper(), _random.Next(2) == 0);
            if (roll == 17) return new FuzzWrapNode("WithinToken", GenerateWithinTokenInner());
            if (roll <= 19) return new FuzzSimpleLeafNode("AnyToken");
            if (roll == 20) return new FuzzSimpleLeafNode("EndOfLine");
            if (roll == 21) return new FuzzSimpleLeafNode("Eof");
            if (roll == 22) return new FuzzSimpleLeafNode("ScanUntilEof");
            if (roll == 23) return new FuzzSimpleLeafNode("InlineWhitespace");
            if (roll == 24) return new FuzzSimpleLeafNode("AnyWhitespace");
            if (roll == 25) return new FuzzSimpleLeafNode("Integer");
            if (roll == 26) return new FuzzSimpleLeafNode("Float");
            if (roll == 27) return new FuzzSimpleLeafNode("EndOfLineEofIsEol");
            if (roll == 28) return new FuzzScanUntilRuleStopNode(GenerateStopper(), _random.Next(2) == 0, withRuneEscape: true);
            if (roll == 29) return new FuzzScanUntilEscapeStartRuleNode(RandomSetChars(), RandomSetGraphemes(), GenerateConsumingLeaf(), _random.Next(2) == 0);
            if (roll <= 31) return new FuzzIdentifierNode(PickIdentifierText(), _random.Next(2) == 0);
            return new FuzzTokenNode(Alphabet[_random.Next(Alphabet.Length)]);
        }

        private FuzzNode GenerateStopper()
        {
            int roll = _random.Next(4);
            if (roll == 0) return new FuzzSimpleLeafNode("Eof");
            if (roll == 1) return new FuzzListNode("Or", new FuzzNode[] { new FuzzTokenNode(Alphabet[_random.Next(Alphabet.Length)]), new FuzzSimpleLeafNode("Eof") });
            if (roll == 2) return new FuzzLiteralNode(RandomText(1, 2), ignoreCase: false);
            return new FuzzGraphemeTokenNode(PickCluster());
        }

        // Rune-level inner for WithinToken: it walks the runes inside one
        // outer token, so keep it small and always able to terminate.
        private FuzzNode GenerateWithinTokenInner()
        {
            int roll = _random.Next(4);
            if (roll == 0) return new FuzzRepetitionNode("OneOrMore", new FuzzSimpleLeafNode("AnyToken"), 0);
            if (roll == 1) return new FuzzRepetitionNode("Exactly", new FuzzSimpleLeafNode("AnyToken"), _random.Next(1, 4));
            if (roll == 2) return new FuzzListNode("And", new FuzzNode[]
            {
                new FuzzSimpleLeafNode("AnyToken"),
                new FuzzRepetitionNode("ZeroOrMore", new FuzzSetLeafNode("OneOf", Array.Empty<char>(), new[] { PickSingleRuneCluster() }), 0),
            });
            return new FuzzRepetitionNode("OneOrMore", new FuzzSetLeafNode("NoneOf", RandomSetChars(), Array.Empty<string>()), 0);
        }

        private FuzzNode GenerateComposite(int depth)
        {
            int roll = _random.Next(16);
            if (roll <= 2) return new FuzzListNode("And", GenerateChildren(depth, _random.Next(2, 4)));
            if (roll <= 4) return new FuzzListNode("Or", GenerateChildren(depth, _random.Next(2, 4)));
            if (roll == 5) return new FuzzRepetitionNode("Optional", GenerateNode(depth + 1), 0);
            if (roll == 6) return new FuzzRepetitionNode("ZeroOrMore", GenerateNode(depth + 1), 0);
            if (roll == 7) return new FuzzRepetitionNode("OneOrMore", GenerateNode(depth + 1), 0);
            if (roll == 8) return new FuzzRepetitionNode("Exactly", GenerateNode(depth + 1), _random.Next(1, 4));
            if (roll == 9) return new FuzzWrapNode("Not", GenerateNode(depth + 1));
            if (roll == 10) return new FuzzWrapNode("Peek", GenerateNode(depth + 1));
            if (roll == 11 && depth <= 1 && !_recursionUsed)
            {
                _recursionUsed = true;
                var open = GenerateConsumingLeaf();
                var baseCase = GenerateNode(depth + 1);
                var close = GenerateNode(depth + 1);
                return new FuzzRecursionNode(open, baseCase, close, _random.Next(2) == 0);
            }
            if (roll == 13) return new FuzzRepetitionNode("AtLeast", GenerateNode(depth + 1), _random.Next(0, 3));
            if (roll == 14) return new FuzzRepetitionNode("AtMost", GenerateNode(depth + 1), _random.Next(1, 4));
            if (roll == 15)
            {
                int lower = _random.Next(0, 3);
                int upper = lower + _random.Next(0, 3);
                return new FuzzRepetitionNode("Between", GenerateNode(depth + 1), lower, upper);
            }
            return new FuzzWrapNode("Alias", GenerateNode(depth + 1));
        }

        // The recursion arm's opener must consume at least one token, so
        // the recursion always makes progress and can't loop at one
        // position.
        private FuzzNode GenerateConsumingLeaf()
        {
            int roll = _random.Next(3);
            if (roll == 0) return new FuzzTokenNode(Alphabet[_random.Next(Alphabet.Length)]);
            if (roll == 1) return new FuzzLiteralNode(RandomText(1, 2), ignoreCase: false);
            return new FuzzGraphemeTokenNode(PickCluster());
        }

        private FuzzNode[] GenerateChildren(int depth, int count)
        {
            var children = new FuzzNode[count];
            for (int i = 0; i < count; i++) children[i] = GenerateNode(depth + 1);
            return children;
        }

        // The dice roll on every path regardless of the outcome or the
        // node kind, so the draw sequence stays identical between the
        // plain and alias-wrapped generations of the same seed.
        private void ApplyModifiers(FuzzNode node)
        {
            int policyRoll = _random.Next(100);
            int errorRoll = _random.Next(100);
            int forcedRoll = _random.Next(100);
            if (node.SkipModifiers) return;

            if (policyRoll < 20) node.AsName = $"n{_nameCounter++}";
            else if (policyRoll < 24) node.ExplicitFlatten = FlattenType.Preserve;
            else if (policyRoll < 27) node.ExplicitFlatten = FlattenType.Delete;
            else if (policyRoll < 30) node.ExplicitFlatten = FlattenType.Flatten;

            if (errorRoll < 12)
            {
                node.ErrorText = $"err{_errorCounter++}";
                node.ErrorForced = forcedRoll < 20;
            }
        }

        private string PickCluster() => FuzzCorpus.SingleClusters[_random.Next(FuzzCorpus.SingleClusters.Count)];

        private string PickSingleRuneCluster() => FuzzCorpus.SingleRuneClusters[_random.Next(FuzzCorpus.SingleRuneClusters.Count)];

        private string PickIdentifierText() => FuzzCorpus.IdentifierTexts[_random.Next(FuzzCorpus.IdentifierTexts.Count)];

        private string PickText()
        {
            string text = FuzzCorpus.WellFormedTexts[_random.Next(FuzzCorpus.WellFormedTexts.Count)];
            if (text.Length <= 12) return text;
            // Back off one char when the cut would split a surrogate pair:
            // grammar text has to stay well-formed.
            int cut = char.IsHighSurrogate(text[11]) ? 11 : 12;
            return text.Substring(0, cut);
        }

        private string RandomText(int minLength, int maxLength)
        {
            int length = _random.Next(minLength, maxLength + 1);
            var builder = new StringBuilder();
            for (int i = 0; i < length; i++) builder.Append(Alphabet[_random.Next(Alphabet.Length)]);
            return builder.ToString();
        }

        private string RandomLetters(int minLength, int maxLength)
        {
            int length = _random.Next(minLength, maxLength + 1);
            var builder = new StringBuilder();
            for (int i = 0; i < length; i++) builder.Append("abc"[_random.Next(3)]);
            return builder.ToString();
        }

        private char[] RandomSetChars()
        {
            int count = _random.Next(1, 4);
            var chars = new List<char>();
            for (int i = 0; i < count; i++)
            {
                char c = Alphabet[_random.Next(Alphabet.Length)];
                if (!chars.Contains(c)) chars.Add(c);
            }
            return chars.ToArray();
        }

        private string[] RandomSetGraphemes()
        {
            // The CRLF roll is separate from the corpus roll: "\r\n" is
            // the one multi-rune grapheme the built-in sets treat as a
            // member (TokenSet.LineTerminators, Ascii.AnyWhitespace), and
            // it isn't in the UnicodeExamples corpus, so without this it
            // would never appear in a generated set.
            int crlfRoll = _random.Next(100);
            int roll = _random.Next(100);
            var members = new List<string>();
            if (crlfRoll < 20) members.Add("\r\n");
            if (roll >= 55)
            {
                int count = roll < 90 ? 1 : 2;
                for (int i = 0; i < count; i++)
                {
                    string cluster = PickCluster();
                    if (!members.Contains(cluster)) members.Add(cluster);
                }
            }
            return members.ToArray();
        }
    }
}

// The neutral rewrites from retired backlog item 0a02: transforms that can't change
// what a grammar matches, applied so the harness can check they also
// don't change what a failing parse reports. Each transform rebuilds
// only the path from a rewritten node to the root and shares every
// untouched blueprint node, which is safe because Build() constructs
// fresh Rule instances on every call.
//
// Soundness is the whole game here: a rewrite the oracle applies where
// it isn't actually neutral produces false divergences. The two
// eligibility rules and their reasoning:
//
// Exactly(n, x) to And(x repeated n) needs x to consume at least one
// token whenever it succeeds. The count rule stops its loop after one
// zero-width success, so Exactly(2, Optional(a)) fails on empty input
// while And(Optional(a), Optional(a)) succeeds. DefinitelyConsumes is
// the under-approximation that keeps the rewrite away from that case:
// it says true only when every success provably consumes.
//
// Reversing an Or's alternatives needs three things per branch:
//   - The branch consumes on success. A branch that can succeed at zero
//     width is a catch-all, and moving a catch-all changes which branch
//     wins.
//   - The branch has a known exact first-token set: every successful
//     match's first consumed token is in the set. Disjoint exact sets
//     mean at most one branch can match a given input, so trial order
//     can't change which branch wins. A branch whose set doesn't hold
//     the input's first token fails with all its records at the Or's
//     start. The one branch whose set does hold it can fail deeper,
//     but it runs to the same failure in either order, so its records
//     are identical no matter where it sits in the trial order.
//   - No .WithError anywhere in the branch subtree. Rejected-branch
//     failures are then all mechanical, and the mechanical tracker is a
//     pure position max, so recording order can't show through. (Named
//     and forced failures break depth ties by first writer, which
//     reordering would legitimately flip. That's by design, not a bug,
//     so branches that could record one are excluded.)
// FirstTokenSet may over-approximate (a superset only makes the
// disjointness check stricter), and unknown shapes return null, which
// excludes the Or. Members compare in FormC because the harness
// compiles the rewritten grammars with FormC and matching is
// insensitive to the source form. The Or's own .WithError is fine: it
// records after every branch has failed in either order.
internal static class FuzzRewriter
{
    public static FuzzNode? RewriteExactlyToAnd(FuzzNode root)
    {
        bool changed = false;
        var result = Transform(root, node =>
        {
            // Count >= 1 because Exactly(0, x) succeeds at zero width
            // while And() rejects an empty child list. The generator
            // never draws 0 today, so this is here for the day it does.
            if (node is FuzzRepetitionNode { Kind: "Exactly" } exactly
                && exactly.Count >= 1
                && DefinitelyConsumes(exactly.Inner))
            {
                changed = true;
                return CopyModifiers(exactly, new FuzzSharedSequenceNode(exactly.Inner, exactly.Count));
            }
            return node;
        });
        return changed ? result : null;
    }

    public static FuzzNode? ReverseEligibleOrAlternatives(FuzzNode root)
    {
        bool changed = false;
        var result = Transform(root, node =>
        {
            if (node is FuzzListNode { Kind: "Or" } alternation && IsReorderableOr(alternation))
            {
                changed = true;
                return CopyModifiers(alternation, new FuzzListNode("Or", Enumerable.Reverse(alternation.Items).ToArray()));
            }
            return node;
        });
        return changed ? result : null;
    }

    // Bottom-up rebuild: transform every child first, rebuild this node
    // only if a child actually changed, then offer the rebuilt node to
    // the rewrite. Unchanged subtrees stay shared with the original
    // blueprint.
    private static FuzzNode Transform(FuzzNode node, Func<FuzzNode, FuzzNode> rewriteNode)
    {
        FuzzNode rebuilt;
        switch (node)
        {
            case FuzzListNode list:
            {
                var items = new FuzzNode[list.Items.Length];
                bool childChanged = false;
                for (int i = 0; i < list.Items.Length; i++)
                {
                    items[i] = Transform(list.Items[i], rewriteNode);
                    childChanged |= !ReferenceEquals(items[i], list.Items[i]);
                }
                rebuilt = childChanged ? CopyModifiers(list, new FuzzListNode(list.Kind, items)) : list;
                break;
            }
            case FuzzRepetitionNode repetition:
            {
                var inner = Transform(repetition.Inner, rewriteNode);
                rebuilt = ReferenceEquals(inner, repetition.Inner)
                    ? repetition
                    : CopyModifiers(repetition, new FuzzRepetitionNode(repetition.Kind, inner, repetition.Count, repetition.UpperCount));
                break;
            }
            case FuzzWrapNode wrap:
            {
                var inner = Transform(wrap.Inner, rewriteNode);
                rebuilt = ReferenceEquals(inner, wrap.Inner)
                    ? wrap
                    : CopyModifiers(wrap, new FuzzWrapNode(wrap.Kind, inner));
                break;
            }
            case FuzzScanUntilRuleStopNode ruleStop:
            {
                var stopper = Transform(ruleStop.Stopper, rewriteNode);
                rebuilt = ReferenceEquals(stopper, ruleStop.Stopper)
                    ? ruleStop
                    : CopyModifiers(ruleStop, new FuzzScanUntilRuleStopNode(stopper, ruleStop.EofIsTerminator, ruleStop.WithRuneEscape));
                break;
            }
            case FuzzScanUntilEscapeStartRuleNode escape:
            {
                var escapeStart = Transform(escape.EscapeStart, rewriteNode);
                rebuilt = ReferenceEquals(escapeStart, escape.EscapeStart)
                    ? escape
                    : CopyModifiers(escape, new FuzzScanUntilEscapeStartRuleNode(escape.Chars, escape.GraphemeMembers, escapeStart, escape.EofIsTerminator));
                break;
            }
            case FuzzRecursionNode recursion:
            {
                var open = Transform(recursion.Open, rewriteNode);
                var baseCase = Transform(recursion.BaseCase, rewriteNode);
                var close = Transform(recursion.Close, rewriteNode);
                rebuilt = ReferenceEquals(open, recursion.Open)
                          && ReferenceEquals(baseCase, recursion.BaseCase)
                          && ReferenceEquals(close, recursion.Close)
                    ? recursion
                    : CopyModifiers(recursion, new FuzzRecursionNode(open, baseCase, close, recursion.ViaAlias));
                break;
            }
            case FuzzSharedSequenceNode shared:
            {
                var inner = Transform(shared.Inner, rewriteNode);
                rebuilt = ReferenceEquals(inner, shared.Inner)
                    ? shared
                    : CopyModifiers(shared, new FuzzSharedSequenceNode(inner, shared.RepeatCount));
                break;
            }
            default:
                rebuilt = node;
                break;
        }
        return rewriteNode(rebuilt);
    }

    private static FuzzNode CopyModifiers(FuzzNode from, FuzzNode to)
    {
        to.AsName = from.AsName;
        to.ExplicitFlatten = from.ExplicitFlatten;
        to.ErrorText = from.ErrorText;
        to.ErrorForced = from.ErrorForced;
        return to;
    }

    private static bool IsReorderableOr(FuzzListNode alternation)
    {
        var branchSets = new List<HashSet<string>>();
        foreach (var branch in alternation.Items)
        {
            if (!DefinitelyConsumes(branch)) return false;
            if (ContainsWithError(branch)) return false;
            var firstTokens = FirstTokenSet(branch);
            if (firstTokens == null || firstTokens.Count == 0) return false;
            foreach (var earlier in branchSets)
                if (earlier.Overlaps(firstTokens)) return false;
            branchSets.Add(firstTokens);
        }
        return true;
    }

    private static bool ContainsWithError(FuzzNode node) =>
        node.ErrorText != null || node.ChildNodes.Any(ContainsWithError);

    // True only when every successful match consumes at least one token.
    // Must under-approximate: unknown shapes answer false.
    private static bool DefinitelyConsumes(FuzzNode node) => node switch
    {
        FuzzTokenNode => true,
        FuzzGraphemeTokenNode => true,
        FuzzLiteralNode literal => literal.S.Length > 0,
        FuzzSetLeafNode set => set.Kind switch
        {
            "OneOf" or "NoneOf" => true,
            "ScanWhile" => set.ScanWhileMinimum >= 1,
            // The ScanUntil kinds match zero-width when the stopper is
            // immediate.
            _ => false,
        },
        // EndOfLine(eofIsEol: true), Eof, and ScanUntilEof succeed at
        // zero width, so they're deliberately missing from this list.
        FuzzSimpleLeafNode simple => simple.Kind
            is "AnyToken" or "EndOfLine" or "InlineWhitespace" or "AnyWhitespace" or "Integer" or "Float",
        FuzzListNode list => list.Kind switch
        {
            "And" => list.Items.Any(DefinitelyConsumes),
            "Or" => list.Items.All(DefinitelyConsumes),
            // Unknown kinds answer false per this function's rule.
            _ => false,
        },
        FuzzRepetitionNode repetition => repetition.Kind switch
        {
            "OneOrMore" => DefinitelyConsumes(repetition.Inner),
            "Exactly" or "AtLeast" or "Between" => repetition.Count >= 1 && DefinitelyConsumes(repetition.Inner),
            // Optional, ZeroOrMore, and AtMost succeed at zero width.
            _ => false,
        },
        FuzzWrapNode wrap => wrap.Kind switch
        {
            "Alias" or "LateBound" => DefinitelyConsumes(wrap.Inner),
            // WithinToken consumes exactly one outer token on success.
            "WithinToken" => true,
            // Not and Peek are zero-width.
            _ => false,
        },
        FuzzIdentifierNode => true,
        // The recursion body is Or(baseCase, And(open, reference, close)),
        // so it consumes when both arms do, and the And arm consumes
        // whenever open does.
        FuzzRecursionNode recursion => DefinitelyConsumes(recursion.BaseCase) && DefinitelyConsumes(recursion.Open),
        FuzzSharedSequenceNode shared => DefinitelyConsumes(shared.Inner),
        _ => false,
    };

    // The clusters EndOfLine can match, derived from the set EndOfLine
    // itself is built on (TokenSet.LineTerminators) so this list can't
    // drift when a terminator is ever added there. EndOfLine's separate
    // Literal("\r\n") alternative is covered by the CRLF grapheme member
    // of the same set. TokenSet has no rune enumerator, so the derivation
    // asks ContainsRune about every code point once. That's about 1.1M
    // short linear scans, a few milliseconds, paid one time when the
    // first eligibility check runs.
    private static readonly Lazy<HashSet<string>> LineTerminatorClusters = new(BuildLineTerminatorClusters);

    private static HashSet<string> BuildLineTerminatorClusters()
    {
        var terminators = TokenSet.LineTerminators;
        var clusters = NewSet();
        for (int codepoint = 0; codepoint <= 0x10FFFF; codepoint++)
        {
            if (!terminators.ContainsRune(codepoint)) continue;
            // A TokenSet can hold surrogate code points (TokenSet.SurrogateRange
            // exists for matching ill-formed input), and ConvertFromUtf32
            // throws on those, so they render as their single char.
            string member = codepoint is >= 0xD800 and <= 0xDFFF
                ? ((char)codepoint).ToString()
                : char.ConvertFromUtf32(codepoint);
            clusters.Add(Nfc(member));
        }
        foreach (string grapheme in terminators.EnumerateMultiRuneGraphemes())
            clusters.Add(Nfc(grapheme));
        return clusters;
    }

    // The exact first-token set: every successful match's first consumed
    // token (grapheme cluster, compared in FormC) is in the returned
    // set. null means the shape's set isn't computable here, which
    // excludes the enclosing Or from reordering. May over-approximate
    // (that only makes the disjointness check stricter), must never
    // under-approximate. Zero-width shapes (Not, Peek, Eof) return the
    // empty set: they never consume, so they contribute no first tokens.
    private static HashSet<string>? FirstTokenSet(FuzzNode node)
    {
        switch (node)
        {
            case FuzzTokenNode token:
                return NewSet(Nfc(token.C.ToString()));
            case FuzzGraphemeTokenNode grapheme:
                return NewSet(Nfc(grapheme.Grapheme));
            case FuzzLiteralNode literal when literal.S.Length == 0:
                return null;
            case FuzzLiteralNode literal when literal.IgnoreCase:
            {
                // Generated case-insensitive literals hold ASCII letters
                // only. Anything else is unexpected, so bail to null.
                char first = literal.S[0];
                if (first is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
                    return NewSet(char.ToLowerInvariant(first).ToString(), char.ToUpperInvariant(first).ToString());
                return null;
            }
            case FuzzLiteralNode literal:
                return NewSet(StringInfo.GetNextTextElement(Nfc(literal.S), 0));
            case FuzzSetLeafNode set when set.Kind is "OneOf" or "ScanWhile":
            {
                var members = NewSet();
                foreach (char c in set.Chars) members.Add(Nfc(c.ToString()));
                foreach (string member in set.GraphemeMembers) members.Add(Nfc(member));
                return members;
            }
            // NoneOf matches the complement of its members, and the
            // ScanUntil kinds consume anything before their stopper, so
            // neither has an enumerable set.
            case FuzzSetLeafNode:
                return null;
            case FuzzSimpleLeafNode simple:
                return simple.Kind switch
                {
                    "Eof" => NewSet(),
                    "EndOfLine" or "EndOfLineEofIsEol" => new HashSet<string>(LineTerminatorClusters.Value, StringComparer.Ordinal),
                    // AnyToken matches everything. Integer and Float
                    // start on TokenSet.Digits, the full Unicode
                    // decimal-digit set, and the whitespace factories on
                    // TokenSet.InlineWhitespace. None of those member
                    // lists are enumerable from the blueprint.
                    _ => null,
                };
            case FuzzListNode { Kind: "Or" } alternation:
            {
                var union = NewSet();
                foreach (var branch in alternation.Items)
                {
                    var branchSet = FirstTokenSet(branch);
                    if (branchSet == null) return null;
                    union.UnionWith(branchSet);
                }
                return union;
            }
            case FuzzListNode { Kind: "And" } sequence:
            {
                // Children before the first definitely-consuming one may
                // match at zero width, so the first consumed token can
                // come from any of them: union until the first child
                // that always consumes. Matched on Kind "And" explicitly:
                // an unknown list kind must fall to the null default, not
                // get this scan, because stopping the union early is an
                // under-approximation for anything Or-shaped.
                var union = NewSet();
                foreach (var child in sequence.Items)
                {
                    var childSet = FirstTokenSet(child);
                    if (childSet == null) return null;
                    union.UnionWith(childSet);
                    if (DefinitelyConsumes(child)) return union;
                }
                return union;
            }
            // For every count, the first consumed token (if any) comes
            // from the first inner attempt.
            case FuzzRepetitionNode repetition:
                return FirstTokenSet(repetition.Inner);
            case FuzzWrapNode { Kind: "Not" or "Peek" }:
                return NewSet();
            case FuzzWrapNode { Kind: "Alias" or "LateBound" } wrap:
                return FirstTokenSet(wrap.Inner);
            case FuzzSharedSequenceNode shared:
                return FirstTokenSet(shared.Inner);
            // WithinToken (token acceptance depends on the rune-level
            // inner), Identifier (the start set is the full XID table),
            // recursion, and the rule-stopper ScanUntil shapes have no
            // computable set.
            default:
                return null;
        }
    }

    private static HashSet<string> NewSet(params string[] members) =>
        new HashSet<string>(members, StringComparer.Ordinal);

    private static string Nfc(string s)
    {
        try { return s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return s; }
    }
}
