using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches any single token, regardless of content. Fails only at EOF.
// Under RuneLexer each token is one rune, so AnyChar() matches any rune.
// Under GraphemeLexer each token is one grapheme cluster (possibly
// multi-rune), so AnyChar() matches any grapheme including 👨‍👩‍👧‍👦.
//
// This is the "match one character, whatever it is" primitive. Its
// companion idiom is ZeroOrMore(And(Not(stopRule), AnyChar())), which
// sweeps up content until wherever stopRule would fire. See the
// "Stopping at a Multi-Character Terminator" section of Recipes.md.
internal sealed class AnyCharRule : Rule
{
    public AnyCharRule() : base(FlattenType.None) { }

    internal override Symbol? TryParseRule(Lexer lexer)
    {
        using var transaction = lexer.BeginTransaction();
        var token = lexer.Read();
        if (token.IsEof)
        {
            TraceFailure(lexer, $"found '<EOF>'");
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}'");
        transaction.Commit();
        // Same leaf-id convention as RuneInRule / RuneNotInRule: a single-
        // rune token carries the rune value as its id; a multi-rune token
        // falls through to the rule's Compile-assigned id.
        int runeValue = token.RuneValue;
        SymbolId leafId = runeValue >= 0 ? new SymbolId(runeValue) : Id;
        return new Symbol(leafId, FlattenType, token.Memory);
    }
}
