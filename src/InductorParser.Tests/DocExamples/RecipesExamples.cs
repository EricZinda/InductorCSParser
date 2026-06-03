using System.Linq;
using NUnit.Framework;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

using static InductorParser.Tests.CanaryHelper;
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
        var formatting = TokenSet.Runes("*_#`[]()\\");
        var textChar = NoneOf(formatting);
        var text = OneOrMore(textChar).As("text");

        var bold = And(
            Literal("**"),
            OneOrMore(NoneOf(TokenSet.Runes("*"))),
            Literal("**")
        ).As("bold");

        var code = And(
            Token('`'),
            OneOrMore(NoneOf(TokenSet.Runes("`"))),
            Token('`')
        ).As("code");

        var inline = Or(bold, code, text);
        var paragraph = OneOrMore(inline).As("paragraph");

        string input = $"Hello {UnicodeExamples.GuitarGrapheme} **world** {Canary("你好", "cjk unified ideograph-4f60 + cjk unified ideograph-597d", 0x4F60, 0x597D)} `code` done";
        var result = paragraph.Parse(input);

        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // The guitar emoji belongs to a text node before the bold, and
        // the CJK belongs to a text node between bold and code. The doc
        // claims "ToString() reassembles each node losslessly," meaning
        // each text/bold/code wrapper carries its content back as text.
        var texts = result.Tree!.FindAll(text).Select(t => t.ToString()).ToList();
        Assert.That(texts.Any(t => t.Contains(UnicodeExamples.GuitarGrapheme)), Is.True,
            "guitar emoji lives in some text node");
        Assert.That(texts.Any(t => t.Contains(Canary("你好", "cjk unified ideograph-4f60 + cjk unified ideograph-597d", 0x4F60, 0x597D))), Is.True,
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
    // Doc claim: ZeroOrMore(And(Not(stop), AnyToken())) followed by stop
    // matches a block comment without prematurely consuming the close marker.
    [Test]
    public void Block_comment_grammar_stops_at_close_marker()
    {
        var closeMarker = And(Token('*'), Token('/'));
        var blockComment = And(
            Token('/'), Token('*'),
            ZeroOrMore(And(Not(closeMarker), AnyToken())).Preserve(),
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
    // καλημέρα, Devanagari, Thai under the lexer.
    [Test]
    public void Identifier_matches_unicode_scripts()
    {
        var identifier = Identifier().Compile();

        Assert.That(identifier.Parse("foo").Success, Is.True);
        Assert.That(identifier.Parse(UnicodeExamples.CafePrecomposedGrapheme).Success, Is.True);
        Assert.That(identifier.Parse(UnicodeExamples.GreekKalimeraIdentifier).Success, Is.True);
        Assert.That(identifier.Parse(UnicodeExamples.DevanagariHindiIdentifier).Success, Is.True, "Devanagari");
        Assert.That(identifier.Parse(UnicodeExamples.ThaiKamGrapheme).Success, Is.True, "Thai with SARA AM");

        // Doesn't accept things that aren't identifiers under strict UAX #31.
        Assert.That(identifier.Parse("2foo").Success, Is.False, "starts with digit");
        Assert.That(identifier.Parse("_foo").Success, Is.False,
            "strict UAX #31 doesn't include underscore in XID_Start");
    }

    // "Matching an Identifier" / "programming-language profile that also
    // allows leading underscore": Identifier(extraStartRunes:
    // TokenSet.Runes("_")) accepts _foo.
    [Test]
    public void Identifier_with_underscore_extra_start_rune()
    {
        var identifier = Identifier(extraStartRunes: TokenSet.Runes("_")).Compile();
        Assert.That(identifier.Parse("_foo").Success, Is.True);
        Assert.That(identifier.Parse("foo_bar").Success, Is.True);
    }

    // "Matching an Identifier" / "ECMAScript-style": adds _ and $.
    [Test]
    public void Identifier_with_dollar_sign_for_ecmascript()
    {
        var identifier = Identifier(
            extraStartRunes: TokenSet.Runes("_$"),
            extraBodyRunes: TokenSet.Runes("$")
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
        var identifierFormC = Identifier().Compile();
        var identifierFormKC = Identifier(System.Text.NormalizationForm.FormKC).Compile(System.Text.NormalizationForm.FormKC);

        // With default FormC, fullwidth ｆｏｏ is its own valid identifier
        // (still letters, just different code points than ASCII foo).
        Assert.That(identifierFormC.Parse(UnicodeExamples.FullwidthFooGrapheme).Success, Is.True);

        // With FormKC, fullwidth normalizes to ASCII, so the matched
        // text after the parser sees is "foo". The critical claim is
        // that it parses successfully under FormKC.
        var result = identifierFormKC.Parse(UnicodeExamples.FullwidthFooGrapheme);
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
            Or(
                Float().Flatten(FlattenType.Flatten),
                Integer().Flatten(FlattenType.Flatten),
                Identifier()
            ).As(nameof(SettingValue))
             .Flatten(FlattenType.Preserve);

        public static readonly Rule Document =
            And(
                Optional(AnyWhitespace()),
                SettingName,
                Optional(AnyWhitespace()),
                Token('='),
                Optional(AnyWhitespace()),
                SettingValue,
                Optional(AnyWhitespace()),
                Token(';'),
                Optional(AnyWhitespace()),
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

    // "Numbers with no leading zeros": the natural Or(Token('0'), ...)
    // translation positions the error one column past the actual
    // problem on bad input like "01.2.3". The Or commits to Token('0'),
    // the outer And then fails on Token('.') at offset 1.
    //
    // The test verifies this ordered-choice trap so the recipe's claim
    // ("the error message points one column past the actual problem")
    // doesn't drift if the parser ever changes default messages.
    [Test]
    public void No_leading_zero_natural_translation_positions_error_one_column_past_the_problem()
    {
        // Build the Or shape once, then give each position its own
        // identity with AliasedAs. .As is set-once, so naming the same
        // instance three times would throw; AliasedAs wraps the shared
        // shape in a fresh alias on each call, so no factory is needed.
        var numericCore = Or(
            Token('0'),
            And(OneOf(TokenSet.Range('1', '9')), ZeroOrMore(OneOf(TokenSet.Ascii.Digits)))
        );
        var grammar = And(
            numericCore.AliasedAs("major"), Token('.'),
            numericCore.AliasedAs("minor"), Token('.'),
            numericCore.AliasedAs("patch"), Eof());

        var result = grammar.Parse("01.2.3");
        Assert.That(result.Success, Is.False);
        // The actual leading-zero problem is at column 0. The reported
        // column is 1, the '1' that follows the committed-to '0'.
        Assert.That(result.ErrorColumn, Is.EqualTo(1));
    }

    // "Numbers with no leading zeros": the lookahead-inside-Or pattern
    // (only accept '0' if not followed by another digit). Under
    // depth-primary ranking, the outer Or's named WithError anchors at
    // the deepest position its branches reached (column 1, the digit
    // after the committed-to '0', where the Not's lookahead got stuck),
    // and wins the named-beats-mechanical tie there. The author's
    // message surfaces, at the spot the parse actually stalled.
    [Test]
    public void No_leading_zero_lookahead_inside_or_reports_outer_WithError_at_the_stuck_digit()
    {
        // Build the Or shape once, then give each position its own
        // identity with AliasedAs, no factory needed. The WithError
        // sits on the shared Or, and since every position wants the
        // same message, sharing that one shape is exactly right.
        var number = Or(
            And(Token('0'), Not(OneOf(TokenSet.Ascii.Digits))),
            And(OneOf(TokenSet.Range('1', '9')), ZeroOrMore(OneOf(TokenSet.Ascii.Digits)))
        ).WithError("Number with no leading zeros expected");
        var grammar = And(
            number.AliasedAs("major"), Token('.'),
            number.AliasedAs("minor"), Token('.'),
            number.AliasedAs("patch"), Eof());

        var result = grammar.Parse("01.2.3");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorColumn, Is.EqualTo(1),
            "the Or's WithError anchors at the deepest position its branches reached");
        Assert.That(result.ErrorMessage,
            Does.Contain("Number with no leading zeros"),
            "the Or's named WithError wins the named-beats-mechanical tie at that depth");
    }

    // "Numbers with no leading zeros": the reject-first pattern puts
    // the Not before any consumption, so the Not's inner succeeds on
    // bad input (consumed "01" internally) and no failures get
    // recorded inside it. The outer And's WithError on the Not fires
    // at the rule's start position.
    //
    // This is the working approach the recipe recommends.
    [Test]
    public void No_leading_zero_reject_first_pattern_positions_error_at_the_bad_digit()
    {
        // Build the reject-first shape once and alias it under three
        // names. The WithError here is generic, so the shared shape
        // works. When each position needs its own message text, a
        // factory parameterized by name is the way (see the SemVer
        // sample under E2ESamples/SemVer/Rewrite).
        var numericCore = And(
            Not(And(Token('0'), OneOf(TokenSet.Ascii.Digits)))
                .WithError("Number with no leading zeros expected"),
            OneOrMore(OneOf(TokenSet.Ascii.Digits)));

        var grammar = And(
            numericCore.AliasedAs("major"), Token('.'),
            numericCore.AliasedAs("minor"), Token('.'),
            numericCore.AliasedAs("patch"), Eof());

        var leadingZeroOnMajor = grammar.Parse("01.2.3");
        Assert.That(leadingZeroOnMajor.Success, Is.False);
        Assert.That(leadingZeroOnMajor.ErrorColumn, Is.EqualTo(0));
        Assert.That(leadingZeroOnMajor.ErrorMessage,
            Does.Contain("no leading zeros"));

        var leadingZeroOnMinor = grammar.Parse("1.02.3");
        Assert.That(leadingZeroOnMinor.Success, Is.False);
        Assert.That(leadingZeroOnMinor.ErrorColumn, Is.EqualTo(2));

        var leadingZeroOnPatch = grammar.Parse("1.2.03");
        Assert.That(leadingZeroOnPatch.Success, Is.False);
        Assert.That(leadingZeroOnPatch.ErrorColumn, Is.EqualTo(4));

        // Bare 0 is fine (the spec allows it), and "10.20.30" is fine
        // because the reject-first probe rejects only "0[digit]".
        Assert.That(grammar.Parse("0.0.0").Success, Is.True);
        Assert.That(grammar.Parse("10.20.30").Success, Is.True);
    }

    // "Numbers with no leading zeros" / "validate after parsing": accept any
    // digit run in the grammar, then check the leading zero in the consumer
    // and use the node's source position to build a "line X, column Y"
    // message. The recipe walks to the named node and reads its position.
    //
    // Pins the API the recipe uses to get a node's position: Symbol.SourceRange
    // (public, standalone, no ParseResult needed). The earlier doc text
    // described a result.SourceRangeOf(node) call the library no longer has
    // (it moved onto Symbol once every Symbol carried its ParseContext).
    [Test]
    public void No_leading_zero_validate_after_parsing_uses_node_SourceRange()
    {
        var numericCore = OneOrMore(OneOf(TokenSet.Ascii.Digits)).As("major");

        var result = numericCore.Parse("01");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var node = result.Tree!.Find(numericCore)!;
        var text = node.ToString();
        Assert.That(text, Is.EqualTo("01"));

        // The recipe's leading-zero branch: text.Length > 1 && text[0] == '0'.
        Assert.That(text.Length > 1 && text[0] == '0', Is.True);

        // The position the error message points at comes straight off the
        // Symbol, no ParseResult round-trip.
        var range = node.SourceRange;
        Assert.That(range, Is.Not.Null);
        Assert.That(range!.Value.Start.Line, Is.EqualTo(0));
        Assert.That(range.Value.Start.Column, Is.EqualTo(0));
        // The recipe formats these one-based: "line 1, column 1".
        string message =
            $"line {range.Value.Start.Line + 1}, column {range.Value.Start.Column + 1}: " +
            $"Major version '{text}' must not have leading zeros";
        Assert.That(message, Is.EqualTo("line 1, column 1: Major version '01' must not have leading zeros"));
    }
}
