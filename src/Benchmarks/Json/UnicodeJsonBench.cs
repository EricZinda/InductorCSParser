using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using InductorParser.Benchmarks.Json.InductorParsers;
using global::InductorParser;
using global::InductorParser.Lexing;

namespace InductorParser.Benchmarks.Json;

// Compares the parser's two Unicode implementations (the runtime's
// string.Normalize / StringInfo against the built-in UAX #15 / UAX #29
// code, see UnicodeEnvironment) on JSON whose string values hold real
// non-ASCII content. The ASCII JsonBench can't see a difference because
// both normalizers return all-ASCII input unchanged. These pools make
// the per-parse FormC normalization actually walk Unicode text.
//
// The setting is process-wide and freezes on first use, so no single
// process can measure both. Instead the config defines two jobs that
// differ only in the INDUCTORPARSER_UNICODE_IMPLEMENTATION environment
// variable, which UnicodeImplementationOverride applies in each
// BenchmarkDotNet child process at assembly load. One run therefore
// produces both sides in one summary, and the Runtime job is the
// baseline, so the Ratio column reads as the built-in implementation's
// cost relative to the runtime's.
//
// Three measurements per pool:
//   ParseFormC       - the real path: normalize the input to FormC, then
//                      parse.
//   ParseNoNormalize - the same grammar compiled with Compile(null), the
//                      floor with input normalization skipped entirely.
//   NormalizeOnly    - just the normalizer, to tell how much of any
//                      ParseFormC gap is normalization rather than lexing.
[Config(typeof(BothImplementationsConfig))]
[MemoryDiagnoser]
public class UnicodeJsonBench
{
    private class BothImplementationsConfig : ManualConfig
    {
        public BothImplementationsConfig()
        {
            AddJob(Job.ShortRun
                .WithEnvironmentVariable("INDUCTORPARSER_UNICODE_IMPLEMENTATION", "Runtime")
                .WithId("Runtime")
                .AsBaseline());
            AddJob(Job.ShortRun
                .WithEnvironmentVariable("INDUCTORPARSER_UNICODE_IMPLEMENTATION", "Bundled")
                .WithId("Bundled"));
        }
    }

    public enum ContentPool
    {
        NfcLatin,
        Cjk,
        Hangul,
        Decomposed,
        Emoji,
    }

    [ParamsAllValues]
    public ContentPool Pool { get; set; }

#nullable disable
    private string _json;
    private Rule _noNormalizeRule;
#nullable restore

    [GlobalSetup]
    public void Setup()
    {
        string[] atoms = AtomsFor(Pool);
        var random = new Random(42);
        string RandomAtomString(int atomCount)
        {
            var builder = new StringBuilder(atomCount * 2);
            for (int i = 0; i < atomCount; i++)
                builder.Append(atoms[random.Next(atoms.Length)]);
            return builder.ToString();
        }
        _json = JsonBench.BuildJson(4, 4, 3, RandomAtomString).ToString()!;

        _noNormalizeRule = InductorJsonGrammar.Build().RootRule.Compile(null);

        // A failed parse would measure the error path instead of a parse,
        // so check both variants once before benchmarking.
        var formCResult = InductorJsonParser.Parse(_json);
        if (!formCResult.Success)
            throw new InvalidOperationException(
                $"FormC parse failed on pool {Pool}: {formCResult.ErrorMessage}");
        var noNormalizeResult = _noNormalizeRule.Parse(_json);
        if (!noNormalizeResult.Success)
            throw new InvalidOperationException(
                $"No-normalize parse failed on pool {Pool}: {noNormalizeResult.ErrorMessage}");
    }

    [Benchmark]
    public object ParseFormC() => InductorJsonParser.Parse(_json);

    [Benchmark]
    public object ParseNoNormalize() => _noNormalizeRule.Parse(_json);

    [Benchmark]
    public string NormalizeOnly() => NormalizationHelpers.Normalize(_json, NormalizationForm.FormC);

    // One array of atoms per pool. A generated string is a sequence of
    // atoms, so multi-char pieces (a decomposed pair, a joined emoji)
    // stay intact instead of being torn by per-char selection. Every
    // atom is valid Unicode text: no lone surrogate halves and no
    // U+FFFE, which the pre-parse scan would reject as MalformedInput.
    private static string[] AtomsFor(ContentPool pool) => pool switch
    {
        // European text: plain ASCII letters with precomposed accents
        // mixed in. Already NFC, so a normalizer with quick-check tables
        // rewrites nothing. The common real-world case.
        ContentPool.NfcLatin =>
            ["a", "e", "i", "n", "o", "r", "s", "t", "é", "è", "ü", "ñ", "ç", "ö", "â", "ä"],

        // Chinese / Japanese ideographs: non-Latin BMP text with no
        // combining marks and nothing for normalization to rewrite.
        ContentPool.Cjk =>
            ["世", "界", "日", "本", "語", "漢", "字", "文", "中", "国"],

        // Precomposed Hangul syllables: already NFC, but they exercise
        // the composition-heavy part of the tables (the algorithmic
        // LV / LVT decomposition and recomposition).
        ContentPool.Hangul =>
            ["한", "국", "어", "세", "계", "글", "말", "소"],

        // Base letter plus combining mark (the decomposed NFD shape).
        // The one pool where FormC normalization genuinely rewrites the
        // input, so every parse pays for the rebuild.
        ContentPool.Decomposed =>
            ["é", "à", "ô", "ü", "ñ", "ç"],

        // Emoji: surrogate pairs, a flag (regional indicators), a skin
        // tone, a variation selector, and a joined family. Nothing for
        // normalization to rewrite, but the widest range of scalar
        // values the tables get probed with.
        ContentPool.Emoji =>
            ["😀", "🎉", "🚀", "🌍", "🇺🇸", "👍🏽", "❤️", "👩‍👩‍👧"],

        _ => throw new ArgumentOutOfRangeException(nameof(pool), pool, "Unknown pool."),
    };
}
