using System;
using System.Text;
using InductorParser.Lexing.Unicode;

namespace InductorParser.Lexing;

/// <summary>
/// Shared normalization helpers built on <c>UnicodeNormalization</c>,
/// so callers get the same answers the parser does. This is the
/// normalization layer. Its segmentation counterpart is
/// <see cref="GraphemeHelpers"/>. Public so user-defined rules and
/// test oracles can normalize text exactly the way <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> and Parse
/// do. Which normalizer is used is the process-wide
/// <see cref="UnicodeEnvironment.Implementation">UnicodeEnvironment.Implementation</see> setting, which
/// governs normalization and segmentation together.
/// </summary>
public static class NormalizationHelpers
{
    /// <summary>
    /// Normalize <paramref name="text"/> to <paramref name="form"/>
    /// with the process-wide normalizer, exactly the way Parse
    /// normalizes input and <see cref="InductorParser.Rule.Compile(System.Text.NormalizationForm?)">Rule.Compile</see> converts grammar text. Returns the
    /// same string instance when the text is already normalized.
    /// Throws <see cref="ArgumentException"/> on text the normalizers
    /// reject: an unpaired UTF-16 surrogate (ill-formed UTF-16), or
    /// U+FFFE (a noncharacter .NET's string.Normalize rejects, matched
    /// here so every runtime behaves the same). Also throws
    /// <see cref="ArgumentException"/> when <paramref name="form"/>
    /// isn't one of the four defined normalization forms.
    /// </summary>
    public static string Normalize(string text, NormalizationForm form)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        return UnicodeNormalization.Normalize(text, form);
    }

    /// <summary>
    /// Whether <paramref name="text"/> is already in
    /// <paramref name="form"/>, answered by the process-wide
    /// normalizer. Throws <see cref="ArgumentException"/> on text the
    /// normalizers reject (an unpaired UTF-16 surrogate, or U+FFFE) and
    /// on a <paramref name="form"/> outside the four defined forms.
    /// </summary>
    public static bool IsNormalized(string text, NormalizationForm form)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        return UnicodeNormalization.IsNormalized(text, form);
    }
}
