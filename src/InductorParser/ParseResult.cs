using System.Globalization;
using InductorParser.SyntaxTree;

namespace InductorParser;

public readonly struct ParseResult
{
    // Reference to the original input string, kept so the derived error-
    // position properties (ErrorLine, ErrorColumn, ErrorRuneIndex,
    // ErrorGraphemeIndex) can scan it on demand. The input is already
    // rooted by the caller so this adds no GC pressure; the cost is one
    // extra reference field on the struct.
    private readonly string? _input;

    public ParseOutcome Outcome { get; }
    public Symbol? Tree { get; }
    public string ErrorMessage { get; }

    // UTF-16 char index into the original input where the deepest failure
    // was recorded. Valid range is [0, input.Length] inclusive:
    //   * 0 points at the very first character of the input.
    //   * input.Length points one past the last character (at end-of-input).
    //   * Intermediate values point at specific characters.
    // Always 0 on a successful parse. Use with input[ErrorCharIndex] to
    // recover the offending character, after checking that the value is
    // less than input.Length (values equal to input.Length indicate EOF).
    //
    // For budget aborts (Timeout / WorkLimitExceeded / DepthLimitExceeded /
    // Canceled) this carries the lexer position at the moment the budget
    // tripped, which gives callers a coarse "how far did the parser get"
    // hint useful for diagnostics.
    public int ErrorCharIndex { get; }

    // 0-based line number containing ErrorCharIndex, matching LSP
    // semantics end-to-end so callers forwarding parser errors into an
    // LSP diagnostic don't need to shift the value. Always 0 on a
    // successful parse. See docs/ProgrammingModel.md "LSP Position
    // Semantics" for why 0-based.
    //
    // Line terminators recognized are "\n", "\r", and "\r\n" (the last
    // treated atomically, matching LSP). An index that lands on the "\n"
    // half of a "\r\n" pair is reported on the prior line, since LSP
    // positions cannot fall inside a line terminator.
    //
    // Computed on each read by scanning _input[0..ErrorCharIndex] once.
    // No caching: readonly struct plus "you only pay if you read" means a
    // caller that reads this twice pays for two scans; single digit
    // microseconds on a megabyte of input, so cheap in absolute terms.
    public int ErrorLine
    {
        get
        {
            ComputeLineAndColumn(out int line, out _);
            return line;
        }
    }

    // 0-based column in UTF-16 chars on ErrorLine, matching LSP. Always
    // 0 on a successful parse.
    public int ErrorColumn
    {
        get
        {
            ComputeLineAndColumn(out _, out int column);
            return column;
        }
    }

    // Count of Unicode scalar values (runes) before ErrorCharIndex. A
    // surrogate pair counts as one rune; a lone surrogate half counts as
    // one rune (the same count produced by Rune.DecodeFromUtf16, which
    // surfaces lone surrogates as U+FFFD but still advances one char).
    //
    // Always 0 on a successful parse. For callers that track positions in
    // runes rather than UTF-16 chars (Go-style iteration, some REST APIs).
    public int ErrorRuneIndex
    {
        get
        {
            string input = _input ?? string.Empty;
            int limit = ErrorCharIndex;
            int count = 0;
            int i = 0;
            while (i < limit)
            {
                if (char.IsHighSurrogate(input[i])
                    && i + 1 < input.Length
                    && i + 1 < limit
                    && char.IsLowSurrogate(input[i + 1]))
                {
                    i += 2;
                }
                else
                {
                    i++;
                }
                count++;
            }
            return count;
        }
    }

    // Count of grapheme clusters before ErrorCharIndex, computed with the
    // same StringInfo.GetNextTextElement primitive GraphemeLexer uses so
    // the count agrees with what the lexer saw. Always 0 on a successful
    // parse. Inherits the legacy-runtime UAX#29 caveat documented on
    // GraphemeLexer: on pre-.NET 5 runtimes a few real grapheme clusters
    // split incorrectly. Upgrades automatically when the vendored UAX#29
    // implementation lands.
    public int ErrorGraphemeIndex
    {
        get
        {
            string input = _input ?? string.Empty;
            int limit = ErrorCharIndex;
            if (limit <= 0) return 0;
            int count = 0;
            int i = 0;
            while (i < limit)
            {
                string element = StringInfo.GetNextTextElement(input, i);
                int step = element.Length;
                if (step <= 0) step = 1;
                i += step;
                count++;
            }
            return count;
        }
    }

    public bool Success => Outcome == ParseOutcome.Success;

    private ParseResult(ParseOutcome outcome, Symbol? tree, string errorMessage, int errorCharIndex, string? input)
    {
        Outcome = outcome;
        Tree = tree;
        ErrorMessage = errorMessage;
        ErrorCharIndex = errorCharIndex;
        _input = input;
    }

    public static ParseResult Succeeded(Symbol tree, string input) =>
        new ParseResult(ParseOutcome.Success, tree, string.Empty, 0, input);

    public static ParseResult Failed(int errorCharIndex, string message, string input) =>
        new ParseResult(ParseOutcome.GrammarMismatch, null, message, errorCharIndex, input);

    // Parse aborted because a runtime budget tripped. The outcome
    // identifies which one (Timeout, WorkLimitExceeded, DepthLimitExceeded,
    // Canceled).
    public static ParseResult Aborted(ParseOutcome outcome, int errorCharIndex, string message, string input) =>
        new ParseResult(outcome, null, message, errorCharIndex, input);

    // Shared forward scan for ErrorLine and ErrorColumn. Counts line
    // terminators strictly before ErrorCharIndex:
    //   * "\n" is a break.
    //   * "\r" is a break iff not immediately followed by "\n".
    //   * "\r\n" is one break, attributed to the "\n" so that an index
    //     landing on the "\n" reports on the prior line (column = run
    //     length so far), and an index landing on the character after
    //     reports line N+1, column 1.
    private void ComputeLineAndColumn(out int line, out int column)
    {
        string input = _input ?? string.Empty;
        int limit = ErrorCharIndex;
        if (limit > input.Length) limit = input.Length;

        line = 0;
        int lineStart = 0;
        for (int i = 0; i < limit; i++)
        {
            char c = input[i];
            if (c == '\n')
            {
                line++;
                lineStart = i + 1;
            }
            else if (c == '\r' && (i + 1 >= input.Length || input[i + 1] != '\n'))
            {
                line++;
                lineStart = i + 1;
            }
        }
        column = limit - lineStart;
    }
}
