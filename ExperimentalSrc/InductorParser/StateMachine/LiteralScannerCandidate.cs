using System;
using InductorParser.Lexing;

namespace InductorParser.StateMachine;

// Pre-built candidate the StateMachine's scanner-skip can hand to
// Lexer.AdvanceUntilLiteralCandidateIn. Carries the expected literal
// text, an ASCII-case-insensitivity flag, and the cached first-rune so
// the scanner's IndexOf jump can be checked against the lexer's
// peeked rune before paying for a full substring compare.
//
// Lives in the StateMachine assembly because no recursive-evaluator
// code path constructs or uses one. The Lowerer collects these during
// compile, and Stepper hands them to the Lexer's
// AdvanceUntilLiteralCandidateIn extension method (also in this
// project) at scanner-skip time.
internal readonly struct LiteralScannerCandidate
{
    private readonly int _firstRune;

    public LiteralScannerCandidate(string text, bool ignoreAsciiCase)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        IgnoreAsciiCase = ignoreAsciiCase;
        Lexer.TryPeekRune(text, 0, out _firstRune, out _);
    }

    public string Text { get; }
    public bool IgnoreAsciiCase { get; }

    public int IndexIn(string input, int position, int endPosition)
    {
        int count = endPosition - position;
        if (count < Text.Length)
            return -1;

        // Use the BCL's optimized substring search to hop across whole
        // spans of non-candidates. OrdinalIgnoreCase is broader than this
        // parser's ASCII-only ignore-case rule for some Unicode text, so
        // callers still confirm with MatchesAt before stopping. Broader
        // prefilter candidates are safe: they may cause extra parser work,
        // but they never skip a real ASCII-ignore-case match.
        return input.IndexOf(
            Text,
            position,
            count,
            IgnoreAsciiCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public bool CanStartWith(int runeValue)
    {
        if (runeValue == _firstRune)
            return true;

        // LiteralIgnoreAsciiCase is intentionally ASCII-only. Keep the
        // scanner prefilter under the exact same rule: only A-Z/a-z match
        // case-insensitively, and every other rune has to match by exact code
        // point. This prevents the optimization from accepting full-Unicode
        // case-insensitive candidates that the real rule would reject.
        return IgnoreAsciiCase
            && _firstRune >= 0
            && _firstRune <= char.MaxValue
            && runeValue >= 0
            && runeValue <= char.MaxValue
            && IsAsciiLetter((char)_firstRune)
            && IsAsciiLetter((char)runeValue)
            && (_firstRune | 0x20) == (runeValue | 0x20);
    }

    public bool MatchesAt(string input, int position, int endPosition)
    {
        if (position + Text.Length > endPosition)
            return false;

        ReadOnlySpan<char> actual = input.AsSpan(position, Text.Length);
        ReadOnlySpan<char> expected = Text.AsSpan();
        if (!IgnoreAsciiCase)
            return actual.SequenceEqual(expected);

        for (int index = 0; index < expected.Length; index++)
        {
            char ca = actual[index];
            char cb = expected[index];
            if (ca == cb) continue;
            if (IsAsciiLetter(ca) && IsAsciiLetter(cb) && (ca | 0x20) == (cb | 0x20)) continue;
            return false;
        }
        return true;
    }

    private static bool IsAsciiLetter(char c) =>
        (uint)((c | 0x20) - 'a') <= ('z' - 'a');
}
