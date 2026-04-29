using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches one token if it's a single rune that belongs to the given
// RuneSet. Under GraphemeLexer a multi-rune grapheme (skin-toned
// emoji, ZWJ sequences, CJK + combining mark) fails because it isn't
// a single code point. EOF also fails. NoneOfRule is the mirror:
// same rule, opposite membership test (one rune whose value is NOT
// in the set).
internal sealed class OneOfRule : Rule
{
    private readonly RuneSet _set;

    // Pre-rendered "[A-Z,a-z]" form of the set, computed once at
    // construction. Trace lines reference this instead of the RuneSet
    // directly so we don't re-render the same string on every traced
    // match. The RuneSet is immutable, so the rendering is too.
    // Worth caching because tracing is intended to be usable while
    // iterating on a grammar, not just for one-off debug runs.
    private readonly string _setRendered;

    public OneOfRule(RuneSet runeSet) : base(FlattenType.Preserve)
    {
        _set = runeSet;
        _setRendered = runeSet.ToString();
    }

    // Accessor for the state-machine evaluator's lowering pass.
    internal RuneSet LoweringSet => _set;

    internal override (string Text, bool IgnoreCase)? ComputeRequiredLiteral() =>
        ComputeConcatenableText();

    internal override (string Text, bool IgnoreCase)? ComputeConcatenableText()
    {
        // OneOfRule consumes exactly one rune. Two shapes feed the
        // required-literal prefilter cleanly: a single-rune set
        // (OneOf("x") matches only 'x') and a two-rune set that's an
        // ASCII letter pair (OneOf("Nn") is effectively a case-
        // insensitive 'n'). Anything broader has too many candidate
        // chars to act as a useful substring-search trigger, so we
        // return null and the caller falls back to the first-rune skip.
        if (!_set.TryGetBmpChars(maxChars: 2, out char[] chars) || chars.Length == 0)
            return null;
        if (chars.Length == 1)
            return (chars[0].ToString(), false);
        char a = chars[0];
        char b = chars[1];
        if (IsAsciiLetter(a) && IsAsciiLetter(b) && (a | 0x20) == (b | 0x20))
            return (((char)(a | 0x20)).ToString(), true);
        return null;
    }

    private static bool IsAsciiLetter(char c) =>
        (uint)((c | 0x20) - 'a') <= ('z' - 'a');

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        var token = lexer.Read();
        if (token.IsEof || !_set.Contains(token.RuneValue))
        {
            TraceFailure(lexer,
                $"found '{(token.IsEof ? "<EOF>" : lexer.Input.Substring(token.Offset, token.Length))}', wanted one of '{_setRendered}'");
            // Error Positioning: the position of the rune we tried to read. OneOfRule
            // does exactly one Read, so the transaction's saved start
            // position is exactly where that rune sits in the input (or
            // equals input.Length on EOF).
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one of '{_setRendered}'");
        transaction.Commit();
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        var leafSymbol = new Symbol(new SymbolId(token.RuneValue), FlattenType, token.Memory);
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
        return new RuleStartRequirements(_set, Advance.Always);
    }
}
