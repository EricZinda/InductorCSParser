using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;

namespace InductorParser.Tests;

// Sibling to MalformedUnicodeTests. Where that file probes inputs that
// are encoding-broken or sit outside UAX #29's normal model, this file
// covers Unicode inputs that are valid and well-formed but unusual
// enough that a reader might wonder if the parser handles them. The
// tests lock in that the parser follows UAX #29 / UAX #15 to the letter
// on these inputs and doesn't mis-cluster, mis-normalize, or crash.
[TestFixture]
public class ValidUnicodeTests
{
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
        // string is valid; the lexer follows UAX #29 and hands back one
        // multi-rune token.
        string input = "a" + UnicodeExamples.EmojiVariationSelectorText + UnicodeExamples.EmojiVariationSelectorText;

        // AnyToken consumes the whole cluster.
        Assert.That(AllOf(AnyToken(), Eof()).Parse(input).Success, Is.True);

        // Targeting the multi-rune cluster as a single literal via
        // Token(string) also works.
        Assert.That(AllOf(Token("a" + UnicodeExamples.EmojiVariationSelectorText + UnicodeExamples.EmojiVariationSelectorText), Eof()).Parse(input).Success, Is.True);
    }
}
