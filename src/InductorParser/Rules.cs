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

    public static Rule RuneIn(RuneSet cls) => new RuneInRule(cls);

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

    public static Rule OneOrMore(Rule inner) => new OneOrMoreRule(inner);
    public static Rule ZeroOrMore(Rule inner) => new ZeroOrMoreRule(inner);
    public static Rule Optional(Rule inner) => new OptionalRule(inner);
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
