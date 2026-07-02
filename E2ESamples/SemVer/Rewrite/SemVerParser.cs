// SemVer parser using InductorParser, vs ../Original/SemVerRegexParser.cs:
//
//                     Original (regex)       Rewrite (this file)
//   Failure position  none                   line + column
//   Error message     "not a valid semver"   names the rule and value
//   AST               four capture groups    walkable parse tree
//
// Major/minor/patch leading-zero checks run in the grammar via the
// reject-first pattern (see SemVerGrammar.cs and docs/Recipes.md), so
// only the Int32 range check stays here for those positions. The
// numeric pre-release ident leading-zero check still runs as a
// post-parse check because the pre-release ident rule mixes numeric
// and alphanumeric shapes and per-field diagnostics are easier to
// express that way.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InductorParser;
using InductorParser.SyntaxTree;
using static SemVerSample.Rewrite.SemVerGrammar;

namespace SemVerSample.Rewrite;

public sealed record RewriteSemVer(
    int Major,
    int Minor,
    int Patch,
    IReadOnlyList<RewritePreReleaseIdentifier> PreRelease,
    IReadOnlyList<string> BuildMetadata)
{
    public override string ToString()
    {
        var formatted = $"{Major}.{Minor}.{Patch}";
        if (PreRelease.Count > 0)
            formatted += "-" + string.Join(".", PreRelease.Select(identifier => identifier.Text));
        if (BuildMetadata.Count > 0)
            formatted += "+" + string.Join(".", BuildMetadata);
        return formatted;
    }
}

public sealed record RewritePreReleaseIdentifier(string Text, bool IsNumeric);

public sealed record SemVerParseError(string Message, int CharIndex, int Line, int Column)
{
    public override string ToString() =>
        $"line {Line + 1}, column {Column + 1}: {Message}";
}

public static class SemVerParser
{
    public static RewriteSemVer Parse(string input)
    {
        if (!TryParse(input, out var version, out var error))
            throw new System.FormatException(error!.ToString());
        return version!;
    }

    public static bool TryParse(string input, out RewriteSemVer? version, out SemVerParseError? error)
    {
        version = null;
        // This sample renders the failure position itself (see the error type's
        // formatting), so the message text stays position-less.
        var options = new ParseOptions
        {
            WithErrorTemplate = "{message}",
            PositionalErrorTemplate = "unexpected '{character}'.",
            EndOfInputErrorTemplate = "unexpected end of input.",
        };
        var result = SemVer.Parse(input, options);
        if (!result.Success)
        {
            error = new SemVerParseError(
                result.ErrorMessage, result.ErrorCharIndex, result.ErrorLine, result.ErrorCharColumn);
            return false;
        }

        var tree = result.Tree!;
        var positions = new[] {
            (Rule: MajorVersion, Label: "Major"),
            (Rule: MinorVersion, Label: "Minor"),
            (Rule: PatchVersion, Label: "Patch"),
        };
        var values = new int[3];
        for (int positionIndex = 0; positionIndex < positions.Length; positionIndex++)
        {
            var node = tree.Find(positions[positionIndex].Rule)!;
            string text = node.ToString();
            string label = positions[positionIndex].Label;

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out values[positionIndex]))
            {
                error = ErrorAt(node, $"{label} version '{text}' is out of range for Int32");
                return false;
            }
        }

        var preRelease = new List<RewritePreReleaseIdentifier>();
        var preReleaseSection = tree.Find(PreReleaseSection);
        if (preReleaseSection != null)
        {
            foreach (var node in preReleaseSection.Children)
            {
                string text = node.ToString();
                bool isNumeric = text.All(char.IsAsciiDigit);
                if (isNumeric && text.Length > 1 && text[0] == '0')
                {
                    error = ErrorAt(node, $"Numeric pre-release identifier '{text}' must not have leading zeros");
                    return false;
                }
                preRelease.Add(new RewritePreReleaseIdentifier(text, isNumeric));
            }
        }

        var buildMetadata = new List<string>();
        var buildSection = tree.Find(BuildMetadataSection);
        if (buildSection != null)
            foreach (var node in buildSection.Children)
                buildMetadata.Add(node.ToString());

        version = new RewriteSemVer(values[0], values[1], values[2], preRelease, buildMetadata);
        error = null;
        return true;
    }

    private static SemVerParseError ErrorAt(Symbol node, string message)
    {
        var range = node.SourceRange;
        return new SemVerParseError(message,
            range?.Start.CharIndex ?? 0,
            range?.Start.Line ?? 0,
            range?.Start.CharColumn ?? 0);
    }
}
