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
    // construction and refreshed by ValidateNormalization when
    // Compile's normalization pass mutates _set. Trace lines reference
    // this instead of the TokenSet directly so we don't re-render the
    // same string on every traced match. Worth caching because tracing
    // is intended to be usable while iterating on a grammar, not just
    // for one-off debug runs.
    private string _setRendered;

    public OneOfRule(TokenSet runeSet) : base(FlattenType.Preserve, emitsLeaf: true)
    {
        _set = runeSet;
        _setRendered = runeSet.ToString();
    }

    // Read-only accessor for the post-Compile set, used by analyzers
    // that need to inspect the rule's matchable tokens.
    internal TokenSet LoweringSet => _set;

    // Replace the rule's TokenSet wholesale and refresh the cached trace
    // rendering. Used by IdentifierRule to install its form-aware expanded
    // sets on its embedded start / body OneOfs before the form-validation
    // walker reaches them. Mirrors the in-place mutation
    // ValidateNormalization already does on _set / _setRendered.
    internal void ReplaceSet(TokenSet newSet)
    {
        _set = newSet;
        _setRendered = newSet.ToString();
    }

    // See Rule.ValidateNormalization for how this works. OneOf-
    // specific: form-project the set and report any entry whose
    // conversion is multi-grapheme as an offender, since OneOf matches
    // exactly one grapheme per token. Shared with NoneOfRule via
    // NormalizeAndValidate.
    protected override void ValidateNormalization(
        System.Text.NormalizationForm form,
        INormalizationReporter reporter)
    {
        NormalizeAndValidate(this, ref _set, form, reporter);
        // The constructor already wrote _setRendered from the user-typed
        // entries, which is the right answer for a Compile(null) parse
        // (no projection runs, _set keeps the typed shape). This pass
        // rewrites _set onto the lexer-normalized form (canonical
        // singletons substituted, precomposed runes possibly decomposed
        // to multi-rune clusters), so the rendering has to follow or
        // the trace shows entries the rule no longer matches.
        _setRendered = _set.ToString();
    }

    // Re-project every TokenSet entry under the grammar's chosen Unicode
    // normalization form (FormC/FormD/FormKC/FormKD), so the set's
    // entries are in the same form the lexer applies to input before
    // matching. Without this, a user-typed 'é' (precomposed U+00E9)
    // wouldn't match an input lexed under FormD (decomposed
    // "e + U+0301"), or vice versa. Set entries can be single runes or
    // multi-rune grapheme clusters. Both go through the projection.
    //
    // An entry that converts to a multi-grapheme sequence (e.g. the
    // ligature ﬁ -> "fi" under FormKC) can't stay in a TokenSet (set
    // members are single graphemes by invariant), so it's dropped from
    // the projected set and reported as an offender for the user to
    // fix at Compile time. The offender description is rule-agnostic
    // because the four callers (OneOfRule, NoneOfRule, ScanWhileRule,
    // ScanUntilRule) have different matching consequences.
    // BuildNormalizationErrorMessage already names the offending rule.
    internal static void NormalizeAndValidate(
        Rule rule, ref TokenSet set, NormalizationForm form,
        INormalizationReporter reporter)
    {
        var multiGraphemeConversions = new List<(string original, string normalized)>();
        set = set.NormalizedFor(form, multiGraphemeConversions);
        foreach (var (original, normalized) in multiGraphemeConversions)
        {
            reporter.ReportOffender(rule, original,
                $"<converts under {form} to the multi-grapheme sequence " +
                $"\"{normalized}\", but a TokenSet member has to be exactly " +
                $"one grapheme. Call `set.WithCompatibilityEquivalents({form})` " +
                $"before building the rule to expand this entry into its " +
                $"individual graphemes as separate set members, or remove " +
                $"the entry.>");
        }
    }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        var token = lexer.Read();
        if (token.IsEof || !_set.ContainsToken(token.Chars))
        {
            TraceFailure(lexer,
                $"found '{(token.IsEof ? "<EOF>" : lexer.Input.Substring(token.Offset, token.Length))}', wanted one of '{_setRendered}'");
            // Error Positioning: the position of the token we tried to read.
            // OneOfRule does exactly one Read, so startPosition (the
            // cursor when Rule.TryParse's transaction opened) is exactly
            // where that token sits in the input (or equals input.Length
            // on EOF).
            lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
            return null;
        }
        TraceSuccess(lexer, $"found '{lexer.Input.Substring(token.Offset, token.Length)}', wanted one of '{_setRendered}'");
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

}
