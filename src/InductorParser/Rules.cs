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
///     OneOrMore(RuneIn(RuneSet.Letters)),
///     OptionalWhitespace(),
///     Token('='),
///     OptionalWhitespace(),
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
/// </remarks>
public static class Rules
{
    /// <summary>
    /// Match one token whose content is exactly the given character.
    /// Default <see cref="FlattenType"/>: <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// What counts as "one token" depends on the configured lexer.
    /// Under GraphemeLexer (the default) a token is one grapheme, so
    /// <c>Token('a')</c> matches when the grapheme is the single
    /// char 'a' but fails when 'a' is combined with a following
    /// accent (because the grapheme is then two runes). Under
    /// RuneLexer a token is one rune, so <c>Token('a')</c> matches
    /// the 'a' rune regardless of what follows. That last case is
    /// easy to trip over: if the input has 'a' followed by U+0301
    /// combining acute (which together render as 'á' in an editor),
    /// <c>Token('a')</c> under RuneLexer happily matches the 'a'
    /// and the combining mark stays in the stream as its own token
    /// for the next rule to handle. Grammars running under
    /// RuneLexer have to be explicit about combining marks or
    /// normalize the input before parsing. See
    /// docs/UnicodeGotchas.md for the list of cases where this
    /// bites.
    ///
    /// All four Token overloads funnel into <see cref="TokenRule"/>.
    /// The overloads exist for convenience and for early validation
    /// of their specific argument shape. For multi-character
    /// matches use <see cref="Literal"/>.
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
                "Surrogate halves aren't valid grapheme content. Use Token(Rune) or Token(int) for a supplementary-plane code point.");
        return new TokenRule(c.ToString());
    }

    /// <summary>
    /// Match one token whose content is exactly the given
    /// <see cref="Rune"/>. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// Under GraphemeLexer (the default) a token is one grapheme,
    /// so this matches when the grapheme at the current position
    /// is exactly the rune <c>r</c> standing alone. It fails when
    /// <c>r</c> is followed by a combining mark or is part of a ZWJ
    /// sequence, because the grapheme is then two or more runes
    /// and doesn't equal the one-rune expected content. Under
    /// RuneLexer a token is one rune, so this matches the <c>r</c>
    /// rune directly regardless of what follows. That last case is
    /// easy to trip over: if the input has <c>r</c> followed by a
    /// combining mark, <c>Token(r)</c> under RuneLexer happily
    /// matches the <c>r</c> rune and the combining mark stays in
    /// the stream as its own token for the next rule to handle.
    /// Grammars running under RuneLexer have to be explicit about
    /// combining marks or normalize the input before parsing. See
    /// docs/UnicodeGotchas.md for the list of cases where this
    /// bites.
    /// </remarks>
    public static Rule Token(Rune r) => new TokenRule(r.ToString());

    /// <summary>
    /// Match one token whose content is exactly the rune with the
    /// given integer code point. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// Under GraphemeLexer (the default) a token is one grapheme,
    /// so this matches when the grapheme at the current position
    /// is exactly the given rune standing alone. It fails when the
    /// rune is followed by a combining mark or is part of a ZWJ
    /// sequence, because the grapheme is then two or more runes
    /// and doesn't equal the one-rune expected content. Under
    /// RuneLexer a token is one rune, so this matches the rune
    /// directly regardless of what follows. That last case is easy
    /// to trip over: if the input has the rune followed by a
    /// combining mark, this rule under RuneLexer happily matches
    /// the rune and the combining mark stays in the stream as its
    /// own token for the next rule to handle. Grammars running
    /// under RuneLexer have to be explicit about combining marks
    /// or normalize the input before parsing. See
    /// docs/UnicodeGotchas.md for the list of cases where this
    /// bites.
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
        return new TokenRule(new Rune(codepoint).ToString());
    }

    /// <summary>
    /// Match one grapheme whose content equals the given string.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// The string may itself be multi-rune (ZWJ sequences, skin
    /// tone modifiers, etc.). The <see cref="TokenRule"/>
    /// constructor validates at grammar-build time that the string
    /// is exactly one grapheme.
    ///
    /// Under GraphemeLexer the input grapheme arrives as a single
    /// token and this rule matches it in one compare. Under
    /// RuneLexer the input grapheme arrives as N rune tokens (one
    /// per rune in the grapheme) and this rule matches them in
    /// lockstep.
    /// </remarks>
    public static Rule Token(string grapheme) => new TokenRule(grapheme);

    /// <summary>
    /// Match an exact multi-character string in a single
    /// transaction. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// For a one-grapheme match use <see cref="Token(string)"/>.
    /// Literal is the N-grapheme generalization and collapses what
    /// would otherwise be N Token rules (and N transactions) into
    /// one. Rejects empty strings at construction.
    /// </remarks>
    public static Rule Literal(string value) => new LiteralRule(value);

    /// <summary>
    /// ASCII-case-insensitive variant of <see cref="Literal"/>.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/>.
    /// </summary>
    /// <remarks>
    /// The pattern string can contain any characters, including
    /// non-ASCII ones. ASCII letters (A-Z, a-z) in the pattern
    /// match either case in the input. Everything else (digits,
    /// punctuation, non-ASCII characters) compares bit-exact, so
    /// the pattern "café" matches input "café" and "CAFé" (c/a/f
    /// are ASCII letters and match case-insensitively, é matches
    /// itself) but not "CAFÉ" (because é is non-ASCII and doesn't
    /// match É under ASCII case-insensitive rules).
    ///
    /// The ASCII in the name is critical: full Unicode
    /// case-insensitive matching is locale- and script-dependent
    /// and this leaf doesn't attempt it. See
    /// docs/UnicodeGotchas.md for the reasoning and limits.
    /// </remarks>
    public static Rule LiteralIgnoreAsciiCase(string value) => new LiteralIgnoreAsciiCaseRule(value);

    /// <summary>
    /// Match one rune whose value is in the given
    /// <see cref="RuneSet"/>. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// The workhorse character-class rule. Pass any RuneSet built
    /// from the factories (Single, Range, Runes, Category) or one
    /// of the built-ins (Letters, Digits, Whitespace, Ascii.*).
    /// RuneSets compose with <c>|</c> (union), <c>&amp;</c>
    /// (intersection), and <c>~</c> (complement):
    /// <code>
    /// // Identifier character: any letter, digit, or underscore
    /// var idChar = RuneIn(RuneSet.Letters | RuneSet.Digits | RuneSet.Runes("_"));
    ///
    /// // ASCII consonant: ASCII letter minus vowels
    /// var consonant = RuneIn(RuneSet.Ascii.Letters &amp; ~RuneSet.Runes("aeiouAEIOU"));
    ///
    /// // Printable non-whitespace: letters and digits only, in Latin script
    /// var latinAlnum = RuneIn(
    ///     (RuneSet.Letters | RuneSet.Digits) &amp; RuneSet.Range(0x0000, 0x024F));
    /// </code>
    /// </remarks>
    public static Rule RuneIn(RuneSet cls) => new RuneInRule(cls);

    /// <summary>
    /// Shortcut for the common "one of these literal runes" case.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>RuneIn(RuneSet.Runes(runes))</c>. When you
    /// need ranges, category unions, or complements, reach for
    /// <see cref="RuneSet"/> directly and pass it to the
    /// <see cref="RuneIn(RuneSet)"/> overload.
    /// </remarks>
    public static Rule RuneIn(string runes) => new RuneInRule(RuneSet.Runes(runes));

    /// <summary>
    /// Match one rune whose value is NOT in the given
    /// <see cref="RuneSet"/>. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// The idiomatic "any rune except these" rule, commonly used
    /// as the body character in a bounded scan (for example,
    /// everything up to a closing quote).
    /// </remarks>
    public static Rule RuneNotIn(RuneSet cls) => new RuneNotInRule(cls);

    /// <summary>
    /// Shortcut for "any rune except these specific ones."
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>RuneNotIn(RuneSet.Runes(runes))</c>.
    /// </remarks>
    public static Rule RuneNotIn(string runes) => new RuneNotInRule(RuneSet.Runes(runes));

    /// <summary>
    /// Match a run of runes up to (but not including) a rune in
    /// the stoppers set. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Consumes at least one rune on success. One leaf that scans
    /// chars directly, which is a meaningful speedup over
    /// <c>ZeroOrMore(RuneNotIn(stoppers))</c> for long strings.
    /// <code>
    /// // CSV field body: scan until the next comma or newline
    /// var field = StringChars(RuneSet.Runes(",\n"));
    ///
    /// // Line comment body: scan until end-of-line
    /// var lineCommentBody = StringChars(RuneSet.Runes("\r\n"));
    /// </code>
    /// </remarks>
    public static Rule StringChars(RuneSet stoppers) =>
        new StringCharsRule(stoppers);

    /// <summary>
    /// <see cref="StringChars(RuneSet)"/> with escape sequences.
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
    /// var body = StringChars(
    ///     RuneSet.Runes("\"\\"),
    ///     new Rune('\\'),
    ///     RuneIn("ntr\"\\"));
    /// </code>
    /// </remarks>
    public static Rule StringChars(RuneSet stoppers, Rune escapeStart, Rule escapeEnd) =>
        new StringCharsRule(stoppers, escapeStart, escapeEnd);

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
    /// var body = StringChars(
    ///     RuneSet.Runes("\"$"),
    ///     Literal("${"),
    ///     And(OneOrMore(RuneNotIn("}")), Token('}')));
    /// </code>
    /// </remarks>
    public static Rule StringChars(RuneSet stoppers, Rule escapeStart, Rule escapeEnd) =>
        new StringCharsRule(stoppers, escapeStart, escapeEnd);

    /// <summary>
    /// <see cref="StringChars(RuneSet)"/> with a rule-valued stop
    /// condition. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// Use when the "end of string" boundary is more than a set of
    /// runes (for example, the closing delimiter is a multi-char
    /// sequence like <c>]]&gt;</c>).
    /// <code>
    /// // XML CDATA body: scan until the closing "]]&gt;"
    /// var cdataBody = StringChars(Literal("]]&gt;"));
    ///
    /// // C-style block comment body: scan until "*/"
    /// var blockCommentBody = StringChars(Literal("*/"));
    /// </code>
    /// </remarks>
    public static Rule StringChars(Rule stopper) =>
        new StringCharsRule(stopper);

    /// <summary>
    /// Rule-stopper StringChars with escape sequences. Default
    /// <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <remarks>
    /// <code>
    /// // Python-style triple-quoted string body: scan until
    /// // """, with \" and \\ as single-rune escapes
    /// var body = StringChars(
    ///     Literal("\"\"\""),
    ///     new Rune('\\'),
    ///     RuneIn("\"\\"));
    /// </code>
    /// </remarks>
    public static Rule StringChars(Rule stopper, Rune escapeStart, Rule escapeEnd) =>
        new StringCharsRule(stopper, escapeStart, escapeEnd);

    /// <summary>
    /// Match any one token (one grapheme under GraphemeLexer, one
    /// rune under RuneLexer). Default <see cref="FlattenType"/>:
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
    /// <see cref="RuneNotIn(RuneSet)"/> can't express because X is
    /// longer than one rune.
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
    /// read like <c>AtLeast(3, RuneIn("-*+"))</c> instead of
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
    /// <c>Exactly(4, RuneIn(RuneSet.Ascii.HexDigits))</c> instead
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
    /// Pre-built because every grammar ends up wanting it.
    /// </remarks>
    public static Rule Integer() =>
        And(
            Optional(Or(Token('+'), Token('-'))),
            OneOrMore(RuneIn(RuneSet.Digits))
        );

    /// <summary>
    /// Match a simple decimal: an optional leading -, one or more
    /// digits, a literal '.', and one or more digits. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Flatten"/>
    /// (from the composed outer <see cref="And"/>).
    /// </summary>
    /// <remarks>
    /// Doesn't handle exponents, scientific notation, or leading
    /// '+'. Grammars that need those compose their own.
    /// </remarks>
    public static Rule Float() =>
        And(
            Optional(Token('-').Flatten(FlattenType.Flatten)),
            Integer(),
            Token('.').Flatten(FlattenType.Preserve),
            Integer()
        );

    /// <summary>
    /// Match one or more whitespace runes as defined by
    /// <c>RuneSet.Whitespace</c>. Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/> (applied by the factory).
    /// </summary>
    /// <remarks>
    /// Matches the common "skip whitespace without storing it"
    /// case, so the factory pre-applies
    /// <c>.Flatten(FlattenType.Delete)</c>: the match contributes
    /// nothing to the tree. Without the override the underlying
    /// <see cref="OneOrMore"/> would default to
    /// <see cref="FlattenType.Flatten"/>.
    /// </remarks>
    public static Rule Whitespace() => OneOrMore(RuneIn(RuneSet.Whitespace)).Flatten(FlattenType.Delete);

    /// <summary>
    /// Match zero or more whitespace runes. Always succeeds.
    /// Default <see cref="FlattenType"/>:
    /// <see cref="FlattenType.Delete"/> (applied by the factory).
    /// </summary>
    /// <remarks>
    /// The "skip any whitespace here, including none" shape. The
    /// factory pre-applies <c>.Flatten(FlattenType.Delete)</c> so
    /// the match contributes nothing to the tree. Without the
    /// override the underlying <see cref="ZeroOrMore"/> would
    /// default to <see cref="FlattenType.Flatten"/>.
    /// </remarks>
    public static Rule OptionalWhitespace() => ZeroOrMore(RuneIn(RuneSet.Whitespace)).Flatten(FlattenType.Delete);

    /// <summary>
    /// Encodes the Unicode definition of a "programming language
    /// identifier" that would be appropriate worldwide (UAX #31 R1). Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>,
    /// so the match appears in the tree as one named node whose
    /// children are the per-rune leaves.
    /// </summary>
    /// <param name="extraStartRunes">
    /// Runes to union into <see cref="RuneSet.XidStart"/> for the
    /// first character. UAX #31 calls this a "profile extension":
    /// the base Start property plus language-specific additions.
    /// Typical value for a programming-language grammar is
    /// <c>RuneSet.Runes("_")</c>, which is what C#, Python, Rust,
    /// and friends do on top of XID_Start. Defaults to
    /// <see cref="RuneSet.Empty"/> (strict UAX #31).
    /// </param>
    /// <param name="extraBodyRunes">
    /// Runes to union into <see cref="RuneSet.XidContinue"/> for
    /// every character after the first. Same idea as
    /// <paramref name="extraStartRunes"/>. ECMAScript, for example,
    /// adds <c>$</c> to both positions. Defaults to
    /// <see cref="RuneSet.Empty"/>.
    /// </param>
    /// <remarks>
    /// The same word can be typed more than one way. "café" might be
    /// stored with a single precomposed "é", or with a plain "e"
    /// followed by a combining accent mark drawn on top. Both look
    /// identical in an editor but differ byte-for-byte. By default
    /// the parser treats them as the same identifier, so a grammar
    /// doesn't have to care which form it gets.
    /// <para>
    /// Pass <c>NormalizeInput = null</c> on
    /// <see cref="ParseOptions"/> to match bytes as-written. Pass
    /// <c>NormalizationForm.FormKC</c> for a stronger rule that also
    /// treats fullwidth <c>ｆｏｏ</c> and plain <c>foo</c>, or the
    /// ligature <c>ﬀ</c> and <c>ff</c>, as the same identifier.
    /// That's the Python 3 and Rust behavior.
    /// The stronger rule can, however, collapse things
    /// you may want kept distinct. It folds <c>ℓ</c> (script small L,
    /// used in physics) into <c>l</c>, and <c>Ⅷ</c> (Roman numeral)
    /// into <c>VIII</c>. A grammar that parses math or legal text
    /// probably wants those distinctions. Identifier-heavy grammars
    /// (Python source, say) almost always don't.
    /// </para>
    /// <para>
    /// See docs/UnicodeGotchas.md for recipes that reproduce the
    /// identifier rules of specific languages (Python 3, Rust,
    /// ECMAScript) via these parameters plus NormalizeInput.
    /// </para>
    /// </remarks>
    public static Rule Identifier(RuneSet extraStartRunes = default, RuneSet extraBodyRunes = default)
    {
        var start = RuneSet.XidStart | extraStartRunes;
        var body = RuneSet.XidContinue | extraBodyRunes;
        return And(
            // First grapheme: starts with a Start rune, rest of its runes
            // (if any) are Body runes. Under GraphemeLexer this handles
            // precomposed "é", "ñ", etc. as single-rune graphemes and
            // "हि"-style consonant+vowel-sign graphemes as multi-rune.
            WithinGrapheme(And(RuneIn(start), ZeroOrMore(RuneIn(body)))),
            // Subsequent graphemes: every rune must be a Body rune.
            ZeroOrMore(WithinGrapheme(OneOrMore(RuneIn(body))))
        ).Flatten(FlattenType.Preserve);
    }

    /// <summary>
    /// Reads one token from the lexer and runs <paramref name="innerRule"/>
    /// against the runes inside that token. Under
    /// <see cref="InputUnit.Grapheme"/> (the default) the token is a
    /// grapheme cluster that may span several runes, and the inner rule
    /// walks them one at a time. Under <see cref="InputUnit.Rune"/> the
    /// token is already one rune, so the inner rule sees a single-rune
    /// stream and behaves as it would outside the wrapper. Default
    /// <see cref="FlattenType"/>: <see cref="FlattenType.Preserve"/>.
    /// </summary>
    /// <param name="innerRule">
    /// The rule to run against the grapheme's runes. Must consume every
    /// rune of the grapheme on success; a rule that matches only a
    /// prefix causes the whole <c>WithinGrapheme</c> to fail. Any rule
    /// composition is allowed inside (<see cref="And"/>, <see cref="Or"/>,
    /// <c>RuneIn</c>, etc.).
    /// </param>
    /// <remarks>
    /// Building block for rules that care about grapheme-internal
    /// structure. Used by <see cref="Identifier"/> to make identifier
    /// matching work on Devanagari, Thai, Arabic-with-vowels, and other
    /// scripts whose "letters" are multi-rune graphemes. Other uses:
    /// emoji-with-modifier matchers (<c>WithinGrapheme(And(RuneIn(EmojiBase),
    /// ZeroOrMore(RuneIn(SkinToneOrZWJ))))</c>), ASCII-only strictness
    /// (<c>WithinGrapheme(RuneIn(RuneSet.Ascii.Letters))</c> rejects any
    /// multi-rune grapheme), Hangul jamo clusters, etc.
    /// <para>
    /// One leaf Symbol is emitted per successful match, representing the
    /// whole grapheme. Inner-rule symbols are discarded. Inner-rule
    /// tracing is not propagated to the outer trace. The inner parse is
    /// bounded by the grapheme's rune count, so runaway is impossible.
    /// </para>
    /// </remarks>
    public static Rule WithinGrapheme(Rule innerRule) => new WithinGraphemeRule(innerRule);
}
