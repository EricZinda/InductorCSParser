using System;

namespace InductorParser.Lexing;

// The Inductor parser backtracks, so the same cursor gets visited many times
// as alternatives are tried and abandoned. If every lexer.Read() allocated a
// new string for the matched text, most of those strings would be thrown away
// within microseconds, when the matching rule fails and the parser tries the
// next alternative. Token avoids that by pointing into the original input
// instead of copying out of it.
//
// Token is a `readonly ref struct`:
//
//   * `struct` (value type) is what keeps Token off the heap. A Token lives
//     on the stack or inline in whatever holds it, and returning one from a
//     method copies its fields into the caller's storage rather than
//     allocating. The input string is the only heap object in the picture.
//     Every Token just stores an 8-byte pointer to it plus a few ints.
//
//   * `readonly` means the Token's fields never change after construction.
//     That lets the compiler avoid defensive copies when passing a Token
//     around, and gives callers a guarantee that nothing will mutate the
//     Token under them.
//
//   * `ref` makes Token stack-only: the compiler won't let a ref struct be
//     boxed or stored on the heap (a class field, a List, a Dictionary, a
//     lambda capture). That restriction is what buys us the stored Chars
//     span. A ReadOnlySpan<char> is itself a ref struct, so it can only be a
//     field, or a get-only auto-property the compiler backs with one, inside
//     another ref struct (error CS8345). A non-ref Token couldn't store the
//     span at all. It would have to recompute Source.AsSpan(Offset, Length)
//     on every Chars access.

/// <summary>
/// One chunk of input the lexer just consumed, or the EOF token at the end
/// of the input. In the default grapheme mode a token is one Unicode
/// grapheme (i.e. one <a href="https://www.unicode.org/reports/tr29/">UAX #29</a> grapheme cluster, possibly several runes
/// wide). In the one-rune-per-token sub-lexer mode (<see cref="InductorParser.Rules.WithinToken(InductorParser.Rule)">Rules.WithinToken</see>) it's one
/// rune, which can be a fragment of a cluster, and under Compile(null) it
/// can be a lone surrogate. Rather than copying the matched text into a new
/// string, a Token keeps a reference to the original input plus an offset and
/// a length.
/// </summary>
/// <remarks>
/// Not to be confused with the <see cref="InductorParser.Rules.Token(char)">Rules.Token</see> factory in
/// <see cref="InductorParser.Rules"/>, which constructs a rule that matches
/// one Token from the input. 
/// </remarks>
public readonly ref struct Token
{
    /// <summary>The input string this token points into, shared with every
    /// other token from the same parse.</summary>
    public string Source { get; }

    /// <summary>Index into <see cref="Source">Token.Source</see> where this token's text
    /// begins.</summary>
    public int Offset { get; }

    /// <summary>Number of C# chars (UTF-16 code units) this token spans. A single
    /// grapheme cluster can be several.</summary>
    public int Length { get; }

    /// <summary>True for the token the lexer returns at
    /// end-of-input. That token has an empty <see cref="Chars">Token.Chars</see> and a
    /// <see cref="RuneValue">Token.RuneValue</see> of -1.</summary>
    public bool IsEof { get; }

    /// <summary>
    /// A span over <see cref="Source">Token.Source</see> covering this token's text, without
    /// allocating. Empty for the end-of-input token.
    /// </summary>
    /// <remarks>
    /// Comparison rules like Literal("function") or Token('=') precompute
    /// their expected sequence and call SequenceEqual against this span, so
    /// the matching loop never allocates a string.
    /// </remarks>
    public ReadOnlySpan<char> Chars { get; }

    /// <summary>
    /// A <see cref="ReadOnlyMemory{T}"/> over this token's text. Unlike
    /// <see cref="Chars">Token.Chars</see>, Memory is heap-safe: it can be stored on a class,
    /// in a dictionary, or across an await, where a span can't go. Empty for
    /// the end-of-input token.
    /// </summary>
    /// <remarks>
    /// Leaf Symbols hold their matched text as Memory so they don't copy it
    /// into a string until someone calls ToString().
    /// </remarks>
    public ReadOnlyMemory<char> Memory =>
        IsEof ? ReadOnlyMemory<char>.Empty : Source.AsMemory(Offset, Length);

    /// <summary>
    /// The Unicode scalar value when this token is exactly one rune, or -1
    /// otherwise. End-of-input, empty, multi-rune tokens (the family
    /// emoji 👨‍👩‍👧‍👦, for example), and lone-surrogate tokens (a stray isn't a
    /// scalar value) all return -1, so single-rune tests like
    /// GraphemeRule and OneOfRule fail correctly without each caller having
    /// to special-case the multi-rune path.
    /// </summary>
    /// <remarks>
    /// Returned as int rather than <see cref="System.Text.Rune">System.Text.Rune</see> because -1 is the "no
    /// single rune here" marker, and Rune has no invalid state.
    /// </remarks>
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
            if (Length == 2 && RuneHelpers.IsSurrogatePairAt(Source, Offset))
            {
                return char.ConvertToUtf32(Source[Offset], Source[Offset + 1]);
            }
            return -1;
        }
    }

    /// <summary>
    /// Creates a token spanning <paramref name="length"/> chars of
    /// <paramref name="source"/> starting at <paramref name="offset"/>. When
    /// <paramref name="isEof"/> is true the token is the flag-only
    /// end-of-input token and its span is empty.
    /// </summary>
    public Token(string source, int offset, int length, bool isEof)
    {
        Source = source;
        Offset = offset;
        Length = length;
        IsEof = isEof;
        Chars = isEof ? ReadOnlySpan<char>.Empty : source.AsSpan(offset, length);
    }
}
