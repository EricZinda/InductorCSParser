// A single-rune leaf rule that matches if a delegate predicate accepts
// the rune's code point. The built-in `OneOf(TokenSet)` works against a
// static set known at grammar-build time; this is the consumer-side
// version for cases where the accepted set is determined at runtime
// (e.g. matching runes from the user's configured script).
//
// OneRuneRuleTests has the surrounding context: this is the leaf rule
// counterpart to FailRule for the external Rule-subclass surface.
// FailRule alone doesn't touch Lexer.Read, Symbol-construction, or the
// effectiveFlattenType branching that every leaf-with-success-path
// rule has to handle. This rule does.

using System;
using System.Collections.Generic;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser.ExternalContractTests;

public sealed class OneRuneRule : Rule
{
    private readonly Func<int, bool> _predicate;
    private readonly string _label;

    public OneRuneRule(string label, Func<int, bool> predicate)
        // Preserve so the matched leaf reaches the tree by default. A
        // caller who wants it Delete'd applies `.Delete()` themselves.
        : base(FlattenType.Preserve)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        _label = label ?? throw new ArgumentNullException(nameof(label));
    }

    protected override Symbol? TryParseRule(
        Lexer lexer,
        int startPosition,
        FlattenType effectiveFlattenType,
        List<Symbol>? outputSymbols)
    {
        var token = lexer.Read();
        if (token.IsEof)
        {
            TraceFailure(lexer, $"found '<EOF>', wanted {_label}");
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }
        if (!_predicate(token.RuneValue))
        {
            TraceFailure(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted {_label}");
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }

        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}'");

        // Delete: emit no Symbol.
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;

        // Compute the leaf id the same way OneOf/NoneOf/AnyToken do.
        // The shared helper picks the rune's code point as the id for
        // single-rune leaves, which is what gives Symbol.Is(Token('x'))
        // its "id == rune" semantics.
        SymbolId leafId = ResolveLeafId(token.RuneValue);
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory, lexer.Context);

        // Flatten: caller wants the leaf appended to its list; the
        // rule returns Discarded so the caller's And/Or knows not to
        // double-add. Preserve: return the leaf directly so its caller
        // wraps it under whatever named composite asked for it.
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }
}
