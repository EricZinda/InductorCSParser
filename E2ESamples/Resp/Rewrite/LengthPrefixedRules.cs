// Two user-defined Rule subclasses for length-prefixed input: read a count
// from the input, then consume input driven by that count. They're a port of
// the two "length_*" combinators nom ships in its `multi` module:
//
//   * LengthDataRule  <- nom's `length_data`: "Gets a number from the parser
//     and returns a subslice of the input of that size." Consume exactly that
//     many characters as one leaf.
//   * LengthCountRule <- nom's `length_count`: "Gets a number from the first
//     parser, then applies the second parser that many times." Run an item
//     rule exactly that many times and collect the results.
//
// nom (https://github.com/rust-bakery/nom) is a Rust parser-combinator library,
// MIT-licensed, Copyright (c) 2014-2019 Geoffroy Couprie. The two combinators
// live in src/multi/mod.rs (doc text quoted above is verbatim from upstream
// commit fcc9f16b31804448d5cc71f6474c883ac7cf5624). This is an independent C#
// reimplementation from the documented behavior, not a translation of nom's
// Rust source. nom returns the count parser's *value*. The InductorParser
// analog of a rule's value is its Symbol's text, so these rules read the count
// rule's Symbol text (ToString(), the leaves that reached the tree) and parse a
// non-negative integer out of it. A count rule can therefore Delete framing it
// doesn't want counted, the same way nom's count parser can map its match to a
// number that isn't a literal copy of the bytes it read. See ReadCount.
//
// Why a custom rule and not a composition of built-ins:
//
//   These are genuinely context-sensitive: how much input the rule consumes is
//   decided at parse time by a number that's *in* the input. No fixed grammar
//   built from And / Or / OneOrMore / BetweenInclusive can express "now match
//   exactly N more things" because N isn't known until the count is read. This
//   is the textbook case that takes a parser beyond what context-free
//   combinators can do, which is exactly why nom ships these as primitives
//   instead of leaving them to its other combinators. The matching logic has
//   to live below the combinator layer, in a Rule subclass that can read the
//   count and then loop on it.
//
// Both are built on the public + protected surface of InductorParser:
// ParseRuleAgainst runs the count and separator rules (discarding their
// symbols, since only the count's *value* matters), ParseChild runs each item
// with its own transaction, lexer.Read advances the cursor one grapheme
// cluster at a time, lexer.TickBudget bounds every loop so the Timeout /
// Cancellation / RuleCountLimit budget can observe the work, and
// lexer.RecordCompositeFailure anchors the failure where a user would look. No
// internal members.
//
// A divergence from nom worth stating up front, because it's a real behavior
// change and not just a different counting unit. nom's length_data splits the
// input freely at the count boundary: on byte input it takes exactly N bytes,
// on a string it takes exactly N code points (Rust chars), cutting through a
// grapheme cluster without complaint. nom has no concept of grapheme clusters.
//
// LengthDataRule can't reproduce that, and the reason is the lexer surface, not
// a choice. The only way a rule advances the cursor is lexer.Read(), whose
// smallest step is one grapheme cluster in normal mode (one rune in the
// WithinToken sub-lexer mode). It never stops in the middle of a cluster or a
// rune. But lexer.Position, and so the `length` these rules count against, is
// in UTF-16 code units (C# chars), which is a *finer* unit than Read can land
// on. So a length that points inside a multi-char grapheme (a surrogate pair, a
// combining sequence, a flag emoji) names an offset Read can never reach, and
// the rule rejects it rather than splitting the cluster. nom would have
// returned a result there. There's no public API to advance Position by a raw
// char count, so this isn't worked around at the rule level. See the backlog
// item "No primitive to advance the cursor by a fixed code-unit (or byte)
// count" for the underlying gap. For ASCII payloads, the common case for the
// text protocols these target, none of this bites: one char per rune per
// grapheme, and byte / code-unit / code-point counts all agree.
//
// These rules are still a great sample, though, just not exactly what the original did.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using InductorParser;
using InductorParser.Lexing;
using InductorParser.SyntaxTree;

namespace RespSample.Rewrite;

// Shared base for the length-prefixed family: both rules start by reading a
// count and a separator, and only then diverge (consume N characters vs run a
// child N times). The shared part lives here so neither subclass reinvents the
// count read or its error anchoring.
public abstract class LengthPrefixedRule : Rule
{
    protected readonly Rule Count;
    protected readonly Rule Separator;

    protected LengthPrefixedRule(
        FlattenType defaultFlatten, bool emitsLeaf,
        Rule count, Rule separator, params Rule[] rest)
        : base(defaultFlatten, emitsLeaf, BuildChildren(count, separator, rest))
    {
        Count = count;
        Separator = separator;
    }

    private static Rule[] BuildChildren(Rule count, Rule separator, Rule[] rest)
    {
        if (count == null) throw new ArgumentNullException(nameof(count));
        if (separator == null) throw new ArgumentNullException(nameof(separator));
        var children = new Rule[2 + rest.Length];
        children[0] = count;
        children[1] = separator;
        for (int index = 0; index < rest.Length; index++)
            children[2 + index] = rest[index];
        return children;
    }

    // Read the count rule, parse a non-negative integer from the *text of its
    // Symbol*, then consume the separator. Returns the count, or null on any
    // failure (with the failure recorded so the error reporter can surface it).
    // The separator's Symbol is discarded: only that it consumed matters.
    //
    // The count's value is its Symbol's text, not the characters it consumed.
    // That's the same decoupling nom's length_data has (the count parser
    // returns a number, independent of what bytes it read), and it lets a count
    // rule Delete framing it doesn't want in the number: a count of
    // And(Token('#').Delete(), digits) reads "#12" but ToString()s to "12".
    // The rule is simply that the count Symbol's ToString() is the decimal
    // number. int.TryParse with NumberStyles.None still rejects a sign or any
    // stray character, so a bad count fails here rather than producing a wrong
    // length.
    protected int? ReadCount(Lexer lexer)
    {
        // ToString() reads the leaves that reached the tree: the returned
        // Symbol for a Preserve count rule, or the collected leaves for a
        // Flatten one. Capture both and let CountText pick.
        var countSymbols = new List<Symbol>();
        var countResult = ParseRuleAgainst(Count, lexer, countSymbols);
        if (countResult == null)
        {
            TraceFailure(lexer, $"count rule did not match");
            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
            return null;
        }

        string text = CountText(countResult, countSymbols);
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            TraceFailure(lexer, $"count '{text}' is not a non-negative integer");
            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
            return null;
        }

        if (ParseRuleAgainst(Separator, lexer, null) == null)
        {
            TraceFailure(lexer, $"missing separator after count {count}");
            lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
            return null;
        }

        return count;
    }

    // A Preserve count rule returns its Symbol directly. A Flatten one
    // returns Discarded and writes its leaves into `collected`. Either way the
    // number is the concatenation of the leaf text, which is what ToString does.
    private static string CountText(Symbol countResult, List<Symbol> collected)
    {
        if (!ReferenceEquals(countResult, Symbol.Discarded))
            return countResult.ToString();
        if (collected.Count == 1)
            return collected[0].ToString();
        var builder = new StringBuilder();
        foreach (var symbol in collected)
            builder.Append(symbol.ToString());
        return builder.ToString();
    }
}

// nom's length_data: read a count, then return the next `count` characters as
// one leaf. The genuinely context-sensitive piece, and the one no composition
// of context-free combinators can express.
public sealed class LengthDataRule : LengthPrefixedRule
{
    public LengthDataRule(Rule count, Rule separator)
        // Preserve by default: the consumed run is a value the grammar wants in
        // the tree (a bulk string's bytes). A caller who wants it dropped uses
        // .Delete(). Naming it with .As(name) keeps Preserve and findable.
        : base(FlattenType.Preserve, emitsLeaf: true, count, separator)
    {
    }

    protected override Symbol? TryParseRule(
        Lexer lexer,
        int startPosition,
        FlattenType effectiveFlattenType,
        List<Symbol>? outputSymbols)
    {
        int? maybeCount = ReadCount(lexer);
        if (maybeCount == null) return null;
        int length = maybeCount.Value;

        int dataStart = lexer.Position;
        int dataEnd = dataStart + length;

        // Advance to the code-unit offset dataEnd one grapheme cluster at a
        // time, which is Read's unit in normal mode. `length` is a code-unit
        // count, so a length that lands inside a cluster overshoots dataEnd and
        // is rejected by the check after the loop, rather than splitting it.
        // Same tick-budgeted shape as the built-in bulk scanners.
        while (lexer.Position < dataEnd)
        {
            lexer.TickBudget();
            if (lexer.IsEof)
            {
                TraceFailure(lexer, $"input ended {dataEnd - lexer.Position} chars short of the {length}-char payload");
                lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                return null;
            }
            lexer.Read();
        }

        // Overshoot means a multi-code-unit cluster straddled the boundary, so
        // the declared length splits a character in half. Reject it rather than
        // emit a leaf that ends mid-character.
        if (lexer.Position != dataEnd)
        {
            TraceFailure(lexer, $"length {length} falls inside a multi-unit character");
            lexer.RecordCompositeFailure(dataStart, ErrorMessage, ErrorForced);
            return null;
        }

        TraceSuccess(lexer, $"consumed {length}-char payload");

        if (effectiveFlattenType == FlattenType.Delete)
            return Symbol.Discarded;

        var leaf = new Symbol(Id, FlattenType,
            lexer.Input.AsMemory(dataStart, length), lexer.Context);
        if (effectiveFlattenType == FlattenType.Flatten)
        {
            outputSymbols!.Add(leaf);
            return Symbol.Discarded;
        }
        return leaf;
    }
}

// nom's length_count: read a count, then run an item rule exactly that many
// times, collecting the matches. Each item is self-delimiting, so there's no
// separator between items, only the one separator between the count and the
// first item.
public sealed class LengthCountRule : LengthPrefixedRule
{
    public LengthCountRule(Rule count, Rule separator, Rule item)
        // Flatten by default like And / Or: the collected items usually want to
        // bubble into the parent. A caller who names it with .As(...) gets
        // Preserve and a findable Symbol whose children are the items.
        : base(FlattenType.Flatten, emitsLeaf: false, count, separator, RequireItem(item))
    {
    }

    private static Rule RequireItem(Rule item) =>
        item ?? throw new ArgumentNullException(nameof(item));

    // The item rule is the third child (count and separator are 0 and 1).
    private Rule Item => Children[2];

    protected override Symbol? TryParseRule(
        Lexer lexer,
        int startPosition,
        FlattenType effectiveFlattenType,
        List<Symbol>? outputSymbols)
    {
        int? maybeCount = ReadCount(lexer);
        if (maybeCount == null) return null;
        int count = maybeCount.Value;

        // Preserve builds its own child list to wrap. Flatten writes into the
        // caller's list. Delete writes nowhere (outputSymbols is null here).
        List<Symbol>? collected =
            effectiveFlattenType == FlattenType.Preserve ? new List<Symbol>() : outputSymbols;

        for (int index = 0; index < count; index++)
        {
            // Tick once per item so a huge declared count can't pin the parser
            // past its budget before the items run out.
            lexer.TickBudget();

            var itemSymbol = ParseChild(Item, lexer, collected);
            if (itemSymbol == null)
            {
                TraceFailure(lexer, $"item {index + 1} of {count} failed to match");
                lexer.RecordCompositeFailure(lexer.Position, ErrorMessage, ErrorForced);
                return null;
            }

            // A Preserve item hands back its own Symbol here, so add it. A
            // Flatten item already wrote its pieces into `collected` and
            // returned Discarded, so adding it would double-count. Same rule
            // And and BetweenInclusive follow.
            if (collected != null && !ReferenceEquals(itemSymbol, Symbol.Discarded))
                collected.Add(itemSymbol);
        }

        TraceSuccess(lexer, $"matched all {count} items");

        if (effectiveFlattenType != FlattenType.Preserve)
            return Symbol.Discarded;

        int length = lexer.Position - startPosition;
        return new Symbol(Id, FlattenType, collected!,
            lexer.Input.AsMemory(startPosition, length), lexer.Context);
    }
}
