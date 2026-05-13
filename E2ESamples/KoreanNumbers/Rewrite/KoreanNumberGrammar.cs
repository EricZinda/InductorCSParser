// Grammar for Korean number expressions, mirroring the algorithm in
// korean-numbers.js (https://github.com/ohgyun/korean-numbers, MIT
// license). The upstream library is regex-validate + linear-scan with
// a digit buffer; this grammar is a structural rewrite that produces
// the same numbers from the same inputs, plus a walkable parse tree
// and positioned errors.
//
//   <number>     ::= (<scaledTerm> | <bareDigits>)+ EOF
//   <scaledTerm> ::= <digits>? <scale>
//   <bareDigits> ::= <digits>
//   <digits>     ::= (ASCII 0-9 | 일|이|삼|사|오|육|칠|팔|구)+
//   <scale>      ::= 만 | 억 | 천 | 백 | 십
//
// Three things worth naming because the grammar has to match the
// upstream's permissive behavior:
//
// 1. The scale alternatives use Token (one grapheme each), not Literal.
//    Hangul syllables are single graphemes; Token is the right tool and
//    avoids the per-keyword multi-token-compare overhead Literal exists
//    to handle.
// 2. ScaledTerm orders before BareDigits in the Or. ScaledTerm consumes
//    "digits then scale" as one term, and BareDigits picks up a trailing
//    digit run like the "일" in "만일" (which is 10001 by the upstream
//    algorithm). Reversed, BareDigits would greedily eat the digits
//    before the scale and the parse would fail.
// 3. Whitespace is trimmed by the caller (Parse / ParseMoney), not by
//    the grammar. The upstream's ensureText strips leading and trailing
//    whitespace before the regex check; mid-string whitespace is invalid.
//    Keeping the trim in the public surface keeps the grammar focused
//    about what counts as a number token.

using InductorParser;
using static InductorParser.Rules;

namespace KoreanNumbersSample.Rewrite;

public static class KoreanNumberGrammar
{
    public static readonly Rule KoreanNumber;
    public static readonly Rule ScaledTerm;
    public static readonly Rule Digits;
    public static readonly Rule Scale;

    static KoreanNumberGrammar()
    {
        var hangulDigit = TokenSet.Runes("일이삼사오육칠팔구");
        var anyDigit = TokenSet.Ascii.Digits | hangulDigit;
        Digits = ScanWhile(anyDigit, minimumCount: 1)
            .As("digits").Preserve();

        // Scale alternatives in size-descending order to keep the
        // tree readable: a reader walking left-to-right sees 만 dominate
        // 천 dominate 백 dominate 십, which matches how Korean speakers
        // read these numbers aloud. The choice order doesn't affect
        // correctness (each scale is one disjoint Hangul syllable) but
        // does affect error-message wording when the parser names the
        // alternatives it tried.
        Scale = Or(
            Token('만'),
            Token('억'),
            Token('천'),
            Token('백'),
            Token('십')
        ).As("scale").Preserve()
         .WithError("expected one of: 만, 억, 천, 백, 십");

        ScaledTerm = And(Optional(Digits), Scale).As("scaledTerm").Preserve();

        // The top-level Or tries ScaledTerm first so a "digits then scale"
        // run is consumed as one term. If ScaledTerm fails (no scale
        // follows the digit run), Digits picks up the bare digit run as
        // a trailing-ones term. At the parse-tree top level a Digits
        // child always means a bare run, because any digits that
        // belong to a scale are nested inside a ScaledTerm wrapper.
        KoreanNumber = And(
            OneOrMore(Or(ScaledTerm, Digits))
                .WithError("expected a digit (0-9 or 일이삼사오육칠팔구) or a scale (만 억 천 백 십)"),
            Eof()
        ).As("koreanNumber").Preserve();

        KoreanNumber.Compile();
    }
}
