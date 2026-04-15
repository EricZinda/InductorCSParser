using InductorParser.SyntaxTree;

namespace InductorParser;

public readonly struct ParseResult
{
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
    public int ErrorCharIndex { get; }

    public bool Success => Outcome == ParseOutcome.Success;

    private ParseResult(ParseOutcome outcome, Symbol? tree, string errorMessage, int errorCharIndex)
    {
        Outcome = outcome;
        Tree = tree;
        ErrorMessage = errorMessage;
        ErrorCharIndex = errorCharIndex;
    }

    public static ParseResult Succeeded(Symbol tree) =>
        new ParseResult(ParseOutcome.Success, tree, string.Empty, 0);

    public static ParseResult Failed(int errorCharIndex, string message) =>
        new ParseResult(ParseOutcome.GrammarMismatch, null, message, errorCharIndex);
}
