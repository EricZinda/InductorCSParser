using System;

namespace InductorParser.Lexing;

// A Token is one chunk of input the lexer just consumed: one scalar-value
// token (Rune) under RuneLexer for well-formed UTF-16 input, one .NET text element (Grapheme)
// under GraphemeLexer, or a flag-only token at end-of-input. Stray surrogate
// halves under RuneLexer surface as one-code-unit tokens with no RuneValue.
// Instead of copying the matched text into a new
// string, a Token keeps a reference to the original input plus an offset
// and a length.
//
// This matters because PEG parsers backtrack. The
// same cursor gets visited many times as alternatives are tried and
// abandoned. If every lexer.Read() allocated a new string for the matched
// text, most of those strings would be thrown away within microseconds,
// when the matching rule fails and the parser tries the next alternative.
//
// Token is a `readonly ref struct`:
//
//   * `struct` (value type) is what keeps Token off the heap. A Token
//     lives on the stack or inline in whatever holds it, and returning
//     one from a method copies its fields into the caller's storage
//     rather than allocating. The input string is the only heap object
//     in the picture. Every Token just carries an 8-byte pointer to it
//     plus a few ints.
//
//   * `readonly` means the Token's fields never change after
//     construction. That lets the compiler avoid defensive copies when
//     passing a Token around, and gives callers a guarantee that
//     nothing will mutate the Token under them.
//
//   * `ref` is the language-enforced lifetime guarantee. A ref struct
//     is stack-only. The compiler forbids storing it in a
//     class field, a List, a Dictionary, a lambda capture, etc, 
//     anywhere the span inside it could outlive the
//     input string. That's why Chars can be a direct field (below)
//     rather than a property that reconstructs the span on each access.
public readonly ref struct Token
{
    public string Source { get; }
    public int Offset { get; }
    public int Length { get; }
    public bool IsEof { get; }

    // Chars is a ReadOnlySpan<char> over the source input. Spans don't
    // allocate. They're (pointer, length) structs that live on the
    // stack, pointing into the original string. Comparison rules like
    // Literal("function") or Grapheme('=') precompute their expected sequence
    // at construction time and at match time call SequenceEqual on the
    // spans. No string allocation anywhere in the matching loop.
    //
    // Storing this as a field (rather than a property that calls AsSpan
    // on each access) is only legal because Token is a ref struct. A
    // regular struct can't carry a Span field because the compiler
    // can't guarantee the struct stays on the stack.
    public ReadOnlySpan<char> Chars { get; }

    public Token(string source, int offset, int length, bool isEof)
    {
        Source = source;
        Offset = offset;
        Length = length;
        IsEof = isEof;
        Chars = isEof ? ReadOnlySpan<char>.Empty : source.AsSpan(offset, length);
    }

    // Memory returns a ReadOnlyMemory<char> which is the heap-safe version
    // of Span: it's a regular struct (not a ref struct), so it can be
    // stored on classes, dictionaries, async state machines, places where
    // Span can't go. Internally it has three things we need:
    // source string reference, offset, length.
    // Constructing one is a handful of field writes, and it doesn't
    // allocate. Leaf Symbols use Memory to hold onto the matched
    // text without copying it until
    // someone actually calls ToString() on them.
    public ReadOnlyMemory<char> Memory =>
        IsEof ? ReadOnlyMemory<char>.Empty : Source.AsMemory(Offset, Length);

    // RuneValue returns the rune value when the token is exactly one rune,
    // or -1 otherwise. EOF returns -1. Multi-rune grapheme tokens (the
    // family emoji 👨‍👩‍👧‍👦 under GraphemeLexer, for example) also return
    // -1, so single-rune tests like GraphemeRule and OneOfRule fail correctly
    // without each caller having to special-case the multi-rune path.
    // Returned as int rather than System.Text.Rune because -1 is the "no
    // single rune here" marker, and Rune has no invalid state.
    public int RuneValue
    {
        get
        {
            if (IsEof || Length == 0) return -1;
            if (Length == 1)
            {
                char c = Source[Offset];
                return char.IsSurrogate(c) ? -1 : c;
            }
            if (Length == 2
                && char.IsHighSurrogate(Source[Offset])
                && char.IsLowSurrogate(Source[Offset + 1]))
            {
                return char.ConvertToUtf32(Source[Offset], Source[Offset + 1]);
            }
            return -1;
        }
    }
}
