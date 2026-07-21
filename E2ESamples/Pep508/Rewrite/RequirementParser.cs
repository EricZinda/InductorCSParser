// Typed AST builder + Parse / TryParse entry points for the
// InductorParser rewrite of the PEP 508 dependency-specifier parser.
//
// The grammar in RequirementGrammar.cs produces a Symbol tree shaped
// like:
//
//   requirement
//     name        SourceText: requests
//     extras
//       extra     SourceText: security
//       extra     SourceText: socks
//     specifier
//       constraint
//         operator  SourceText: >=
//         version   SourceText: 2.8.1
//
// This file walks that tree into the Requirement record below, which
// mirrors the shape of packaging.requirements.Requirement (name,
// extras, specifier, url) minus the environment marker.

using System;
using System.Collections.Generic;
using System.Linq;
using InductorParser;

namespace Pep508Sample.Rewrite;

public sealed record VersionConstraint(string Operator, string Version);

public sealed record Requirement(
    string Name,
    IReadOnlyList<string> Extras,
    IReadOnlyList<VersionConstraint> Specifiers,
    string? Url);

public sealed record RequirementParseError(string Message, int CharIndex, int Line, int Column, string Source)
{
    // Renders the same shape as packaging's ParserSyntaxError.__str__:
    // the message, the source, and a caret line under the bad column.
    public override string ToString()
    {
        string caret = new string(' ', CharIndex) + "^";
        return $"{Message}\n    {Source}\n    {caret}";
    }
}

public static class RequirementParser
{
    public static Requirement Parse(string input)
    {
        if (!TryParse(input, out var requirement, out var error))
            throw new FormatException(error!.ToString());
        return requirement!;
    }

    public static bool TryParse(string input, out Requirement? requirement, out RequirementParseError? error)
    {
        requirement = null;
        // This sample renders the failure position itself (see the error type's
        // formatting), so the message text stays position-less.
        var options = new ParseOptions
        {
            WithErrorTemplate = "{message}",
            PositionalErrorTemplate = "unexpected '{character}'.",
            EndOfInputErrorTemplate = "unexpected end of input.",
        };
        var result = RequirementGrammar.Requirement.Parse(input, options);
        if (!result.Success)
        {
            error = new RequirementParseError(
                result.ErrorMessage,
                result.ErrorCharIndex,
                result.ErrorLine,
                result.ErrorCharColumn,
                input);
            return false;
        }

        var tree = result.Tree!;

        string name = tree.Find(RequirementGrammar.Name)!.SourceText;

        // An extras list holds two rule instances ("extra" before any
        // comma, "extra after comma" for the rest), so walk the extras
        // node's children in source order and keep both.
        var extrasNode = tree.Find(RequirementGrammar.Extras);
        IReadOnlyList<string> extras = extrasNode is null
            ? Array.Empty<string>()
            : extrasNode.Children
                .Where(child => child.Is(RequirementGrammar.ExtraName)
                             || child.Is(RequirementGrammar.ExtraNameAfterComma))
                .Select(child => child.SourceText)
                .ToList();

        var specifierNode = tree.Find(RequirementGrammar.Specifier);
        IReadOnlyList<VersionConstraint> specifiers = specifierNode is null
            ? Array.Empty<VersionConstraint>()
            : specifierNode.FindAll(RequirementGrammar.VersionConstraint)
                .Select(constraint => new VersionConstraint(
                    constraint.Find(RequirementGrammar.ComparisonOperator)!.SourceText,
                    constraint.Find(RequirementGrammar.Version)!.SourceText))
                .ToList();

        string? url = tree.Find(RequirementGrammar.UrlReference)?.SourceText;

        requirement = new Requirement(name, extras, specifiers, url);
        error = null;
        return true;
    }
}
