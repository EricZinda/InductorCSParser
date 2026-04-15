namespace InductorParser;

public sealed class ParseOptions
{
    // Atomic unit the lexer reads. Default matches the architecture doc:
    // grapheme so user-typed text behaves the way users expect, even though
    // the underlying StringInfo implementation has known gaps on pre-.NET 5
    // runtimes (see GraphemeLexer.cs and backlog/i001).
    public InputUnit InputUnit { get; set; } = InputUnit.Grapheme;
}
