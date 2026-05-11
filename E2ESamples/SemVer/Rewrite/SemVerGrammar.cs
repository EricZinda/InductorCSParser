// SemVer 2.0.0 grammar (https://semver.org/spec/v2.0.0.html).
//
//   <semver>          ::= <num>.<num>.<num> ('-' <preRelease>)? ('+' <build>)?
//   <num>             ::= 0 | [1-9][0-9]*
//   <preRelease>      ::= <preReleaseIdent> ('.' <preReleaseIdent>)*
//   <preReleaseIdent> ::= 0 | [1-9][0-9]* | [0-9A-Za-z-]* with at least one [A-Za-z-]
//   <build>           ::= <buildIdent> ('.' <buildIdent>)*
//   <buildIdent>      ::= [0-9A-Za-z-]+
//
// The numeric core and the numeric-pre-release shape both have a
// no-leading-zero rule. Both are accepted as any digit run here and
// validated post-parse in SemVerParser.cs (see backlog/z0aa for the
// diagnostics-vs-grammar trade-off).

using InductorParser;
using static InductorParser.Rules;

namespace SemVerSample.Rewrite;

public static class SemVerGrammar
{
    public static readonly Rule SemVer;
    public static readonly Rule MajorVersion;
    public static readonly Rule MinorVersion;
    public static readonly Rule PatchVersion;
    public static readonly Rule PreReleaseSection;
    public static readonly Rule BuildMetadataSection;
    public static readonly Rule PreReleaseIdent;
    public static readonly Rule BuildMetadataIdent;

    static SemVerGrammar()
    {
        // Each named position needs its own rule instance; a shared
        // variable plus three .As(name) calls would silently rename
        // them all to the last name. See backlog/z0ab.
        static Rule NumericCore(string name) =>
            OneOrMore(OneOf(TokenSet.Ascii.Digits))
                .As(name).Preserve()
                .WithError($"Expected {name} version (digits 0-9)");

        MajorVersion = NumericCore("major");
        MinorVersion = NumericCore("minor");
        PatchVersion = NumericCore("patch");

        var identRune = TokenSet.Ascii.Digits | TokenSet.Ascii.Letters | TokenSet.Runes("-");
        PreReleaseIdent = ScanWhile(identRune, minimumCount: 1)
            .As("preReleaseIdent").Preserve()
            .WithError("Pre-release identifier expected");
        BuildMetadataIdent = ScanWhile(identRune, minimumCount: 1)
            .As("buildIdent").Preserve()
            .WithError("Build metadata identifier expected");

        PreReleaseSection = And(
            Token('-'),
            PreReleaseIdent,
            ZeroOrMore(And(Token('.'), PreReleaseIdent))
        ).As("preRelease").Preserve();

        BuildMetadataSection = And(
            Token('+'),
            BuildMetadataIdent,
            ZeroOrMore(And(Token('.'), BuildMetadataIdent))
        ).As("buildMetadata").Preserve();

        SemVer = And(
            MajorVersion,
            Token('.').WithError("Expected '.' after major version"),
            MinorVersion,
            Token('.').WithError("Expected '.' after minor version"),
            PatchVersion,
            Optional(PreReleaseSection),
            Optional(BuildMetadataSection),
            Eof().WithError("Unexpected text after version")
        ).As("semver").Preserve();

        SemVer.Compile();
    }
}
