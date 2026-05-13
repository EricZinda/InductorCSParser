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
        // ResolveLeafId carries the leaf-id rule shared with NoneOfRule,
        // AnyTokenRule, and WithinTokenRule: rune value when the rule is
        // truly anonymous and the token is one rune, rule's own Id when
        // the user identified the rule via .As(string) / .As(SymbolId)
        // or the token is multi-rune.
        SymbolId leafId = ResolveLeafId(token.RuneValue);
        var leafSymbol = new Symbol(leafId, FlattenType, token.Memory, lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.FirstTokenMustBeInSet(_set);
}
