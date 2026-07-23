// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/runtime (branch release/8.0), files
//   src/libraries/System.Private.CoreLib/src/System/Text/Unicode/TextSegmentationUtility.cs
//   src/libraries/System.Private.CoreLib/src/System/Text/Unicode/GraphemeClusterBreakType.cs
// License text in LICENSE-DOTNET-RUNTIME.txt next to this file.
//
// This is the parser's custom UAX #29 extended-grapheme-cluster segmenter,
// the implementation of "where does one user-visible character end and
// the next begin". .NET 5+ ships the same algorithm
// inside StringInfo, but Unity's Mono and IL2CPP runtimes ship a legacy
// pre-UAX-#29 implementation that segments differently (it splits CRLF,
// ZWJ emoji sequences, regional-indicator flags, and more). This
// built-in segmenter exists so those runtimes tokenize correctly, at
// Unicode 15.0 (the version .NET 8 ships). It's only used for those
// runtimes by default, the others use .NET's implementation.
//
// This port differs from the upstream file in two deliberate ways.
// Upstream writes the state machine once, generically, and plugs in a
// decoder function so the same code can walk UTF-8 bytes or UTF-16
// chars. The parser only ever segments C# strings, which are UTF-16,
// so this port drops that plumbing and reads chars directly. The
// reading happens in DecodeRuneAt at the bottom of the file, which
// explains how its results match upstream's. And the break type of
// a rune comes from the checked-in Unicode 15.0 table in
// GraphemeSegmentation.Data.cs instead of CharUnicodeInfo. Everything
// else is kept as close to upstream as possible, so diffing this file
// against a future dotnet/runtime version shows real changes only.
//
// The implementation is chosen by a process-wide setting, surfaced as
// UnicodeEnvironment.Implementation and resolved at most once per
// process. That one setting governs this segmenter and the UAX #15
// normalizer (UnicodeNormalization) together, so the two can never
// answer from different Unicode data, and the first query from either
// freezes the choice. The default, UnicodeImplementation.Automatic,
// means the right implementations for the runtime that loaded the
// assembly: each target framework's build bakes in its own answer, set
// by the INDUCTORPARSER_USE_BUNDLED_UNICODE symbol in
// InductorParser.csproj, and the runtime picks which assembly it
// loads. With the symbol undefined (the default), Automatic means the
// runtime's StringInfo, so segmentation stays in sync with the rest of
// the runtime's Unicode machinery: token boundaries, string.Normalize,
// and character categories all move together, at whatever Unicode
// version the runtime ships, and a newly added target framework gets
// that behavior with no extra wiring. The netstandard2.1 build defines
// the symbol, so Automatic means this built-in state machine there,
// because the StringInfo on the runtimes that load that build (Unity's
// Mono, IL2CPP) predates UAX #29. Callers on any build can override
// the default at startup by setting UnicodeEnvironment.Implementation
// before building grammars or parsing. The consequence for callers:
// parse results agree across machines when the machines end up on the
// same implementation, so a client and server that must agree either
// run the same runtime or both opt into Bundled.
// On .NET 8 the two implementations are verifiably identical (the
// differential tests in GraphemeSegmentationTests hold them equal), so
// the choice only shows once a newer runtime's Unicode data moves past
// 15.0.

using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace InductorParser.Lexing.Unicode;

// Grapheme cluster break property values, as specified in
// https://www.unicode.org/reports/tr29/#Grapheme_Cluster_Boundaries, Sec. 3.1.
// Order matches dotnet/runtime's GraphemeClusterBreakType. The values in
// GraphemeSegmentation.Data.cs store these as bytes, so reordering this
// enum means regenerating that file.
internal enum GraphemeClusterBreakType
{
    Other,
    CR,
    LF,
    Control,
    Extend,
    ZWJ,
    Regional_Indicator,
    Prepend,
    SpacingMark,
    L,
    V,
    T,
    LV,
    LVT,
    Extended_Pictograph,
}

/// <summary>
/// Computes UAX #29 extended grapheme cluster boundaries
/// (https://www.unicode.org/reports/tr29/). The built-in state machine
/// is compliant per Rev. 41
/// (https://www.unicode.org/reports/tr29/tr29-41.html), the Unicode
/// 15.0 edition of the spec,
/// and its generated break-property table in
/// GraphemeSegmentation.Data.cs is the other half of this partial
/// class. The implementation is chosen by the process-wide setting
/// surfaced as UnicodeEnvironment.Implementation, which governs the
/// segmenter and the normalizer together and is resolved and frozen on
/// the first query (the file header explains the policy).
/// </summary>
internal static partial class GraphemeSegmentation
{
    /// <summary>
    /// Given UTF-16 input text, returns the length (in chars) of the first
    /// extended grapheme cluster. If the input is
    /// empty, returns 0. The first call resolves and freezes the
    /// process-wide implementation choice
    /// (UnicodeEnvironment.Implementation): the length comes from the
    /// runtime's StringInfo or the built-in state machine below (the
    /// file header explains the policy).
    /// </summary>
    public static int GetLengthOfFirstExtendedGraphemeCluster(ReadOnlySpan<char> input)
    {
        return UnicodeEnvironment.ResolveUseBundled()
            ? GetBundledLengthOfFirstExtendedGraphemeCluster(input)
            : GetRuntimeLengthOfFirstExtendedGraphemeCluster(input);
    }

    // The StringInfo-backed path, used when the setting picks
    // UnicodeImplementation.Runtime. The #if picks the span-based
    // StringInfo overload on .NET 6+, which doesn't allocate. The
    // netstandard2.1 build has to allocate a string per call. Either
    // way the result is whatever segmentation the runtime's StringInfo
    // implements (pre-UAX-#29 on Unity's Mono).
    internal static int GetRuntimeLengthOfFirstExtendedGraphemeCluster(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty) return 0;
#if NET6_0_OR_GREATER
        return StringInfo.GetNextTextElementLength(input);
#else
        return StringInfo.GetNextTextElement(input.ToString(), 0).Length;
#endif
    }

    // The built-in segmenter. The body below is a near-literal paste of
    // GetLengthOfFirstExtendedGraphemeCluster from dotnet/runtime's
    // TextSegmentationUtility.cs, comments included. Compare against
    // the original at
    // https://github.com/dotnet/runtime/blob/release/8.0/src/libraries/System.Private.CoreLib/src/System/Text/Unicode/TextSegmentationUtility.cs
    // and expect only the two differences the file header lists (no
    // decoder delegate, break types from the checked-in table). Keep
    // any future edits out of the state machine so that diff stays
    // clean. Internal (not private) so the differential tests can
    // compare it against StringInfo directly, independent of how the
    // process-wide setting resolves.
    internal static int GetBundledLengthOfFirstExtendedGraphemeCluster(ReadOnlySpan<char> input)
    {
        // Algorithm given at https://www.unicode.org/reports/tr29/#Grapheme_Cluster_Boundary_Rules.

        Processor processor = new Processor(input);
        processor.MoveNext();

        // First, consume as many Prepend scalars as we can (rule GB9b).

        while (processor.CurrentType == GraphemeClusterBreakType.Prepend)
        {
            processor.MoveNext();
        }

        // Next, make sure we're not about to violate control character restrictions.
        // Essentially, if we saw Prepend data, we can't have Control | CR | LF data afterward (rule GB5).

        if (processor.CurrentCodeUnitOffset > 0)
        {
            if (processor.CurrentType == GraphemeClusterBreakType.Control
                || processor.CurrentType == GraphemeClusterBreakType.CR
                || processor.CurrentType == GraphemeClusterBreakType.LF)
            {
                goto Return;
            }
        }

        // Now begin the main state machine.

        GraphemeClusterBreakType previousClusterBreakType = processor.CurrentType;
        processor.MoveNext();

        switch (previousClusterBreakType)
        {
            case GraphemeClusterBreakType.CR:
                if (processor.CurrentType != GraphemeClusterBreakType.LF)
                {
                    goto Return; // rules GB3 & GB4 (only <LF> can follow <CR>)
                }

                processor.MoveNext();
                goto case GraphemeClusterBreakType.LF;

            case GraphemeClusterBreakType.Control:
            case GraphemeClusterBreakType.LF:
                goto Return; // rule GB4 (no data after Control | LF)

            case GraphemeClusterBreakType.L:
                if (processor.CurrentType == GraphemeClusterBreakType.L)
                {
                    processor.MoveNext(); // rule GB6 (L x L)
                    goto case GraphemeClusterBreakType.L;
                }
                else if (processor.CurrentType == GraphemeClusterBreakType.V)
                {
                    processor.MoveNext(); // rule GB6 (L x V)
                    goto case GraphemeClusterBreakType.V;
                }
                else if (processor.CurrentType == GraphemeClusterBreakType.LV)
                {
                    processor.MoveNext(); // rule GB6 (L x LV)
                    goto case GraphemeClusterBreakType.LV;
                }
                else if (processor.CurrentType == GraphemeClusterBreakType.LVT)
                {
                    processor.MoveNext(); // rule GB6 (L x LVT)
                    goto case GraphemeClusterBreakType.LVT;
                }
                else
                {
                    break;
                }

            case GraphemeClusterBreakType.LV:
            case GraphemeClusterBreakType.V:
                if (processor.CurrentType == GraphemeClusterBreakType.V)
                {
                    processor.MoveNext(); // rule GB7 (LV | V x V)
                    goto case GraphemeClusterBreakType.V;
                }
                else if (processor.CurrentType == GraphemeClusterBreakType.T)
                {
                    processor.MoveNext(); // rule GB7 (LV | V x T)
                    goto case GraphemeClusterBreakType.T;
                }
                else
                {
                    break;
                }

            case GraphemeClusterBreakType.LVT:
            case GraphemeClusterBreakType.T:
                if (processor.CurrentType == GraphemeClusterBreakType.T)
                {
                    processor.MoveNext(); // rule GB8 (LVT | T x T)
                    goto case GraphemeClusterBreakType.T;
                }
                else
                {
                    break;
                }

            case GraphemeClusterBreakType.Extended_Pictograph:
                // Attempt processing extended pictographic (rules GB11, GB9).
                // First, drain any Extend scalars that might exist

                while (processor.CurrentType == GraphemeClusterBreakType.Extend)
                {
                    processor.MoveNext();
                }

                // Now see if there's a ZWJ + extended pictograph again.

                if (processor.CurrentType != GraphemeClusterBreakType.ZWJ)
                {
                    break;
                }

                processor.MoveNext();
                if (processor.CurrentType != GraphemeClusterBreakType.Extended_Pictograph)
                {
                    break;
                }

                processor.MoveNext();
                goto case GraphemeClusterBreakType.Extended_Pictograph;

            case GraphemeClusterBreakType.Regional_Indicator:
                // We've consumed a single RI scalar. Try to consume another (to make it a pair).

                if (processor.CurrentType == GraphemeClusterBreakType.Regional_Indicator)
                {
                    processor.MoveNext();
                }

                // Standalone RI scalars (or a single pair of RI scalars) can only be followed by trailers.

                break; // nothing but trailers after the final RI

            default:
                break;
        }

        // rules GB9, GB9a
        while (processor.CurrentType == GraphemeClusterBreakType.Extend
            || processor.CurrentType == GraphemeClusterBreakType.ZWJ
            || processor.CurrentType == GraphemeClusterBreakType.SpacingMark)
        {
            processor.MoveNext();
        }

    Return:

        return processor.CurrentCodeUnitOffset; // rules GB2, GB999
    }

    // The grapheme cluster break property of one code point, looked up in
    // the generated transition table (see GraphemeSegmentation.Data.cs
    // for the representation). Unlike the state machine above, this
    // method isn't copied from upstream: it stands in for
    // CharUnicodeInfo.GetGraphemeClusterBreakType, which reads .NET's
    // internal packed tables that don't port, so this is our own ASCII
    // fast path plus a binary search over the checked-in arrays. The
    // [Explicit] UCD re-derivation test in GraphemeSegmentationDataTests
    // verifies its results per code point. Callers pass Unicode scalar
    // values. A surrogate code unit passed directly also lands on Other,
    // the same result upstream produces for ill-formed input via U+FFFD.
    internal static GraphemeClusterBreakType GetBreakType(int codePointValue)
    {
        // Printable ASCII dominates real input and is entirely Other, so
        // handle it with one range check instead of a binary search.
        if (codePointValue >= 0x20 && codePointValue < 0x7F)
            return GraphemeClusterBreakType.Other;

        // Binary search for the last transition at or below the code
        // point. RangeStarts[0] is 0, so the search always lands.
        int low = 0;
        int high = RangeStarts.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (RangeStarts[middle] <= codePointValue)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        return (GraphemeClusterBreakType)RangeValues[low];
    }

    // Upstream's Processor<T> from TextSegmentationUtility.cs with the
    // generic decoder plumbing removed (the first difference the file
    // header lists): same fields and the same MoveNext shape, but
    // char-only, decoding through DecodeRuneAt below instead of a
    // passed-in delegate.
    [StructLayout(LayoutKind.Auto)]
    private ref struct Processor
    {
        private readonly ReadOnlySpan<char> _buffer;
        private int _codeUnitLengthOfCurrentScalar;

        internal Processor(ReadOnlySpan<char> buffer)
        {
            _buffer = buffer;
            _codeUnitLengthOfCurrentScalar = 0;
            CurrentType = GraphemeClusterBreakType.Other;
            CurrentCodeUnitOffset = 0;
        }

        public int CurrentCodeUnitOffset { get; private set; }

        /// <summary>
        /// Will be <see cref="GraphemeClusterBreakType.Other"/> if invalid data or EOF reached.
        /// Caller shouldn't need to special-case this since the normal rules will halt on this condition.
        /// </summary>
        public GraphemeClusterBreakType CurrentType { get; private set; }

        public void MoveNext()
        {
            // For ill-formed subsequences (like unpaired UTF-16 surrogate code points), we rely on
            // the decoder's behavior of interpreting these ill-formed subsequences as
            // equivalent to U+FFFD REPLACEMENT CHARACTER. This code point has a boundary property
            // of Other (XX), which matches the modifications made to UAX#29, Rev. 35.
            // See: https://www.unicode.org/reports/tr29/tr29-35.html#Modifications
            // End of input also decodes as U+FFFD with zero length consumed, so the
            // state machine halts there the same way.

            CurrentCodeUnitOffset += _codeUnitLengthOfCurrentScalar;
            DecodeRuneAt(_buffer, CurrentCodeUnitOffset, out int runeValue, out _codeUnitLengthOfCurrentScalar);
            CurrentType = GetBreakType(runeValue);
        }
    }

    // Decode the Unicode scalar at offset, with the same behavior as
    // upstream Rune.DecodeFromUtf16
    // (https://learn.microsoft.com/en-us/dotnet/api/system.text.rune.decodefromutf16):
    // a well-formed surrogate pair
    // decodes as one scalar of length two, a lone surrogate decodes as
    // U+FFFD of length one, and end of input decodes as U+FFFD of length
    // zero. The U+FFFD convention is what halts the state machine because
    // U+FFFD's break type is Other, which every rule path stops on.
    // Substituting U+FFFD for a stray surrogate doesn't change any 
    // boundaries, because the spec gives surrogate code points the same
    // break class as U+FFFD (Other/XX, since rev. 35:
    // https://www.unicode.org/reports/tr29/tr29-35.html#Modifications).
    // The visible effect of that break class is that Other accepts trailing combining
    // marks, so a stray surrogate plus a combining mark segments as
    // one cluster.
    // To ensure it operates the same: there are differential tests in 
    // GraphemeSegmentationTests that compare
    // this segmenter against .NET 8's StringInfo for every code point
    // (lone surrogates included), the whole UnicodeExamples corpus and
    // its pairwise concatenations, and 100,000 randomized sequences.
    private static void DecodeRuneAt(
        ReadOnlySpan<char> buffer, int offset, out int runeValue, out int lengthConsumed)
    {
        if (offset >= buffer.Length)
        {
            runeValue = 0xFFFD;
            lengthConsumed = 0;
            return;
        }
        char first = buffer[offset];
        if (!char.IsSurrogate(first))
        {
            runeValue = first;
            lengthConsumed = 1;
            return;
        }
        if (RuneHelpers.IsSurrogatePairAt(buffer, offset))
        {
            runeValue = char.ConvertToUtf32(first, buffer[offset + 1]);
            lengthConsumed = 2;
            return;
        }
        runeValue = 0xFFFD;
        lengthConsumed = 1;
    }
}
