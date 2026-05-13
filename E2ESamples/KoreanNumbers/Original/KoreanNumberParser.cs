// Original Korean-number parser. Hand-written C# port of
// korean-numbers.js (https://github.com/ohgyun/korean-numbers, MIT
// license). Mirrors the upstream behavior: regex-validate the input
// against the allowed character set, then walk it left-to-right with
// a digit buffer and a running total, applying medium-scale and
// big-scale multipliers as they appear.
//
// The upstream library's public surface is three functions:
//
//   parse(text)       -> integer       (returns 0 on garbage)
//   parseMoney(text)  -> integer       (strips one trailing '원' then parse)
//   parseCount(text)  -> integer       (lookup table for native Korean
//                                       count words: 하나, 둘, 셋 ...)
//
// This port keeps the same three entry points, the same lookup tables,
// and the same return-0-on-garbage behavior so the InductorParser
// rewrite under ../Rewrite/ can be compared apples-to-apples.
//
// Things the original parser does NOT do, which the rewrite adds:
//
//   - Position info on failure (returns 0, no message, no char index).
//   - A walkable AST (the upstream caller only ever sees the final number).
//   - Distinguishing "empty input" from "input that legitimately parses
//     to zero." Both return 0.
//
// License: MIT (see ../README.md for attribution and the upstream link).

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KoreanNumbersSample.Original;

public static class OriginalKoreanNumberParser
{
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

    private const string MoneySuffix = "원";

    // Mirrors rKoreanNumbers in korean-numbers.js. Anything outside this
    // class falls through to int.TryParse (digits-only) or returns 0.
    private static readonly Regex KoreanNumberShape =
        new("^[일이삼사오육칠팔구십백천만억0-9]+$", RegexOptions.Compiled);

    public static long Parse(string? text)
    {
        text = EnsureText(text);

        if (!KoreanNumberShape.IsMatch(text))
        {
            return long.TryParse(text, out var parsed) ? parsed : 0;
        }

        long total = 0;
        var buffer = new Stack<int>();

        foreach (char value in text)
        {
            if (BigUnitMap.TryGetValue(value, out long bigScale))
            {
                long buffered = FlushBuffer(buffer);
                if (total != 0)
                {
                    total = total * bigScale + buffered * bigScale;
                }
                else
                {
                    long left = buffered == 0 ? 1 : buffered;
                    total = left * bigScale;
                }
            }
            else if (MediumUnitMap.TryGetValue(value, out long mediumScale))
            {
                long buffered = FlushBuffer(buffer);
                long left = buffered == 0 ? 1 : buffered;
                total += left * mediumScale;
            }
            else if (value >= '0' && value <= '9')
            {
                buffer.Push(value - '0');
            }
            else if (SmallUnitMap.TryGetValue(value, out int smallValue))
            {
                buffer.Push(smallValue);
            }
            // Validation above guarantees no other branches are reached.
        }

        total += FlushBuffer(buffer);
        return total;
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

    private static string EnsureText(string? text) => (text ?? string.Empty).Trim();

    private static long FlushBuffer(Stack<int> buffer)
    {
        long buffered = 0;
        int placeIndex = 0;
        while (buffer.Count > 0)
        {
            buffered += buffer.Pop() * Pow10(placeIndex++);
        }
        return buffered;
    }

    private static long Pow10(int exponent)
    {
        long result = 1;
        for (int index = 0; index < exponent; index++) result *= 10;
        return result;
    }
}
