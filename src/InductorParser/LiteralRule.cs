using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches an exact multi-character string in a single transaction. This is
// the N-character generalization of GraphemeRule. GraphemeRule stores exactly
// one token (one character as the user sees it) at construction time and
// matches it as one read. LiteralRule stores any non-empty string and uses
// the same lockstep compare loop.
//
// This is better than using And(Token('m'), Token('a'), Token('j')) since each
// Token rule runs under its own transaction. A three-character And of three
// Token rules pays three transaction cycles and three RecordFailure slots.
// Literal("maj") does one. For keyword-heavy grammars (chord notation, SQL
// keywords, HTTP methods) this is the difference between per-keyword O(N)
// transaction overhead and O(1).
//
// The match loop is the same lockstep pattern GraphemeRule uses: read a token,
// compare its Chars span against the matching section of the expected string,
// advance by token.Length. Each iteration consumes exactly one token's worth
// of chars. SequenceEqual only cares about the underlying chars lining up,
// not how the lexer chose to group them.
//
// Error position on mismatch is the pre-read offset of the specific failing
// token, not the start of the whole attempt. Same as GraphemeRule: this is what
// "points-at-the-offender" means in a multi-token lockstep match.
//
// Default FlattenType is Delete, matching GraphemeRule. The common case for a
// literal is a keyword or delimiter the grammar wants to assert is present
// but doesn't need to materialize in the output tree.
internal sealed class LiteralRule : Rule
{
    private string _expected;

    public LiteralRule(string expected) : base(FlattenType.Delete)
    {
        if (expected == null)
            throw new ArgumentNullException(nameof(expected));
        if (expected.Length == 0)
            throw new ArgumentException("Literal requires a non-empty string.", nameof(expected));
        _expected = expected;
    }

    internal override string? ExpectedText => _expected;

    internal override void CollectNormalizationOffenders(
        System.Text.NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
    {
        // See Rule.CollectNormalizationOffenders for the contract.
        // Literal accepts multi-grapheme conversions (the rule matches
        // multi-grapheme text by design), so just replace _expected with
        // the converted form.
        string? normalized = TryConvertToForm(this, _expected, form, offenders, failures);
        if (normalized == null) return;
        if (string.Equals(normalized, _expected, StringComparison.Ordinal)) return;
        _expected = normalized;
    }

    internal override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        int consumed = 0;

        while (consumed < _expected.Length)
        {
            int tokenStart = lexer.Position;
            var token = lexer.Read();
            if (token.IsEof)
            {
                TraceFailure(lexer, $"found '<EOF>', wanted '{_expected}'");
                lexer.RecordFailure(tokenStart, ErrorMessage, ErrorForced);
                return null;
            }
            if (consumed + token.Length > _expected.Length
                || !token.Chars.SequenceEqual(_expected.AsSpan(consumed, token.Length)))
            {
                TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted '{_expected}'");
                lexer.RecordFailure(tokenStart, ErrorMessage, ErrorForced);
                return null;
            }
            consumed += token.Length;
        }

        TraceSuccess(lexer, $"found '{_expected}'");
        // Default FlattenType is Delete: the common case collapses to
        // the shared Discarded value and skips the per-match Symbol allocation.
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        var leafSymbol = new Symbol(Id, FlattenType, lexer.Input.AsMemory(startPosition, consumed), lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

}
