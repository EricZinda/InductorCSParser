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
//     set. Only does one TokenSet.Contains per rune and handles any grammar whose
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
// Lazy decoding means a syntax highlighter or a code-formatter, which WANTS the raw
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
    private readonly string _stopperRendered;

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
    private readonly int _escapeStartRune;
    private readonly Rule? _escapeStartRule;

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

    // FAST PATH, no escape. Per rune: one TokenSet.Contains.
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

    // FAST PATH, single-rune escape start. Per rune: one
    // TokenSet.Contains plus one int equality on non-stopper runes.
    // Covers JSON, C, C++ regular, Python single-line.
    public ScanUntilRule(TokenSet stopAt, Rune escapeStart, Rule escapeEnd, bool eofIsTerminator = false)
        : base(FlattenType.Preserve, emitsLeaf: true, escapeEnd)
    {
        if (escapeEnd == null)
            throw new ArgumentNullException(nameof(escapeEnd));
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
        : base(FlattenType.Preserve, emitsLeaf: true, escapeStart, escapeEnd)
    {
        if (escapeStart == null)
            throw new ArgumentNullException(nameof(escapeStart));
        if (escapeEnd == null)
            throw new ArgumentNullException(nameof(escapeEnd));
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
        : base(FlattenType.Preserve, emitsLeaf: true, stopAt)
    {
        if (stopAt == null)
            throw new ArgumentNullException(nameof(stopAt));
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
        : base(FlattenType.Preserve, emitsLeaf: true, stopAt, escapeEnd)
    {
        if (stopAt == null)
            throw new ArgumentNullException(nameof(stopAt));
        if (escapeEnd == null)
            throw new ArgumentNullException(nameof(escapeEnd));
        _stopperSet = default;
        _stopperRule = stopAt;
        _stopperRendered = $"rule {stopAt.Name ?? stopAt.GetType().Name}";
        _escapeEnd = escapeEnd;
        _hasEscape = true;
        _escapeStartRune = escapeStart.Value;
        _escapeStartRule = null;
        _eofIsTerminator = eofIsTerminator;
    }

    internal override void CollectNormalizationOffenders(
        System.Text.NormalizationForm form,
        List<(Rule rule, string original, string normalized)> offenders,
        List<ArgumentException> failures)
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
        // walker's recursion into Children, so only _stopperSet needs
        // projection here.
        if (_stopperRule == null)
            OneOfRule.NormalizeAndValidate(this, ref _stopperSet, form, offenders);
    }

    internal override Symbol? TryParseRule(Lexer lexer, int startPosition, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        string input = lexer.Input;
        int inputLen = input.Length;

        // Scan forward one token (one user-perceived character) at a
        // time: a token is one grapheme cluster. The loop
        // has two ways out: end-of-input (the while condition) or a
        // stopper match. Which one happened decides the post-loop
        // path: stopper match always succeeds; EOF succeeds only if
        // _eofIsTerminator is true (the tolerant and ScanUntilEof
        // cases), and otherwise fails with "unterminated body". The
        // local `stopperMatched` flag carries that decision out of
        // the loop. Each iteration consumes one token as body or one
        // escape sequence.
        //
        // The loop bound is `!lexer.IsEof`, which is `_position <
        // _endPosition` for a sub-lexer (the one WithinToken hands us
        // when this rule is the inner of WithinToken(ScanUntil(...))).
        // Comparing against input.Length would be the FULL outer string
        // and let the loop run past the sub-lexer's bound: PeekTokenLength
        // would return 0 there, the body fall-through's
        // SetPositionUnchecked(pos + 0) wouldn't move the cursor, and the
        // loop would spin forever.
        //
        // Lone surrogates flow through as body. The lexer surfaces
        // each unpaired surrogate code unit as a one-char token with
        // RuneValue == -1 (see UnexpectedUnicodeTests for the canonical
        // behavior). Such a token can't be in any TokenSet (entries
        // are valid Unicode scalars) and can't equal the escape-start
        // rune (also a valid scalar), so the stopper and escape-start
        // checks both correctly say "no match" and the body fall-
        // through advances past it. The Memory the leaf Symbol holds
        // is a zero-copy slice of the input string, so the surrogate
        // round-trips through ToString() byte-for-byte. Mirrors what
        // ZeroOrMore(NoneOf(stopAt)) would do on the same input.
        // Lone surrogates only reach this rule under Compile(null),
        // because string.Normalize rejects malformed UTF-16 with
        // ArgumentException out of Parse() under any other form.
        bool stopperMatched = false;
        while (!lexer.IsEof)
        {
            int pos = lexer.Position;

            // Peek the next rune for the single-rune escape-start
            // fast path's runeValue compare. TryPeekRune returns false
            // on a lone surrogate (sets runeValue = -1, runeLen = 0);
            // we DON'T short-circuit on that, because the surrogate
            // is still a valid token and falls through to body. The
            // tokenLen != runeLen check on the escape-start branch
            // already excludes lone-surrogate tokens (tokenLen == 1,
            // runeLen == 0) from the fast path, and -1 isn't a valid
            // escape-start rune anyway, so no further guard is needed.
            Lexer.TryPeekRune(input, pos, out int runeValue, out int runeLen);

            // tokenLen is the next whole token's length (one grapheme
            // cluster). The escape-start fast path and the stopper check
            // below both need it.
            int tokenLen = lexer.PeekTokenLength(pos);

            // Escape-start check, BEFORE the stopper check (see the
            // header comment for why the escape wins when its start
            // shares a prefix with a stopper). Single-rune and Rule
            // forms are mutually exclusive; the constructor picks one.
            // When the escape-start doesn't match here, the scan falls
            // through to the stopper check below.
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
                        start = _escapeStartRule.TryParse(lexer, outputSymbols: null);
                        if (start != null)
                            probe.Commit();
                    }
                    if (start != null)
                    {
                        // Start matched and was kept. End failure is a hard
                        // failure: an escape sequence was started, so
                        // the input isn't a well-formed string body.
                        var end = _escapeEnd!.TryParse(lexer, outputSymbols: null);
                        if (end == null)
                        {
                            TraceFailure(lexer, $"bad escape end at offset {lexer.Position}");
                            // Composite anchor: a multi-step escape-end's deeper inner
                            // failure would otherwise shadow this .WithError. See
                            // docs/ErrorArchitecture.md.
                            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                            return null;
                        }
                        // Zero-width guard: if both the start and end
                        // happen to be zero-width rules, position is
                        // unchanged and the loop would spin forever
                        // on the same rune. That's a grammar-author
                        // error (passing zero-width rules here makes
                        // no sense), but we break cleanly rather than
                        // hang. Same pattern as BetweenInclusiveRule.
                        if (lexer.Position == pos) break;
                        continue;
                    }
                    // Start didn't match: the Probe restored the position,
                    // the failure tracker, and the subtree extent. Fall
                    // through to the stopper check.
                }
                else if (tokenLen == runeLen && runeValue == _escapeStartRune)
                {
                    // Single-rune start fast path. The token must be
                    // exactly the escape rune with nothing else glued
                    // onto it: '\' alone matches, but '\<combining
                    // mark>' (one cluster, two runes by UAX #29 GB9)
                    // does NOT, the same way Token('\\') would refuse
                    // it. tokenLen == runeLen is the test for "this
                    // cluster is one rune long," which is what makes
                    // the fast path safe under the parser-wide
                    // grapheme invariant.
                    lexer.SetPositionUnchecked(pos + tokenLen);
                    var end = _escapeEnd!.TryParse(lexer, outputSymbols: null);
                    if (end == null)
                    {
                        TraceFailure(lexer, $"bad escape end at offset {pos + tokenLen}");
                        // Composite anchor, same shape as the rule-form path above.
                        lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                        return null;
                    }
                    continue;
                }
            }

            // Stopper check. The TokenSet path is the fast case. The
            // Rule path opens a peek transaction that always rolls
            // back, so the stopper itself is never consumed by this
            // rule. ContainsToken handles both halves of the set
            // (single-rune intervals and multi-rune entries) against
            // the next full token, so a stopper of '"' doesn't match
            // a '"<combining-mark>' cluster — the same answer
            // OneOf("\"") would give on the same input.
            if (_stopperRule == null)
            {
                if (pos + tokenLen <= inputLen
                    && _stopperSet.ContainsToken(input.AsSpan(pos, tokenLen)))
                {
                    stopperMatched = true;
                    break;
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
                    stopMatched = _stopperRule.TryParse(lexer, outputSymbols: null) != null;
                }
                if (stopMatched)
                {
                    stopperMatched = true;
                    break;
                }
            }

            // Not a stopper, not an escape start: consume the whole
            // token as body and keep scanning. Token-by-token advance
            // (rather than rune-by-rune) keeps the loop on real
            // grapheme-cluster boundaries, which is what the rest of
            // the parser sees.
            if (pos + tokenLen > inputLen) break;
            lexer.SetPositionUnchecked(pos + tokenLen);
        }

        // Tests the Rule-stopper at the EOF position. The scan loop
        // above runs the stopper at each token position; this covers
        // end of input, where an EOF-sensitive stopper rule (Eof(),
        // Not(AnyToken()), Or(..., Eof())) can match. A match here ends
        // the body at EOF, the same as a match mid-input. Strict mode
        // only: _eofIsTerminator already stops at EOF on its own.
        // TokenSet stoppers skip this — a TokenSet is tested against a
        // real token, and EOF produces none.
        if (!stopperMatched && !_eofIsTerminator && _stopperRule != null && lexer.IsEof)
        {
            // Same pure-lookahead Probe as the in-loop stopper check: the
            // stopper is never consumed by ScanUntil, and the probe's
            // position, failure tracker, and subtree extent are all
            // restored on Dispose.
            bool matchedAtEof;
            using (lexer.BeginProbe())
            {
                matchedAtEof = _stopperRule.TryParse(lexer, outputSymbols: null) != null;
            }
            if (matchedAtEof)
                stopperMatched = true;
        }

        if (!stopperMatched && !_eofIsTerminator)
        {
            // Strict: the loop ran off the end without ever matching
            // the stopper. Record at the stuck position the scan
            // reached (EOF, here). A .WithError rides along at that
            // same spot rather than being pulled back to the rule's
            // start. See docs/ErrorArchitecture.md.
            TraceFailure(lexer, $"unterminated body, expected stopper '{_stopperRendered}'");
            lexer.RecordFailure(lexer.Position, ErrorMessage, ErrorForced);
            return null;
        }

        int length = lexer.Position - startPosition;
        TraceSuccess(lexer, $"{length} chars, stopper '{_stopperRendered}'");
        if (effectiveFlattenType == FlattenType.Delete) return Symbol.Discarded;
        var leafSymbol = new Symbol(Id, FlattenType, input.AsMemory(startPosition, length), lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leafSymbol);
            return Symbol.Discarded;
        }
        return leafSymbol;
    }

    internal override RuleStartRequirements ComputeRuleStart() =>
        RuleStartRequirements.MayAdvanceByAnyTokens;
}
