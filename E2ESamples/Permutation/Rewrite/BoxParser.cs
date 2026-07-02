// A box-attributes parser using InductorParser, vs ../Original/parsec_perm_example.hs:
//
//                     Original (parsec)        Rewrite (this file)
//   Any order         permute (...)            PermutationRule(...)
//   Failure position  parsec SourcePos         char index + line + column
//   Error message     "expected ..."           names what's still missing
//   AST               Box record               Box record + parse tree
//
// The grammar is thin glue, exactly as parsec's `box` is thin glue over
// `permute`: three "<name>=<digits>" attributes in any order, then
// end-of-input. The custom rule (../Rewrite/PermutationRule.cs) does the
// ordering work. Each attribute's digit run is a named node, so the
// projection reads width / height / depth by name no matter what order they
// were written in, which is the whole point of a permutation parser.

using System;
using System.Globalization;
using InductorParser;
using InductorParser.SyntaxTree;
using static InductorParser.Rules;

namespace PermutationSample.Rewrite;

public readonly record struct Box(int Width, int Height, int Depth);

public sealed record BoxError(string Message, int CharIndex, int Line, int Column)
{
    public override string ToString() =>
        $"line {Line + 1}, column {Column + 1}: {Message}";
}

public static class BoxParser
{
    // The findable value nodes: the digit run of each attribute, named so the
    // projection can locate it regardless of input order. Each is its own
    // instance (a factory, not one shared rule) because .As(...) is set-once
    // and mutates in place.
    public static readonly Rule Width = DigitRun("width");
    public static readonly Rule Height = DigitRun("height");
    public static readonly Rule Depth = DigitRun("depth");

    public static readonly Rule Box;

    static BoxParser()
    {
        // An attribute is optional leading whitespace, the literal name, '=',
        // then the named digit run. The name literal and the '=' are Delete by
        // default (Literal and Token both default to Delete), so only the
        // named digit run reaches the tree. The And is Flatten, so that digit
        // node bubbles up into the permutation's child list.
        Rule Attribute(string name, Rule valueNode) =>
            And(Optional(InlineWhitespace()), Literal(name), Token('='), valueNode)
                .WithError($"expected the '{name}' attribute");

        var permutation = new PermutationRule(
            Attribute("width", Width),
            Attribute("height", Height),
            Attribute("depth", Depth)
        ).WithError("expected width, height, and depth, each exactly once");

        Box = And(
            permutation,
            Optional(InlineWhitespace()),
            Eof().WithError("unexpected text after the box attributes")
        ).As("box");

        Box.Compile();
    }

    private static Rule DigitRun(string name) =>
        OneOrMore(OneOf(TokenSet.Ascii.Digits)).As(name);

    public static Box Parse(string input)
    {
        if (!TryParse(input, out var box, out var error))
            throw new FormatException(error!.ToString());
        return box;
    }

    public static bool TryParse(string input, out Box box, out BoxError? error)
    {
        box = default;
        // BoxError surfaces position through its Line / Column fields, so the
        // message text stays position-less.
        var options = new ParseOptions
        {
            WithErrorTemplate = "{message}",
            PositionalErrorTemplate = "unexpected '{character}'.",
            EndOfInputErrorTemplate = "unexpected end of input.",
        };
        var result = BoxParser.Box.Parse(input, options);
        if (!result.Success)
        {
            error = new BoxError(
                result.ErrorMessage, result.ErrorCharIndex, result.ErrorLine, result.ErrorCharColumn);
            return false;
        }

        var tree = result.Tree!;
        box = new Box(
            Number(tree.Find(Width)!),
            Number(tree.Find(Height)!),
            Number(tree.Find(Depth)!));
        error = null;
        return true;
    }

    private static int Number(Symbol valueNode) =>
        int.Parse(valueNode.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
}
