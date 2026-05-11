// Original SemVer 2.0.0 parser. Wraps the canonical regex from
// https://semver.org/spec/v2.0.0.html (CC-BY-3.0) with a typed-record
// projection. This is the shape almost every C# semver library on
// NuGet ships (NuGet's SemanticVersion, WalkerCodeRanger/semver,
// Ruhrpottpatriot/SemanticVersion, McSherry.SemanticVersioning,
// adamreeve/semver.net).
//
// The regex tells the caller two things: did it match, and if it
// did, what are the four capture groups. It does not say where in
// the input something broke or why. The InductorParser rewrite under
// ../Rewrite/ keeps the same public API and adds positioned errors,
// per-rule diagnostic messages, and a walkable parse tree.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SemVerSample.Original;

public sealed record OriginalSemVer(
    int Major,
    int Minor,
    int Patch,
    IReadOnlyList<OriginalPreReleaseIdentifier> PreRelease,
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

public sealed record OriginalPreReleaseIdentifier(string Text, bool IsNumeric);

public static class OriginalSemVerParser
{
    private static readonly Regex SemVerRegex = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)" +
        @"(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
        @"(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?" +
        @"$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericIdentifier =
        new(@"^(?:0|[1-9]\d*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static OriginalSemVer Parse(string input)
    {
        if (!TryParse(input, out var version, out string error))
            throw new FormatException(error);
        return version;
    }

    public static bool TryParse(string input, out OriginalSemVer version, out string error)
    {
        version = null!;
        error = string.Empty;

        if (input == null)
        {
            error = "Input cannot be null.";
            return false;
        }

        var match = SemVerRegex.Match(input);
        if (!match.Success)
        {
            error = $"'{input}' is not a valid semver 2.0.0 version string.";
            return false;
        }

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major))
        {
            error = "Major version is out of range for Int32.";
            return false;
        }
        if (!int.TryParse(match.Groups["minor"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minor))
        {
            error = "Minor version is out of range for Int32.";
            return false;
        }
        if (!int.TryParse(match.Groups["patch"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int patch))
        {
            error = "Patch version is out of range for Int32.";
            return false;
        }

        var preRelease = new List<OriginalPreReleaseIdentifier>();
        if (match.Groups["prerelease"].Success)
            foreach (var part in match.Groups["prerelease"].Value.Split('.'))
                preRelease.Add(new OriginalPreReleaseIdentifier(part, NumericIdentifier.IsMatch(part)));

        var buildMetadata = match.Groups["build"].Success
            ? match.Groups["build"].Value.Split('.').ToList()
            : new List<string>();

        version = new OriginalSemVer(major, minor, patch, preRelease, buildMetadata);
        return true;
    }
}
