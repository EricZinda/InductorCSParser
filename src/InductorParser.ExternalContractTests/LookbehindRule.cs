// A user-defined Rule subclass: lookbehind. Succeeds, consuming nothing,
// when the inner rule matches the input ending exactly at the current
// position. The mirror image of the built-in Peek (which asserts content
// ahead); InductorParser ships no lookbehind of its own.
//
// Ported from LPeg's lpeg.B(patt) lookbehind predicate
// (https://www.inf.puc-rio.br/~roberto/lpeg/, by Roberto Ierusalimschy):
// "Returns a pattern that matches only if the input string at the current
// position is preceded by patt. Pattern patt must match only strings with
// some fixed length ... Like the and predicate, this pattern never
// consumes any input."
//
// LPeg is distributed under the MIT license, Copyright (c) 2007-2023
// Lua.org, PUC-Rio. This is an independent C# reimplementation written
// from LPeg's documented behavior (the manual text quoted above), not a
// translation of LPeg's C source, so no LPeg code is included here; the
// attribution is for the rule's design.
//
// `inner` should match a fixed length (a Literal, a single Token / OneOf,
// an Exactly), as lpeg.B requires. The rule finds where the preceding
// match must start by walking token boundaries backward from the current
// position and running `inner` from each, succeeding on the first whose
// match ends exactly at the current position. A fixed-length inner has one
// candidate that can match; a variable-length inner matches the shortest
// preceding span, which is rarely intended.
//
// It uses only the public surface (no internal members), which the no-IVT
// build of this project enforces: Lexer.SetPosition moves the cursor to an
// earlier token boundary, ParseChild runs `inner` there, and
// Lexer.PeekTokenLength walks the boundaries.
//
// Negative lookbehind ("NOT preceded by X") composes with the built-in
// Not: Not(new LookbehindRule(X)).

using System;
using System.Collections.Generic;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace InductorParser.ExternalContractTests;

public sealed class LookbehindRule : Rule
{
    public LookbehindRule(Rule inner)
        // Delete by default: a lookbehind is zero-width and contributes no
        // text to the tree, same as the built-in Peek / Not.
        : base(FlattenType.Delete, emitsLeaf: false, inner ?? throw new ArgumentNullException(nameof(inner)))
    {
    }

    private Rule Inner => Children[0];

    protected override Symbol? TryParseRule(
        Lexer lexer,
        int startPosition,
        FlattenType effectiveFlattenType,
        List<Symbol>? outputSymbols)
    {
        // Nothing precedes offset 0, so a lookbehind there can't match.
        // (LPeg fails the same way.)
        if (startPosition > 0 && MatchesBehind(lexer, startPosition))
        {
            TraceSuccess(lexer, $"inner matched ending at {startPosition}");
            // Zero-width: the probes below restored the cursor to
            // startPosition, and the engine's transaction commits it
            // unchanged. Record a zero-length span at the anchor.
            return effectiveFlattenType == FlattenType.Preserve
                ? new Symbol(Id, FlattenType, new List<Symbol>(), lexer.Input.AsMemory(startPosition, 0), lexer.Context)
                : Symbol.Discarded;
        }

        TraceFailure(lexer, $"no preceding match ending at {startPosition}");
        // Anchor the failure at the lookbehind's own start, where the user
        // would change something, the same place Peek / Not anchor theirs.
        lexer.RecordFailure(startPosition, ErrorMessage, ErrorForced);
        return null;
    }

    // Walk token boundaries up to startPosition (forward is the only public
    // direction: PeekTokenLength reads a token's length at a boundary), then
    // try each candidate start nearest-first. At each, run Inner inside a
    // probe and check it lands exactly on the anchor. The probe restores the
    // cursor and discards the behind-the-anchor failures Inner recorded, so
    // the excursion leaves nothing on the real parse path (same discipline
    // as Peek / Not).
    private bool MatchesBehind(Lexer lexer, int startPosition)
    {
        var boundaries = new List<int>();
        for (int position = 0; position < startPosition;)
        {
            boundaries.Add(position);
            int tokenLength = lexer.PeekTokenLength(position);
            if (tokenLength <= 0) break;
            position += tokenLength;
        }

        for (int index = boundaries.Count - 1; index >= 0; index--)
        {
            int candidate = boundaries[index];
            bool landedOnAnchor;
            using (lexer.BeginProbe())
            {
                lexer.SetPosition(candidate);
                bool innerMatched = ParseChild(Inner, lexer, outputSymbols: null) != null;
                landedOnAnchor = innerMatched && lexer.Position == startPosition;
                // No Commit: Dispose restores the cursor to startPosition and
                // drops Inner's failures recorded behind the anchor.
            }
            if (landedOnAnchor)
                return true;
        }
        return false;
    }
}
