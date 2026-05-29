using System;
using System.Text;
using InductorParser.SyntaxTree;

namespace InductorParser;

/// <summary>
/// The factory surface for building grammars. Every built-in rule
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
/// Most factories are thin wrappers that forward their arguments to
/// the matching rule constructor. Argument validation (null checks,
/// empty-string checks, range checks) lives on the rule type itself
/// so every construction path goes through the same gate. A few
/// factories are compositions of other rules rather than wrappers
/// over a single rule type, bundled here because every grammar ends
/// up wanting them.
///
/// Every rule carries a default <see cref="FlattenType"/> that
/// controls how the match contributes to the parse tree: Delete
/// drops the node, Flatten lifts its children into the parent,
/// Preserve keeps a wrapper. Each factory's summary names its
/// default. Override on any rule with
/// <see cref="Rule.Flatten(FlattenType)"/> to change it for a
/// specific use site.
///
/// Calling <see cref="Rule.As(string)"/> or
/// <see cref="Rule.As(SymbolId)"/> on a rule whose default
/// <see cref="FlattenType"/> isn't Preserve silently flips it
/// to Preserve. Identification implies findability, and a
/// non-Preserve rule's wrapper Symbol doesn't reach the tree
/// for Tree.Find to locate. Calling .As after the caller has
/// explicitly set a non-Preserve policy via .Flatten / .Delete /
/// .Flatten() throws, and so does setting a non-Preserve policy
/// on a rule that's already been named. See <see cref="Rule.As(string)"/>
/// for the full story.
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
    ///
    /// For multi-character matches use <see cref="Literal"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="c"/> is a surrogate half. Use
    /// <see cref="Token(Rune)"/> or <see cref="Token(int)"/> for a
    /// supplementary-plane code point.
    /// </exception>
    public static Rule Token(char c)
    {
        if (char.IsSurrogate(c))
            throw new ArgumentOutOfRangeException(nameof(c),
                "Surrogate halves aren't valid token content. Use Token(Rune) or Token(int) for a supplementary-plane code point.");
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
                "Not a valid Unicode scalar value (0..0x10FFFF, excluding surrogates 0xD800..0xDFFF).");
        return new GraphemeRule(new Rune(codepoint).ToString());
    }

    /// <summary>
    /// Match one token (one character as the user sees it) whose
    /// content equals the given string. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// The string can be longer than one C# char as long as the
    /// user sees it as one character (skin-tone emoji like 👋🏽,
    /// regional-indicator flags like 🇺🇸, family emoji like 👨‍👩‍👧,
    /// an accented letter typed as base + accent, etc.).
    /// Construction validates that the string is exactly one such
    /// character and throws otherwise; <c>Token("ab")</c> fails at
    /// grammar-build time, not at parse time. The input arrives as
    /// a single token from the lexer and this rule matches it in
    /// one compare.
    /// </remarks>
    public static Rule Token(string token) => new GraphemeRule(token);

    /// <summary>
    /// Match an exact multi-character string in a single
    /// transaction. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// For a one-token match use <see cref="Token(string)"/>.
    /// Literal is the N-token generalization and collapses what
    /// would otherwise be N Token rules (and N transactions) into
    /// one. Rejects empty strings at construction.
    /// </remarks>
    public static Rule Literal(string value) => new LiteralRule(value);

    /// <summary>
    /// ASCII-case-insensitive variant of <see cref="Literal"/>. The
    /// pattern must be ASCII-only; construction throws on any char
    /// outside <c>0x00..0x7F</c>. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// ASCII letters (A-Z, a-z) in the pattern match either case in
    /// the input. ASCII non-letters (digits, punctuation, controls)
    /// compare bit-exact. ASCII in the name is the rule: full Unicode
    /// case-insensitive matching is locale- and script-dependent and
    /// this leaf doesn't attempt it.
    /// <para>
    /// For a pattern with non-ASCII content (a keyword with an accented
    /// letter, a CJK identifier, etc.), use <see cref="Literal"/>
    /// instead. To mix the two ("case-insensitive ASCII prefix followed
    /// by an exact non-ASCII char"), compose with <see cref="And"/>:
    /// <c>And(LiteralIgnoreAsciiCase("caf"), Token('é'))</c>.
    /// </para>
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
    /// built with <c>TokenSet.Letters | TokenSet.Runes("🇺🇸")</c>
    /// matches either a letter or the US flag, each as one token.
    /// <code>
    /// // Identifier character: any letter, digit, or underscore
    /// var idChar = OneOf(TokenSet.Letters | TokenSet.Digits | TokenSet.Runes("_"));
    ///
    /// // ASCII consonant: ASCII letter minus vowels
    /// var consonant = OneOf(TokenSet.Ascii.Letters &amp; ~TokenSet.Runes("aeiouAEIOU"));
    ///
    /// // Printable non-whitespace: letters and digits only, in Latin script
    /// var latinAlnum = OneOf(
    ///     (TokenSet.Letters | TokenSet.Digits) &amp; TokenSet.Range(0x0000, 0x024F));
    /// </code>
    /// </remarks>
    public static Rule OneOf(TokenSet set) => new OneOfRule(set);

    /// <summary>
    /// Shortcut for the common "one of these literal runes" case. The
    /// input is walked rune by rune; each scalar becomes a set member.
    /// Throws at construction if any two consecutive runes in the input
    /// form one grapheme cluster (CRLF, NFD accent, ZWJ emoji, etc.) —
    /// for cluster-shaped sets, build the <see cref="TokenSet"/> with
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
    /// that token ISN'T in the given <see cref="TokenSet"/>.
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
    /// Shortcut for "any rune except these specific ones."
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>NoneOf(TokenSet.Runes(runes))</c>.
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
    /// whitespace runs, and other character-class runs where the run is
    /// a unit, not N independent tokens. The matched text is the same as
    /// a greedy <c>AtLeast(minimumCount, OneOf(set))</c>, but the runtime
    /// cost is very different. The <c>OneOf</c> form opens a transaction
    /// and allocates a Symbol per token (which the tree then flattens
    /// away). This rule opens one transaction at the top, runs a tight
    /// scan loop in the lexer, and emits one Symbol over the whole run.
    /// The preserved-leaf shape is also what the scanner-skip optimization
    /// needs to recognize a run as a single match span.
    ///
    /// The exact converse of <see cref="ScanUntil(TokenSet, bool)"/>: ScanUntil
    /// stops when the next token is in its stop set, ScanWhile stops when
    /// the next token is outside its match set. Use <c>ScanWhile</c> when
    /// the run's character class is the natural way to describe the body
    /// (identifiers, words, numbers), and <see cref="ScanUntil(TokenSet, bool)"/>
    /// when only the boundary is namable (string bodies, comment bodies).
    ///
    /// <paramref name="minimumCount"/> is the minimum number of tokens the
    /// run must contain to succeed. The default of 1 keeps the rule in
    /// the "always advances on success" lane, which lets enclosing rules
    /// use the LL(1) lookahead shortcut to skip it when the peek isn't
    /// in <paramref name="set"/>. Passing 0 makes the run optional: the
    /// rule always succeeds and emits a single leaf Symbol whose text is
    /// the matched run (possibly empty). The cost of 0 is that
    /// the lookahead shortcut can no longer skip the rule, since a
    /// zero-width match can succeed on any input. Use 0 for grammars
    /// where an empty run is legal (string bodies that may be empty,
    /// optional text fields) and the cleaner tree shape (always one
    /// leaf) is worth more than the skip.
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
    /// Succeeds with a possibly empty body when the stopper matches
    /// (if the stopper is already next, the matched text is empty).
    /// Fails if end-of-input is reached without ever matching the
    /// stopper, unless <paramref name="eofIsTerminator"/> is true.
    /// One leaf scans chars directly, which is a meaningful speedup
    /// over <c>ZeroOrMore(NoneOf(stopAt))</c> for long strings.
    /// <para>
    /// Stopper membership is checked against the next whole token (one
    /// user-perceived character / grapheme cluster), the same way
    /// <see cref="OneOf(TokenSet)"/> does. A stopper of <c>'"'</c>
    /// matches a bare quote token but not a <c>'"'</c> followed by a
    /// combining mark (which is one cluster, not equal to <c>'"'</c>).
    /// For multi-rune stops, put the whole grapheme in the set: the
    /// built-in <see cref="TokenSet.LineTerminators"/> already includes
    /// the <c>CRLF</c> cluster, and <c>TokenSet.Runes("...")</c> adds a
    /// custom multi-rune cluster.
    /// </para>
    /// <para>
    /// Pass <paramref name="eofIsTerminator"/> = <c>true</c> for
    /// grammars where the body legitimately ends at the stopper OR at
    /// EOF (line comments that may close with a newline or with the
    /// end of file, for example). For "match the rest of the input,"
    /// use <see cref="ScanUntilEof"/> instead.
    /// </para>
    /// <code>
    /// // CSV field body: scan until the next comma or LF (strict)
    /// var field = ScanUntil(TokenSet.Runes(",\n"));
    ///
    /// // Line comment body: stops at any UAX #18 line terminator OR EOF
    /// var lineCommentBody = ScanUntil(TokenSet.LineTerminators,
    ///                                 eofIsTerminator: true);
    /// </code>
    /// </remarks>
    public static Rule ScanUntil(TokenSet stopAt, bool eofIsTerminator = false) =>
        new ScanUntilRule(stopAt, eofIsTerminator);

    /// <summary>
    /// <see cref="ScanUntil(TokenSet, bool)"/> with escape sequences.
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
    /// Same as the Rune-valued escape-start overload, with a
    /// rule-valued escape start. Default <see cref="FlattenType"/>:
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
    /// no stopper ever matches, the scan runs to EOF, and the tolerant
    /// branch returns one leaf over the whole remaining span.
    /// <code>
    /// // Conventional-commit subject: everything after the header marker
    /// var subject = ScanUntilEof().As("subject");
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
    /// Fails only at EOF. The idiomatic "catch-all" inside
    /// error-recovery loops and pass-through text grammars.
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
    /// more than one token (a multi-character literal like
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
    /// Wrap <paramref name="inner"/> in an alias that can be given its
    /// own name via <c>.As(string)</c> or <c>.As(SymbolId)</c>. The
    /// alias parses by forwarding to <paramref name="inner"/> and
    /// produces a Symbol carrying the alias's own Id, with the inner's
    /// matched content as that Symbol's children. If
    /// <paramref name="inner"/> is itself a Preserve rule (the default
    /// for any named rule), aliasing replaces the inner's Symbol with
    /// the alias's Symbol rather than nesting them: the inner's
    /// identity is hidden when accessed through this alias. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten"/>,
    /// which auto-flips to <see cref="FlattenType.Preserve"/> when
    /// <c>.As(...)</c> is applied.
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
    /// Errors: the identity substitution is success-only, so it doesn't affect error
    /// attribution. A <c>.WithError</c> on <paramref name="inner"/>
    /// still fires from inside the inner exactly as it would without
    /// an alias around it. A <c>.WithError</c> on the alias itself
    /// fires when the inner fails to match anywhere within it. If both
    /// are set, the standard deepest-failure tie-breaking decides
    /// which message surfaces.
    /// </para>
    /// </remarks>
    public static Rule Alias(Rule inner) => new AliasRule(inner);

    /// <summary>
    /// Sequence: match every child in order. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Fails if any child fails, rolling the lexer back to the
    /// start of the sequence.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="children"/> is null or empty.
    /// </exception>
    public static Rule And(params Rule[] children)
    {
        if (children == null || children.Length == 0)
            throw new ArgumentException("And requires at least one child rule.", nameof(children));
        return new AndRule(children);
    }

    /// <summary>
    /// Ordered choice: try each child left-to-right and commit to
    /// the first one that matches. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Unlike regex alternation, once a child commits there's no
    /// backtracking into an earlier branch. Fails only if every
    /// child fails.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="children"/> is null or empty.
    /// </exception>
    public static Rule Or(params Rule[] children)
    {
        if (children == null || children.Length == 0)
            throw new ArgumentException("Or requires at least one child rule.", nameof(children));
        return new OrRule(children);
    }

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
    /// cases of this composite. Argument validation lives on
    /// <see cref="BetweenInclusiveRule"/>'s constructor so every
    /// construction path goes through it.
    /// </remarks>
    public static Rule BetweenInclusive(int atLeast, int atMost, Rule inner) =>
        new BetweenInclusiveRule(inner, atLeast, atMost);

    /// <summary>
    /// Match <paramref name="inner"/> one or more times, greedy.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Fails if the first attempt fails. Equivalent to
    /// <c>BetweenInclusive(1, int.MaxValue, inner)</c>.
    /// </remarks>
    public static Rule OneOrMore(Rule inner) =>
        new BetweenInclusiveRule(inner, 1, int.MaxValue, "OneOrMore");

    /// <summary>
    /// Match <paramref name="inner"/> zero or more times, greedy.
    /// Always succeeds. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// A zero-match run is legal. Equivalent to
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
    /// Equivalent to <c>BetweenInclusive(0, 1, inner)</c>.
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
    /// Equivalent to
    /// <c>BetweenInclusive(atLeast, int.MaxValue, inner)</c>. The
    /// shortcut exists so grammars that want "3 or more of these"
    /// read like <c>AtLeast(3, OneOf("-*+"))</c> instead of
    /// <c>BetweenInclusive(3, int.MaxValue, ...)</c>, matching how
    /// <see cref="OneOrMore"/> and <see cref="ZeroOrMore"/> replace
    /// the 0/1 cases. The trace name carries the lower bound so
    /// traces stay self-describing.
    /// </remarks>
    public static Rule AtLeast(int atLeast, Rule inner) =>
        new BetweenInclusiveRule(inner, atLeast, int.MaxValue, $"AtLeast[{atLeast}]");

    /// <summary>
    /// Match <paramref name="inner"/> zero to
    /// <paramref name="atMost"/> times, greedy. Always succeeds
    /// (a zero-match run is legal). Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/>.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>BetweenInclusive(0, atMost, inner)</c>.
    /// The shortcut is for the "up to N of these" shape: zero,
    /// one, or up to <paramref name="atMost"/> matches. The trace
    /// name carries the upper bound so traces stay self-describing.
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
    /// Equivalent to <c>BetweenInclusive(count, count, inner)</c>.
    /// The shortcut is for the "N of these, no more, no less"
    /// shape: four hex runes in a \uXXXX escape, three digits in
    /// an area code, etc. Reads as
    /// <c>Exactly(4, OneOf(TokenSet.Ascii.HexDigits))</c> instead
    /// of <c>BetweenInclusive(4, 4, ...)</c>. The trace name
    /// carries the count so traces stay self-describing.
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
    /// top of a grammar.
    /// </remarks>
    public static Rule Eof() => new EofRule();

    /// <summary>
    /// Match a signed integer: an optional leading + or -,
    /// followed by one or more decimal digits. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten"/>
    /// (from the composed outer <see cref="And"/>).
    /// </summary>
    /// <remarks>
    /// Pre-built because every grammar ends up wanting it. The leading
    /// sign is matched as <c>OneOf("+-").Flatten(FlattenType.Flatten)</c>
    /// so the sign rune bubbles into the parent tree: a named
    /// <c>Integer().As(...)</c> renders its full matched text via
    /// <c>ToString()</c>, including the sign. <see cref="Float"/> uses
    /// the same shape for the same reason.
    /// </remarks>
    public static Rule Integer() =>
        And(
            Optional(OneOf("+-").Flatten(FlattenType.Flatten)),
            OneOrMore(OneOf(TokenSet.Digits))
        );

    /// <summary>
    /// Match a simple decimal: an optional leading <c>+</c> or
    /// <c>-</c>, one or more digits, a literal <c>'.'</c>, and one
    /// or more digits. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Flatten"/> (from the composed outer
    /// <see cref="And"/>).
    /// </summary>
    /// <remarks>
    /// The optional leading sign matches <see cref="Integer"/>'s
    /// convention, so a grammar that uses
    /// <c>Or(Float(), Integer())</c> treats <c>+5</c> and
    /// <c>+5.5</c> consistently. A sign is allowed only at the front:
    /// the fractional part is digits-only, and inputs like
    /// <c>3.+14</c> or <c>--3.14</c> don't match.
    /// <para>
    /// Doesn't handle exponents or scientific notation. Grammars
    /// that need those compose their own.
    /// </para>
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
    /// <c>TokenSet.InlineWhitespace</c>. Does NOT match line terminators
    /// (<c>\n</c>, <c>\r</c>, <c>\r\n</c>, NEL, LINE SEPARATOR,
    /// PARAGRAPH SEPARATOR, VT, FF). Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/> (applied by the factory).
    /// </summary>
    /// <remarks>
    /// Matches the common "skip whitespace without storing it"
    /// case, so the factory pre-applies
    /// <c>.FlattenByDefault(FlattenType.Delete)</c>: the match contributes
    /// nothing to the tree. Without the override the underlying
    /// <see cref="OneOrMore"/> would default to
    /// <see cref="FlattenType.Flatten"/>. <c>.FlattenByDefault</c> rather
    /// than <c>.Flatten</c> so a caller can still <c>.As(...)</c> the
    /// result or override the policy.
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
    /// <see cref="EndOfLine"/> is tried first inside the
    /// <see cref="Or"/>, so that a CRLF grapheme is consumed as one
    /// terminator rather than only matching the CR via the single-rune side.
    /// </para>
    /// <para>
    /// For "skip whitespace here, possibly none," wrap as
    /// <c>Optional(AnyWhitespace())</c>. For strict intra-line whitespace
    /// (no line terminators) use <see cref="InlineWhitespace"/>.
    /// </para>
    /// </remarks>
    public static Rule AnyWhitespace() =>
        OneOrMore(Or(EndOfLine(), OneOf(TokenSet.InlineWhitespace))).FlattenByDefault(FlattenType.Delete);

    /// <summary>
    /// Match one Unicode line terminator per UAX #18 Annex C. When
    /// <paramref name="eofIsEol"/> is <c>true</c>, also matches at
    /// end-of-input. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <param name="eofIsEol">
    /// When <c>true</c>, end-of-input counts as an end-of-line. The
    /// last line of a document typically isn't followed by a terminator,
    /// so a grammar that wants one at the end of every line has to
    /// accept EOF as equivalent. Default is <c>false</c>: a real
    /// terminator is required.
    /// </param>
    /// <remarks>
    /// Consumes one of:
    /// <list type="bullet">
    /// <item><description>CRLF (the two-rune sequence <c>\r\n</c>)</description></item>
    /// <item><description>LF, VT, FF, CR, NEL, LINE SEPARATOR, or PARAGRAPH SEPARATOR
    /// (the single-rune terminators in <see cref="TokenSet.LineTerminators"/>)</description></item>
    /// <item><description>End-of-input, but only when <paramref name="eofIsEol"/> is <c>true</c></description></item>
    /// </list>
    /// CRLF is tried first so a CR immediately followed by an LF is
    /// consumed as one terminator rather than split into two.
    /// <para>
    /// For "an end-of-line here, or none at all" (a terminator that
    /// may or may not be present at the very end of a file), wrap with
    /// <see cref="Optional"/> and pass <c>eofIsEol: true</c> so the
    /// intent reads at the call:
    /// <c>Optional(EndOfLine(eofIsEol: true))</c>.
    /// </para>
    /// </remarks>
    public static Rule EndOfLine(bool eofIsEol = false)
    {
        var alternatives = eofIsEol
            ? new Rule[] { Literal("\r\n"), OneOf(TokenSet.LineTerminators), Eof() }
            : new Rule[] { Literal("\r\n"), OneOf(TokenSet.LineTerminators) };
        return Or(alternatives).FlattenByDefault(FlattenType.Delete);
    }

    /// <summary>
    /// Encodes a UAX #31-style "programming language identifier" using
    /// runtime-backed XID tables plus the built-in exception tables in
    /// <see cref="TokenSet"/>. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>,
    /// so the match appears in the tree as one named node whose
    /// children are the per-rune leaves.
    /// </summary>
    /// <param name="form">
    /// The normalization form the rule will be compiled under. Must
    /// match the form passed to <c>Compile</c>. Default is
    /// <see cref="NormalizationForm.FormC"/>, the same default Compile
    /// uses. Pass <see cref="NormalizationForm.FormKC"/> /
    /// <see cref="NormalizationForm.FormKD"/> for compatibility-form
    /// identifiers (Python 3 / Rust style: fullwidth Latin and ligatures
    /// match their plain ASCII equivalents). Pass <c>null</c> to opt out
    /// of normalization at parse time, matching the unnormalized Compile
    /// path.
    /// </param>
    /// <param name="extraStartRunes">
    /// Runes to union into <see cref="TokenSet.XidStart"/> for the
    /// first character. UAX #31 calls this a "profile extension":
    /// the base Start property plus language-specific additions.
    /// Typical value for a programming-language grammar is
    /// <c>TokenSet.Runes("_")</c>. Python and Rust use this shape; C#
    /// also permits leading underscores, though its full identifier
    /// specification differs. Defaults to
    /// <see cref="TokenSet.Empty"/> (the base UAX #31-style profile).
    /// </param>
    /// <param name="extraBodyRunes">
    /// Runes to union into <see cref="TokenSet.XidContinue"/> for
    /// every character after the first. Same idea as
    /// <paramref name="extraStartRunes"/>. ECMAScript, for example,
    /// adds <c>$</c> to both positions. Defaults to
    /// <see cref="TokenSet.Empty"/>.
    /// </param>
    /// <remarks>
    /// The same word can be typed more than one way. "café" might be
    /// stored with a single precomposed "é", or with a plain "e"
    /// followed by a combining accent mark drawn on top. Both look
    /// identical in an editor but use different Unicode scalar sequences. By default
    /// the parser treats them as the same identifier, so a grammar
    /// doesn't have to care which form it gets.
    /// <para>
    /// Pass <c>null</c> to <c>Compile(NormalizationForm?)</c> to match
    /// the input string as written, without canonical or compatibility
    /// normalization. Pass <c>NormalizationForm.FormKC</c> for a stronger
    /// rule that also treats fullwidth <c>ｆｏｏ</c> and plain <c>foo</c>,
    /// or the ligature <c>ﬀ</c> and <c>ff</c>, as the same identifier.
    /// That's the Python 3 and Rust behavior. The stronger rule can,
    /// however, collapse things you may want kept distinct. It converts
    /// <c>ℓ</c> (script small L, used in physics) into <c>l</c>, and
    /// <c>Ⅷ</c> (Roman numeral) into <c>VIII</c>. A grammar that parses
    /// math or legal text probably wants those distinctions.
    /// Identifier-heavy grammars (Python source, say) almost always
    /// don't.
    /// </para>
    /// <para>
    /// See docs/UnicodeGotchas.md for recipes that reproduce the
    /// identifier rules of specific languages (Python 3, Rust,
    /// ECMAScript) via these parameters plus the form chosen at
    /// <c>Compile</c> time.
    /// </para>
    /// </remarks>
    public static Rule Identifier(
        NormalizationForm? form = NormalizationForm.FormC,
        TokenSet extraStartRunes = default,
        TokenSet extraBodyRunes = default)
    {
        var start = TokenSet.XidStart | extraStartRunes;
        var body = TokenSet.XidContinue | extraBodyRunes;
        // Under FormKC / FormKD, XidStart and XidContinue contain entries
        // (ligatures, fullwidth Latin, math-bold) whose compatibility
        // conversion is a multi-grapheme sequence. A OneOf rule can't
        // match a multi-grapheme entry as a single token, so Compile
        // would throw. Pre-apply WithCompatibilityEquivalents to expand
        // those entries into their grapheme pieces, which the rest of
        // the rule structure (ZeroOrMore(OneOf(body))) consumes one at
        // a time. Pass the same form to Compile afterward.
        if (form == NormalizationForm.FormKC || form == NormalizationForm.FormKD)
        {
            start = start.WithCompatibilityEquivalents(form.Value);
            body = body.WithCompatibilityEquivalents(form.Value);
        }
        return And(
            // First token: starts with a Start rune, rest of its runes
            // (if any) are Body runes. Handles precomposed "é", "ñ",
            // etc. as single-rune tokens and "हि"-style
            // consonant+vowel-sign tokens as multi-rune.
            WithinToken(And(OneOf(start), ZeroOrMore(OneOf(body)))),
            // Subsequent tokens: every rune must be a Body rune.
            ZeroOrMore(WithinToken(OneOrMore(OneOf(body))))
        ).FlattenByDefault(FlattenType.Preserve);
    }

    /// <summary>
    /// Reads one token from the lexer and runs <paramref name="innerRule"/>
    /// against the runes inside that token. The outer token is one
    /// character as the user sees it (a grapheme cluster) that may
    /// span several runes, and the inner rule walks them one at a
    /// time over a sub-lexer switched to one-rune-per-token mode.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <param name="innerRule">
    /// The rule to run against the token's runes. Must consume every
    /// rune of the token on success; a rule that matches only a
    /// prefix causes the whole <c>WithinToken</c> to fail. Any rule
    /// composition is allowed inside (<see cref="And"/>, <see cref="Or"/>,
    /// <c>OneOf</c>, etc.).
    /// </param>
    /// <remarks>
    /// Building block for rules that care about token-internal
    /// structure. Used by <see cref="Identifier"/> to make identifier
    /// matching work on Devanagari, Thai, Arabic-with-vowels, and other
    /// scripts whose "letters" are multi-rune tokens. Other uses:
    /// emoji-with-modifier matchers (<c>WithinToken(And(OneOf(EmojiBase),
    /// ZeroOrMore(OneOf(SkinToneOrZWJ))))</c>), ASCII-only strictness
    /// (<c>WithinToken(OneOf(TokenSet.Ascii.Letters))</c> rejects any
    /// multi-rune token), Hangul jamo clusters, etc.
    /// <para>
    /// One leaf Symbol is emitted per successful match, representing the
    /// whole token. Inner-rule symbols are discarded. Inner-rule
    /// tracing isn't propagated to the outer trace. The inner parse is
    /// bounded to the token's rune span, but the sub-lexer doesn't
    /// share the outer parse's trace or budget counters.
    /// </para>
    /// </remarks>
    public static Rule WithinToken(Rule innerRule) => new WithinTokenRule(innerRule);
}
