using System.Collections.Generic;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches one token whose value belongs to the given RuneSet. A token
// is either a single rune (matched against the set's rune intervals)
// or a multi-rune grapheme cluster (matched against the set's
// multi-rune graphemes). Under RuneLexer every token is one rune so
// only the rune intervals are reachable. Under GraphemeLexer a
// multi-rune grapheme (skin-toned emoji, ZWJ sequence, CJK + combining
// mark, regional-indicator pair, CRLF) is one token and matches only
// when the set has the same grapheme as a multi-rune entry. EOF
// always fails. NoneOfRule is the mirror: same rule, opposite
// membership test (one token whose value ISN'T in the set).
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
        if (token.IsEof || !TokenInSet(token))
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
        // SymbolId wraps a single int. A single-rune token fits, so we
        // pin its rune value into the id and tree consumers can branch
        // on which rune matched. A multi-rune grapheme cluster is two
        // or more code points, which won't fit in one int, so we fall
        // back to the rule's own id.
        int runeValue = token.RuneValue;
        SymbolId leafId = runeValue >= 0 ? new SymbolId(runeValue) : Id;
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    // Membership test that handles both halves of the set. Single-rune
    // tokens (RuneValue >= 0) hit the rune intervals via Contains(int).
    // Multi-rune tokens (RuneValue == -1 under GraphemeLexer) probe the
    // multi-rune array via the token's Chars span. Sets without any
    // multi-rune entries short-circuit on the first branch and never
    // touch the grapheme array.
    private bool TokenInSet(Lexing.Token token)
    {
        int runeValue = token.RuneValue;
        if (runeValue >= 0) return _set.Contains(runeValue);
        if (!_set.HasMultiRuneGraphemes) return false;
        return _set.ContainsGrapheme(token.Chars);
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (RuneSet.Empty when Advance.Never. RuneSet.Universe means "I don't know").
    // Then say whether the rule Always / Sometimes / Never consumes at least
    // that first rune on success.
    internal override RuleStartRequirements ComputeRuleStart()
    {
        // The lookahead shortcut peeks ONE rune off the input. If the
        // set has multi-rune entries, the first rune of each multi-rune
        // grapheme is also a valid lookahead (the lexer might be about
        // to hand us that whole grapheme as one token). Add those first
        // runes to the rune intervals so CannotMatchLookahead doesn't
        // wrongly skip OneOfRule when the input begins with a grapheme
        // whose first rune isn't otherwise in the set.
        return new RuleStartRequirements(LookaheadFirstRunes(_set), Advance.Always);
    }

    // Build a rune-only set covering every possible first rune of any
    // member of `set`. Single-rune members contribute themselves;
    // multi-rune members contribute their first rune. Used as the
    // FirstConsumedRunes value for OneOf and ScanWhile when their set
    // has multi-rune entries, so the lookahead shortcut stays sound.
    internal static RuneSet LookaheadFirstRunes(RuneSet set)
    {
        if (!set.HasMultiRuneGraphemes) return set;
        var firstRunes = set.RunesOnlyPart;
        foreach (string grapheme in set.MultiRuneGraphemes)
        {
            int firstRune;
            if (grapheme.Length >= 2
                && char.IsHighSurrogate(grapheme[0])
                && char.IsLowSurrogate(grapheme[1]))
            {
                firstRune = char.ConvertToUtf32(grapheme[0], grapheme[1]);
            }
            else
            {
                firstRune = grapheme[0];
            }
            firstRunes = firstRunes | RuneSet.Single(firstRune);
        }
        return firstRunes;
    }
}
