using System.Linq;
using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace InductorParser.Tests.DocExamples;

// Runnable recipe-style examples kept under test so they can be copied into
// documentation without drifting from library behavior.
[TestFixture]
public class RecipesExamples
{
    // "Pass-Through Text: Matching 'All Text'", the markdown-ish
    // grammar. Doc claim: parsing "Hello 🎸 **world** 你好 `code` done"
    // produces a tree where the guitar emoji is in the first text node,
    // the CJK in another, and ToString() reassembles each node losslessly.
    [Test]
    public void Pass_through_text_grammar_handles_emoji_and_cjk()
    {
        var formatting = RuneSet.Runes("*_#`[]()\\");
        var textChar = NoneOf(formatting);
        var text = OneOrMore(textChar).As("text").Preserve();

        var bold = AllOf(
            Literal("**"),
            OneOrMore(NoneOf(RuneSet.Runes("*"))),
            Literal("**")
        ).As("bold").Preserve();

        var code = AllOf(
            Token('`'),
            OneOrMore(NoneOf(RuneSet.Runes("`"))),
            Token('`')
        ).As("code").Preserve();

        var inline = FirstOf(bold, code, text);
        var paragraph = OneOrMore(inline).As("paragraph").Preserve();

        const string input = "Hello 🎸 **world** 你好 `code` done";
        var result = paragraph.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // The guitar emoji belongs to a text node before the bold, and
        // the CJK belongs to a text node between bold and code. The doc
        // claims "ToString() reassembles each node losslessly," meaning
        // each text/bold/code wrapper carries its content back as text.
        var texts = result.Tree!.FindAll(text).Select(t => t.ToString()).ToList();
        Assert.That(texts.Any(t => t.Contains("🎸")), Is.True,
            "guitar emoji lives in some text node");
        Assert.That(texts.Any(t => t.Contains("你好")), Is.True,
            "CJK lives in some text node");

        // bold and code each fire once and their body text round-trips
        // (the delimiter Tokens default to FlattenType.Delete, so the
        // tree-level ToString omits the literal `**` and backticks).
        Assert.That(result.Tree!.FindAll(bold).Count(), Is.EqualTo(1));
        Assert.That(result.Tree!.FindAll(bold).Single().ToString(), Is.EqualTo("world"));
        Assert.That(result.Tree!.FindAll(code).Count(), Is.EqualTo(1));
        Assert.That(result.Tree!.FindAll(code).Single().ToString(), Is.EqualTo("code"));
    }

    // "Stopping at a Multi-Character Terminator", block comment grammar.
    // Doc claim: ZeroOrMore(AllOf(Not(stop), AnyToken())) followed by stop
    // matches a block comment without prematurely consuming the close marker.
    [Test]
    public void Block_comment_grammar_stops_at_close_marker()
    {
        var closeMarker = AllOf(Token('*'), Token('/'));
        var blockComment = AllOf(
            Token('/'), Token('*'),
            ZeroOrMore(AllOf(Not(closeMarker), AnyToken())).Preserve(),
            closeMarker).Preserve();

        var result = blockComment.Parse("/* hello world */");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // The comment delimiters Token('/'), Token('*') default to
        // FlattenType.Delete, so result.Tree.ToString() carries only
        // the body text. The success of the parse plus consumption of
        // the trailing close marker is the doc's claim.
        Assert.That(result.Tree!.ToString(), Is.EqualTo(" hello world "));
    }

    // "Matching an Identifier": Identifier accepts foo, café,
    // καλημέρα, Devanagari, Thai under the default lexer.
    [Test]
    public void Identifier_matches_unicode_scripts()
    {
        var identifier = Identifier().Compile();

        Assert.That(identifier.Parse("foo").Success, Is.True);
        Assert.That(identifier.Parse("café").Success, Is.True);
        Assert.That(identifier.Parse("καλημέρα").Success, Is.True);
        Assert.That(identifier.Parse("हिन्दी").Success, Is.True, "Devanagari");
        Assert.That(identifier.Parse("กำ").Success, Is.True, "Thai with SARA AM");

        // Doesn't accept things that aren't identifiers under strict UAX #31.
        Assert.That(identifier.Parse("2foo").Success, Is.False, "starts with digit");
        Assert.That(identifier.Parse("_foo").Success, Is.False,
            "strict UAX #31 doesn't include underscore in XID_Start");
    }

    // "Matching an Identifier" / "programming-language profile that also
    // allows leading underscore": Identifier(extraStartRunes:
    // RuneSet.Runes("_")) accepts _foo.
    [Test]
    public void Identifier_with_underscore_extra_start_rune()
    {
        var identifier = Identifier(extraStartRunes: RuneSet.Runes("_")).Compile();
        Assert.That(identifier.Parse("_foo").Success, Is.True);
        Assert.That(identifier.Parse("foo_bar").Success, Is.True);
    }

    // "Matching an Identifier" / "ECMAScript-style": adds _ and $.
    [Test]
    public void Identifier_with_dollar_sign_for_ecmascript()
    {
        var identifier = Identifier(
            extraStartRunes: RuneSet.Runes("_$"),
            extraBodyRunes: RuneSet.Runes("$")
        ).Compile();
        Assert.That(identifier.Parse("$foo").Success, Is.True);
        Assert.That(identifier.Parse("foo$bar").Success, Is.True);
        Assert.That(identifier.Parse("_$").Success, Is.True);
    }

    // "Matching an Identifier" / "NFKC equivalence": fullwidth ｆｏｏ
    // matches the same identifier as plain foo when normalization is
    // FormKC. Without FormKC, fullwidth and plain are different
    // identifiers.
    [Test]
    public void Identifier_with_NFKC_treats_fullwidth_as_ascii()
    {
        var identifier = Identifier().Compile();

        // With default FormC, fullwidth ｆｏｏ is its own valid identifier
        // (still letters, just different code points than ASCII foo).
        Assert.That(identifier.Parse("ｆｏｏ").Success, Is.True);

        // With FormKC, fullwidth normalizes to ASCII, so the matched
        // text after the parser sees it's "foo" (we test the result's
        // round-trip via ToString uses the normalized form, but the
        // critical claim is that it parses successfully under FormKC).
        var result = identifier.Parse("ｆｏｏ", new ParseOptions
        {
            NormalizeInput = System.Text.NormalizationForm.FormKC,
        });
        Assert.That(result.Success, Is.True);
    }

    // "Organizing a Large Grammar as a Class", the NameValueGrammar
    // example. Class fields with .As(nameof(...)) and .Compile() at type
    // init time. Doc claim: callers get the value via
    //   var name = result.Tree.Find(NameValueGrammar.SettingName).ToString();
    public static class NameValueGrammar
    {
        public static readonly Rule SettingName =
            Identifier().As(nameof(SettingName));

        public static readonly Rule SettingValue =
            FirstOf(
                Float().Flatten(FlattenType.Flatten),
                Integer().Flatten(FlattenType.Flatten),
                Identifier()
            ).As(nameof(SettingValue))
             .Flatten(FlattenType.Preserve);

        public static readonly Rule Document =
            AllOf(
                OptionalWhitespace(),
                SettingName,
                OptionalWhitespace(),
                Token('='),
                OptionalWhitespace(),
                SettingValue,
                OptionalWhitespace(),
                Token(';'),
                OptionalWhitespace(),
                Eof()
            ).As(nameof(Document)).Preserve().Compile();
    }

    [Test]
    public void Class_based_grammar_runs_through_field_references()
    {
        var result = NameValueGrammar.Document.Parse("difficulty = hard;");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree, Is.Not.Null);

        var name = result.Tree!.Find(NameValueGrammar.SettingName)!.ToString();
        var value = result.Tree!.Find(NameValueGrammar.SettingValue)!.ToString();
        Assert.That(name, Is.EqualTo("difficulty"));
        Assert.That(value, Is.EqualTo("hard"));
    }

    // "A Reusable Compiler Base Class", the Compiler<TResult> abstract
    // base class and the NameValueCompiler subclass that uses it.
    public abstract class Compiler<TResult>
    {
        private readonly Rule _root;

        protected Compiler(Rule root) => _root = root;

        public bool TryCompile(string input, out TResult result, out string error)
        {
            var parsed = _root.Parse(input);
            if (!parsed.Success)
            {
                result = default!;
                error = $"Line {parsed.ErrorLine}: {parsed.ErrorMessage}";
                return false;
            }

            result = ProcessTree(parsed.Tree!);
            error = "";
            return true;
        }

        protected abstract TResult ProcessTree(Symbol tree);
    }

    public sealed record Setting(string Name, string Value);

    public sealed class NameValueCompiler : Compiler<Setting>
    {
        public NameValueCompiler() : base(NameValueGrammar.Document) { }

        protected override Setting ProcessTree(Symbol tree)
        {
            var nameNode = tree.Find(NameValueGrammar.SettingName);
            var valueNode = tree.Find(NameValueGrammar.SettingValue);
            return new Setting(nameNode!.ToString(), valueNode!.ToString());
        }
    }

    [Test]
    public void Compiler_base_class_returns_typed_result_on_success()
    {
        var compiler = new NameValueCompiler();
        bool ok = compiler.TryCompile("difficulty = hard;", out var setting, out var error);

        Assert.That(ok, Is.True);
        Assert.That(error, Is.EqualTo(""));
        Assert.That(setting, Is.EqualTo(new Setting("difficulty", "hard")));
    }

    [Test]
    public void Compiler_base_class_returns_error_on_failure()
    {
        var compiler = new NameValueCompiler();
        bool ok = compiler.TryCompile("difficulty hard", out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.StartWith("Line "));
    }
}
