using System;
using System.Collections.Generic;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// This is the leaf for a JSON / C++ / Python string body.
// It's a specialized scanner for the "string body" grammar shape: scan
// forward until a stopper is seen at the current lexer position, handling
// escape sequences inline.
// Collapses ZeroOrMore(Or(bodyRune, And(escapeStart, escapeEnd))) into one rule that
// does the scan in a tight loop and returns one leaf Symbol
// covering the matched section of input. One dispatch for the outer rule
// and one Symbol allocation per matched run, however many runes the run
// contains.
//
// There are two options for the string body stop condition:
//   * TokenSet stopAt (fast path): stop when the next rune is in the
//     set. Only does one TokenSet.ContainsRune per rune and handles any grammar whose
//     closing boundary is a single rune: JSON ", Python ' or ", C# $"..."
//     closing, etc.
//   * Rule stopAt (general path): stop when a user-supplied rule
//     matches. The rule is executed in a peek
//     transaction that always rolls back, so the stopper itself isn't
//     consumed. The surrounding grammar is still responsible for
//     matching it. Handles multi-rune boundaries like C++ raw strings
//     R"delim(...)delim" and Python triple-quote """...""".
//
// Escape handling (optional) also comes in two forms. An escape sequence has
// a "start" (what triggers escape mode) and an "end" (what follows and
// completes the escape). For example in \n, the \ is the start and n is the
// end.
//   * Rune escapeStart: fast single-rune escape trigger (the JSON / C /
//     Python backslash case).
//   * Rule escapeStart: general sub-rule escape trigger for multi-rune
//     starts like $$ / ??.
//
// The order of checks is: escape start first, then stopper. The escape
// runs first so a grammar whose escape-start shares a prefix with a
// stopper still works. ScanUntil(stopAt: "\"$", escapeStart: Literal("${"),
// ...) treats "${" as an interpolation escape and a bare '$' as a
// stopper: at a '$' the escape Literal("${") is tried, and only when it
// fails to match does the scan fall through to the '$' stopper. With the
// stopper checked first the '$' would terminate the body before "${" was
// ever tried, leaving the escape unreachable.
//
// The resulting Symbol carries a ReadOnlyMemory<char> over the
// original input, same shape as OneOfRule's Symbol. ToString() returns
// the raw source text, including escape-start runes and their ends as written originally.
// Callers who want to actually decode the escapes need to walk that text themselves.
// Lazy decoding means a syntax highlighter or a code-formatter, which wants the raw
// source preserved, doesn't have to pay for it.
//
// Worked examples: see
// src/InductorParser.Tests/E2EExamples/StringLiteralGrammars.cs for
// runnable grammars that wire this leaf up to
// real string syntaxes (JSON RFC 8259, Python single-line, Python
// triple-quote, Python raw). The companion
// StringLiteralGrammarsTests.cs verifies their positive and negative
// behavior, including the language-specific corner cases (JSON's
// U+0000..U+001F control-char rejection, Python's "no raw newline
// in single-line strings" rule, triple-quote's multi-rune boundary,
// raw strings' lack of escape processing).
//
// Out of scope: C++ raw strings R"delim(...)delim" need a stopper
// rule built dynamically from whatever `delim` the opening
// captured. That's context-sensitive and not directly expressible
// as a fixed Rule at grammar-build time.
internal sealed class ScanUntilRule : Rule
{
    // Stopper discrimination. _stopperRule != null selects the general
    // path, otherwise _stopperSet is used. The general path is one
    // predictable branch per rune. JSON-style grammars that take the
    // TokenSet path never pay for Rule dispatch.
    private TokenSet _stopperSet;
    private readonly Rule? _stopperRule;
    // Refreshed by ValidateNormalization (TokenSet stopper,
    // post-projection entries) or by ValidateCompiled (rule-mode
    // stopper, picks up a .As(name) the inner rule got after this
    // ScanUntil was constructed).
    private string _stopperRendered;

    // When false (strict, the default), reaching end-of-input without
    // matching the stopper fails the rule. When true, EOF is itself a
    // valid stopping condition and the rule succeeds with whatever it
    // scanned. Strict matches the plain reading of "scan until X":
    // running off the end means the body never terminated. Tolerant is
    // for grammars where the body legitimately admits two ends, like a
    // line comment that may close with a newline OR EOF, and for the
    // ScanUntilEof() helper (TokenSet.Empty + eofIsTerminator: true).
    private readonly bool _eofIsTerminator;

    // Escape discrimination. Three modes:
    //   _hasEscape == false: no escape support.
    //   _hasEscape == true, _escapeStartRune != -1, _escapeStartRule == null:
    //       single-rune start fast path. One int equality check per
    //       non-stopper rune. Covers the JSON / C / Python "backslash"
    //       case, which is essentially every real escape grammar.
    //   _hasEscape == true, _escapeStartRule != null, _escapeStartRune == -1:
    //       general Rule-based start. One Rule.TryParse per non-stopper
    //       rune. Covers multi-rune starts like $$ / ??, or any
    //       grammar where the start itself is a sub-rule.
    //
    // The Symbol tree the start rule produces on success is discarded.
    // Our output is one leaf over the raw source text, so nothing a
    // child Symbol carries reaches the parent tree. Grammar authors
    // who want to skip the start's per-parse allocation can give the
    // start rule .Flatten(FlattenType.Delete), and the parse-time
    // Delete filter will remove it.
    private readonly bool _hasEscape;
    private int _escapeStartRune;
    private readonly Rule? _escapeStartRule;

    // Non-null when Compile's normalization-form pass converted the
    // single-rune escape start to a multi-rune-but-single-grapheme
    // sequence (e.g. 'é' → "e + U+0301" under FormD). The fast path
    // can't compare a multi-rune cluster against a single int, so the
    // whole normalized cluster lives here and gets a span-equal check
    // instead. Null on every other path: no escape, rule-form escape
    // start, or single-rune escape start that stayed single-rune under
    // the chosen form (canonical-singleton substitutions like
    // U+212A → 'K' still take the single-rune path, just at the new
    // rune value).
    private string? _escapeStartGrapheme;

    // _escapeEnd runs once per escape occurrence (not per rune), so
    // its cost is amortized across the whole escape sequence rather
    // than the whole string body. Whatever Symbol tree the end
    // produces on success is allocated then discarded. Our output
    // is one leaf over the raw source text, so nothing the end
    // carries reaches the parent tree. For an escape-heavy grammar
    // where that allocation registers, give the end
    // .Flatten(FlattenType.Delete) and the parse-time Delete filter
    // removes the Symbol construction entirely. On an end failure,
    // ScanUntil fails as a whole (a started escape that can't
    // complete isn't a well-formed body) and the outer transaction
    // rolls the lexer back to where ScanUntil opened.
    private readonly Rule? _escapeEnd;

    // Accessors for an alternative evaluator. The recursive evaluator
    // reads these private fields directly inside TryParseRule. An
    // alternative evaluator needs the same data without running the rule.
    internal TokenSet LoweringStopperSet => _stopperSet;
    internal Rule? LoweringStopperRule => _stopperRule;
    internal bool LoweringHasEscape => _hasEscape;
    internal int LoweringEscapeStartRune => _escapeStartRune;
    internal Rule? LoweringEscapeStartRule => _escapeStartRule;
    internal Rule? LoweringEscapeEnd => _escapeEnd;
    internal bool LoweringEofIsTerminator => _eofIsTerminator;

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
        // Catch a .As(name) the inner rule got between this ScanUntil's
        // constructor and Compile.
        if (_stopperRule != null)
            _stopperRendered = $"rule {_stopperRule.Name ?? _stopperRule.GetType().Name}";
    }

    protected override void ValidateNormalization(
        System.Text.NormalizationForm form,
        INormalizationReporter reporter)
    {
        // ScanUntil's TokenSet stopper checks one grapheme at a time
        // (via ContainsToken on the next whole token), so its set
        // entries need the same form projection OneOf / NoneOf get.
        // Without this override a stopper written as the precomposed
        // 'é' wouldn't match the decomposed "e + combining acute"
        // the FormD-normalized lexer hands the rule, and the body
        // would silently swallow the boundary the user typed in.
        // Rule-mode stoppers (and the optional escapeEnd / escapeStartRule
        // sub-rules) handle their own normalization through the
        // walker's recursion into Children.
        if (_stopperRule == null)
        {
            OneOfRule.NormalizeAndValidate(this, ref _stopperSet, form, reporter);
            // See OneOfRule.ValidateNormalization for why the
            // rendering has to be refreshed after the set is projected.
            _stopperRendered = _stopperSet.ToString();
        }

        // Same shape for the single-rune escape start: the fast-path
        // check (`runeValue == _escapeStartRune`) sees the lexer's
        // post-normalization view, so a user-typed precomposed 'é'
        // never matches the FormD-decomposed cluster the lexer reads.
        // Three outcomes:
        //   * Single rune unchanged under the form: nothing to do, the
        //     existing int compare already matches the input the lexer
        //     hands the rule.
        //   * Single rune that converts to a different single rune
        //     (canonical-singleton substitutions like U+212A → 'K'):
        //     re-point _escapeStartRune at the new rune and keep the
        //     int compare path.
        //   * Single rune that converts to a multi-rune single
        //     grapheme (the 'é' → "e + U+0301" case under FormD): the
        //     int compare can't match the cluster, so stash the whole
        //     normalized cluster in _escapeStartGrapheme for the
        //     span-equal path in TryParseRule.
        //   * Single rune that converts to a multi-grapheme sequence
        //     (e.g. ligature ﬁ → "fi" under FormKC): report as
        //     offender, since one escape start can't expand into two
        //     graphemes the lexer would read separately.
        // Rule-form escape starts handle their own normalization
        // through the walker's recursion into Children, so skip the
        // projection here.
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

        if (TokenSet.TrySingleRune(normalized, out int newRune))
        {
            _escapeStartRune = newRune;
            return;
        }

        _escapeStartGrapheme = normalized;
    }

    // How a scan loop ended. StopperFound and ReachedEnd leave the cursor at
    // the body's end (the stopper unconsumed); Failed means the loop already
    // recorded its own failure (a started escape whose end didn't complete)
    // and TryParseRule should return null.
    private enum ScanResult { StopperFound, ReachedEnd, Failed }

    protected override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        // Scan forward one token (one grapheme cluster) at a time until the
        // stopper matches or input runs out, then decide the outcome: a stopper
        // match always succeeds; reaching EOF succeeds only if _eofIsTerminator
        // is set (the tolerant and ScanUntilEof cases) and otherwise fails with
        // "unterminated body". The TokenSet-stopper-without-a-rule-escape case
        // takes the Read-based ScanFastPath; everything else takes
        // ScanGeneralPath, which peeks sub-rules without consuming.
        //
        // Lone surrogates the lexer surfaces (one-char tokens with RuneValue
        // -1, see UnexpectedUnicodeTests) are consumed as body unless the
        // stopper set opts into surrogates
        // matching how ZeroOrMore(NoneOf(stopAt)) treats them.
        // Surrogates reach this
        // rule only under Compile(null); otherwise string.Normalize rejects
        // malformed UTF-16 before Parse() runs.
        ScanResult result = _stopperRule == null && _escapeStartRule == null
            ? ScanFastPath(lexer)
            : ScanGeneralPath(lexer);

        if (result == ScanResult.Failed)
            return null; // the scan already recorded its failure

        if (result == ScanResult.ReachedEnd && !_eofIsTerminator)
        {
            // Strict: the scan ran off the end without matching the stopper.
            // Record at the stuck position (EOF). A .WithError rides along
            // there rather than being pulled back to the rule's start. See
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
    // this: they peek a sub-rule without consuming, which Read() can't do, so
    // they take ScanGeneralPath. 
    private ScanResult ScanFastPath(Lexer lexer)
    {
        while (!lexer.IsEof)
        {
            lexer.TickBudget();
            int position = lexer.Position;
            Token token = lexer.Read();

            // Escape-start check first, so an escape whose start shares a prefix
            // with a stopper still wins. Read() already consumed the start; its
            // end runs next. Token.RuneValue is -1 for a multi-rune cluster or
            // lone surrogate, so the int compare can only match a one-rune
            // token. The grapheme case (a single-rune start that decomposed to
            // a multi-rune cluster under the chosen form) compares the whole
            // cluster span instead.
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
            // this token started and stop. ContainsToken covers the single-rune
            // intervals, any multi-rune entries (e.g. a CRLF stopper), and the
            // lone-surrogate opt-in.
            if (_stopperSet.ContainsToken(token.Chars))
            {
                lexer.SetPosition(position);
                return ScanResult.StopperFound;
            }
            // Otherwise the token Read() consumed is body; keep scanning.
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
            // See Lexer.AdvanceWhileRuneIn for why every iteration
            // ticks; the whole scan is one rule invocation from the
            // engine's view.
            lexer.TickBudget();

            int position = lexer.Position;

            // Decode the next rune for the single-rune escape-start compare
            // below. On a lone surrogate TryPeekRune returns false (runeValue
            // -1, runeLength 0); that's fine, since the escape-start check's
            // tokenLength == runeLength rejects it and it falls through to body.
            Lexer.TryPeekRune(input, position, out int runeValue, out int runeLength);

            // tokenLength is the next whole token's length (one grapheme
            // cluster). The escape-start fast path and the stopper check
            // below both need it.
            int tokenLength = lexer.PeekTokenLength(position);

            // Escape-start check, before the stopper check (so the escape wins
            // when its start shares a prefix with a stopper). Single-rune and
            // Rule forms are mutually exclusive; the constructor picks one.
            // When the escape-start doesn't match, the scan falls through to
            // the stopper check below.
            if (_hasEscape)
            {
                if (_escapeStartRule != null)
                {
                    // General start path. The escape-start runs inside a
                    // Probe. On a mismatch it was pure lookahead, so the
                    // Probe's Dispose restores position, failure tracker,
                    // and subtree extent. On a match the start is consumed
                    // content, so probe.Commit keeps all three. See
                    // docs/ErrorArchitecture.md.
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
                            // Composite anchor: a multi-step escape-end's deeper inner
                            // failure would otherwise shadow this .WithError. See
                            // docs/ErrorArchitecture.md.
                            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                            return ScanResult.Failed;
                        }
                        // Zero-width guard: if both the start and end
                        // happen to be zero-width rules, position is
                        // unchanged and the loop would spin forever
                        // on the same rune. That's a grammar-author
                        // error (passing zero-width rules here makes
                        // no sense), but we break cleanly rather than
                        // hang. Same pattern as BetweenInclusiveRule.
                        if (lexer.Position == position) break;
                        continue;
                    }
                    // Start didn't match: the Probe restored the position,
                    // the failure tracker, and the subtree extent. Fall
                    // through to the stopper check.
                }
                else
                {
                    // No-sub-rule escape-start match. Normalization may have
                    // turned a single-rune start into a multi-rune cluster
                    // (e.g. 'é' → "e + U+0301" under FormD): _escapeStartGrapheme
                    // holds it and the whole cluster has to span-match. Otherwise
                    // it's a single rune, and tokenLength == runeLength requires a
                    // one-rune token, so '\<combining mark>' (one cluster, two
                    // runes) doesn't match, the same as Token('\\').
                    bool escapeStartMatched = _escapeStartGrapheme != null
                        ? position + tokenLength <= inputLength
                            && input.AsSpan(position, tokenLength).SequenceEqual(_escapeStartGrapheme.AsSpan())
                        : tokenLength == runeLength && runeValue == _escapeStartRune;
                    if (escapeStartMatched)
                    {
                        lexer.SetPosition(position + tokenLength);
                        var end = ParseChild(_escapeEnd!, lexer, outputSymbols: null);
                        if (end == null)
                        {
                            TraceFailure(lexer, $"bad escape end at offset {position + tokenLength}");
                            // Composite anchor, same shape as the rule-form path above.
                            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                            return ScanResult.Failed;
                        }
                        continue;
                    }
                }
            }

            // Stopper check. The TokenSet path is the fast case. The
            // Rule path opens a peek transaction that always rolls
            // back, so the stopper itself is never consumed by this
            // rule. ContainsToken handles both halves of the set
            // (single-rune intervals and multi-rune entries) against
            // the next full token, so a stopper of '"' doesn't match
            // a '"<combining-mark>' cluster, the same answer
            // OneOf("\"") would give on the same input.
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
                // The stopper runs as pure lookahead: ScanUntil never
                // consumes it (the surrounding grammar matches it). The
                // Probe brackets position, failure tracker, and subtree
                // extent, and with no Commit restores all three on
                // Dispose. See docs/ErrorArchitecture.md, "Lookahead
                // failures are discarded".
                bool stopMatched;
                using (lexer.BeginProbe())
                {
                    stopMatched = ParseChild(_stopperRule, lexer, outputSymbols: null) != null;
                }
                if (stopMatched)
                    return ScanResult.StopperFound;
            }

            // Not a stopper, not an escape start: consume the whole
            // token as body and keep scanning. Token-by-token advance
            // (rather than rune-by-rune) keeps the loop on real
            // grapheme-cluster boundaries, which is what the rest of
            // the parser sees.
            if (position + tokenLength > inputLength) break;
            lexer.SetPosition(position + tokenLength);
        }

        // Tests the Rule-stopper at the EOF position. The loop above runs the
        // stopper at each token position; this covers end of input, where an
        // EOF-sensitive stopper rule (Eof(), Not(AnyToken()), Or(..., Eof()))
        // can match, ending the body at EOF the same as a match mid-input.
        // Strict mode only: _eofIsTerminator already stops at EOF on its own.
        // TokenSet stoppers skip this, since a TokenSet is tested against a
        // real token and EOF produces none.
        if (!_eofIsTerminator && _stopperRule != null && lexer.IsEof)
        {
            // Same pure-lookahead Probe as the in-loop stopper check: the
            // stopper is never consumed by ScanUntil, and the probe's
            // position, failure tracker, and subtree extent are all
            // restored on Dispose.
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
