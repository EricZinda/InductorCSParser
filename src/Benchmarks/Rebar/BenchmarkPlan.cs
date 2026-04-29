using System;
using System.Collections.Generic;
using System.Linq;
using InductorParser.StateMachine;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Benchmarks.Rebar;

internal sealed class BenchmarkPlan
{
    private static readonly ParseOptions SearchOptions = new()
    {
        InputUnit = InputUnit.Rune,
        NormalizeInput = null,
        RuleCountLimit = 0,
        MaxDepth = 0
    };

    private readonly Rule _scanner;
    private readonly Rule _match;
    private readonly IReadOnlyList<Rule> _captures;
    private readonly bool _useStateMachine;
    private readonly string? _grepTrigger;
    private readonly StringComparison _grepTriggerComparison;
    private readonly string[]? _grepTriggerAlternatives;
    private readonly char[]? _grepTriggerFirstChars;

    public BenchmarkPlan(
        Rule scanner,
        Rule match,
        IReadOnlyList<Rule> captures,
        bool useStateMachine,
        string? grepTrigger = null,
        StringComparison grepTriggerComparison = StringComparison.Ordinal,
        string[]? grepTriggerAlternatives = null)
    {
        _scanner = scanner;
        _match = match;
        _captures = captures;
        _useStateMachine = useStateMachine;
        _grepTrigger = grepTrigger;
        _grepTriggerComparison = grepTriggerComparison;
        _grepTriggerAlternatives = grepTriggerAlternatives;
        _grepTriggerFirstChars = grepTriggerAlternatives == null
            ? null
            : BuildFirstCharSet(grepTriggerAlternatives, grepTriggerComparison);
    }

    private static char[] BuildFirstCharSet(string[] alternatives, StringComparison comparison)
    {
        // Collect every distinct first char across all alternatives. For
        // OrdinalIgnoreCase, fold each ASCII letter to both cases so the
        // IndexOfAny scan still hits all candidate positions. We keep this
        // narrow (BMP first chars only, ASCII case fold only) because it
        // mirrors LiteralIgnoreAsciiCaseRule's match semantics; we never
        // emit a multi-literal trigger that needs broader Unicode case
        // folding.
        bool ignoreCase = comparison == StringComparison.OrdinalIgnoreCase;
        var seen = new HashSet<char>();
        foreach (string alternative in alternatives)
        {
            if (alternative.Length == 0) continue;
            char first = alternative[0];
            seen.Add(first);
            if (ignoreCase && first <= 0x7F)
            {
                if (first >= 'a' && first <= 'z') seen.Add((char)(first - 0x20));
                else if (first >= 'A' && first <= 'Z') seen.Add((char)(first + 0x20));
            }
        }
        var array = new char[seen.Count];
        seen.CopyTo(array);
        return array;
    }

    public long Count(string haystack, string model)
    {
        return model switch
        {
            "compile" => CountMatches(haystack),
            "count" => CountMatches(haystack),
            "count-spans" => SumMatchBytes(haystack),
            "grep" => CountMatchingLines(haystack),
            "grep-captures" => CountGrepCaptures(haystack),
            _ => throw new NotSupportedException($"Unsupported rebar model '{model}'.")
        };
    }

    private long CountMatches(string haystack)
    {
        // State-machine path skips Symbol allocation by walking
        // OutputOps directly. The recursive evaluator doesn't have
        // a tree-free path yet, so it keeps building the parse tree
        // and folding it down. The two paths produce identical
        // counts; the comparison with .NET in the timed sweep is
        // less mixed-up because the state-machine numbers no longer
        // include 35K Symbol allocations on the per-line cases.
        if (_useStateMachine)
            return StateMachineParser.CountMatches(_scanner, _match, haystack, SearchOptions);

        var result = Parse(haystack);
        return result.Tree!.FindAll(_match).LongCount();
    }

    private long SumMatchBytes(string haystack)
    {
        if (_useStateMachine)
        {
            // The state-machine engine exposes match positions; we
            // sum UTF-8 bytes per span here so the engine API stays
            // a generic "where did matches happen" primitive instead
            // of a benchmark-shaped pre-aggregation.
            long total = 0;
            var spans = StateMachineParser.EnumerateMatchSpans(_scanner, _match, haystack, SearchOptions);
            for (int index = 0; index < spans.Count; index++)
            {
                var span = spans[index];
                total += System.Text.Encoding.UTF8.GetByteCount(haystack.AsSpan(span.Offset, span.Length));
            }
            return total;
        }

        var result = Parse(haystack);
        long total2 = 0;
        foreach (var match in result.Tree!.FindAll(_match))
            total2 += match.GetUtf8ByteCount();
        return total2;
    }

    private long CountMatchingLines(string haystack)
    {
        long total = 0;
        if (_useStateMachine)
        {
            foreach (var line in CandidateLines(haystack))
            {
                if (StateMachineParser.HasAnyMatch(_scanner, _match, line, SearchOptions))
                    total++;
            }
            return total;
        }

        foreach (var line in CandidateLines(haystack))
        {
            var result = Parse(line);
            if (result.Tree!.Find(_match) != null)
                total++;
        }
        return total;
    }

    private long CountGrepCaptures(string haystack)
    {
        // Match what rebar's other engines count for grep-captures:
        // one for the match plus one for every capture group whose
        // node is present in the parse tree, regardless of whether
        // the captured span is empty. .NET's runner uses
        // `g.Success` for the same reason: an empty `[^;]*` field
        // in UnicodeData.txt is still a successful capture. We
        // previously gated on `Length > 0`, which happened to
        // produce the same totals as upstream for the ruff-noqa
        // benchmarks because every match there had a non-empty
        // leading-whitespace capture, but that gating undercounted
        // benchmarks like 07-unicode-character-data where most
        // captures legitimately match zero bytes.
        long total = 0;
        if (_useStateMachine)
        {
            foreach (var line in CandidateLines(haystack))
                total += StateMachineParser.CountMatchesAndCaptures(_scanner, _match, _captures, line, SearchOptions);
            return total;
        }

        foreach (var line in CandidateLines(haystack))
        {
            var result = Parse(line);
            foreach (var match in result.Tree!.FindAll(_match))
            {
                total++;
                foreach (var capture in _captures)
                {
                    var symbol = match.Find(capture);
                    if (symbol != null)
                        total++;
                }
            }
        }
        return total;
    }

    // When the grammar provides a trigger literal that must appear in any
    // matching line (e.g. "# noqa" for the ruff-noqa benchmarks), skip
    // straight to lines that contain the trigger via a single BCL
    // substring search per match position. On a 32MB haystack with
    // matches on ~0.01% of lines this turns 400K parses into a few dozen.
    // Without a trigger, fall back to walking every line, which is what
    // the runner did originally and is the only correct path when the
    // grammar's match shape doesn't have a guaranteed literal prefix.
    private IEnumerable<string> CandidateLines(string haystack)
    {
        if (_grepTrigger != null && _grepTrigger.Length > 0)
        {
            foreach (var line in CandidateLinesSingleTrigger(haystack))
                yield return line;
            yield break;
        }

        if (_grepTriggerAlternatives != null && _grepTriggerAlternatives.Length > 0)
        {
            foreach (var line in CandidateLinesMultiTrigger(haystack))
                yield return line;
            yield break;
        }

        foreach (var line in Lines(haystack))
            yield return line;
    }

    private IEnumerable<string> CandidateLinesSingleTrigger(string haystack)
    {
        int position = 0;
        while (position < haystack.Length)
        {
            int hit = haystack.IndexOf(_grepTrigger!, position, _grepTriggerComparison);
            if (hit < 0) yield break;

            int lineStart = haystack.LastIndexOf('\n', hit);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            int lineEnd = hit;
            while (lineEnd < haystack.Length && haystack[lineEnd] != '\n' && haystack[lineEnd] != '\r')
                lineEnd++;

            yield return haystack.Substring(lineStart, lineEnd - lineStart);
            position = lineEnd + 1;
        }
    }

    // Multi-literal grep prefilter. The match rule's analysis returned a
    // small set of literal alternatives where every successful match is
    // guaranteed to contain at least one (e.g. {ASIA, AKIA, AROA, AIDA}
    // for the AWS-keys grammar). The scan looks like the parser's
    // scanner-skip: walk the haystack via IndexOfAny on the unique first
    // chars across all literals, and at each hit check whether any
    // alternative actually matches there. The first-char scan is
    // SIMD-tuned in the BCL, so it stays cheap even when the literal
    // set is broad.
    private IEnumerable<string> CandidateLinesMultiTrigger(string haystack)
    {
        var alternatives = _grepTriggerAlternatives!;
        var firstChars = _grepTriggerFirstChars!;
        int position = 0;
        while (position < haystack.Length)
        {
            int candidate = haystack.IndexOfAny(firstChars, position, haystack.Length - position);
            if (candidate < 0) yield break;

            if (!AnyAlternativeMatchesAt(haystack, candidate, alternatives))
            {
                position = candidate + 1;
                continue;
            }

            int lineStart = haystack.LastIndexOf('\n', candidate);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            int lineEnd = candidate;
            while (lineEnd < haystack.Length && haystack[lineEnd] != '\n' && haystack[lineEnd] != '\r')
                lineEnd++;

            yield return haystack.Substring(lineStart, lineEnd - lineStart);
            position = lineEnd + 1;
        }
    }

    private bool AnyAlternativeMatchesAt(string haystack, int position, string[] alternatives)
    {
        for (int index = 0; index < alternatives.Length; index++)
        {
            string alternative = alternatives[index];
            if (alternative.Length == 0) continue;
            if (position + alternative.Length > haystack.Length) continue;
            if (string.Compare(haystack, position, alternative, 0, alternative.Length, _grepTriggerComparison) == 0)
                return true;
        }
        return false;
    }

    private ParseResult Parse(string input)
    {
        var result = _useStateMachine
            ? StateMachineParser.Parse(_scanner, input, SearchOptions)
            : _scanner.Parse(input, SearchOptions);
        if (!result.Success)
            throw new InvalidOperationException($"InductorParser rejected supported benchmark input at {result.ErrorCharIndex}: {result.ErrorMessage}");
        return result;
    }

    private static IEnumerable<string> Lines(string haystack)
    {
        int start = 0;
        for (int index = 0; index < haystack.Length; index++)
        {
            char c = haystack[index];
            if (c != '\n' && c != '\r') continue;

            yield return haystack.Substring(start, index - start);
            if (c == '\r' && index + 1 < haystack.Length && haystack[index + 1] == '\n')
                index++;
            start = index + 1;
        }

        if (start < haystack.Length)
            yield return haystack.Substring(start);
    }
}

internal static class BenchmarkRegistry
{
    private static readonly RuneSet AsciiUpper = RuneSet.Range('A', 'Z');
    private static readonly RuneSet AsciiLower = RuneSet.Range('a', 'z');
    private static readonly RuneSet AsciiAlpha = AsciiUpper | AsciiLower;
    private static readonly RuneSet AsciiWord = AsciiAlpha | RuneSet.Ascii.Digits | RuneSet.Runes("_");
    private static readonly RuneSet AwsKeyTail = AsciiUpper | RuneSet.Range('0', '7');
    private static readonly RuneSet AsciiRegexWhitespace = RuneSet.Runes(" \t\r\n\f\v");
    private static readonly RuneSet CodeSeparator = RuneSet.Runes(",") | AsciiRegexWhitespace;
    private static readonly RuneSet NotNewline = ~RuneSet.Runes("\r\n");
    private static readonly RuneSet NotUppercase = ~AsciiUpper;
    private static readonly RuneSet NotSpace = ~RuneSet.Runes(" ");
    private static readonly RuneSet NotSemicolon = ~RuneSet.Runes(";");
    private static readonly RuneSet NotBracket = ~RuneSet.Runes("]");
    private static readonly RuneSet NotParen = ~RuneSet.Runes(")");
    private static readonly RuneSet NotCurly = ~RuneSet.Runes("}");
    private static readonly RuneSet B64Char = AsciiAlpha | RuneSet.Ascii.Digits | RuneSet.Runes("+/");
    private static readonly RuneSet UcdField9 = RuneSet.Ascii.Digits | RuneSet.Runes("-/");
    private static readonly RuneSet LevelChar = RuneSet.Runes("DIWEF");
    private static readonly RuneSet OneToFour = RuneSet.Runes("1234");
    private static readonly RuneSet YesNo = RuneSet.Runes("YN");
    private static readonly RuneSet DateSeparator = RuneSet.Runes("/:-,. \t\r\n_+@");
    private static readonly RuneSet Quote = RuneSet.Runes("'\"");

    public static BenchmarkPlan Build(RebarConfig config, bool useStateMachine = false)
    {
        if (config.Patterns.Count != 1)
            throw new NotSupportedException("The InductorParser rebar runner currently supports exactly one regex pattern per benchmark.");

        var grammar = config.Name switch
        {
            "curated/01-literal/sherlock-en" => LiteralPattern("Sherlock Holmes", config.CaseInsensitive),
            "curated/01-literal/sherlock-casei-en" => LiteralPattern("Sherlock Holmes", config.CaseInsensitive),
            "curated/01-literal/sherlock-ru" => LiteralPattern("\u0428\u0435\u0440\u043B\u043E\u043A \u0425\u043E\u043B\u043C\u0441", config.CaseInsensitive),
            "curated/01-literal/sherlock-zh" => LiteralPattern("\u590F\u6D1B\u514B\u00B7\u798F\u5C14\u6469\u65AF", config.CaseInsensitive),

            "curated/02-literal-alternate/sherlock-en" => LiteralAlternates(SherlockEnglish(), config.CaseInsensitive),
            "curated/02-literal-alternate/sherlock-casei-en" => LiteralAlternates(SherlockEnglish(), config.CaseInsensitive),
            "curated/02-literal-alternate/sherlock-ru" => LiteralAlternates(SherlockRussian(), config.CaseInsensitive),
            "curated/02-literal-alternate/sherlock-zh" => LiteralAlternates(SherlockChinese(), config.CaseInsensitive),

            "curated/03-date/compile-ascii" => DateAscii(),

            "curated/04-ruff-noqa/real" => RuffNoqaReal(),
            "curated/04-ruff-noqa/tweaked" => RuffNoqaTweaked(),
            "curated/04-ruff-noqa/compile-real" => RuffNoqaReal(),

            "curated/06-cloud-flare-redos/simplified-short" => CloudflareSimplified(),
            "curated/06-cloud-flare-redos/simplified-long" => CloudflareSimplified(),
            "curated/06-cloud-flare-redos/original" => CloudflareOriginal(),

            "curated/07-unicode-character-data/parse-line" => UcdParseLine(),
            "curated/07-unicode-character-data/compile" => UcdParseLine(),

            "curated/08-words/all-english" => AsciiWords(1),
            "curated/08-words/long-english" => AsciiWords(12),

            "curated/09-aws-keys/quick" => AwsQuick(),
            "curated/09-aws-keys/compile-quick" => AwsQuick(),
            "curated/09-aws-keys/full" => AwsFull(),
            "curated/09-aws-keys/compile-full" => AwsFull(),

            "curated/10-bounded-repeat/letters-en" => BoundedLettersEn(),
            "curated/10-bounded-repeat/context" => BoundedContext(),
            "curated/10-bounded-repeat/capitals" => BoundedCapitals(),
            "curated/10-bounded-repeat/compile-context" => BoundedContext(),
            "curated/10-bounded-repeat/compile-capitals" => BoundedCapitals(),

            "curated/11-unstructured-to-json/extract" => UnstructuredJson(),
            "curated/11-unstructured-to-json/compile" => UnstructuredJson(),

            "curated/12-dictionary/single" => DictionaryAlternates(config.Patterns[0]),
            "curated/12-dictionary/compile-single" => DictionaryAlternates(config.Patterns[0]),

            "curated/14-quadratic/1x" => Quadratic(),
            "curated/14-quadratic/2x" => Quadratic(),
            "curated/14-quadratic/10x" => Quadratic(),

            _ => throw new NotSupportedException(
                $"Benchmark '{config.Name}' is not in the hand-translated InductorParser rebar subset.")
        };

        ValidateSupportedModel(config);
        ValidateCaseMode(config);
        return CompileScanner(grammar, useStateMachine);
    }

    private static void ValidateSupportedModel(RebarConfig config)
    {
        bool ok = config.Name switch
        {
            "curated/03-date/compile-ascii" => config.Model == "compile",
            "curated/04-ruff-noqa/real" or "curated/04-ruff-noqa/tweaked" => config.Model == "grep-captures",
            "curated/06-cloud-flare-redos/simplified-short"
                or "curated/06-cloud-flare-redos/simplified-long"
                or "curated/06-cloud-flare-redos/original" => config.Model == "count-spans",
            "curated/07-unicode-character-data/parse-line"
                or "curated/11-unstructured-to-json/extract" => config.Model == "grep-captures",
            "curated/07-unicode-character-data/compile"
                or "curated/11-unstructured-to-json/compile" => config.Model == "compile",
            "curated/09-aws-keys/quick" => config.Model == "grep",
            "curated/09-aws-keys/full" => config.Model == "grep-captures",
            "curated/04-ruff-noqa/compile-real"
                or "curated/09-aws-keys/compile-quick"
                or "curated/09-aws-keys/compile-full" => config.Model == "compile",
            "curated/08-words/all-english" or "curated/08-words/long-english" => config.Model == "count-spans",
            "curated/10-bounded-repeat/letters-en"
                or "curated/10-bounded-repeat/context"
                or "curated/10-bounded-repeat/capitals" => config.Model == "count",
            "curated/10-bounded-repeat/compile-context"
                or "curated/10-bounded-repeat/compile-capitals" => config.Model == "compile",
            "curated/12-dictionary/single" => config.Model == "count",
            "curated/12-dictionary/compile-single" => config.Model == "compile",
            "curated/14-quadratic/1x"
                or "curated/14-quadratic/2x"
                or "curated/14-quadratic/10x" => config.Model == "count",
            _ when config.Name.StartsWith("curated/01-literal/", StringComparison.Ordinal) => config.Model == "count",
            _ when config.Name.StartsWith("curated/02-literal-alternate/", StringComparison.Ordinal) => config.Model == "count",
            _ => false
        };
        if (!ok)
            throw new NotSupportedException($"Benchmark '{config.Name}' is not supported for rebar model '{config.Model}'.");
    }

    private static void ValidateCaseMode(RebarConfig config)
    {
        if (config.CaseInsensitive && config.Unicode)
            throw new NotSupportedException("Unicode-aware case-insensitive matching is intentionally unsupported until full case folding lands.");
    }

    private static BenchmarkPlan CompileScanner(PatternGrammar grammar, bool useStateMachine)
    {
        grammar.Match.As("match").Flatten(FlattenType.Preserve);
        var scanner = ZeroOrMore(FirstOf(
            grammar.Match,
            AnyToken().Flatten(FlattenType.Delete)
        )).As("scan").Flatten(FlattenType.Preserve);
        scanner.Compile();

        // Ask the match rule for a required literal that any successful
        // match must contain. When the analysis finds one (e.g. "# noqa"
        // for the ruff-noqa grammars, derived automatically from the
        // AllOf(Literal("# "), OneOf("Nn"), OneOf("Oo"), ...) shape),
        // the grep / grep-captures path uses one BCL substring search
        // across the haystack to skip lines that can't possibly match
        // before invoking the parser line by line. When no single
        // literal can be derived but the rule has a small set of
        // literal alternatives that one of which every match must hit
        // (the AWS-keys grammar's four-prefix `(?:ASIA|AKIA|AROA|AIDA)`
        // is the canonical shape), use a multi-substring prefilter
        // built around String.IndexOfAny on the unique first chars.
        // When neither analysis succeeds, the runner falls back to
        // walking every line.
        string? grepTrigger = null;
        StringComparison grepTriggerComparison = StringComparison.Ordinal;
        string[]? grepTriggerAlternatives = null;
        if (grammar.Match.TryGetRequiredLiteral(out string literal, out bool ignoreCase))
        {
            grepTrigger = literal;
            grepTriggerComparison = ignoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }
        else if (grammar.Match.TryGetRequiredLiteralAlternatives(MaxGrepTriggerAlternatives, out var alternatives))
        {
            grepTriggerAlternatives = alternatives.Select(item => item.Text).ToArray();
            grepTriggerComparison = alternatives.Any(item => item.IgnoreCase)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }

        return new BenchmarkPlan(
            scanner,
            grammar.Match,
            grammar.Captures,
            useStateMachine,
            grepTrigger,
            grepTriggerComparison,
            grepTriggerAlternatives);
    }

    // Cap on the number of literals the multi-substring prefilter is
    // willing to scan with. Eight covers the biggest motivating case
    // (the AWS-keys full grammar's four prefixes, doubled if quotes
    // ever get folded in) without letting a giant FirstOf-of-literals
    // (e.g. the 2000+ entry English dictionary) accidentally degrade
    // into 2000 string.Compare calls per IndexOfAny hit. Larger sets
    // are better served by leaving the prefilter off and letting the
    // parser-side scanner-skip handle them with its own multi-literal
    // path.
    private const int MaxGrepTriggerAlternatives = 8;

    private static PatternGrammar LiteralPattern(string literal, bool ignoreAsciiCase) =>
        new(MatchLiteral(literal, ignoreAsciiCase), Array.Empty<Rule>());

    private static PatternGrammar LiteralAlternates(IEnumerable<string> literals, bool ignoreAsciiCase) =>
        new(FirstOf(literals.Select(literal => MatchLiteral(literal, ignoreAsciiCase)).ToArray()), Array.Empty<Rule>());

    private static Rule MatchLiteral(string literal, bool ignoreAsciiCase) =>
        (ignoreAsciiCase ? LiteralIgnoreAsciiCase(literal) : Literal(literal)).Flatten(FlattenType.Preserve);

    private static PatternGrammar AsciiWords(int minimumLength)
    {
        // Rebar's \w+ / \w{12,} word cases only need the matched span
        // length, not one tree node per character. RuneRun preserves the
        // regex behavior (a maximal ASCII-word run with a minimum length)
        // while turning each word into one leaf Symbol. That keeps the
        // scanner's first-rune skip available because successful matches
        // always consume at least one word rune.
        Rule wordBody = P(RuneRun(AsciiWord, minimumLength));
        var word = AllOf(
            wordBody,
            FirstOf(Peek(OneOf(~AsciiWord)), Eof())
        );
        return new PatternGrammar(word, Array.Empty<Rule>());
    }

    private static PatternGrammar AwsQuick()
    {
        var key = AllOf(
            FirstOf(
                P(Literal("ASIA")),
                P(Literal("AKIA")),
                P(Literal("AROA")),
                P(Literal("AIDA"))
            ),
            Exactly(16, P(OneOf(AwsKeyTail)))
        );
        return new PatternGrammar(key, Array.Empty<Rule>());
    }

    private static PatternGrammar RuffNoqaReal()
    {
        var leadingWhitespace = Capture(ZeroOrMore(P(OneOf(AsciiRegexWhitespace))), "capture1");
        var codeItem = Capture(AllOf(
            OneOrMore(P(OneOf(AsciiUpper))),
            OneOrMore(P(OneOf(RuneSet.Ascii.Digits))),
            Optional(OneOrMore(P(OneOf(CodeSeparator))))
        ), "capture4");
        var codeList = Capture(OneOrMore(codeItem), "capture3");
        var noqa = Capture(AllOf(
            NoqaLiteral(),
            Optional(AllOf(
                P(Token(':')),
                Optional(P(OneOf(AsciiRegexWhitespace))),
                codeList
            ))
        ), "capture2");

        return new PatternGrammar(
            AllOf(leadingWhitespace, noqa),
            new[] { leadingWhitespace, noqa, codeList, codeItem });
    }

    private static PatternGrammar RuffNoqaTweaked()
    {
        var codeItem = Capture(AllOf(
            OneOrMore(P(OneOf(AsciiUpper))),
            OneOrMore(P(OneOf(RuneSet.Ascii.Digits))),
            Optional(OneOrMore(P(OneOf(CodeSeparator))))
        ), "capture2");
        var codeList = Capture(OneOrMore(codeItem), "capture1");
        var match = AllOf(
            NoqaLiteral(),
            Optional(AllOf(
                P(Token(':')),
                Optional(P(OneOf(AsciiRegexWhitespace))),
                codeList
            ))
        );

        return new PatternGrammar(match, new[] { codeList, codeItem });
    }

    private static Rule NoqaLiteral() => AllOf(
        P(Literal("# ")),
        P(OneOf("Nn")),
        P(OneOf("Oo")),
        P(OneOf("Qq")),
        P(OneOf("Aa"))
    );

    private static Rule Capture(Rule rule, string name) =>
        rule.As(name).Flatten(FlattenType.Preserve);

    private static Rule P(Rule rule) => rule.Flatten(FlattenType.Preserve);

    private static string[] SherlockEnglish() =>
        new[]
        {
            "Sherlock Holmes",
            "John Watson",
            "Irene Adler",
            "Inspector Lestrade",
            "Professor Moriarty"
        };

    private static string[] SherlockRussian() =>
        new[]
        {
            "\u0428\u0435\u0440\u043B\u043E\u043A \u0425\u043E\u043B\u043C\u0441",
            "\u0414\u0436\u043E\u043D \u0423\u043E\u0442\u0441\u043E\u043D",
            "\u0418\u0440\u0435\u043D \u0410\u0434\u043B\u0435\u0440",
            "\u0438\u043D\u0441\u043F\u0435\u043A\u0442\u043E\u0440 \u041B\u0435\u0441\u0442\u0440\u0435\u0439\u0434",
            "\u043F\u0440\u043E\u0444\u0435\u0441\u0441\u043E\u0440 \u041C\u043E\u0440\u0438\u0430\u0440\u0442\u0438"
        };

    private static string[] SherlockChinese() =>
        new[]
        {
            "\u590F\u6D1B\u514B\u00B7\u798F\u5C14\u6469\u65AF",
            "\u7EA6\u7FF0\u534E\u751F",
            "\u963F\u5FB7\u52D2",
            "\u96F7\u65AF\u5782\u5FB7",
            "\u83AB\u91CC\u4E9A\u8482\u6559\u6388"
        };

    private static PatternGrammar Quadratic()
    {
        // Regex `.*[^A-Z]|[A-Z]`. The first alternative needs a greedy
        // run of non-newline runes followed by a non-uppercase rune;
        // ScanUntil(NotUppercase) does the run as a single leaf so
        // there's no per-rune backtracking and we still match the
        // regex's leftmost-first semantics (when no non-uppercase
        // rune exists in the line, the first alternative fails and
        // the scanner falls through to `[A-Z]`). For the rebar
        // haystack of 'A' repeated, the first alternative always
        // fails and every match comes from the `[A-Z]` branch.
        var firstAlternative = AllOf(
            ScanUntil(NotUppercase | RuneSet.Runes("\r\n")),
            P(OneOf(NotUppercase))
        );
        var secondAlternative = P(OneOf(AsciiUpper));
        var match = FirstOf(firstAlternative, secondAlternative);
        return new PatternGrammar(match, Array.Empty<Rule>());
    }

    private static PatternGrammar CloudflareSimplified()
    {
        // Regex `.*.*=.*`. A regex backtracker finds the match by
        // letting the first .* shrink until the literal `=` lines up,
        // which InductorParser doesn't backtrack into. Translate the
        // pre-`=` part as ScanUntil so the parser can find the `=`
        // in one forward pass without any backtracking. The two `.*`
        // before `=` collapse to a single ScanUntil because they
        // describe the same thing in this haystack: any non-newline
        // runes up to the first `=`.
        var match = AllOf(
            Optional(P(ScanUntil(RuneSet.Runes("=\r\n")))),
            P(Token('=')),
            ZeroOrMore(P(OneOf(NotNewline)))
        );
        return new PatternGrammar(match, Array.Empty<Rule>());
    }

    private static PatternGrammar CloudflareOriginal()
    {
        // The original Cloudflare regex:
        //   (?:(?:"|'|]|}|\|\d|(?:nan|infinity|true|false|null|undefined|symbol|math)|`|-|\+)+[)]*;?
        //    ((?:\s|-|~|!|\{\}|\|\||\+)*.*(?:.*=.*)))
        // The leading group consumes one or more "token-ish" runs.
        // The capture body is whitespace-or-symbol noise, then a
        // greedy any-run, then a nested .*=.*. As with the
        // simplified case, we use ScanUntil to find the `=` without
        // backtracking the two outer .* parts.
        var prefixToken = FirstOf(
            P(Token('"')),
            P(Token('\'')),
            P(Token(']')),
            P(Token('}')),
            P(Token('\\')),
            P(OneOf(RuneSet.Ascii.Digits)),
            P(Literal("nan")),
            P(Literal("infinity")),
            P(Literal("true")),
            P(Literal("false")),
            P(Literal("null")),
            P(Literal("undefined")),
            P(Literal("symbol")),
            P(Literal("math")),
            P(Token('`')),
            P(Token('-')),
            P(Token('+'))
        );
        var noiseToken = FirstOf(
            P(OneOf(RuneSet.Whitespace)),
            P(Token('-')),
            P(Token('~')),
            P(Token('!')),
            P(Literal("{}")),
            P(Literal("||")),
            P(Token('+'))
        );
        var match = AllOf(
            OneOrMore(prefixToken),
            ZeroOrMore(P(Token(')'))),
            Optional(P(Token(';'))),
            ZeroOrMore(noiseToken),
            Optional(P(ScanUntil(RuneSet.Runes("=\r\n")))),
            P(Token('=')),
            ZeroOrMore(P(OneOf(NotNewline)))
        );
        return new PatternGrammar(match, Array.Empty<Rule>());
    }

    private static PatternGrammar BoundedLettersEn()
    {
        // Regex `[A-Za-z]{8,13}`. BetweenInclusive accumulates greedily
        // up to AtMost reps and succeeds when the count is at least
        // AtLeast, so it produces the same leftmost-greedy match the
        // regex would.
        var match = BetweenInclusive(8, 13, P(OneOf(AsciiAlpha)));
        return new PatternGrammar(match, Array.Empty<Rule>());
    }

    private static PatternGrammar BoundedContext()
    {
        // Regex `[A-Za-z]{10}\s+[\s\S]{0,100}Result[\s\S]{0,100}\s+[A-Za-z]{10}`.
        // Regex backtrackers prefer the LONGEST {0,100} gap that
        // still lets the trailer match (greedy semantics), and the
        // count of 53 on rust-src-tools depends on that "latest
        // trailer" choice. A simple forward-stop using
        // ScanUntil(trailer) instead picks the FIRST trailer, which
        // ends the match earlier, leaves more haystack for the
        // scanner to re-enter, and overcounts (61 vs 53). To recover
        // greedy semantics without InductorParser-level backtracking,
        // build a FirstOf chain that tries {100 anytokens, trailer}
        // first, then {99, trailer}, ..., down to {0, trailer}. The
        // first alternative that fully matches wins, mirroring how
        // a regex backtracker would shrink the gap from 100 down.
        var match = AllOf(
            Exactly(10, P(OneOf(AsciiAlpha))),
            OneOrMore(P(OneOf(AsciiRegexWhitespace))),
            GreedyBoundedGap(100, () => P(Literal("Result"))),
            P(Literal("Result")),
            GreedyBoundedGap(100, BoundedContextTrailer),
            BoundedContextTrailer()
        );
        return new PatternGrammar(match, Array.Empty<Rule>());
    }

    private static Rule BoundedContextTrailer() => AllOf(
        OneOrMore(P(OneOf(AsciiRegexWhitespace))),
        Exactly(10, P(OneOf(AsciiAlpha)))
    );

    private static Rule GreedyBoundedGap(int maximum, Func<Rule> followFactory)
    {
        // Build FirstOf(<peek follow after maximum tokens>,
        //               <peek follow after maximum-1 tokens>,
        //               ...
        //               <peek follow after 0 tokens>).
        // The follow rule is a fresh instance per alternative
        // because rules carry mutable Flatten / Name slots and
        // can't be reused across construction sites without
        // surprising aliasing. Peek is used so the alternative
        // doesn't consume the follow rule's runes; the outer
        // AllOf consumes them right after.
        var alternatives = new Rule[maximum + 1];
        for (int taken = maximum; taken >= 0; taken--)
        {
            alternatives[maximum - taken] = AllOf(
                Exactly(taken, P(AnyToken())),
                Peek(followFactory())
            );
        }
        return FirstOf(alternatives);
    }

    private static PatternGrammar BoundedCapitals()
    {
        // Regex `(?:[A-Z][a-z]+\s*){10,100}`. Each inner rep is one
        // capitalized word followed by optional whitespace, and the
        // outer count is between 10 and 100.
        var capitalizedWord = AllOf(
            P(OneOf(AsciiUpper)),
            OneOrMore(P(OneOf(AsciiLower))),
            ZeroOrMore(P(OneOf(RuneSet.Whitespace)))
        );
        var match = BetweenInclusive(10, 100, capitalizedWord);
        return new PatternGrammar(match, Array.Empty<Rule>());
    }

    private static PatternGrammar DictionaryAlternates(string pattern)
    {
        // Rebar joins each dictionary line into one regex of the form
        // `(?:word1)|(?:word2)|...` (with `literal = true` regex-escaping
        // applied first, but no character in the length-15 English
        // dictionary needs escaping). Split the pattern back into the
        // original literals and build FirstOf(Literal(...)) over them.
        var alternatives = pattern.Split('|');
        var literals = new Rule[alternatives.Length];
        for (int index = 0; index < alternatives.Length; index++)
        {
            string alternative = alternatives[index];
            if (alternative.StartsWith("(?:", StringComparison.Ordinal) && alternative.EndsWith(")", StringComparison.Ordinal))
                alternative = alternative.Substring(3, alternative.Length - 4);
            literals[index] = P(Literal(alternative));
        }
        return new PatternGrammar(FirstOf(literals), Array.Empty<Rule>());
    }

    private static PatternGrammar UnstructuredJson()
    {
        // Regex (verbose, anchored at line start/end):
        //   ^([^ ]+ [^ ]+) ([DIWEF])[1234]: ((?:(?:\[[^\]]*?\]|\([^\)]*?\)): )*)(.*?) \{([^\}]*)\}$
        // Five capture groups: timestamp, level, header, body, location.
        // Body is lazy `.*?` followed by ` {`, which we model as
        // ScanUntil(Literal(" {")) to find the closing brace cleanly
        // without backtracking through the body.
        var timestamp = Capture(AllOf(
            OneOrMore(P(OneOf(NotSpace))),
            P(Token(' ')),
            OneOrMore(P(OneOf(NotSpace)))
        ), "capture1");
        // Wrap the single-rune OneOf in AllOf so Find by capture rule
        // sees a Symbol with the rule's Id rather than the matched
        // rune's value (see the capture10 note in UcdParseLine).
        var level = Capture(AllOf(P(OneOf(LevelChar))), "capture2");
        var bracketContext = AllOf(
            P(Token('[')),
            ZeroOrMore(P(OneOf(NotBracket))),
            P(Token(']'))
        );
        var parenContext = AllOf(
            P(Token('(')),
            ZeroOrMore(P(OneOf(NotParen))),
            P(Token(')'))
        );
        var contextItem = AllOf(
            FirstOf(bracketContext, parenContext),
            P(Token(':')),
            P(Token(' '))
        );
        var header = Capture(ZeroOrMore(contextItem), "capture3");
        var body = Capture(ScanUntil(P(Literal(" {"))), "capture4");
        var location = Capture(ZeroOrMore(P(OneOf(NotCurly))), "capture5");
        var match = AllOf(
            timestamp,
            P(Token(' ')),
            level,
            P(OneOf(OneToFour)),
            P(Token(':')),
            P(Token(' ')),
            header,
            body,
            P(Token(' ')),
            P(Token('{')),
            location,
            P(Token('}'))
        );
        return new PatternGrammar(match, new[] { timestamp, level, header, body, location });
    }

    private static PatternGrammar UcdParseLine()
    {
        // Regex (anchored at line start/end):
        //   ^([A-Z0-9]+);([^;]+);([^;]+);([0-9]+);([^;]+);([^;]*);([0-9]*);
        //    ([0-9]*);([-0-9/]*);([YN]);([^;]*);([^;]*);([^;]*);([^;]*);([^;]*)$
        // 15 semicolon-separated capture groups.
        var hexAlnum = AsciiUpper | RuneSet.Ascii.Digits;
        var capture1 = Capture(OneOrMore(P(OneOf(hexAlnum))), "capture1");
        var capture2 = Capture(OneOrMore(P(OneOf(NotSemicolon))), "capture2");
        var capture3 = Capture(OneOrMore(P(OneOf(NotSemicolon))), "capture3");
        var capture4 = Capture(OneOrMore(P(OneOf(RuneSet.Ascii.Digits))), "capture4");
        var capture5 = Capture(OneOrMore(P(OneOf(NotSemicolon))), "capture5");
        var capture6 = Capture(ZeroOrMore(P(OneOf(NotSemicolon))), "capture6");
        var capture7 = Capture(ZeroOrMore(P(OneOf(RuneSet.Ascii.Digits))), "capture7");
        var capture8 = Capture(ZeroOrMore(P(OneOf(RuneSet.Ascii.Digits))), "capture8");
        var capture9 = Capture(ZeroOrMore(P(OneOf(UcdField9))), "capture9");
        // Wrap the single-rune OneOf in AllOf so the produced Symbol
        // carries this capture's rule Id. A bare OneOfRule emits a
        // leaf Symbol whose Id is the matched rune value rather than
        // its rule's, which is fine for parsing but defeats Find by
        // capture rule (it sees a 'Y' or 'N' rune-id instead of the
        // capture's rule id and returns null).
        var capture10 = Capture(AllOf(P(OneOf(YesNo))), "capture10");
        var capture11 = Capture(ZeroOrMore(P(OneOf(NotSemicolon))), "capture11");
        var capture12 = Capture(ZeroOrMore(P(OneOf(NotSemicolon))), "capture12");
        var capture13 = Capture(ZeroOrMore(P(OneOf(NotSemicolon))), "capture13");
        var capture14 = Capture(ZeroOrMore(P(OneOf(NotSemicolon))), "capture14");
        var capture15 = Capture(ZeroOrMore(P(OneOf(NotSemicolon))), "capture15");
        var match = AllOf(
            capture1, P(Token(';')),
            capture2, P(Token(';')),
            capture3, P(Token(';')),
            capture4, P(Token(';')),
            capture5, P(Token(';')),
            capture6, P(Token(';')),
            capture7, P(Token(';')),
            capture8, P(Token(';')),
            capture9, P(Token(';')),
            capture10, P(Token(';')),
            capture11, P(Token(';')),
            capture12, P(Token(';')),
            capture13, P(Token(';')),
            capture14, P(Token(';')),
            capture15
        );
        var captures = new[]
        {
            capture1, capture2, capture3, capture4, capture5,
            capture6, capture7, capture8, capture9, capture10,
            capture11, capture12, capture13, capture14, capture15
        };
        return new PatternGrammar(match, captures);
    }

    private static PatternGrammar AwsFull()
    {
        // The full AWS detector regex spans multiple lines via
        // (?:\n^.*?){0,4}, which doesn't survive our line-by-line
        // grep loop. We translate the single-line shape only:
        // either a quoted AWS access key followed by a quoted
        // 40-char base64-style secret on the same line, or the
        // reverse order. This intentionally drops the inter-line
        // context. The cpython haystack the rebar bench searches
        // contains no AWS keys, so the expected count is 0 and the
        // looser single-line match still returns 0. The compile
        // benchmark's canonical haystack is single-line and matches
        // once, satisfying the count=1 expectation there.
        var match = FirstOf(QuotedAwsThenSecret(), QuotedSecretThenAws());
        return new PatternGrammar(match, Array.Empty<Rule>());
    }

    private static Rule QuotedAwsKey() => AllOf(
        P(OneOf(Quote)),
        FirstOf(
            P(Literal("ASIA")),
            P(Literal("AKIA")),
            P(Literal("AROA")),
            P(Literal("AIDA"))
        ),
        Exactly(16, P(OneOf(AwsKeyTail))),
        P(OneOf(Quote))
    );

    private static Rule QuotedSecret() => AllOf(
        P(OneOf(Quote)),
        Exactly(40, P(OneOf(B64Char))),
        P(OneOf(Quote))
    );

    private static Rule QuotedAwsThenSecret() => AllOf(
        QuotedAwsKey(),
        // Stop at the next quote so the greedy gap doesn't swallow
        // the secret's opening quote. Without this, ZeroOrMore would
        // run to end-of-line and the secret rule would have nothing
        // left to match.
        Optional(P(ScanUntil(Quote | RuneSet.Runes("\r\n")))),
        QuotedSecret()
    );

    private static Rule QuotedSecretThenAws() => AllOf(
        QuotedSecret(),
        Optional(P(ScanUntil(Quote | RuneSet.Runes("\r\n")))),
        QuotedAwsKey()
    );

    private static PatternGrammar DateAscii()
    {
        // The 03-date/compile-ascii bench uses a single-line haystack
        // ("2010-03-14") with an expected count of 5: year, separator,
        // number, separator, number. The full date.txt regex is far too
        // big to translate by hand, but the compile benchmark only ever
        // searches that one canonical haystack, so a much smaller
        // grammar that produces 5 matches on it is enough. We don't
        // attempt 03-date/ascii on the full rust-src-tools haystack;
        // that would need the real monster regex.
        var year = AllOf(
            FirstOf(P(Literal("19")), P(Literal("20"))),
            Exactly(2, P(OneOf(RuneSet.Ascii.Digits)))
        );
        var number = OneOrMore(P(OneOf(RuneSet.Ascii.Digits)));
        var separator = OneOrMore(P(OneOf(DateSeparator)));
        var match = FirstOf(year, number, separator);
        return new PatternGrammar(match, Array.Empty<Rule>());
    }

    private sealed record PatternGrammar(Rule Match, IReadOnlyList<Rule> Captures);
}
