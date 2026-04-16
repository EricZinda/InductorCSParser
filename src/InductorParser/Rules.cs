using System;
using System.Text;
using InductorParser.SyntaxTree;

namespace InductorParser;

public static class Rules
{
    // Matches one grapheme by exact content. All four overloads funnel into
    // CharRule(string); the overloads exist for convenience and for early
    // validation of their specific argument shape.
    public static Rule Char(char c)
    {
        if (char.IsSurrogate(c))
            throw new ArgumentOutOfRangeException(nameof(c),
                "Surrogate halves aren't valid grapheme content. Use Char(Rune) or Char(int) for a supplementary-plane code point.");
        return new CharRule(c.ToString());
    }

    // Rune's own ctor already enforces validity, so we just need to stringify.
    public static Rule Char(Rune r) => new CharRule(r.ToString());

    public static Rule Char(int codepoint)
    {
        if (!Rune.IsValid(codepoint))
            throw new ArgumentOutOfRangeException(nameof(codepoint), codepoint,
                "Not a valid Unicode scalar value (0..0x10FFFF, excluding surrogates 0xD800..0xDFFF).");
        return new CharRule(new Rune(codepoint).ToString());
    }

    // Match a whole grapheme, which may be multi-rune (ZWJ sequences, skin
    // tone modifiers, etc.). The CharRule constructor validates that the
    // string is exactly one grapheme.
    public static Rule Char(string grapheme) => new CharRule(grapheme);

    // Match an exact multi-character string in a single transaction. For a
    // one-grapheme match use Char(string); Literal is the N-grapheme
    // generalization and collapses what would otherwise be N Char rules
    // (and N transactions) into one. Rejects empty strings at construction.
    public static Rule Literal(string value) => new LiteralRule(value);

    // ASCII-case-insensitive variant of Literal. Letters A-Z / a-z fold to
    // the same match; non-ASCII code units compare bit-exact. The ASCII in
    // the name is critical: full Unicode case folding is locale- and
    // script-dependent and this primitive doesn't attempt it. See
    // docs/UnicodeGotchas.md for the reasoning and limits.
    public static Rule LiteralIgnoreAsciiCase(string value) => new LiteralIgnoreAsciiCaseRule(value);

    public static Rule RuneIn(RuneSet cls) => new RuneInRule(cls);

    // Shortcut for the common "one of these literal runes" case. Equivalent
    // to RuneIn(RuneSet.Runes(runes)). When you need ranges, category unions,
    // or complements, reach for RuneSet directly and pass it to the overload
    // above.
    public static Rule RuneIn(string runes) => new RuneInRule(RuneSet.Runes(runes));

    public static Rule RuneNotIn(RuneSet cls) => new RuneNotInRule(cls);

    public static Rule RuneNotIn(string runes) => new RuneNotInRule(RuneSet.Runes(runes));

    public static Rule AnyChar() => new AnyCharRule();

    public static Rule Not(Rule inner) => new NotRule(inner);

    public static Rule Peek(Rule inner) => new PeekRule(inner);

    public static Rule And(params Rule[] children)
    {
        if (children == null || children.Length == 0)
            throw new ArgumentException("And requires at least one child rule.", nameof(children));
        return new AndRule(children);
    }

    public static Rule Or(params Rule[] children)
    {
        if (children == null || children.Length == 0)
            throw new ArgumentException("Or requires at least one child rule.", nameof(children));
        return new OrRule(children);
    }

    // BetweenInclusive(inner, n, m) matches inner between n and m times
    // inclusive. The three count-rule shapes below are special cases of
    // this primitive. Argument validation lives on BetweenInclusiveRule's
    // constructor so every construction path goes through it.
    public static Rule BetweenInclusive(Rule inner, int atLeast, int atMost) =>
        new BetweenInclusiveRule(inner, atLeast, atMost);

    // The common shapes pass a friendly trace name so the trace reads
    // as the factory name the grammar author chose (OneOrMore, ZeroOrMore,
    // Optional, NOrMore) rather than the raw bounds notation.
    public static Rule OneOrMore(Rule inner) =>
        new BetweenInclusiveRule(inner, 1, int.MaxValue, "OneOrMore");

    public static Rule ZeroOrMore(Rule inner) =>
        new BetweenInclusiveRule(inner, 0, int.MaxValue, "ZeroOrMore");

    public static Rule Optional(Rule inner) =>
        new BetweenInclusiveRule(inner, 0, 1, "Optional");

    // NOrMore(inner, n) == BetweenInclusive(inner, n, int.MaxValue). The
    // shortcut exists so grammars that want "3+ of these" read like
    // NOrMore(RuneIn("-*+"), 3) instead of BetweenInclusive(..., 3, int.MaxValue),
    // matching how OneOrMore/ZeroOrMore replace the 0/1 cases. The trace
    // name carries the lower bound so traces stay self-describing.
    public static Rule NOrMore(Rule inner, int atLeast) =>
        new BetweenInclusiveRule(inner, atLeast, int.MaxValue, $"NOrMore[{atLeast}]");
    public static Rule Eof() => new EofRule();

    // [+|-]? Digit+
    public static Rule Integer() =>
        And(
            Optional(Or(Char('+'), Char('-'))),
            OneOrMore(RuneIn(RuneSet.Digits))
        );

    // -? Integer "." Integer
    public static Rule Float() =>
        And(
            Optional(Char('-').Flatten(FlattenType.Flatten)),
            Integer(),
            Char('.').Flatten(FlattenType.None),
            Integer()
        );

    public static Rule Whitespace() => OneOrMore(RuneIn(RuneSet.Whitespace)).Flatten(FlattenType.Delete);
    public static Rule OptionalWhitespace() => ZeroOrMore(RuneIn(RuneSet.Whitespace)).Flatten(FlattenType.Delete);
}
