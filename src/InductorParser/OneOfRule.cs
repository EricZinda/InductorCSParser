using System;
using System.Collections.Generic;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// Matches one token whose value belongs to the given TokenSet. A token
// is either a single rune (matched against the set's rune intervals)
// or a multi-rune sequence the lexer grouped as one user-visible
// character (skin-toned emoji, ZWJ family, regional-indicator pair,
// CRLF, etc., matched against the set's multi-rune entries). EOF
// always fails. NoneOfRule is the mirror: same rule, opposite
// membership test (one token whose value ISN'T in the set).
internal sealed class OneOfRule : Rule
{
    private TokenSet _set;

    // Pre-rendered "[A-Z,a-z]" form of the set, computed once at
    // construction. Trace lines reference this instead of the TokenSet
    // directly so we don't re-render the same string on every traced
    // match. The TokenSet is immutable, so the rendering is too.
    // Worth caching because tracing is intended to be usable while
    // iterating on a grammar, not just for one-off debug runs.
    private readonly string _setRendered;

    public OneOfRule(TokenSet runeSet) : base(FlattenType.Preserve)
    {
        _set = runeSet;
        _setRendered = runeSet.ToString();
    }

    // Read-only accessor for the post-Compile set, used by analyzers
    // that need to inspect the rule's matchable tokens.
    internal TokenSet LoweringSet => _set;

    // See Rule.CollectNormalizationOffenders for the contract. OneOf-
    // specific: form-project the set and report any entry whose
    // conversion is multi-grapheme as an offender, since OneOf matches
    // exactly one grapheme per token. Shared with NoneOfRule via
    // NormalizeAndValidate.
    internal override void CollectNormalizationOffenders(
        System.Text.NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
    {
        NormalizeAndValidate(this, ref _set, form, offenders);
    }

    // Shared form-projection + validation between OneOfRule and NoneOfRule.
    // NormalizedFor projects the set and reports any multi-grapheme
    // conversion result via the out list (excluded from the projected
    // set so the TokenSet single-grapheme invariant holds). For each
    // report, contribute an offender so the user gets a clear Compile-
    // time error pointing at the fix.
    internal static void NormalizeAndValidate(
        Rule rule, ref TokenSet set, NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders)
    {
        var multiGraphemeConversions = new List<(string original, string normalized)>();
        set = set.NormalizedFor(form, multiGraphemeConversions);
        foreach (var (original, normalized) in multiGraphemeConversions)
        {
            offenders.Add((rule, original,
                $"<converts under {form} to multi-grapheme sequence " +
                $"\"{normalized}\". OneOf / NoneOf match exactly one grapheme " +
                $"per token, so no single input token can match. Use " +
                $"Literal(\"{normalized}\") for the whole sequence, " +
                $"And(Token-per-grapheme) for token-by-token control, or call " +
                $"`set.WithCompatibilityEquivalents({form})` before OneOf / " +
                $"NoneOf to expand into the grapheme pieces as separate " +
                $"set members.>"));
        }
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        var token = lexer.Read();
        if (token.IsEof || !_set.ContainsToken(token.Chars))
        {
            TraceFailure(lexer,
                $"found '{(token.IsEof ? "<EOF>" : lexer.Input.Substring(token.Offset, token.Length))}', wanted one of '{_setRendered}'");
            // Error Positioning: the position of the token we tried to read.
            // OneOfRule does exactly one Read, so the transaction's saved start
            // position is exactly where that token sits in the input (or
            // equals input.Length on EOF).
            lexer.RecordFailure(transaction.StartPosition, ErrorMessage);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one of '{_setRendered}'");
        transaction.Commit();
        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;
        // SymbolId wraps a single int. A single-rune token fits, so for
        // an unnamed rule we use the rune value as the leaf id directly,
        // letting tree consumers branch on which rune matched without
        // going through a synthetic per-OneOf id. A multi-rune grapheme
        // cluster (two or more code points) doesn't fit in one int, so
        // it falls back to the rule's own id either way. When the user
        // named the rule via .As("..."), that name is the user's
        // explicit signal "find me by reference," so the rule's Id wins
        // over the rune value: Tree.Find, Tree.Is, and NameOf all need
        // leaf.Id == rule.Id for the named rule to be findable. Same
        // gate applies in AnyTokenRule, NoneOfRule, and WithinTokenRule.
        int runeValue = token.RuneValue;
        SymbolId leafId = (Name == null && runeValue >= 0) ? new SymbolId(runeValue) : Id;
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    // Return the set of runes this rule might consume first (can be a superset)
    // (TokenSet.Empty when Advance.Never. TokenSet.Universe means "I don't know").
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
    // FirstConsumedTokens value for OneOf and ScanWhile when their set
    // has multi-rune entries, so the lookahead shortcut stays sound.
    internal static TokenSet LookaheadFirstRunes(TokenSet set)
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
            firstRunes = firstRunes | TokenSet.Single(firstRune);
        }
        return firstRunes;
    }
}
