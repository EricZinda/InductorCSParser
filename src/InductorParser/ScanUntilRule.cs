using System;
using System.Collections.Generic;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// The leaf for a string body (JSON / C++ / Python and the like): scan
// forward until a stopper matches at the current position, handling escape
// sequences inline. Collapses ZeroOrMore(Or(bodyRune, And(escapeStart,
// escapeEnd))) into one tight loop that returns a single leaf Symbol over
// the matched span, one allocation per run however many runes it covers.
//
// The stopper comes in two forms:
//   * TokenSet (fast path): stop when the next token is in the set, one
//     ContainsToken per token. Covers single-token boundaries: JSON ",
//     Python ' or ", etc.
//   * Rule (general path): stop when a user-supplied rule matches, peeked
//     in a transaction that always rolls back so the stopper isn't consumed
//     (the surrounding grammar matches it). Covers multi-rune boundaries
//     like Python triple-quote """...""".
//
// Escapes (optional) have a start (what triggers the escape) and an end
// (what completes it). In \n the \ is the start and n is the end. The start
// is either a single Rune (the JSON / C / Python backslash) or a Rule
// (multi-rune starts like ${ ). The start is checked before the stopper, so
// an escape whose start shares a prefix with a stopper still wins: with
// stopAt "\"$" and escapeStart Literal("${"), a '$' tries "${" first and
// only falls through to the bare-'$' stopper when that fails.
//
// The Symbol holds a ReadOnlyMemory<char> over the raw source, escapes
// included. A caller that wants the decoded value has to run its own unescape
// pass over the text, so a highlighter or formatter pays nothing for a
// decode it doesn't want.
//
// See StringLiteralGrammars.cs / StringLiteralGrammarsTests.cs for runnable
// grammars (JSON, Python single-line, triple-quote, raw) and their tests.
//
// Out of scope: C++ raw strings R"delim(...)delim", whose stopper depends on
// the delimiter the opening captured, that needs a custom rule.
internal sealed class ScanUntilRule : Rule
{
    // Stopper discrimination. _stopperRule != null selects the general
    // path, otherwise _stopperSet is used. The general path is one
    // predictable branch per rune.
    private TokenSet _stopperSet;
    private readonly Rule? _stopperRule;
    // How the stopper should show up in trace messages. Recomputed at compile time
    // so it reflects the stopper's final form. For a TokenSet stopper,
    // ValidateNormalization refreshes it after projecting the set under the
    // normalization form. For a rule stopper, ValidateCompiled refreshes it
    // to pick up a .As(name) the stopper rule may have gained after this
    // ScanUntil was built.
    private string _stopperRendered;

    // When false (strict, the default), reaching end-of-input without
    // matching the stopper fails the rule. When true, EOF is itself a
    // valid stopping condition and the rule succeeds with whatever it
    // scanned.
    private readonly bool _eofIsTerminator;

    // Escape modes:
    //   no escape: _hasEscape false.
    //   single-rune start: _escapeStartRune set, _escapeStartRule null. One
    //       int compare per non-stopper rune. The JSON / C / Python
    //       backslash case, which is nearly every real escape grammar.
    //   rule start: _escapeStartRule set, _escapeStartRune -1. One
    //       Rule.TryParse per non-stopper rune, for multi-rune starts like
    //       $$ or a choice across several starts.
    private readonly bool _hasEscape;
    private int _escapeStartRune;
    private readonly Rule? _escapeStartRule;

    // Set when Compile's normalization pass turned the single-rune escape
    // start into a multi-rune (but single-grapheme) cluster, e.g. 'é' →
    // "e + U+0301" under FormD. The int fast path can't match a cluster, so
    // the whole cluster lives here for a span-equal check. Null otherwise
    // (no escape, rule-form start, or a start that stayed single-rune under
    // the form).
    private string? _escapeStartGrapheme;

    // _escapeEnd runs once per escape (not per rune), only to consume the
    // escape body. Any Symbol it produces is discarded: ScanUntil emits one
    // flat leaf over the raw source, so nothing the end builds reaches the
    // tree. If the end fails, the whole ScanUntil fails, since a started
    // escape that can't complete isn't a well-formed body.
    private readonly Rule? _escapeEnd;

    // The recursive evaluator reads these private fields directly in
    // TryParseRule. An alternative evaluator needs the same data without
    // running the rule.
    internal TokenSet StopperSet => _stopperSet;
    internal Rule? StopperRule => _stopperRule;
    internal bool HasEscape => _hasEscape;
    internal int EscapeStartRune => _escapeStartRune;
    internal Rule? EscapeStartRule => _escapeStartRule;
    internal Rule? EscapeEnd => _escapeEnd;
    internal bool EofIsTerminator => _eofIsTerminator;

    // Fast path, no escape. Per rune: one TokenSet.ContainsRune.
    public ScanUntilRule(TokenSet stopAt, bool eofIsTerminator = false)
        : base(FlattenType.Preserve, emitsLeaf: true)
    {
        _stopperSet = stopAt;
        _stopperRule = null;
        _stopperRendered = stopAt.ToString();
        _escapeEnd = null;
        _hasEscape = false;
        _escapeStartRune = -1;
        _escapeStartRule = null;
        _eofIsTerminator = eofIsTerminator;
    }

    // Fast path, single-rune escape start. Per rune: one
    // TokenSet.ContainsRune plus one int equality on non-stopper runes.
    // Covers JSON, C, C++ regular, Python single-line.
    public ScanUntilRule(TokenSet stopAt, Rune escapeStart, Rule escapeEnd, bool eofIsTerminator = false)
        : base(FlattenType.Preserve, emitsLeaf: true, escapeEnd ?? throw new ArgumentNullException(nameof(escapeEnd)))
    {
        _stopperSet = stopAt;
        _stopperRule = null;
        _stopperRendered = stopAt.ToString();
        _escapeEnd = escapeEnd;
        _hasEscape = true;
        _escapeStartRune = escapeStart.Value;
        _escapeStartRule = null;
        _eofIsTerminator = eofIsTerminator;
    }

    // General escape start. Adds one Rule.TryParse on non-stopper
    // runes only. Use for multi-rune starts like $$ or a choice
    // across several starts.
    public ScanUntilRule(TokenSet stopAt, Rule escapeStart, Rule escapeEnd, bool eofIsTerminator = false)
        : base(
            FlattenType.Preserve,
            emitsLeaf: true,
            escapeStart ?? throw new ArgumentNullException(nameof(escapeStart)),
            escapeEnd ?? throw new ArgumentNullException(nameof(escapeEnd)))
    {
        _stopperSet = stopAt;
        _stopperRule = null;
        _stopperRendered = stopAt.ToString();
        _escapeEnd = escapeEnd;
        _hasEscape = true;
        _escapeStartRune = -1;
        _escapeStartRule = escapeStart;
        _eofIsTerminator = eofIsTerminator;
    }

    // General stopper, no escape. Per rune: one Rule.TryParse for
    // the stopper (peek transaction, never consumed). Use for
    // multi-rune boundaries like C++ raw strings.
    public ScanUntilRule(Rule stopAt, bool eofIsTerminator = false)
        : base(FlattenType.Preserve, emitsLeaf: true, stopAt ?? throw new ArgumentNullException(nameof(stopAt)))
    {
        _stopperSet = default;
        _stopperRule = stopAt;
        _stopperRendered = $"rule {stopAt.Name ?? stopAt.GetType().Name}";
        _escapeEnd = null;
        _hasEscape = false;
        _escapeStartRune = -1;
        _escapeStartRule = null;
        _eofIsTerminator = eofIsTerminator;
    }

    // General stopper with single-rune escape start. Canonical use:
    // Python triple-quote """...""" with backslash escapes.
    public ScanUntilRule(Rule stopAt, Rune escapeStart, Rule escapeEnd, bool eofIsTerminator = false)
        : base(
            FlattenType.Preserve,
            emitsLeaf: true,
            stopAt ?? throw new ArgumentNullException(nameof(stopAt)),
            escapeEnd ?? throw new ArgumentNullException(nameof(escapeEnd)))
    {
        _stopperSet = default;
        _stopperRule = stopAt;
        _stopperRendered = $"rule {stopAt.Name ?? stopAt.GetType().Name}";
        _escapeEnd = escapeEnd;
        _hasEscape = true;
        _escapeStartRune = escapeStart.Value;
        _escapeStartRule = null;
        _eofIsTerminator = eofIsTerminator;
    }

    protected override void ValidateCompiled()
    {
        // Record any .As(name) the inner rule got
        if (_stopperRule != null)
            _stopperRendered = $"rule {_stopperRule.Name ?? _stopperRule.GetType().Name}";
    }

    protected override void ValidateNormalization(
        System.Text.NormalizationForm form,
        INormalizationReporter reporter)
    {
        // The TokenSet stopper matches one whole token at a time, like
        // OneOf / NoneOf, so its entries have to be normalized to the chosen
        // form the same way theirs are. Without this, a stopper typed as the
        // precomposed 'é' wouldn't match the decomposed "e + combining acute"
        // the FormD lexer produces, and the body would silently swallow the
        // boundary the user meant. Rule-mode stoppers (and the escapeEnd /
        // escapeStartRule sub-rules) normalize themselves through their own validation.
        if (_stopperRule == null)
        {
            OneOfRule.NormalizeAndValidate(this, ref _stopperSet, form, reporter);
            // See OneOfRule.ValidateNormalization for why the
            // rendering has to be refreshed after the set is projected.
            _stopperRendered = _stopperSet.ToString();
        }

        // The single-rune escape start is stored as one rune and matched by
        // an int compare. The lexer normalizes the input first, so a start
        // typed as the precomposed 'é' wouldn't match the decomposed grapheme token
        // the FormD lexer reads. Convert the stored rune to the same form:
        //   * unchanged under the form: leave the int compare as is.
        //   * converts to a different single rune (U+212A → 'K'): re-point
        //     _escapeStartRune at it.
        //   * converts to one grapheme of several runes (é → "e + U+0301"):
        //     stash the cluster in _escapeStartGrapheme for a span compare.
        //   * converts to several graphemes (ﬁ → "fi" under FormKC): report
        //     an offender, since one start can't be two graphemes.
        // A rule-form start is a child rule the normalization pass already
        // visits on its own, so only single-rune starts are handled here.
        if (!_hasEscape || _escapeStartRule != null) return;

        string originalRuneText = char.ConvertFromUtf32(_escapeStartRune);
        string? normalized = TryConvertToForm(this, originalRuneText, form, reporter);
        if (normalized == null) return;
        if (string.Equals(normalized, originalRuneText, StringComparison.Ordinal)) return;

        if (GraphemeHelpers.Count(normalized) > 1)
        {
            reporter.ReportOffender(this, originalRuneText,
                $"<escape-start rune converts under {form} to the multi-grapheme " +
                $"sequence \"{normalized}\", but a single-rune escape start matches " +
                $"exactly one grapheme. Use the Rule-valued escape-start constructor " +
                $"with Literal(\"{normalized}\") instead.>");
            return;
        }

        if (RuneHelpers.TrySingleRune(normalized, out int newRune))
        {
            _escapeStartRune = newRune;
            return;
        }

        _escapeStartGrapheme = normalized;
    }

    // How a scan loop ended. StopperFound and ReachedEnd leave the cursor at
    // the body's end with the stopper unconsumed. Failed means the loop
    // already recorded its own failure (a started escape whose end didn't
    // complete) and TryParseRule should return null.
    private enum ScanResult { StopperFound, ReachedEnd, Failed }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Scan one token (i.e. one grapheme cluster) at a time until the stopper
        // matches or input runs out. A stopper match always succeeds.
        // Reaching EOF succeeds only when _eofIsTerminator is set, otherwise it fails with
        // "unterminated body". A TokenSet stopper with no rule-escape takes
        // the Read-based ScanFastPath. Everything else takes ScanGeneralPath,
        // which peeks sub-rules without consuming.
        //
        // Lone surrogates (one-char tokens with RuneValue -1) are consumed as
        // body unless the stopper set opts into surrogates. They reach this rule only under
        // Compile(null), since otherwise string.Normalize rejects malformed
        // UTF-16 before Parse() runs.
        ScanResult result = _stopperRule == null && _escapeStartRule == null
            ? ScanFastPath(lexer)
            : ScanGeneralPath(lexer);

        if (result == ScanResult.Failed)
            return null; // the scan already recorded its failure

        if (result == ScanResult.ReachedEnd && !_eofIsTerminator)
        {
            // Strict: the scan ran off the end without matching the stopper.
            // Record at the stuck position (EOF). A .WithError reports its position
            // there rather than being at the rule's start. See
            // docs/ErrorArchitecture.md.
            TraceFailure(lexer, $"unterminated body, expected stopper '{_stopperRendered}'");
            lexer.RecordFailure(lexer.Position, ErrorMessage, ErrorForced);
            return null;
        }

        int length = lexer.Position - startPosition;
        TraceSuccess(lexer, $"{length} chars, stopper '{_stopperRendered}'");
        if (effectiveFlattenType == FlattenType.Delete) return Symbol.Discarded;
        var leafSymbol = new Symbol(Id, FlattenType, lexer.Input.AsMemory(startPosition, length), lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    // Fast path: a TokenSet stopper with no escape or a single-rune escape
    // start (JSON, Python, C). Consumes body tokens with the public
    // lexer.Read(). The rule-stopper and rule-escape-start paths can't use
    // this: they peek a sub-rule without consuming, so
    // they take ScanGeneralPath. 
    private ScanResult ScanFastPath(Lexer lexer)
    {
        while (!lexer.IsEof)
        {
            lexer.TickBudget();
            int position = lexer.Position;
            Token token = lexer.Read();

            // Check the escape start before the stopper, so an escape whose
            // start shares a prefix with a stopper still wins. 
            if (_hasEscape)
            {
                bool escapeStartMatched = _escapeStartGrapheme != null
                    ? token.Chars.SequenceEqual(_escapeStartGrapheme.AsSpan())
                    : token.RuneValue == _escapeStartRune;
                if (escapeStartMatched)
                {
                    if (ParseChild(_escapeEnd!, lexer, outputSymbols: null) == null)
                    {
                        TraceFailure(lexer, $"bad escape end at offset {lexer.Position}");
                        lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                        return ScanResult.Failed;
                    }
                    continue;
                }
            }

            // Stopper check against the whole token Read() consumed. On a match
            // the stopper isn't part of the body, so back the cursor up to where
            // this token started and stop. ContainsToken matches any stopper
            // shape the set holds: a plain character, a multi-rune grapheme
            // like CRLF, or a lone surrogate.
            if (_stopperSet.ContainsToken(token.Chars))
            {
                lexer.SetPosition(position);
                return ScanResult.StopperFound;
            }
            // Otherwise the token Read() consumed is part of the body so keep scanning.
        }
        return ScanResult.ReachedEnd;
    }

    // General path: a rule-valued stopper and/or a rule-valued escape start.
    // Both peek a sub-rule in a Probe without consuming, so the cursor can't be
    // advanced by Read() up front the way ScanFastPath does. It peeks the next
    // token's length, classifies it, and advances with SetPosition once the
    // token is taken as body.
    private ScanResult ScanGeneralPath(Lexer lexer)
    {
        string input = lexer.Input;
        int inputLength = input.Length;

        while (!lexer.IsEof)
        {
            // Every iteration ticks the budget: the whole scan is one rule
            // invocation from the engine's view. See Lexer.AdvanceWhileRuneIn.
            lexer.TickBudget();

            int position = lexer.Position;

            // Decode the next rune for the single-rune escape-start compare
            // below. On a lone surrogate TryPeekRune returns false (runeValue
            // -1, runeLength 0). That's fine: the escape-start check's
            // tokenLength == runeLength rejects it, so it falls through to body.
            Lexer.TryPeekRune(input, position, out int runeValue, out int runeLength);

            // tokenLength is the next whole token's length (one grapheme
            // cluster). The escape-start fast path and the stopper check
            // below both need it.
            int tokenLength = lexer.PeekTokenLength(position);

            // Escape-start check before the stopper check (so the escape wins
            // when its start shares a prefix with a stopper). Single-rune and
            // Rule forms are mutually exclusive, picked by the constructor.
            // When the escape-start doesn't match, the scan falls through to
            // the stopper check below.
            if (_hasEscape)
            {
                if (_escapeStartRule != null)
                {
                    // The escape-start runs in a Probe. On a mismatch 
                    // Dispose restores position, failure tracker,
                    // and subtree extent. On a match the start is real
                    // content, so Commit keeps it. See docs/ErrorArchitecture.md.
                    Symbol? start;
                    using (var probe = lexer.BeginProbe())
                    {
                        start = ParseChild(_escapeStartRule, lexer, outputSymbols: null);
                        if (start != null)
                            probe.Commit();
                    }
                    if (start != null)
                    {
                        // Start matched and was kept. End failure is a hard
                        // failure: an escape sequence was started, so
                        // the input isn't a well-formed string body.
                        var end = ParseChild(_escapeEnd!, lexer, outputSymbols: null);
                        if (end == null)
                        {
                            TraceFailure(lexer, $"bad escape end at offset {lexer.Position}");
                            // RecordCompositeFailure, not RecordFailure: if the
                            // escape-end is a multi-step rule, a deeper failure
                            // inside it would otherwise win "deepest failure
                            // wins" and hide this rule's .WithError. See
                            // docs/ErrorArchitecture.md.
                            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                            return ScanResult.Failed;
                        }
                        // If both start and end matched zero-width, position
                        // is unchanged and the loop would spin forever. A
                        // grammar-author mistake, but break cleanly rather
                        // than hang. Same as BetweenInclusiveRule.
                        if (lexer.Position == position) break;
                        continue;
                    }
                    // Start didn't match: the Probe restored the position,
                    // the failure tracker, and the subtree extent. Fall
                    // through to the stopper check.
                }
                else
                {
                    // Single-rune escape start. If normalization turned it into
                    // a cluster (e.g. 'é' → "e + U+0301" under FormD),
                    // _escapeStartGrapheme holds it and the whole cluster span-
                    // matches. Otherwise tokenLength == runeLength requires a
                    // one-rune token, so '\<combining mark>' doesn't match, same
                    // as Token('\\').
                    bool escapeStartMatched;
                    if (_escapeStartGrapheme != null)
                    {
                        escapeStartMatched =
                            position + tokenLength <= inputLength
                            && input.AsSpan(position, tokenLength).SequenceEqual(_escapeStartGrapheme.AsSpan());
                    }
                    else
                    {
                        escapeStartMatched = tokenLength == runeLength && runeValue == _escapeStartRune;
                    }
                    if (escapeStartMatched)
                    {
                        lexer.SetPosition(position + tokenLength);
                        var end = ParseChild(_escapeEnd!, lexer, outputSymbols: null);
                        if (end == null)
                        {
                            TraceFailure(lexer, $"bad escape end at offset {position + tokenLength}");
                            // Same RecordCompositeFailure choice as the rule-form path above.
                            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                            return ScanResult.Failed;
                        }
                        continue;
                    }
                }
            }

            // Stopper check. ContainsToken tests the next whole token against
            // the set, matching both single runes and multi-rune graphemes,
            // so a '"' stopper doesn't match a '"<combining-mark>' cluster,
            // the same as OneOf("\""). The Rule path peeks in a transaction
            // that always rolls back, so the stopper is never consumed here.
            if (_stopperRule == null)
            {
                if (position + tokenLength <= inputLength
                    && _stopperSet.ContainsToken(input.AsSpan(position, tokenLength)))
                {
                    return ScanResult.StopperFound;
                }
            }
            else
            {
                // Pure lookahead, same as the escape-start Probe above. With
                // no Commit, Dispose restores position, failure tracker, and
                // subtree extent, so ScanUntil never consumes the stopper.
                // See docs/ErrorArchitecture.md.
                bool stopMatched;
                using (lexer.BeginProbe())
                {
                    stopMatched = ParseChild(_stopperRule, lexer, outputSymbols: null) != null;
                }
                if (stopMatched)
                    return ScanResult.StopperFound;
            }

            // Not a stopper, not an escape start: consume the whole token as
            // body and keep scanning. This path peeks instead of calling
            // Read(), so it advances the cursor by hand with SetPosition (token
            // by token, staying on grapheme-cluster boundaries). PeekTokenLength
            // never overshoots the readable range, so the advance always stays
            // in bounds. The invariant makes that explicit: unlike Read(), the
            // raw SetPosition has no EOF handling of its own, so if the bound
            // were ever exceeded it would run the cursor (and the next
            // iteration's span reads) off the end. Fail loudly here instead.
            Invariant.That(position + tokenLength <= inputLength,
                $"ScanUntil body advance past end of input: position {position} + tokenLength {tokenLength} exceeds inputLength {inputLength}");
            lexer.SetPosition(position + tokenLength);
        }

        // Run the Rule-stopper once more at EOF. The loop runs it at each
        // token position, but an EOF-sensitive stopper (Eof(), Not(AnyToken()),
        // Or(..., Eof())) can match at end of input, ending the body there the
        // same as a mid-input match. Strict mode only (_eofIsTerminator already
        // stops at EOF), and TokenSet stoppers skip it, since a TokenSet is
        // tested against a real token and EOF produces none.
        if (!_eofIsTerminator && _stopperRule != null && lexer.IsEof)
        {
            // Same pure-lookahead Probe as in the loop: never consumes the
            // stopper, restores everything on Dispose.
            bool matchedAtEof;
            using (lexer.BeginProbe())
            {
                matchedAtEof = ParseChild(_stopperRule, lexer, outputSymbols: null) != null;
            }
            if (matchedAtEof)
                return ScanResult.StopperFound;
        }

        return ScanResult.ReachedEnd;
    }

}
