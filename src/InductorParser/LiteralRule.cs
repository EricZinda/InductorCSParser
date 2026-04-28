using System;
using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches an exact multi-character string in a single transaction. This is
// the N-character generalization of TokenRule. Where TokenRule's expected is
// one grapheme, LiteralRule's expected is any non-empty string.
//
// This is better than using And(Token('m'), Token('a'), Token('j')) since each
// Token opens its own transaction. A three-character And(Token, Token, Token)
// does three BeginTransaction/Commit cycles and three RecordFailure slots.
// Literal("maj") does one. For keyword-heavy grammars (chord notation, SQL
// keywords, HTTP methods) this is the difference between per-keyword O(N)
// transaction overhead and O(1).
//
// The match loop is the same lockstep pattern TokenRule uses: read a token,
// compare its Chars span against the matching section of the expected string,
// advance by token.Length. Under GraphemeLexer each iteration consumes
// exactly one grapheme worth of chars. Under RuneLexer each iteration
// consumes exactly one rune. Same loop, both lexers, because SequenceEqual
// only cares about the underlying chars lining up, not how the lexer chose
// to group them.
//
// Error position on mismatch is the pre-read offset of the specific failing
// token, not the start of the whole attempt. Same as TokenRule: this is what
// "points-at-the-offender" means in a multi-token lockstep match.
//
// Default FlattenType is Delete, matching TokenRule. The common case for a
// literal is a keyword or delimiter the grammar wants to assert is present
// but doesn't need to materialize in the output tree.
internal sealed class LiteralRule : Rule
{
    private readonly string _expected;

    public LiteralRule(string expected) : base(FlattenType.Delete)
    {
        if (expected == null)
            throw new ArgumentNullException(nameof(expected));
        if (expected.Length == 0)
            throw new ArgumentException("Literal requires a non-empty string.", nameof(expected));
        _expected = expected;
    }

    // Accessor for the state-machine evaluator's lowering pass
    // (StateMachine/Lowerer.cs). The original TryParseRule reads
    // _expected directly; the lowerer needs the same data without
    // running the rule.
    internal string LoweringExpected => _expected;

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        int consumed = 0;

        while (consumed < _expected.Length)
        {
            int tokenStart = lexer.Position;
            var token = lexer.Read();
            if (token.IsEof)
            {
                TraceFailure(lexer, $"found '<EOF>', wanted '{_expected}'");
                lexer.RecordFailure(tokenStart, ErrorMessage);
                return null;
            }
            if (consumed + token.Length > _expected.Length
                || !token.Chars.SequenceEqual(_expected.AsSpan(consumed, token.Length)))
            {
                TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted '{_expected}'");
                lexer.RecordFailure(tokenStart, ErrorMessage);
                return null;
            }
            consumed += token.Length;
        }

        TraceSuccess(lexer, $"found '{_expected}'");
        transaction.Commit();
        // Default FlattenType is Delete: the common case collapses to
        // the shared Discarded value and skips the per-match Symbol allocation.
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        var leafSymbol = new Symbol(Id, FlattenType, lexer.Input.AsMemory(transaction.StartPosition, consumed));
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (RuneSet.Empty when Advance.Never. RuneSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // The only rune that can start a match of this literal is the first
        // rune of the expected string. Advance is Always because a literal
        // always consumes at least one rune to match.
        //
        // TryPeekRune decodes the first rune correctly even when that rune
        // takes two chars in the C# string (emoji, many CJK). Reading
        // _expected[0] directly would return only the first char, which
        // isn't a usable rune on its own.
        Lexer.TryPeekRune(_expected, 0, out int first, out _);
        return new RuleStartRequirements(RuneSet.Single(first), Advance.Always);
    }
}
