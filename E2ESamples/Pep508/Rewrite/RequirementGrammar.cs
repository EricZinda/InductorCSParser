// PEP 508 dependency-specifier grammar (the thing in a requirements.txt
// line or an `install_requires` entry: `requests[security]>=2.8.1`).
//
// Modeled on the grammar the `packaging` library parses, which itself
// follows the ABNF in PEP 508 (https://peps.python.org/pep-0508/).
// This sample covers the requirement core. Environment markers
// (`; python_version < "3.8"`) are out of scope. See README.md.
//
//   requirement   = wsp* name wsp* extras? wsp* requirement_details wsp*
//   requirement_details = ( '@' wsp* url ) | versionspec | <empty>
//   extras        = '[' wsp* extras_list? wsp* ']'
//   extras_list   = identifier (wsp* ',' wsp* identifier)*
//   versionspec   = ( '(' wsp* version_many wsp* ')' ) | version_many
//   version_many  = version_one (wsp* ',' wsp* version_one)*
//   version_one   = version_cmp wsp* version
//   version_cmp   = '===' | '==' | '~=' | '!=' | '<=' | '>=' | '<' | '>'
//   version       = ( letterOrDigit | '-' | '_' | '.' | '*' | '+' | '!' )+
//   name          = identifier
//   identifier    = letterOrDigit ( letterOrDigit | '-' | '_' | '.' )*
//   url           = non-whitespace+
//   wsp           = ' ' | '\t'                 (PEP 508 whitespace: space/tab only)
//
// This grammar started as a natural-first draft with no `.WithError`
// anywhere (see the git history of this file, and README.md). The
// `.WithError` calls below were added afterward, one per error-message
// test in Tests/ErrorMessageTests.cs that the bare grammar got wrong.
// README.md walks through which ones the natural grammar already got
// right, which the WithErrors fixed, and which stayed stubborn.

using InductorParser;
using static InductorParser.Rules;

namespace Pep508Sample.Rewrite;

public static class RequirementGrammar
{
    public static readonly Rule Requirement;
    public static readonly Rule Name;
    public static readonly Rule Extras;
    public static readonly Rule ExtraName;
    public static readonly Rule ExtraNameAfterComma;
    public static readonly Rule Specifier;
    public static readonly Rule VersionConstraint;
    public static readonly Rule ComparisonOperator;
    public static readonly Rule Version;
    public static readonly Rule Url;
    public static readonly Rule UrlReference;

    static RequirementGrammar()
    {
        // PEP 508 whitespace is the ASCII space and tab only. A newline
        // or other Unicode space is not whitespace here and trips the
        // parser like any other unexpected rune.
        var whitespace = ZeroOrMore(OneOf(" \t")).Delete();

        // identifier: starts with an ASCII letter or digit, then any run
        // of letters, digits, '-', '_', '.'. Built fresh per call: every
        // place an identifier appears needs its own rule instance so it
        // can carry its own `.WithError` text (`.WithError` is set-once
        // per instance).
        var nameStart = TokenSet.Ascii.Letters | TokenSet.Ascii.Digits;
        var nameBody = nameStart | TokenSet.Runes("-_.");
        Rule MakeIdentifier() => And(OneOf(nameStart), ScanWhile(nameBody, minimumCount: 0));

        Name = MakeIdentifier()
            .As("name")
            .WithError("expected a package name at the start of the dependency specifier");

        ExtraName = MakeIdentifier().As("extra");
        // The after-a-comma extra needs its own `.WithError`, so it has
        // to be a separate rule instance. It can't also be named
        // "extra" (Compile rejects two reachable rules with the same
        // `.As` name), so it stays unnamed and is kept in the tree with
        // `.Preserve()` instead. See backlog item 0a05.
        ExtraNameAfterComma = MakeIdentifier()
            .Preserve()
            .WithError("expected an extra name after ','");

        var extrasList = And(
            ExtraName,
            ZeroOrMore(And(whitespace, Token(','), whitespace, ExtraNameAfterComma)));
        Extras = And(
            Token('['),
            whitespace,
            Optional(extrasList),
            whitespace,
            Token(']').WithError("expected ',' or ']' after the extra name")
        ).As("extras");

        // Longer operators first so '==' doesn't win over '===', etc.
        // The WithError anchors at the deepest position the branches
        // reach: on input like "=1.0" the '==' / '===' branches consume
        // the first '=' and fail one char in, so the message surfaces
        // there, at the character that isn't a valid operator.
        ComparisonOperator = Or(
            Literal("==="),
            Literal("=="),
            Literal("~="),
            Literal("!="),
            Literal("<="),
            Literal(">="),
            Token('<'),
            Token('>')
        ).As("operator").WithError("expected a version comparison operator");

        var versionChar = nameStart | TokenSet.Runes("-_.*+!");
        Version = ScanWhile(versionChar, minimumCount: 1)
            .As("version")
            .WithError("expected a version after the comparison operator");

        VersionConstraint = And(ComparisonOperator, whitespace, Version).As("constraint");

        var versionMany = And(
            VersionConstraint,
            ZeroOrMore(And(whitespace, Token(','), whitespace, VersionConstraint)));

        var parenthesizedSpecifier = And(
            Token('('),
            whitespace,
            versionMany,
            whitespace,
            Token(')').WithError("expected ',' or ')' after the version specifier"));

        Specifier = Or(parenthesizedSpecifier, versionMany).As("specifier");

        // urlspec: '@' then whitespace then a run of non-whitespace.
        UrlReference = ScanWhile(TokenSet.Universe - TokenSet.Runes(" \t"), minimumCount: 1)
            .As("url")
            .WithError("expected a URL after '@'");
        Url = And(Token('@'), whitespace, UrlReference).As("urlSpec");

        var requirementDetails = Or(Url, Specifier);

        Requirement = And(
            whitespace,
            Name,
            whitespace,
            Optional(Extras),
            whitespace,
            Optional(requirementDetails),
            whitespace,
            Eof().WithError("expected the end of the dependency specifier")
        ).As("requirement");

        Requirement.Compile();
    }
}
