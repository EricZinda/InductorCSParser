using System;
using System.Text;
using InductorParser.SyntaxTree;

namespace InductorParser;

/// <summary>
/// This is the main class for building grammars. Every built-in rule
/// type has a factory method here and most of the classes themselves are internal. Thus, grammar code
/// composes rules by calling these functions instead of
/// instantiating rule classes directly.
/// </summary>
/// <remarks>
/// The idiomatic usage is to put <c><see cref="Rules">using static InductorParser.Rules;</see></c>
/// at the top of a grammar file, which allows the rule factory calls to drop the class prefix. This makes a grammar 
/// read close to the way you'd write it on a whiteboard:
/// <code>
/// var expression = And(
///     Identifier(),
///     Optional(AnyWhitespace()),
///     Token('='),
///     Optional(AnyWhitespace()),
///     Integer()
/// );
/// </code>
/// Most of the factory methods just forward their arguments to the matching rule
/// class constructor. A few factories
/// compose several rules instead of forwarding to a single rule type. This is a convenience since
/// many grammars end up wanting them.
///
/// Every rule has a default <see cref="FlattenType"/> that
/// controls how it contributes to the parse tree when it matches: <see cref="InductorParser.SyntaxTree.FlattenType.Delete">FlattenType.Delete</see>
/// drops the node, <see cref="FlattenType.Flatten">FlattenType.Flatten</see> lifts its children into the parent,
/// <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> keeps the rule's Symbol. Each factory's summary lists its
/// default. You can override it on any rule by using
/// <see cref="Rule.Flatten(FlattenType)">Rule.Flatten(FlattenType)</see> to change it for a
/// specific use.
///
/// A fluent modifier configures a rule and returns that same rule, so
/// you can chain several calls in one expression. Two such modifiers on
/// <see cref="Rule"/> show up in almost every grammar:
/// <list type="bullet">
/// <item><description>
/// <see cref="Rule.As(string)">Rule.As(string)</see> (or <see cref="Rule.As(SymbolId)">Rule.As(SymbolId)</see>)
/// names the rule so trace output, error messages, and
/// <see cref="Symbol.Find(SymbolId)">Symbol.Find(SymbolId)</see> can refer to it. Naming also flips the rule's
/// <see cref="FlattenType"/> to <see cref="InductorParser.SyntaxTree.FlattenType.Preserve">FlattenType.Preserve</see> so Find can see it.
/// </description></item>
/// <item><description>
/// <see cref="Rule.WithError(string, bool)">Rule.WithError(string, bool)</see> attaches a custom
/// message that surfaces when this rule is the deepest point a parse
/// failed, in place of the generic "unexpected 'x'".
/// </description></item>
/// </list>
/// For example, create an identifier rule, name it, and attach an error message:
/// <code>
/// var variableName = Identifier()
///     .As("variableName")
///     .WithError("Expected a variable name");
/// </code>
/// Each call configures the rule returned by the previous call. The final
/// result is one rule that you can use inside a larger grammar.
/// </remarks>
public static class Rules
{
    /// <summary>
    /// Match one token whose content is exactly the given character.
    /// Default <see cref="FlattenType"/>: <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// A token is one character as the user sees it (a grapheme
    /// cluster), so <c><see cref="Rules.Token(char)">Rules.Token('a')</see></c> matches when the token is the
    /// single char 'a' but fails when 'a' is combined with a
    /// following accent (because the token is then rendered as one
    /// 'a' with an accent over it, which doesn't match a bare 'a').
    /// To match a single character that's built from several code
    /// points like that (a base letter plus a combining accent, a
    /// skin-tone emoji, a flag), pass the whole thing as a string to
    /// <see cref="Token(string)">Rules.Token(string)</see>.
    ///
    /// For multi-grapheme matches use <see cref="Literal">Rules.Literal(string)</see>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value passed to <paramref name="c"/> is a UTF-16 surrogate
    /// (U+D800 through U+DFFF). These values represent one half of the
    /// two-<c>char</c> pair needed to encode a code point above U+FFFF,
    /// so this overload rejects them. To match such a code point, use
    /// <see cref="Token(Rune)">Rules.Token(Rune)</see> or <see cref="Token(int)">Rules.Token(int)</see>.
    /// </exception>
    public static Rule Token(char c)
    {
        if (char.IsSurrogate(c))
            throw new ArgumentOutOfRangeException(nameof(c),
                "A single char can't hold a character above U+FFFF such as an emoji. Those need two chars together and this is just one half. To match a character above U+FFFF, use Token(Rune) or Token(int).");
        return new GraphemeRule(c.ToString());
    }

    /// <summary>
    /// Match one token whose content is exactly the given
    /// <see cref="Rune"/>. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// A token is one character as the user sees it (a grapheme
    /// cluster), so this matches when the user sees one bare
    /// <c>r</c> at the current position. It fails when <c>r</c> is
    /// followed by Unicode characters that tell the renderer to
    /// glue them all into a single grapheme cluster, because
    /// then the user sees one combined character at that position,
    /// not a bare <c>r</c>.
    /// To match that combined character, pass the whole thing as a
    /// string to <see cref="Token(string)">Rules.Token(string)</see>.
    ///
    /// For multi-grapheme matches use <see cref="Literal">Rules.Literal(string)</see>.
    /// </remarks>
    public static Rule Token(Rune r) => new GraphemeRule(r.ToString());

    /// <summary>
    /// Match one token whose content is exactly the rune with the
    /// given integer code point (the integer Unicode assigns to a
    /// character). Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// A token is one character as the user sees it (a grapheme
    /// cluster), so this matches when the user sees the given rune
    /// standing alone at the current position. It fails when the
    /// rune is followed by Unicode characters that tell the
    /// renderer to glue them all into a single grapheme cluster,
    /// because then the user sees one combined character at that
    /// position, not the bare rune.
    /// To match that combined character, pass the whole thing as a
    /// string to <see cref="Token(string)">Rules.Token(string)</see>.
    ///
    /// For multi-grapheme matches use <see cref="Literal">Rules.Literal(string)</see>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="codepoint"/> isn't a valid Unicode scalar
    /// (outside 0..0x10FFFF or inside the surrogate block
    /// 0xD800..0xDFFF).
    /// </exception>
    public static Rule Token(int codepoint)
    {
        if (!Rune.IsValid(codepoint))
            throw new ArgumentOutOfRangeException(nameof(codepoint), codepoint,
                "Not a valid Rune (i.e. Unicode scalar value): 0..0x10FFFF, excluding surrogates 0xD800..0xDFFF.");
        return new GraphemeRule(new Rune(codepoint).ToString());
    }

    /// <summary>
    /// Match one token (one character as the user sees it) whose
    /// content equals the given string. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// A token is one character as the user sees it (a grapheme
    /// cluster), and the string is that character's content. The
    /// string can be longer than one C# char as long as the user
    /// still sees it as one character (multi-char examples are:
    /// skin-tone emoji like 👋🏽,
    /// regional-indicator flags like 🇺🇸, family emoji like 👨‍👩‍👧,
    /// an accented letter typed as base + accent, etc.).
    /// Construction validates that the string is exactly one such
    /// character and throws otherwise. The input arrives as
    /// a single token from the lexer and this rule matches it in
    /// one compare.
    ///
    /// For multi-grapheme matches use <see cref="Literal">Rules.Literal(string)</see>.
    /// </remarks>
    public static Rule Token(string token) => new GraphemeRule(token);

    /// <summary>
    /// Match an exact multi-token string. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// A token is one character as the user sees it (a grapheme
    /// cluster). Literal matches a sequence of them in order, so the
    /// string is the exact text to match.
    /// For a one-token match use <see cref="Token(string)">Rules.Token(string)</see>.
    /// Literal is the N-token generalization and collapses what
    /// would otherwise be N Token rules into
    /// one. 
    /// </remarks>
    public static Rule Literal(string value) => new LiteralRule(value);

    /// <summary>
    /// ASCII-case-insensitive variant of <see cref="Literal">Rules.Literal(string)</see>. The
    /// pattern must be ASCII-only. Construction throws on any char
    /// outside <c>0x00..0x7F</c>. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// ASCII letters (A-Z, a-z) in the pattern match either case in
    /// the input. ASCII non-letters (digits, punctuation, controls)
    /// compare bit-exact.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> contains any char outside the ASCII
    /// range (<c>0x00..0x7F</c>).
    /// </exception>
    public static Rule LiteralIgnoreAsciiCase(string value) => new LiteralIgnoreAsciiCaseRule(value);

    /// <summary>
    /// Match one token (one character as the user sees it) when that
    /// token is in the given <see cref="TokenSet"/>. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// The workhorse character-class rule.
    /// A <see cref="InductorParser.TokenSet">TokenSet</see> can hold sets of anything the user sees as one
    /// character, including composed sequences like skin-toned
    /// emoji, regional-indicator flags, and family emoji. So a set
    /// built with <c><see cref="TokenSet.Graphemes">TokenSet.Letters | TokenSet.Graphemes("🇺🇸")</see></c>
    /// matches either a letter or the US flag, each as one token.
    /// <code>
    /// // Identifier character: any letter, digit, or underscore
    /// var idChar = OneOf(TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_"));
    ///
    /// // ASCII consonant: ASCII letter minus vowels
    /// var consonant = OneOf(TokenSet.Ascii.Letters - TokenSet.Runes("aeiouAEIOU"));
    ///
    /// // Latin-script letters and digits: the category sets intersected
    /// // with the Basic Latin through Latin Extended-B blocks
    /// var latinAlnum = OneOf(
    ///     (TokenSet.Letters | TokenSet.Digits) &amp; TokenSet.Range(0x0000, 0x024F));
    /// </code>
    /// </remarks>
    public static Rule OneOf(TokenSet set) => new OneOfRule(set);

    /// <summary>
    /// Shortcut for the common "one of these literal runes" case. The
    /// input is walked rune by rune. Each scalar becomes a set member.
    /// Throws at construction if any two consecutive runes in the input
    /// form one grapheme cluster (CRLF, NFD accent, ZWJ emoji, etc.). If you want sets that 
    /// include multi-char tokens, build the <see cref="TokenSet"/> with
    /// <see cref="TokenSet.Graphemes(string[])">TokenSet.Graphemes(string[])</see> and pass it to the
    /// <see cref="OneOf(TokenSet)">Rules.OneOf(TokenSet)</see> overload. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c><see cref="Rules.OneOf(TokenSet)">Rules.OneOf(TokenSet.Runes(runes))</see></c>.
    /// When you need ranges, category unions, complements, or grapheme
    /// clusters, use <see cref="TokenSet"/> directly and pass it to the
    /// <see cref="OneOf(TokenSet)">Rules.OneOf(TokenSet)</see> overload.
    /// </remarks>
    public static Rule OneOf(string runes) => new OneOfRule(TokenSet.Runes(runes));

    /// <summary>
    /// Match one token (one character as the user sees it) when
    /// that token isn't in the given <see cref="TokenSet"/>.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// The idiomatic "any character except these" rule, commonly
    /// used as the body character in a bounded scan (for example,
    /// everything up to a closing quote).
    /// </remarks>
    public static Rule NoneOf(TokenSet set) => new NoneOfRule(set);

    /// <summary>
    /// Shortcut for "any rune except these specific ones." The
    /// input is walked rune by rune. Each scalar becomes a set member.
    /// Throws at construction if any two consecutive runes in the input
    /// form one grapheme cluster (CRLF, NFD accent, ZWJ emoji, etc.). If
    /// you want sets that include multi-char tokens, build the
    /// <see cref="TokenSet"/> with <see cref="TokenSet.Graphemes(string[])">TokenSet.Graphemes(string[])</see>
    /// and pass it to the <see cref="NoneOf(TokenSet)">Rules.NoneOf(TokenSet)</see> overload. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c><see cref="Rules.NoneOf(TokenSet)">Rules.NoneOf(TokenSet.Runes(runes))</see></c>.
    /// When you need ranges, category unions, complements, or grapheme
    /// clusters, use <see cref="TokenSet"/> directly and pass it to the
    /// <see cref="NoneOf(TokenSet)">Rules.NoneOf(TokenSet)</see> overload.
    /// </remarks>
    public static Rule NoneOf(string runes) => new NoneOfRule(TokenSet.Runes(runes));

    /// <summary>
    /// Scan forward while the next token is in <paramref name="set"/>,
    /// stopping at the first token outside the set, and return the whole
    /// run as one leaf <see cref="SyntaxTree.Symbol"/>. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// Use this for the hot scanning paths: identifiers, words, numbers,
    /// whitespace runs, and other character-class runs where you want the run as
    /// a single symbol, not N independent symbols. The matched text is the same as
    /// <c>AtLeast(minimumCount, OneOf(set))</c>, but the runtime
    /// cost is very different. The <see cref="Rules.OneOf(TokenSet)">Rules.OneOf(TokenSet)</see> form opens a transaction
    /// and allocates a Symbol per token. This rule opens one transaction at the top, runs a tight
    /// scan loop in the lexer, and emits one Symbol over the whole run.
    ///
    /// The exact converse of <see cref="ScanUntil(TokenSet, bool)">Rules.ScanUntil(TokenSet, bool)</see>: <see cref="InductorParser.Rules.ScanUntil(InductorParser.Rule,System.Boolean)">Rules.ScanUntil</see>
    /// stops when the next token is in its stop set, <see cref="InductorParser.Rules.ScanWhile(InductorParser.TokenSet,System.Int32)">Rules.ScanWhile</see> stops when
    /// the next token is outside its match set. Use <see cref="Rules.ScanWhile">Rules.ScanWhile(TokenSet, int)</see> when
    /// the run's character class is the natural way to describe the body
    /// (identifiers, words, numbers), and <see cref="ScanUntil(TokenSet, bool)">Rules.ScanUntil(TokenSet, bool)</see>
    /// when only the boundary is namable (string bodies, comment bodies).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minimumCount"/> is negative.
    /// </exception>
    public static Rule ScanWhile(TokenSet set, int minimumCount = 1) =>
        new ScanWhileRule(set, minimumCount);

    /// <summary>
    /// Match text up to (but not including) a token in the stopAt set.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// Succeeds with a possibly empty body when the stopper matches.
    /// Fails if end-of-input is reached without ever matching the
    /// stopper, unless <paramref name="eofIsTerminator"/> is true.
    /// It scans the characters directly in one pass, which is a meaningful
    /// speedup over <c><see cref="Rules.ZeroOrMore">Rules.ZeroOrMore(Rules.NoneOf(stopAt))</see></c> for long strings.
    /// <para>
    /// Pass <paramref name="eofIsTerminator"/> = <c>true</c> for
    /// grammars where the body legitimately ends at the stopper or at
    /// EOF (line comments that may close with a newline or with the
    /// end of file, for example). For "match the rest of the input,"
    /// use <see cref="ScanUntilEof">Rules.ScanUntilEof()</see> instead.
    /// </para>
    /// <code>
    /// // CSV field body: scan until the next comma or line terminator
    /// // (strict). LineTerminators rather than Runes(",\n") because a
    /// // CRLF arrives from the lexer as one two-rune token, which a set
    /// // holding only a bare LF wouldn't stop at.
    /// var field = ScanUntil(TokenSet.Runes(",") | TokenSet.LineTerminators);
    ///
    /// // Line comment body: stops at any line break or EOF
    /// var lineCommentBody = ScanUntil(TokenSet.LineTerminators,
    ///                                 eofIsTerminator: true);
    /// </code>
    /// </remarks>
    public static Rule ScanUntil(TokenSet stopAt, bool eofIsTerminator = false) =>
        new ScanUntilRule(stopAt, eofIsTerminator);

    /// <summary>
    /// The same as <see cref="ScanUntil(TokenSet, bool)">Rules.ScanUntil(TokenSet, bool)</see> but also including escape sequences.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// When the scanner hits the <paramref name="escapeStart"/>
    /// rune, it runs <paramref name="escapeEnd"/> as a sub-rule to
    /// consume the escape body, then resumes. The shape for
    /// backslash-escaped string literals.
    /// <code>
    /// // JSON-style string body: anything up to " or \, with
    /// // \n, \t, \r, \", \\ as the allowed single-rune escapes
    /// var body = ScanUntil(
    ///     TokenSet.Runes("\"\\"),
    ///     new Rune('\\'),
    ///     OneOf("ntr\"\\"));
    /// </code>
    /// </remarks>
    public static Rule ScanUntil(TokenSet stopAt, Rune escapeStart, Rule escapeEnd, bool eofIsTerminator = false) =>
        new ScanUntilRule(stopAt, escapeStart, escapeEnd, eofIsTerminator);

    /// <summary>
    /// Same as the Rune-valued escape-start overload, but with a
    /// rule-valued escape start instead. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// For escapes whose start marker is longer than one rune.
    /// <code>
    /// // Shell-style interpolation: scan until " or $, where
    /// // "${name}" is an interpolation escape. A bare $ (not
    /// // followed by {) falls through to the normal stopper
    /// // path so the outer grammar can handle it separately.
    /// var body = ScanUntil(
    ///     TokenSet.Runes("\"$"),
    ///     Literal("${"),
    ///     And(OneOrMore(NoneOf("}")), Token('}')));
    /// </code>
    /// </remarks>
    public static Rule ScanUntil(TokenSet stopAt, Rule escapeStart, Rule escapeEnd, bool eofIsTerminator = false) =>
        new ScanUntilRule(stopAt, escapeStart, escapeEnd, eofIsTerminator);

    /// <summary>
    /// <see cref="ScanUntil(TokenSet, bool)">Rules.ScanUntil(TokenSet, bool)</see> with a rule-valued stop
    /// condition. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// Use when the stop condition needs to match a sequence of tokens,
    /// such as the closing delimiter <c>]]&gt;</c>, or another rule-based
    /// pattern. A <see cref="TokenSet"/> stop condition matches one token
    /// at a time, so it can't match a delimiter that spans several tokens.
    /// <code>
    /// // XML CDATA body: scan until the closing "]]&gt;"
    /// var cdataBody = ScanUntil(Literal("]]&gt;"));
    ///
    /// // C-style block comment body: scan until "*/"
    /// var blockCommentBody = ScanUntil(Literal("*/"));
    /// </code>
    /// </remarks>
    public static Rule ScanUntil(Rule stopAt, bool eofIsTerminator = false) =>
        new ScanUntilRule(stopAt, eofIsTerminator);

    /// <summary>
    /// Rule-stopper <see cref="InductorParser.Rules.ScanUntil(InductorParser.Rule,System.Boolean)">Rules.ScanUntil</see> with escape sequences. Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// <code>
    /// // Python-style triple-quoted string body: scan until
    /// // """, with \" and \\ as single-rune escapes
    /// var body = ScanUntil(
    ///     Literal("\"\"\""),
    ///     new Rune('\\'),
    ///     OneOf("\"\\"));
    /// </code>
    /// </remarks>
    public static Rule ScanUntil(Rule stopAt, Rune escapeStart, Rule escapeEnd, bool eofIsTerminator = false) =>
        new ScanUntilRule(stopAt, escapeStart, escapeEnd, eofIsTerminator);

    /// <summary>
    /// Match every remaining token to end-of-input as one leaf. Always
    /// succeeds, including on empty input (with a zero-width leaf).
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// For grammars whose final element is "the rest of the input."
    /// Implemented as <c><see cref="Rules.ScanUntil(TokenSet, bool)">Rules.ScanUntil(TokenSet.Empty, eofIsTerminator: true)</see></c>:
    /// no stopper ever matches, so the scan runs to EOF, which
    /// <c>eofIsTerminator</c> allows, and returns one leaf over the whole
    /// remaining span.
    /// <code>
    /// // Grab the rest of the input after a "TODO: " prefix
    /// var note = And(Literal("TODO: "), ScanUntilEof().As("note"));
    /// </code>
    /// </remarks>
    public static Rule ScanUntilEof() =>
        new ScanUntilRule(TokenSet.Empty, eofIsTerminator: true);

    /// <summary>
    /// Match any one token (one character as the user sees it).
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// Fails only at EOF. Use it as a catch-all for pass-through text:
    /// grammars that pull out a few structured pieces (a
    /// <c>{{ name }}</c> interpolation in a template, say) and copy
    /// everything else through as-is use <see cref="Rules.AnyToken">Rules.AnyToken()</see> for the "any
    /// other character" branch.
    /// </remarks>
    public static Rule AnyToken() => new AnyTokenRule();

    /// <summary>
    /// Negative lookahead: succeeds if <paramref name="inner"/>
    /// would fail at the current position, fails if it would
    /// succeed. Consumes nothing either way. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// Use for "anything except X" shapes that
    /// <see cref="NoneOf(TokenSet)">Rules.NoneOf(TokenSet)</see> can't express because X spans
    /// more than one token (a multi-token literal like
    /// <c>"function"</c>, for example).
    /// </remarks>
    public static Rule Not(Rule inner) => new NotRule(inner);

    /// <summary>
    /// Positive lookahead: succeeds if <paramref name="inner"/>
    /// would match at the current position, fails if it wouldn't.
    /// Consumes nothing either way. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// Use to assert content is ahead without committing the lexer
    /// to consuming it.
    /// </remarks>
    public static Rule Peek(Rule inner) => new PeekRule(inner);

    /// <summary>
    /// Wrap <paramref name="inner"/> so it can be given its own name with
    /// <see cref="InductorParser.Rule.As(string)">Rule.As(string)</see> or <see cref="InductorParser.Rule.As(InductorParser.SyntaxTree.SymbolId)">Rule.As(SymbolId)</see>, letting the same rule
    /// shape appear in a grammar under more than one name and be found
    /// under each. The alias matches exactly what <paramref name="inner"/>
    /// matches. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// Use when one rule shape needs to appear in a grammar under
    /// multiple names, instead of building a factory function that
    /// returns a fresh rule each call:
    /// <code>
    /// var digitSequence = OneOrMore(OneOf(TokenSet.Digits));
    /// var year  = Alias(digitSequence).As("year");
    /// var month = Alias(digitSequence).As("month");
    /// </code>
    /// The fluent equivalent is <see cref="Rule.AliasedAs(string)">Rule.AliasedAs(string)</see>.
    /// <para>
    /// Until <c>.As(...)</c> names it, the alias changes nothing. The
    /// match is found under the inner's original name:
    /// <code>
    /// var digits = OneOrMore(OneOf(TokenSet.Digits)).As("digits");
    /// var result = Alias(digits).Parse("1234");
    /// result.Find(digits);   // the "1234" match
    /// </code>
    /// Once named, the alias's Symbol takes the inner's place in the tree
    /// (it doesn't sit above it), so the match is found under the new
    /// name instead:
    /// <code>
    /// var year = Alias(digits).As("year");
    /// var result = year.Parse("1234");
    /// result.Find(year);     // the same "1234" match
    /// result.Find(digits);   // null
    /// </code>
    /// </para>
    /// <para>
    /// Errors: the inner's own <see cref="InductorParser.Rule.WithError(System.String,System.Boolean)">Rule.WithError</see> fires from inside it as
    /// usual. The alias's own <see cref="InductorParser.Rule.WithError(System.String,System.Boolean)">Rule.WithError</see> is anchored at the alias's
    /// start, never deeper than where the inner fails, so under "deepest
    /// failure wins" the inner's failure wins unless the alias's is marked
    /// forced.
    /// </para>
    /// </remarks>
    public static Rule Alias(Rule inner) => new AliasRule(inner);

    /// <summary>
    /// Match every child in order. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// Fails if any child fails, rolling the lexer back to the
    /// start of the sequence.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="children"/> is null, empty, or contains a null child.
    /// </exception>
    public static Rule And(params Rule[] children) => new AndRule(children);

    /// <summary>
    /// Try each child left-to-right and commit to
    /// the first one that matches. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// Unlike regex, once a child commits there's no
    /// backtracking into an earlier branch. Fails only if every
    /// child fails.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="children"/> is null, empty, or contains a null child.
    /// </exception>
    public static Rule Or(params Rule[] children) => new OrRule(children);

    /// <summary>
    /// Match <paramref name="inner"/> between
    /// <paramref name="atLeast"/> and <paramref name="atMost"/>
    /// times inclusive. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// <see cref="OneOrMore">Rules.OneOrMore(Rule)</see>, <see cref="ZeroOrMore">Rules.ZeroOrMore(Rule)</see>,
    /// <see cref="Optional">Rules.Optional(Rule)</see>, <see cref="AtLeast">Rules.AtLeast(int, Rule)</see>,
    /// <see cref="AtMost">Rules.AtMost(int, Rule)</see>, and <see cref="Exactly">Rules.Exactly(int, Rule)</see> are special
    /// cases of this composite.
    /// <para>
    /// All of them, this one included, are greedy: within the allowed
    /// range the inner matches as many times as it can, and never gives a
    /// match back. Unlike a backtracking regex, if taking the most matches
    /// makes a following rule fail, the repetition won't retry with fewer
    /// to rescue it.
    /// </para>
    /// </remarks>
    public static Rule BetweenInclusive(int atLeast, int atMost, Rule inner) =>
        new BetweenInclusiveRule(inner, atLeast, atMost);

    /// <summary>
    /// Match <paramref name="inner"/> one or more times.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// Greedy: it matches as many times as it can and never gives a match
    /// back to let a following rule succeed. Equivalent to
    /// <c><see cref="Rules.BetweenInclusive">Rules.BetweenInclusive(1, int.MaxValue, inner)</see></c>.
    /// </remarks>
    public static Rule OneOrMore(Rule inner) =>
        new BetweenInclusiveRule(inner, 1, int.MaxValue, "OneOrMore");

    /// <summary>
    /// Match <paramref name="inner"/> zero or more times.
    /// Always succeeds. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// Greedy: it matches as many times as it can and never gives a match
    /// back to let a following rule succeed. Equivalent to
    /// <c><see cref="Rules.BetweenInclusive">Rules.BetweenInclusive(0, int.MaxValue, inner)</see></c>.
    /// </remarks>
    public static Rule ZeroOrMore(Rule inner) =>
        new BetweenInclusiveRule(inner, 0, int.MaxValue, "ZeroOrMore");

    /// <summary>
    /// Match <paramref name="inner"/> zero or one time. Always
    /// succeeds. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// Greedy: it takes the match when the inner matches and never gives
    /// it back to let a following rule succeed. Equivalent to
    /// <c><see cref="Rules.BetweenInclusive">Rules.BetweenInclusive(0, 1, inner)</see></c>.
    /// </remarks>
    public static Rule Optional(Rule inner) =>
        new BetweenInclusiveRule(inner, 0, 1, "Optional");

    /// <summary>
    /// Match <paramref name="inner"/> at least
    /// <paramref name="atLeast"/> times, no upper bound. Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// Greedy: it matches as many times as it can and never gives a match
    /// back to let a following rule succeed. Equivalent to
    /// <c><see cref="Rules.BetweenInclusive">Rules.BetweenInclusive(atLeast, int.MaxValue, inner)</see></c>.
    /// </remarks>
    public static Rule AtLeast(int atLeast, Rule inner) =>
        new BetweenInclusiveRule(inner, atLeast, int.MaxValue, $"AtLeast[{atLeast}]");

    /// <summary>
    /// Match <paramref name="inner"/> zero to
    /// <paramref name="atMost"/> times. Always succeeds
    /// (a zero-match run is legal). Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// Greedy: it matches as many times as it can and never gives a match
    /// back to let a following rule succeed. Equivalent to
    /// <c><see cref="Rules.BetweenInclusive">Rules.BetweenInclusive(0, atMost, inner)</see></c>.
    /// </remarks>
    public static Rule AtMost(int atMost, Rule inner) =>
        new BetweenInclusiveRule(inner, 0, atMost, $"AtMost[{atMost}]");

    /// <summary>
    /// Match <paramref name="inner"/> exactly
    /// <paramref name="count"/> times. Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    /// <remarks>
    /// Greedy like the rest, which for a fixed count just means it never
    /// gives a match back: once it has matched count times, it stays
    /// matched. Equivalent to <c><see cref="Rules.BetweenInclusive">Rules.BetweenInclusive(count, count, inner)</see></c>.
    /// </remarks>
    public static Rule Exactly(int count, Rule inner) =>
        new BetweenInclusiveRule(inner, count, count, $"Exactly[{count}]");

    /// <summary>
    /// Match end-of-input. Consumes nothing. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// Succeeds when the lexer is at EOF, fails otherwise. The
    /// standard way to assert the input was fully consumed at the
    /// end of a grammar.
    /// </remarks>
    public static Rule Eof() => new EofRule();

    /// <summary>
    /// Match a signed integer which is an optional leading + or -
    /// followed by one or more decimal digits. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten">FlattenType.Flatten</see>.
    /// </summary>
    public static Rule Integer() =>
        And(
            Optional(OneOf("+-").Flatten(FlattenType.Flatten)),
            OneOrMore(OneOf(TokenSet.Digits))
        );

    /// <summary>
    /// Match a simple decimal which is an optional leading <c>+</c> or
    /// <c>-</c>, one or more digits, a literal <c>'.'</c>, and one
    /// or more digits. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten">FlattenType.Flatten</see> (from the composed outer
    /// <see cref="And">Rules.And(Rule[])</see>).
    /// </summary>
    /// <remarks>
    /// Doesn't handle exponents or scientific notation. Grammars
    /// that need those compose their own.
    /// </remarks>
    public static Rule Float() =>
        And(
            Optional(OneOf("+-").Flatten(FlattenType.Flatten)),
            OneOrMore(OneOf(TokenSet.Digits)),
            Token('.').Flatten(FlattenType.Preserve),
            OneOrMore(OneOf(TokenSet.Digits))
        );

    /// <summary>
    /// Match one or more whitespace tokens within a line: TAB and the
    /// Unicode space separators, per <see cref="TokenSet.InlineWhitespace">TokenSet.InlineWhitespace</see>.
    /// Doesn't match the line terminators recognized by <see cref="EndOfLine">Rules.EndOfLine(bool)</see>.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// For the "skip any whitespace here, including none" shape
    /// (between tokens that don't require a separator), wrap it as
    /// <c><see cref="Rules.Optional">Rules.Optional(Rules.InlineWhitespace())</see></c>.
    /// For "either intra-line whitespace or a line terminator" use
    /// <see cref="AnyWhitespace">Rules.AnyWhitespace()</see>.
    /// </remarks>
    public static Rule InlineWhitespace() =>
        OneOrMore(OneOf(TokenSet.InlineWhitespace)).FlattenByDefault(FlattenType.Delete);

    /// <summary>
    /// Match one or more whitespace tokens, where each token is either
    /// an intra-line whitespace rune (per <see cref="TokenSet.InlineWhitespace">TokenSet.InlineWhitespace</see>)
    /// or a line terminator, using <see cref="EndOfLine">Rules.EndOfLine(bool)</see>, which handles
    /// all end-of-line characters including CRLF, a two-rune unit. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// The "skip any whitespace including newlines" rule, for free-form
    /// grammars (JSON, arithmetic expressions, etc.) that treat line
    /// breaks as ordinary whitespace.
    /// <para>
    /// For "skip whitespace here, possibly none," wrap as
    /// <c><see cref="Rules.Optional">Rules.Optional(Rules.AnyWhitespace())</see></c>. For strict intra-line whitespace
    /// (no line terminators) use <see cref="InlineWhitespace">Rules.InlineWhitespace()</see>.
    /// </para>
    /// </remarks>
    public static Rule AnyWhitespace() =>
        OneOrMore(Or(EndOfLine(), OneOf(TokenSet.InlineWhitespace))).FlattenByDefault(FlattenType.Delete);

    /// <summary>
    /// Match one Unicode line terminator (LF, CR, the CRLF pair, and the
    /// other Unicode line breaks listed below). When
    /// <paramref name="eofIsEol"/> is <c>true</c>, also matches at
    /// end-of-input. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete">FlattenType.Delete</see>.
    /// </summary>
    /// <remarks>
    /// Consumes one of:
    /// <list type="bullet">
    /// <item><description>CRLF (the two-rune sequence <c>\r\n</c>)</description></item>
    /// <item><description>LF, VT, FF, CR, NEL, LINE SEPARATOR, or PARAGRAPH SEPARATOR
    /// (the single-rune terminators in <see cref="TokenSet.LineTerminators">TokenSet.LineTerminators</see>)</description></item>
    /// <item><description>End-of-input, but only when <paramref name="eofIsEol"/> is <c>true</c></description></item>
    /// </list>
    /// Those line terminators are the ones defined by the Unicode regex
    /// spec (<a href="https://www.unicode.org/reports/tr18/#Line_Boundaries">UTS #18</a> §1.6, RL1.6). CRLF is tried first so a CR immediately
    /// followed by an LF is consumed as one terminator rather than split
    /// into two.
    /// </remarks>
    /// <param name="eofIsEol">
    /// When <c>true</c>, end-of-input counts as an end-of-line. The
    /// last line of a document typically isn't followed by a terminator,
    /// so a grammar that wants one at the end of every line has to
    /// accept EOF as equivalent. Default is <c>false</c>: a real
    /// terminator is required.
    /// </param>
    public static Rule EndOfLine(bool eofIsEol = false)
    {
        // The Literal("\r\n") arm looks redundant: LineTerminators already
        // holds CRLF as a multi-rune member, so in the normal grapheme mode
        // the OneOf arm matches the CRLF token on its own. The Literal is
        // there for UnicodeImplementation.Runtime on a legacy runtime
        // (Unity's Mono, .NET Framework), whose StringInfo predates the
        // UAX #29 rule that glues CR to LF and so hands the lexer CR and LF
        // as two separate tokens. OneOf reads exactly one token and would
        // match the CR alone, leaving the LF to count as a second
        // terminator. Literal matches its text across token boundaries, so
        // it takes the pair as one terminator on every runtime. It goes
        // first so it wins over the OneOf arm. The regression test is
        // EndOfLineRuleTests.Literal_crlf_spans_a_two_token_crlf_where_OneOf_stops_at_the_cr.
        var alternatives = eofIsEol
            ? new Rule[] { Literal("\r\n"), OneOf(TokenSet.LineTerminators), Eof() }
            : new Rule[] { Literal("\r\n"), OneOf(TokenSet.LineTerminators) };
        return Or(alternatives).FlattenByDefault(FlattenType.Delete);
    }

    /// <summary>
    /// Reads one token from the lexer and runs <paramref name="innerRule"/>
    /// against the runes inside that token. The outer token is one
    /// character as the user sees it (a grapheme cluster) that may
    /// span several runes, and the inner rule walks them one at a
    /// time over a sub-lexer switched to a special one-rune-per-token mode.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve">FlattenType.Preserve</see>.
    /// </summary>
    /// <remarks>
    /// Building block for rules that care about token-internal
    /// structure. Can be used for things like: 
    /// emoji-with-modifier matchers (<c>WithinToken(And(OneOf(EmojiBase),
    /// ZeroOrMore(OneOf(SkinToneOrZWJ))))</c>), ASCII-only strictness
    /// (<c>WithinToken(OneOf(TokenSet.Ascii.Letters))</c> rejects any
    /// multi-rune token), and validating the ordered jamo inside a Hangul
    /// syllable that's written as conjoining jamo
    /// (<c>WithinToken(And(OneOf(Leading), OneOf(Vowel), Optional(OneOf(Trailing))))</c>
    /// matches one lead-vowel-optional-tail syllable).
    /// <para>
    /// One leaf Symbol is emitted per successful match, representing the
    /// whole token. Inner-rule symbols are discarded. With tracing turned
    /// on, the inner rule's per-rune steps don't show up in the trace,
    /// since the sub-lexer that walks the token doesn't have the trace
    /// sink. <see cref="InductorParser.Rules.WithinToken(InductorParser.Rule)">Rules.WithinToken</see> still logs its own line for whether the token
    /// matched, so you see the outcome but not the steps inside. The inner
    /// parse is bounded to the token's rune span. The sub-lexer's recursion,
    /// rule-count, timeout, and cancellation budgets all count against
    /// the outer parse: a <see cref="InductorParser.ParseCancellation.Cancel">ParseCancellation.Cancel()</see> or expired Timeout observed on
    /// either lexer trips both, and <see cref="InductorParser.ParseOptions.MaxDepth">ParseOptions.MaxDepth</see> / <see cref="InductorParser.ParseOptions.RuleCountLimit">ParseOptions.RuleCountLimit</see> cover the
    /// combined outer-plus-inner work rather than letting the inner
    /// rule spend a fresh budget on top of the outer's.
    /// </para>
    /// </remarks>
    /// <param name="innerRule">
    /// The rule to run against the token's runes. Must consume every
    /// rune of the token on success. A rule that matches only a
    /// prefix causes the whole <see cref="Rules.WithinToken">Rules.WithinToken(Rule)</see> to fail. Any rule
    /// composition is allowed inside (<see cref="And">Rules.And(Rule[])</see>, <see cref="Or">Rules.Or(Rule[])</see>,
    /// <see cref="Rules.OneOf(TokenSet)">Rules.OneOf(TokenSet)</see>, etc.).
    /// </param>
    public static Rule WithinToken(Rule innerRule) => new WithinTokenRule(innerRule);

    /// <summary>
    /// Match a programming-language identifier: a name that starts with a
    /// letter (or other identifier-start character) and continues with
    /// letters, digits, and the like. Follows the Unicode rules for
    /// identifiers (<a href="https://www.unicode.org/reports/tr31/">UAX #31</a>, "Unicode Identifiers and Syntax").
    /// Default <see cref="FlattenType"/>: <see cref="FlattenType.Preserve">FlattenType.Preserve</see>,
    /// so the match appears in the tree as one node whose children are
    /// one leaf per matched character (a leaf covers the character's
    /// whole grapheme cluster, which may span several runes).
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <a href="../docs/UnicodeGotchas.md">Unicode Gotchas</a> for recipes that reproduce the
    /// identifier rules of specific languages (Python 3, Rust,
    /// ECMAScript).
    /// </para>
    /// </remarks>
    /// <param name="extraStartRunes">
    /// Extra characters to allow as the first character, on top of the
    /// base Unicode identifier-start set (<see cref="TokenSet.XidStart">TokenSet.XidStart</see>).
    /// <a href="https://www.unicode.org/reports/tr31/">UAX #31</a> calls this a "profile". Typical value for a
    /// programming-language grammar is <c><see cref="TokenSet.Runes(string)">TokenSet.Runes("_")</see></c>. Python
    /// and Rust use this shape. C# also permits leading underscores, though
    /// its full identifier specification differs. Defaults to
    /// <see cref="TokenSet.Empty">TokenSet.Empty</see> (no additions, just the base set).
    /// </param>
    /// <param name="extraBodyRunes">
    /// Runes to union into <see cref="TokenSet.XidContinue">TokenSet.XidContinue</see> for
    /// every character after the first. Same idea as
    /// <paramref name="extraStartRunes"/>. ECMAScript, for example,
    /// adds <c>$</c> to both positions. Defaults to
    /// <see cref="TokenSet.Empty">TokenSet.Empty</see>.
    /// </param>
    public static Rule Identifier(
        TokenSet extraStartRunes = default,
        TokenSet extraBodyRunes = default)
    {
        // extraStartRunes / extraBodyRunes name single runes (code points).
        // Identifier matches one code point at a time (see the WithinToken
        // note inside IdentifierRule), so a multi-rune grapheme handed in
        // here is consulted only against single-rune tokens and can never
        // match in any position, under any form. Reject it now instead of
        // silently building a rule with a dead entry. This check is
        // form-independent so it stays at construction time.
        static void RejectMultiRuneGraphemes(TokenSet extras, string parameterName)
        {
            if (extras.HasMultiRuneGraphemes)
                throw new InvalidOperationException(
                    $"Identifier: {parameterName} contains the multi-rune grapheme " +
                    $"\"{extras.MultiRuneGraphemes[0]}\", but Identifier matches one code point " +
                    $"at a time, so a multi-rune grapheme can never match. Pass its individual " +
                    $"runes instead.");
        }
        RejectMultiRuneGraphemes(extraStartRunes, nameof(extraStartRunes));
        RejectMultiRuneGraphemes(extraBodyRunes, nameof(extraBodyRunes));

        return new IdentifierRule(extraStartRunes, extraBodyRunes);
    }
}
