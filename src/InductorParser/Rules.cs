using System;
using System.Text;
using InductorParser.SyntaxTree;

namespace InductorParser;

/// <summary>
/// The main class for building grammars. Every built-in rule
/// type has a corresponding factory method here, so grammar code
/// composes rules by calling these functions instead of
/// instantiating rule classes directly.
/// </summary>
/// <remarks>
/// The idiomatic usage is <c>using static InductorParser.Rules;</c>
/// at the top of a grammar file, which drops the class prefix and
/// lets a grammar read close to the shape you'd write on a
/// whiteboard:
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
/// constructor. A few factories
/// compose several rules instead of forwarding to a single rule type,
/// because every grammar ends up wanting them.
///
/// Every rule has a default <see cref="FlattenType"/> that
/// controls how the match contributes to the parse tree: Delete
/// drops the node, Flatten lifts its children into the parent,
/// Preserve keeps the rule's Symbol. Each factory's summary names its
/// default. Override this on any rule by using
/// <see cref="Rule.Flatten(FlattenType)"/> to change it for a
/// specific use.
///
/// Two fluent modifiers on <see cref="Rule"/> show up in almost
/// every grammar:
/// <list type="bullet">
/// <item><description>
/// <see cref="Rule.As(string)"/> (or <see cref="Rule.As(SymbolId)"/>)
/// names the rule so trace output, error messages, and
/// <c>Tree.Find</c> can refer to it. Naming also flips the rule's
/// <see cref="FlattenType"/> to Preserve so Find can see it.
/// </description></item>
/// <item><description>
/// <see cref="Rule.WithError(string, bool)"/> attaches a custom
/// message that surfaces when this rule is the deepest point a parse
/// failed, in place of the generic "unexpected 'x'".
/// </description></item>
/// </list>
/// Both return the same rule, so they chain.
/// </remarks>
public static class Rules
{
    /// <summary>
    /// Match one token whose content is exactly the given character.
    /// Default <see cref="FlattenType"/>: <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// A token is one character as the user sees it (a grapheme
    /// cluster), so <c>Token('a')</c> matches when the token is the
    /// single char 'a' but fails when 'a' is combined with a
    /// following accent (because the token is then rendered as one
    /// 'a' with an accent over it, which doesn't match a bare 'a').
    /// To match a single character that's built from several code
    /// points like that (a base letter plus a combining accent, a
    /// skin-tone emoji, a flag), pass the whole thing as a string to
    /// <see cref="Token(string)"/>.
    ///
    /// For multi-grapheme matches use <see cref="Literal"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="c"/> can't stand alone as a character. A single
    /// <c>char</c> can't hold a character above U+FFFF (like an emoji),
    /// which is written as two <c>char</c>s together, and this is one
    /// half of such a pair. To match a character above U+FFFF, use
    /// <see cref="Token(Rune)"/> or <see cref="Token(int)"/>.
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
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// A token is one character as the user sees it (a grapheme
    /// cluster), so this matches when the user sees one bare
    /// <c>r</c> at the current position. It fails when <c>r</c> is
    /// followed by Unicode characters that tell the renderer to
    /// glue them all into a single composed character, because
    /// then the user sees one combined character at that position,
    /// not a bare <c>r</c>.
    /// To match that combined character, pass the whole thing as a
    /// string to <see cref="Token(string)"/>.
    ///
    /// For multi-grapheme matches use <see cref="Literal"/>.
    /// </remarks>
    public static Rule Token(Rune r) => new GraphemeRule(r.ToString());

    /// <summary>
    /// Match one token whose content is exactly the rune with the
    /// given integer code point (the integer Unicode assigns to a
    /// character). Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// A token is one character as the user sees it (a grapheme
    /// cluster), so this matches when the user sees the given rune
    /// standing alone at the current position. It fails when the
    /// rune is followed by Unicode characters that tell the
    /// renderer to glue them all into a single composed character,
    /// because then the user sees one combined character at that
    /// position, not the bare rune.
    /// To match that combined character, pass the whole thing as a
    /// string to <see cref="Token(string)"/>.
    ///
    /// For multi-grapheme matches use <see cref="Literal"/>.
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
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete"/>.
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
    /// For multi-grapheme matches use <see cref="Literal"/>.
    /// </remarks>
    public static Rule Token(string token) => new GraphemeRule(token);

    /// <summary>
    /// Match an exact multi-token string. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// A token is one character as the user sees it (a grapheme
    /// cluster). Literal matches a sequence of them in order, so the
    /// string is the exact text to match.
    /// For a one-token match use <see cref="Token(string)"/>.
    /// Literal is the N-token generalization and collapses what
    /// would otherwise be N Token rules into
    /// one. 
    /// </remarks>
    public static Rule Literal(string value) => new LiteralRule(value);

    /// <summary>
    /// ASCII-case-insensitive variant of <see cref="Literal"/>. The
    /// pattern must be ASCII-only. Construction throws on any char
    /// outside <c>0x00..0x7F</c>. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
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
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// The workhorse character-class rule.
    /// A TokenSet can hold sets of anything the user sees as one
    /// character, including composed sequences like skin-toned
    /// emoji, regional-indicator flags, and family emoji. So a set
    /// built with <c>TokenSet.Letters | TokenSet.Graphemes("🇺🇸")</c>
    /// matches either a letter or the US flag, each as one token.
    /// <code>
    /// // Identifier character: any letter, digit, or underscore
    /// var idChar = OneOf(TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_"));
    ///
    /// // ASCII consonant: ASCII letter minus vowels
    /// var consonant = OneOf(TokenSet.Ascii.Letters - TokenSet.Runes("aeiouAEIOU"));
    ///
    /// // Printable non-whitespace: letters and digits only, in Latin script
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
    /// <see cref="TokenSet.Graphemes(string[])"/> and pass it to the
    /// <see cref="OneOf(TokenSet)"/> overload. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>OneOf(TokenSet.Runes(runes))</c>.
    /// When you need ranges, category unions, complements, or grapheme
    /// clusters, use <see cref="TokenSet"/> directly and pass it to the
    /// <see cref="OneOf(TokenSet)"/> overload.
    /// </remarks>
    public static Rule OneOf(string runes) => new OneOfRule(TokenSet.Runes(runes));

    /// <summary>
    /// Match one token (one character as the user sees it) when
    /// that token isn't in the given <see cref="TokenSet"/>.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
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
    /// <see cref="TokenSet"/> with <see cref="TokenSet.Graphemes(string[])"/>
    /// and pass it to the <see cref="NoneOf(TokenSet)"/> overload. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>NoneOf(TokenSet.Runes(runes))</c>.
    /// When you need ranges, category unions, complements, or grapheme
    /// clusters, use <see cref="TokenSet"/> directly and pass it to the
    /// <see cref="NoneOf(TokenSet)"/> overload.
    /// </remarks>
    public static Rule NoneOf(string runes) => new NoneOfRule(TokenSet.Runes(runes));

    /// <summary>
    /// Scan forward while the next token is in <paramref name="set"/>,
    /// stopping at the first token outside the set, and return the whole
    /// run as one leaf <see cref="SyntaxTree.Symbol"/>. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Use this for the hot scanning paths: identifiers, words, numbers,
    /// whitespace runs, and other character-class runs where you want the run as
    /// a single symbol, not N independent symbols. The matched text is the same as
    /// <c>AtLeast(minimumCount, OneOf(set))</c>, but the runtime
    /// cost is very different. The <c>OneOf</c> form opens a transaction
    /// and allocates a Symbol per token. This rule opens one transaction at the top, runs a tight
    /// scan loop in the lexer, and emits one Symbol over the whole run.
    ///
    /// The exact converse of <see cref="ScanUntil(TokenSet, bool)"/>: ScanUntil
    /// stops when the next token is in its stop set, ScanWhile stops when
    /// the next token is outside its match set. Use <c>ScanWhile</c> when
    /// the run's character class is the natural way to describe the body
    /// (identifiers, words, numbers), and <see cref="ScanUntil(TokenSet, bool)"/>
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
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Succeeds with a possibly empty body when the stopper matches.
    /// Fails if end-of-input is reached without ever matching the
    /// stopper, unless <paramref name="eofIsTerminator"/> is true.
    /// It scans the characters directly in one pass, which is a meaningful
    /// speedup over <c>ZeroOrMore(NoneOf(stopAt))</c> for long strings.
    /// <para>
    /// Pass <paramref name="eofIsTerminator"/> = <c>true</c> for
    /// grammars where the body legitimately ends at the stopper or at
    /// EOF (line comments that may close with a newline or with the
    /// end of file, for example). For "match the rest of the input,"
    /// use <see cref="ScanUntilEof"/> instead.
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
    /// The same as <see cref="ScanUntil(TokenSet, bool)"/> but also including escape sequences.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
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
    /// <see cref="FlattenType.Preserve"/>.
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
    /// <see cref="ScanUntil(TokenSet, bool)"/> with a rule-valued stop
    /// condition. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Use when the "end of string" boundary is more than a set of
    /// runes (for example, the closing delimiter is a multi-char
    /// sequence like <c>]]&gt;</c>).
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
    /// Rule-stopper ScanUntil with escape sequences. Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
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
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// For grammars whose final element is "the rest of the input."
    /// Implemented as <c>ScanUntil(TokenSet.Empty, eofIsTerminator: true)</c>:
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
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Fails only at EOF. It's the "match any one character" catch-all for
    /// the two shapes where a grammar has to consume input it isn't
    /// otherwise describing:
    /// <list type="bullet">
    /// <item><description>
    /// Error recovery. After a parse hits input it can't handle, a common
    /// fix is to skip ahead to a known resync point (the next newline, a
    /// closing brace) and resume from there. The loop that eats the tokens
    /// in between matches them with <c>AnyToken()</c>:
    /// <c>ZeroOrMore(And(Not(resyncPoint), AnyToken()))</c> keeps consuming
    /// one token at a time until the resync point shows up.
    /// </description></item>
    /// <item><description>
    /// Pass-through text. Grammars that pull out a few structured pieces (a
    /// <c>{{ name }}</c> interpolation in a template, say) and copy
    /// everything else through as-is use <c>AnyToken()</c> for the "any
    /// other character" branch.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static Rule AnyToken() => new AnyTokenRule();

    /// <summary>
    /// Negative lookahead: succeeds if <paramref name="inner"/>
    /// would fail at the current position, fails if it would
    /// succeed. Consumes nothing either way. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// Use for "anything except X" shapes that
    /// <see cref="NoneOf(TokenSet)"/> can't express because X spans
    /// more than one token (a multi-token literal like
    /// <c>"function"</c>, for example).
    /// </remarks>
    public static Rule Not(Rule inner) => new NotRule(inner);

    /// <summary>
    /// Positive lookahead: succeeds if <paramref name="inner"/>
    /// would match at the current position, fails if it wouldn't.
    /// Consumes nothing either way. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// Use to assert content is ahead without committing the lexer
    /// to consuming it.
    /// </remarks>
    public static Rule Peek(Rule inner) => new PeekRule(inner);

    /// <summary>
    /// Wrap <paramref name="inner"/> so it can be given its own name with
    /// <c>.As(string)</c> or <c>.As(SymbolId)</c>, letting the same rule
    /// shape appear in a grammar under more than one name and be found
    /// under each. The alias matches exactly what <paramref name="inner"/>
    /// matches. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/>.
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
    /// The fluent equivalent is <see cref="Rule.AliasedAs(string)"/>.
    /// <para>
    /// Once named, the alias replaces the inner's identity, so aliasing
    /// an already-named (Preserve) inner shows the match under the
    /// alias's name instead of the inner's, not nested under it. Until
    /// .As(...) names it, the alias is transparent: it delegates to the
    /// inner and the parse tree comes out exactly as if the alias weren't
    /// written, so a named inner is still findable through it.
    /// </para>
    /// <para>
    /// Errors: the inner's own <c>.WithError</c> fires from inside it as
    /// usual. The alias's own <c>.WithError</c> is anchored at the alias's
    /// start, never deeper than where the inner fails, so under "deepest
    /// failure wins" the inner's failure wins unless the alias's is marked
    /// forced.
    /// </para>
    /// </remarks>
    public static Rule Alias(Rule inner) => new AliasRule(inner);

    /// <summary>
    /// Match every child in order. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten"/>.
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
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten"/>.
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
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="OneOrMore"/>, <see cref="ZeroOrMore"/>,
    /// <see cref="Optional"/>, <see cref="AtLeast"/>,
    /// <see cref="AtMost"/>, and <see cref="Exactly"/> are special
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
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Greedy: it matches as many times as it can and never gives a match
    /// back to let a following rule succeed. Equivalent to
    /// <c>BetweenInclusive(1, int.MaxValue, inner)</c>.
    /// </remarks>
    public static Rule OneOrMore(Rule inner) =>
        new BetweenInclusiveRule(inner, 1, int.MaxValue, "OneOrMore");

    /// <summary>
    /// Match <paramref name="inner"/> zero or more times.
    /// Always succeeds. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Greedy: it matches as many times as it can and never gives a match
    /// back to let a following rule succeed. Equivalent to
    /// <c>BetweenInclusive(0, int.MaxValue, inner)</c>.
    /// </remarks>
    public static Rule ZeroOrMore(Rule inner) =>
        new BetweenInclusiveRule(inner, 0, int.MaxValue, "ZeroOrMore");

    /// <summary>
    /// Match <paramref name="inner"/> zero or one time. Always
    /// succeeds. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Greedy: it takes the match when the inner matches and never gives
    /// it back to let a following rule succeed. Equivalent to
    /// <c>BetweenInclusive(0, 1, inner)</c>.
    /// </remarks>
    public static Rule Optional(Rule inner) =>
        new BetweenInclusiveRule(inner, 0, 1, "Optional");

    /// <summary>
    /// Match <paramref name="inner"/> at least
    /// <paramref name="atLeast"/> times, no upper bound. Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Greedy: it matches as many times as it can and never gives a match
    /// back to let a following rule succeed. Equivalent to
    /// <c>BetweenInclusive(atLeast, int.MaxValue, inner)</c>.
    /// </remarks>
    public static Rule AtLeast(int atLeast, Rule inner) =>
        new BetweenInclusiveRule(inner, atLeast, int.MaxValue, $"AtLeast[{atLeast}]");

    /// <summary>
    /// Match <paramref name="inner"/> zero to
    /// <paramref name="atMost"/> times. Always succeeds
    /// (a zero-match run is legal). Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Greedy: it matches as many times as it can and never gives a match
    /// back to let a following rule succeed. Equivalent to
    /// <c>BetweenInclusive(0, atMost, inner)</c>.
    /// </remarks>
    public static Rule AtMost(int atMost, Rule inner) =>
        new BetweenInclusiveRule(inner, 0, atMost, $"AtMost[{atMost}]");

    /// <summary>
    /// Match <paramref name="inner"/> exactly
    /// <paramref name="count"/> times. Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Greedy like the rest, which for a fixed count just means it never
    /// gives a match back: once it has matched count times, it stays
    /// matched. Equivalent to <c>BetweenInclusive(count, count, inner)</c>.
    /// </remarks>
    public static Rule Exactly(int count, Rule inner) =>
        new BetweenInclusiveRule(inner, count, count, $"Exactly[{count}]");

    /// <summary>
    /// Match end-of-input. Consumes nothing. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete"/>.
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
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten"/>.
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
    /// <see cref="FlattenType.Flatten"/> (from the composed outer
    /// <see cref="And"/>).
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
    /// Match one or more intra-line whitespace tokens as defined by
    /// <c>TokenSet.InlineWhitespace</c>. Doesn't match line terminators
    /// (<c>\n</c>, <c>\r</c>, <c>\r\n</c>, NEL, LINE SEPARATOR,
    /// PARAGRAPH SEPARATOR, VT, FF). Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// For the "skip any whitespace here, including none" shape
    /// (between tokens that don't require a separator), wrap it as
    /// <c>Optional(InlineWhitespace())</c>.
    /// For line terminators see <see cref="EndOfLine"/>. For
    /// "either intra-line whitespace or a line terminator" use
    /// <see cref="AnyWhitespace"/>.
    /// </remarks>
    public static Rule InlineWhitespace() =>
        OneOrMore(OneOf(TokenSet.InlineWhitespace)).FlattenByDefault(FlattenType.Delete);

    /// <summary>
    /// Match one or more whitespace tokens, where each token is either
    /// an intra-line whitespace rune (per <c>TokenSet.InlineWhitespace</c>)
    /// or a line terminator (per <see cref="EndOfLine"/>, which handles
    /// CRLF as one two-rune unit). Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// The "skip any whitespace including newlines" rule, for free-form
    /// grammars (JSON, arithmetic expressions, etc.) that treat line
    /// breaks as ordinary whitespace.
    /// <para>
    /// For "skip whitespace here, possibly none," wrap as
    /// <c>Optional(AnyWhitespace())</c>. For strict intra-line whitespace
    /// (no line terminators) use <see cref="InlineWhitespace"/>.
    /// </para>
    /// </remarks>
    public static Rule AnyWhitespace() =>
        OneOrMore(Or(EndOfLine(), OneOf(TokenSet.InlineWhitespace))).FlattenByDefault(FlattenType.Delete);

    /// <summary>
    /// Match one Unicode line terminator (LF, CR, the CRLF pair, and the
    /// other Unicode line breaks listed below). When
    /// <paramref name="eofIsEol"/> is <c>true</c>, also matches at
    /// end-of-input. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// Consumes one of:
    /// <list type="bullet">
    /// <item><description>CRLF (the two-rune sequence <c>\r\n</c>)</description></item>
    /// <item><description>LF, VT, FF, CR, NEL, LINE SEPARATOR, or PARAGRAPH SEPARATOR
    /// (the single-rune terminators in <see cref="TokenSet.LineTerminators"/>)</description></item>
    /// <item><description>End-of-input, but only when <paramref name="eofIsEol"/> is <c>true</c></description></item>
    /// </list>
    /// Those line terminators are the ones defined by the Unicode regex
    /// spec (UTS #18 §1.6, RL1.6). CRLF is tried first so a CR immediately
    /// followed by an LF is consumed as one terminator rather than split
    /// into two.
    /// <para>
    /// Why CRLF is a Literal and not just a set member: on .NET 5+ the
    /// lexer hands CRLF back as one token, and
    /// <see cref="TokenSet.LineTerminators"/> includes CRLF as a multi-rune
    /// entry, so the OneOf alternative would match it as a unit on its
    /// own. But on legacy runtimes (.NET Framework, .NET Core 3.x, Unity's
    /// Mono) StringInfo predates the Unicode rule that glues CR to LF, so
    /// CR and LF arrive as two separate tokens. A OneOf reads exactly one
    /// token, so alone it would match the CR and leave the LF to count as
    /// a second terminator. A Literal matches its text across token
    /// boundaries, so the Literal("\r\n") alternative consumes the pair
    /// as one terminator on every runtime.
    /// </para>
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
    /// <see cref="FlattenType.Preserve"/>.
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
    /// sink. WithinToken still logs its own line for whether the token
    /// matched, so you see the outcome but not the steps inside. The inner
    /// parse is bounded to the token's rune span. The sub-lexer's recursion,
    /// rule-count, timeout, and cancellation budgets all count against
    /// the outer parse: a Cancel() or expired Timeout observed on
    /// either lexer trips both, and MaxDepth / RuleCountLimit cover the
    /// combined outer-plus-inner work rather than letting the inner
    /// rule spend a fresh budget on top of the outer's.
    /// </para>
    /// </remarks>
    /// <param name="innerRule">
    /// The rule to run against the token's runes. Must consume every
    /// rune of the token on success. A rule that matches only a
    /// prefix causes the whole <c>WithinToken</c> to fail. Any rule
    /// composition is allowed inside (<see cref="And"/>, <see cref="Or"/>,
    /// <c>OneOf</c>, etc.).
    /// </param>
    public static Rule WithinToken(Rule innerRule) => new WithinTokenRule(innerRule);

    /// <summary>
    /// Match a programming-language identifier: a name that starts with a
    /// letter (or other identifier-start character) and continues with
    /// letters, digits, and the like. Follows the Unicode rules for
    /// identifiers (UAX #31, "Unicode Identifier and Pattern Syntax").
    /// Default <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>,
    /// so the match appears in the tree as one named node whose
    /// children are the per-rune leaves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See docs/UnicodeGotchas.md for recipes that reproduce the
    /// identifier rules of specific languages (Python 3, Rust,
    /// ECMAScript) via these parameters plus the form chosen at
    /// <c>Compile</c> time.
    /// </para>
    /// </remarks>
    /// <param name="extraStartRunes">
    /// Extra characters to allow as the first character, on top of the
    /// base Unicode identifier-start set (<see cref="TokenSet.XidStart"/>).
    /// UAX #31 calls this a "profile". Typical value for a
    /// programming-language grammar is <c>TokenSet.Runes("_")</c>. Python
    /// and Rust use this shape. C# also permits leading underscores, though
    /// its full identifier specification differs. Defaults to
    /// <see cref="TokenSet.Empty"/> (no additions, just the base set).
    /// </param>
    /// <param name="extraBodyRunes">
    /// Runes to union into <see cref="TokenSet.XidContinue"/> for
    /// every character after the first. Same idea as
    /// <paramref name="extraStartRunes"/>. ECMAScript, for example,
    /// adds <c>$</c> to both positions. Defaults to
    /// <see cref="TokenSet.Empty"/>.
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
