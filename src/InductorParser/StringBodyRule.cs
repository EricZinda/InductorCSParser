using System;
using System.Collections.Generic;
using System.Text;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser;

// This is the leaf for a JSON / C++ / Python string body.
// It is a specialized scanner for the "string body" grammar shape: scan runes
// forward until a stopper character or characters is seen, handling escape sequences inline.
// Collapses ZeroOrMore(Or(bodyRune, And(escapeStart, escapeEnd))) into one rule that
// does the scan in a tight loop and returns one leaf Symbol
// covering the matched section of input. One dispatch for the outer rule
// and one Symbol allocation per matched run, however many runes the run
// contains.
//
// There are two options for the string body stop condition:
//   * RuneSet stopAt (fast path): stop when the next rune is in the
//     set. Only does one RuneSet.Contains per rune and handles any grammar whose
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
// The order of checks is: stopper first, then escape start.
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
// captured. That is context-sensitive and not directly expressible
// as a fixed Rule at grammar-build time.
internal sealed class StringBodyRule : Rule
{
    // Stopper discrimination. _stopperRule != null selects the general
    // path, otherwise _stopperSet is used. The general path is one
    // predictable branch per rune. JSON-style grammars that take the
    // RuneSet path never pay for Rule dispatch.
    private readonly RuneSet _stopperSet;
    private readonly Rule? _stopperRule;
    private readonly string _stopperRendered;

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
    // StringBody fails as a whole (a started escape that can't
    // complete isn't a well-formed body) and the outer transaction
    // rolls the lexer back to where StringBody opened.
    private readonly Rule? _escapeEnd;

    // FAST PATH, no escape. Per rune: one RuneSet.Contains.
    public StringBodyRule(RuneSet stopAt)
        : base(FlattenType.Preserve)
    {
        _stopperSet = stopAt;
        _stopperRule = null;
        _stopperRendered = stopAt.ToString();
        _escapeEnd = null;
        _hasEscape = false;
        _escapeStartRune = -1;
        _escapeStartRule = null;
    }

    // Accessors for the state-machine lowering pass (StateMachine/Lowerer.cs).
    // The recursive evaluator reads these private fields directly inside
    // TryParseRule; the lowering pass needs the same data without
    // running the rule.
    internal RuneSet LoweringStopperSet => _stopperSet;
    internal Rule? LoweringStopperRule => _stopperRule;
    internal bool LoweringHasEscape => _hasEscape;
    internal int LoweringEscapeStartRune => _escapeStartRune;
    internal Rule? LoweringEscapeStartRule => _escapeStartRule;
    internal Rule? LoweringEscapeEnd => _escapeEnd;

    // FAST PATH, single-rune escape start. Per rune: one
    // RuneSet.Contains plus one int equality on non-stopper runes.
    // Covers JSON, C, C++ regular, Python single-line.
    public StringBodyRule(RuneSet stopAt, Rune escapeStart, Rule escapeEnd)
        : base(FlattenType.Preserve, escapeEnd)
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
    }

    // General escape start. Adds one Rule.TryParse on non-stopper
    // runes only. Use for multi-rune starts like $$ or a choice
    // across several starts.
    public StringBodyRule(RuneSet stopAt, Rule escapeStart, Rule escapeEnd)
        : base(FlattenType.Preserve, escapeStart, escapeEnd)
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
    }

    // General stopper, no escape. Per rune: one Rule.TryParse for
    // the stopper (peek transaction, never consumed). Use for
    // multi-rune boundaries like C++ raw strings.
    public StringBodyRule(Rule stopAt)
        : base(FlattenType.Preserve, stopAt)
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
    }

    // General stopper with single-rune escape start. Canonical use:
    // Python triple-quote """...""" with backslash escapes.
    public StringBodyRule(Rule stopAt, Rune escapeStart, Rule escapeEnd)
        : base(FlattenType.Preserve, stopAt, escapeEnd)
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
    }

    internal override Symbol? TryParseRule(Lexer lexer, FlattenType effectiveFlattenType, List<Symbol>? outputSymbols)
    {
        using var transaction = lexer.BeginTransaction();
        int startPosition = transaction.StartPosition;
        string input = lexer.Input;
        int inputLen = input.Length;

        // Scan forward one rune at a time. The loop has three ways out:
        // end-of-input (the while condition), a stopper match, or a
        // malformed UTF-16 surrogate that can't form a rune. Each
        // iteration consumes one rune as body, one escape sequence,
        // or bails to one of those exits.
        while (lexer.Position < inputLen)
        {
            int pos = lexer.Position;

            // Peek the next rune without advancing the lexer so the
            // fast stopper-check path can decide whether to consume.
            // Rune-scoped even under GraphemeLexer because RuneSet
            // membership and escape-start comparison are both
            // rune-scoped.
            if (!Lexer.TryPeekRune(input, pos, out int runeValue, out int runeLen))
                // Malformed UTF-16 escape hatch:
                // An isolated surrogate half can't match any
                // RuneSet or rune start, so stop the scan and let the
                // surrounding grammar decide whether it's an error.
                break;

            // Stopper check. The RuneSet path is the fast case. The
            // Rule path opens a peek transaction that always rolls
            // back, so the stopper itself is never consumed by this
            // rule.
            if (_stopperRule == null)
            {
                if (_stopperSet.Contains(runeValue)) break;
            }
            else
            {
                using var peek = lexer.BeginTransaction();
                var stopMatch = _stopperRule.TryParse(lexer, outputSymbols: null);
                // No Commit: the `using` disposes the transaction and
                // rolls the position back regardless of what the
                // stopper rule consumed.
                if (stopMatch != null) break;
            }

            // Escape-start check. Single-rune and Rule forms are
            // mutually exclusive. The constructor picks one.
            if (_hasEscape)
            {
                if (_escapeStartRule != null)
                {
                    // General start path. TryParse opens its own
                    // transaction, so a start mismatch rolls the
                    // position back to `pos` and we fall through to
                    // consume the rune as body.
                    var start = _escapeStartRule.TryParse(lexer, outputSymbols: null);
                    if (start != null)
                    {
                        // Start committed. End failure is a hard
                        // failure: an escape sequence was started, so
                        // the input isn't a well-formed string body.
                        var end = _escapeEnd!.TryParse(lexer, outputSymbols: null);
                        if (end == null)
                        {
                            TraceFailure(lexer, $"bad escape end at offset {lexer.Position}");
                            // Error Positioning: lexer.Position after
                            // the end's rollback sits at the offset
                            // where the end started trying (just past
                            // the start). Point user-facing errors
                            // there, not at the string opener.
                            lexer.RecordFailure(lexer.Position, ErrorMessage);
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
                    // Start didn't match: fall through to consume as body.
                }
                else if (runeValue == _escapeStartRune)
                {
                    // Single-rune start fast path. Consume the start,
                    // then hand off to the end.
                    lexer.Read();
                    var end = _escapeEnd!.TryParse(lexer, outputSymbols: null);
                    if (end == null)
                    {
                        TraceFailure(lexer, $"bad escape end at offset {pos + runeLen}");
                        lexer.RecordFailure(pos + runeLen, ErrorMessage);
                        return null;
                    }
                    continue;
                }
            }

            // Not a stopper, not an escape start: consume one rune as
            // body and keep scanning. Using Read keeps the position
            // bookkeeping (tracing, EOF handling) in one place rather
            // than duplicating the increment here.
            lexer.Read();
        }

        int length = lexer.Position - startPosition;
        TraceSuccess(lexer, $"{length} chars, stopper '{_stopperRendered}'");
        transaction.Commit();
        if (effectiveFlattenType == FlattenType.Delete) return Symbol.Discarded;
        var leafSymbol = new Symbol(Id, FlattenType, input.AsMemory(startPosition, length));
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
        // StringBody always succeeds (a zero-length body is legal),
        // but it also consumes runes when the input has matchable ones.
        // That's Advance.Sometimes.
        //
        // FirstConsumedRunes: the body consumes any rune not in the stopper
        // set (the stop check fires first in the scan loop, so a stopper
        // rune is never consumed). That's ~_stopperSet for the RuneSet
        // stopper path. A Rule-based stopper can't be rendered as a rune
        // set, so we stay at Universe there. Escape-start runes, if a
        // grammar has them, are always outside the stopper set: the
        // scan loop checks the stopper before the escape, so an
        // escape-start that was also a stopper would be unreachable
        // dead code. That means ~_stopperSet already covers the
        // escape path. No separate union needed.
        RuneSet firstConsumed = _stopperRule == null
            ? ~_stopperSet
            : RuneSet.Universe;
        return new RuleStartRequirements(firstConsumed, Advance.Sometimes);
    }
}
