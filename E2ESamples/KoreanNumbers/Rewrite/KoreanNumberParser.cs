// Korean-number parser using InductorParser, vs ../Original/KoreanNumberParser.cs:
//
//                       Original (line scan)            Rewrite (this file)
//   Failure mode        return 0, no message            FormatException + ParseError
//   Failure position    none                            char index + line/column
//   Public surface      Parse / ParseMoney / ParseCount Parse / TryParse / ParseMoney / ParseCount
//
// What the rewrite adds:
//
// 1. Positioned errors. The original returns 0 for "백a", "1+2", "", "원",
//    or anything else that fails the regex check. The rewrite reports
//    line / column / char index and a message naming what the grammar
//    expected.
// 2. TryParse so callers who want to distinguish "0 because the input
//    said zero" from "0 because the input was garbage" can.
// 3. parseMoney trims one trailing 원 before parsing. parseCount is
//    table lookup, same as upstream.
//
// The numeric evaluation matches the upstream algorithm exactly so
// the side-by-side tests under ../Tests/ pass on every upstream
// test case.
//
// Downstream tools that want to highlight individual scale sections
// in the input (a number-input UI, a breakdown view) can walk
// result.Tree from KoreanNumber.Parse directly: each scaledTerm and
// digits node already carries SourceText and SourceRange. The Symbol
// tree IS the AST. We don't materialize a typed-record layer because
// nothing in this sample uses one, and the typed layer is a 30-line
// job to add later if a real consumer appears.

using System;
using System.Collections.Generic;
using InductorParser;
using static KoreanNumbersSample.Rewrite.KoreanNumberGrammar;

namespace KoreanNumbersSample.Rewrite;

public sealed record KoreanNumberParseError(string Message, int CharIndex, int Line, int Column)
{
    public override string ToString() =>
        $"line {Line + 1}, column {Column + 1}: {Message}";
}

public static class KoreanNumberParser
{
    private const string MoneySuffix = "원";

    private static readonly Dictionary<char, int> SmallUnitMap = new()
    {
        ['일'] = 1, ['이'] = 2, ['삼'] = 3, ['사'] = 4, ['오'] = 5,
        ['육'] = 6, ['칠'] = 7, ['팔'] = 8, ['구'] = 9,
    };

    private static readonly Dictionary<char, long> MediumUnitMap = new()
    {
        ['십'] = 10, ['백'] = 100, ['천'] = 1000,
    };

    private static readonly Dictionary<char, long> BigUnitMap = new()
    {
        ['만'] = 10_000L, ['억'] = 100_000_000L,
    };

    private static readonly Dictionary<string, int> CountMap = new()
    {
        ["하나"] = 1, ["둘"] = 2, ["셋"] = 3, ["넷"] = 4, ["다섯"] = 5,
        ["여섯"] = 6, ["일곱"] = 7, ["여덟"] = 8, ["아홉"] = 9, ["열"] = 10,
        ["열하나"] = 11, ["열둘"] = 12, ["열셋"] = 13, ["열넷"] = 14, ["열다섯"] = 15,
        ["열여섯"] = 16, ["열일곱"] = 17, ["열여덟"] = 18, ["열아홉"] = 19, ["스물"] = 20,
        ["일"] = 1, ["이"] = 2, ["삼"] = 3, ["사"] = 4, ["오"] = 5,
        ["육"] = 6, ["칠"] = 7, ["팔"] = 8, ["구"] = 9, ["십"] = 10,
        ["십일"] = 11, ["십이"] = 12, ["십삼"] = 13, ["십사"] = 14, ["십오"] = 15,
        ["십육"] = 16, ["십칠"] = 17, ["십팔"] = 18, ["십구"] = 19, ["이십"] = 20,
        ["한명"] = 1, ["두명"] = 2, ["세명"] = 3, ["네명"] = 4, ["다섯명"] = 5,
        ["여섯명"] = 6, ["일곱명"] = 7, ["여덟명"] = 8, ["아홉명"] = 9, ["열명"] = 10,
        ["열한명"] = 11, ["열두명"] = 12, ["열세명"] = 13, ["열네명"] = 14, ["열다섯명"] = 15,
        ["열여섯명"] = 16, ["열일곱명"] = 17, ["열여덟명"] = 18, ["열아홉명"] = 19, ["스무명"] = 20,
    };

    public static long Parse(string? text)
    {
        if (!TryParse(text, out long value, out var error))
            throw new FormatException(error!.ToString());
        return value;
    }

    public static long ParseMoney(string? text)
    {
        text = EnsureText(text);
        if (text.Length > 0 && text.EndsWith(MoneySuffix))
            text = text.Substring(0, text.Length - MoneySuffix.Length);
        return Parse(text);
    }

    public static int ParseCount(string? text)
    {
        text = EnsureText(text);
        return CountMap.TryGetValue(text, out var value) ? value : 0;
    }

    public static bool TryParse(string? text, out long value, out KoreanNumberParseError? error)
    {
        value = 0;
        error = null;
        var trimmed = EnsureText(text);

        var result = KoreanNumber.Parse(trimmed);
        if (!result.Success)
        {
            error = new KoreanNumberParseError(
                result.ErrorMessage,
                result.ErrorCharIndex,
                result.ErrorLine,
                result.ErrorColumn);
            return false;
        }

        long total = 0;
        foreach (var child in result.Tree!.Children)
        {
            if (child.Is(ScaledTerm))
            {
                var digitsNode = child.Find(Digits);
                var scaleNode = child.Find(Scale)!;
                long digitsValue = ParseDigitsValue(digitsNode?.SourceText ?? string.Empty);
                total = ApplyScaledTerm(total, digitsValue, scaleNode.SourceText[0]);
            }
            else if (child.Is(Digits))
            {
                // Top-level Digits = bare-digit term. Any digits that
                // belong to a scale show up inside a ScaledTerm wrapper.
                total += ParseDigitsValue(child.SourceText);
            }
        }

        value = total;
        return true;
    }

    // Apply a ScaledTerm to the running total, matching the upstream
    // algorithm. Big units (만, 억) act as group separators: the running
    // total scales up by the unit, and the digit prefix of this term
    // also scales up. Medium units (십, 백, 천) just add their (digits
    // or implicit 1) * scale.
    private static long ApplyScaledTerm(long currentTotal, long digitsValue, char scaleChar)
    {
        if (BigUnitMap.TryGetValue(scaleChar, out long bigScale))
        {
            if (currentTotal != 0)
                return currentTotal * bigScale + digitsValue * bigScale;
            long left = digitsValue == 0 ? 1 : digitsValue;
            return left * bigScale;
        }

        long medScale = MediumUnitMap[scaleChar];
        long leftMedium = digitsValue == 0 ? 1 : digitsValue;
        return currentTotal + leftMedium * medScale;
    }

    private static long ParseDigitsValue(string digitsText)
    {
        long value = 0;
        foreach (char ch in digitsText)
        {
            int digit = (ch >= '0' && ch <= '9') ? ch - '0' : SmallUnitMap[ch];
            value = value * 10 + digit;
        }
        return value;
    }

    private static string EnsureText(string? text) => (text ?? string.Empty).Trim();
}
