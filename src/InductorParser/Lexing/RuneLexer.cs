namespace InductorParser.Lexing;

// One token per Unicode code point (rune). Surrogate pairs are coalesced
// into a single 2-char token; everything else is one char.
public sealed class RuneLexer : Lexer
{
    public RuneLexer(string input) : base(input) { }

    protected override int NextTokenLength(int startOffset)
    {
        char c = Input[startOffset];
        if (char.IsHighSurrogate(c)
            && startOffset + 1 < Input.Length
            && char.IsLowSurrogate(Input[startOffset + 1]))
        {
            return 2;
        }
        return 1;
    }
}
