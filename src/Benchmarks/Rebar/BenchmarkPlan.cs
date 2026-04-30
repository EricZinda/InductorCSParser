using System;
using System.Collections.Generic;
using System.Linq;
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

    public BenchmarkPlan(Rule scanner, Rule match, IReadOnlyList<Rule> captures)
    {
        _scanner = scanner;
        _match = match;
        _captures = captures;
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
        var result = Parse(haystack);
        return result.Tree!.FindAll(_match).LongCount();
    }

    private long SumMatchBytes(string haystack)
    {
        var result = Parse(haystack);
        long total = 0;
        foreach (var match in result.Tree!.FindAll(_match))
            total += match.GetUtf8ByteCount();
        return total;
    }

    private long CountMatchingLines(string haystack)
    {
        long total = 0;
        foreach (var line in Lines(haystack))
        {
            var result = Parse(line);
            if (result.Tree!.Find(_match) != null)
                total++;
        }
        return total;
    }

    private long CountGrepCaptures(string haystack)
    {
        long total = 0;
        foreach (var line in Lines(haystack))
        {
            var result = Parse(line);
            foreach (var match in result.Tree!.FindAll(_match))
            {
                total++;
                foreach (var capture in _captures)
                {
                    var symbol = match.Find(capture);
                    if (symbol != null && symbol.ToString().Length > 0)
                        total++;
                }
            }
        }
        return total;
    }

    private ParseResult Parse(string input)
    {
        var result = _scanner.Parse(input, SearchOptions);
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
    private static readonly RuneSet AsciiAlpha = RuneSet.Range('A', 'Z') | RuneSet.Range('a', 'z');
    private static readonly RuneSet AsciiWord = AsciiAlpha | RuneSet.Ascii.Digits | RuneSet.Runes("_");
    private static readonly RuneSet AwsKeyTail = AsciiUpper | RuneSet.Range('0', '7');
    private static readonly RuneSet AsciiRegexWhitespace = RuneSet.Runes(" \t\r\n\f\v");
    private static readonly RuneSet CodeSeparator = RuneSet.Runes(",") | AsciiRegexWhitespace;

    public static BenchmarkPlan Build(RebarConfig config)
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

            "curated/04-ruff-noqa/real" => RuffNoqaReal(),
            "curated/04-ruff-noqa/tweaked" => RuffNoqaTweaked(),
            "curated/04-ruff-noqa/compile-real" => RuffNoqaReal(),

            "curated/08-words/all-english" => AsciiWords(1),
            "curated/08-words/long-english" => AsciiWords(12),

            "curated/09-aws-keys/quick" => AwsQuick(),
            "curated/09-aws-keys/compile-quick" => AwsQuick(),

            _ => throw new NotSupportedException(
                $"Benchmark '{config.Name}' isn't in the hand-translated InductorParser rebar subset.")
        };

        ValidateSupportedModel(config);
        ValidateCaseMode(config);
        return CompileScanner(grammar);
    }

    private static void ValidateSupportedModel(RebarConfig config)
    {
        bool ok = config.Name switch
        {
            "curated/04-ruff-noqa/real" or "curated/04-ruff-noqa/tweaked" => config.Model == "grep-captures",
            "curated/09-aws-keys/quick" => config.Model == "grep",
            "curated/04-ruff-noqa/compile-real" or "curated/09-aws-keys/compile-quick" => config.Model == "compile",
            "curated/08-words/all-english" or "curated/08-words/long-english" => config.Model == "count-spans",
            _ when config.Name.StartsWith("curated/01-literal/", StringComparison.Ordinal) => config.Model == "count",
            _ when config.Name.StartsWith("curated/02-literal-alternate/", StringComparison.Ordinal) => config.Model == "count",
            _ => false
        };
        if (!ok)
            throw new NotSupportedException($"Benchmark '{config.Name}' isn't supported for rebar model '{config.Model}'.");
    }

    private static void ValidateCaseMode(RebarConfig config)
    {
        if (config.CaseInsensitive && config.Unicode)
            throw new NotSupportedException("Unicode-aware case-insensitive matching is intentionally unsupported until full Unicode case-insensitive matching lands.");
    }

    private static BenchmarkPlan CompileScanner(PatternGrammar grammar)
    {
        grammar.Match.As("match").Flatten(FlattenType.Preserve);
        var scanner = ZeroOrMore(FirstOf(
            grammar.Match,
            AnyToken().Flatten(FlattenType.Delete)
        )).As("scan").Flatten(FlattenType.Preserve);
        scanner.Compile();
        return new BenchmarkPlan(scanner, grammar.Match, grammar.Captures);
    }

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

    private sealed record PatternGrammar(Rule Match, IReadOnlyList<Rule> Captures);
}
