using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Sibling to UnexpectedUnicodeTests. Where that file probes inputs that
// are encoding-broken or sit outside UAX #29's normal model, this file
// covers Unicode inputs that are valid and well-formed but unusual
// enough that a reader might wonder if the parser handles them. The
// tests lock in that the parser follows UAX #29 / UAX #15 to the letter
// on these inputs and doesn't mis-cluster, mis-normalize, or crash.
[TestFixture]
public class ValidUnicodeTests
{
    [Test]
    public void Family_ZWJ_sequence_is_one_token()
    {
        // Man + ZWJ + woman + ZWJ + boy. UAX #29 GB11 keeps an
        // emoji + ZWJ + emoji chain as one cluster, with the rule
        // re-firing at every joiner along the chain. Five runes,
        // 8 UTF-16 chars, one token.
        string input = UnicodeExamples.FamilyManWomanBoyGrapheme;

        // AnyToken consumes the whole cluster.
        Assert.That(And(AnyToken(), Eof()).Parse(input).Success, Is.True);

        // Targeting the multi-rune cluster as a single literal via
        // Token(string) also works.
        Assert.That(And(Token(UnicodeExamples.FamilyManWomanBoyGrapheme), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Profession_ZWJ_sequence_is_one_token()
    {
        // Woman + ZWJ + briefcase = woman office worker. Three runes,
        // 5 UTF-16 chars, one cluster. Different shape from the family
        // sequence above (one ZWJ link, not two) and from the woman-
        // shrugging case (no variation selector).
        string input = UnicodeExamples.WomanOfficeWorkerGrapheme;

        Assert.That(And(AnyToken(), Eof()).Parse(input).Success, Is.True);

        Assert.That(And(Token(UnicodeExamples.WomanOfficeWorkerGrapheme), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Keycap_sequence_is_one_token()
    {
        // 1 + VS16 + COMBINING ENCLOSING KEYCAP. Canonical keycap
        // sequence: an ASCII digit followed by the emoji variation
        // selector to pick emoji presentation and the combining
        // enclosing keycap to wrap a box around it. UAX #29 GB9 / GB9a
        // glue the two Extend characters to the digit base, so the
        // whole three-rune sequence is one cluster.
        string input = UnicodeExamples.DigitOneKeycapGrapheme;

        Assert.That(And(AnyToken(), Eof()).Parse(input).Success, Is.True);

        Assert.That(And(Token(UnicodeExamples.DigitOneKeycapGrapheme), Eof()).Parse(input).Success, Is.True);
    }

    [Test]
    public void Multiple_variation_selectors_stacked_form_one_token()
    {
        // Letter 'a' followed by two emoji variation selectors. The chars
        // are all valid Unicode scalars and the result is a well-formed
        // string. UAX #29 classifies variation selectors as Extend, and
        // consecutive Extend characters all attach to the preceding base,
        // so 'a' + VS + VS is one cluster.
        //
        // Stacking two unrelated variation selectors after one base isn't
        // a registered variation sequence (UTS #37 defines those), but
        // that's a glyph-rendering concern, not an encoding one. The
        // string is valid. The lexer follows UAX #29 and hands back one
        // multi-rune token.
        string input = "a" + UnicodeExamples.EmojiVariationSelectorText + UnicodeExamples.EmojiVariationSelectorText;

        // AnyToken consumes the whole cluster.
        Assert.That(And(AnyToken(), Eof()).Parse(input).Success, Is.True);

        // Targeting the multi-rune cluster as a single literal via
        // Token(string) also works.
        Assert.That(And(Token("a" + UnicodeExamples.EmojiVariationSelectorText + UnicodeExamples.EmojiVariationSelectorText), Eof()).Parse(input).Success, Is.True);
    }
}
