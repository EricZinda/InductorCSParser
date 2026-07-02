// SemVer 2.0.0 grammar (https://semver.org/spec/v2.0.0.html).
//
//   <semver>          ::= <num>.<num>.<num> ('-' <preRelease>)? ('+' <build>)?
//   <num>             ::= 0 | [1-9][0-9]*
//   <preRelease>      ::= <preReleaseIdent> ('.' <preReleaseIdent>)*
//   <preReleaseIdent> ::= 0 | [1-9][0-9]* | [0-9A-Za-z-]* with at least one [A-Za-z-]
//   <build>           ::= <buildIdent> ('.' <buildIdent>)*
//   <buildIdent>      ::= [0-9A-Za-z-]+
//
// The major/minor/patch positions express the no-leading-zero rule
// directly in the grammar using the reject-first pattern: a Not(...)
// probe in front of any consumption rejects the bad "0[digit]" prefix
// and positions the error at the start of the bad token. The numeric pre-release ident still goes
// through post-parse validation in SemVerParser.cs because its rule
// mixes numeric and alphanumeric shapes and the per-field message
// is easier to express that way.

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
        // A factory rather than AliasedAs because the per-name
        // WithError has to sit on the Not. The Not is the leading-zero
        // probe: it fails ONLY on the "0[digit]" shape, so a WithError
        // there surfaces only for an actual leading zero. Other
        // failures (a non-digit first character like the 'v' in
        // "v1.2.3") leave the Not succeeding and fall through to the
        // OneOrMore's generic unexpected-token message.
        //
        // AliasedAs can't reproduce that: an alias wraps the whole
        // number rule, so an alias-level WithError fires on every
        // failure of the number, not just the leading-zero one, and
        // would mislabel the 'v' in "v1.2.3" as a leading zero.
        // Keeping the message selective means keeping it on the Not,
        // and a per-name Not means a factory.
        static Rule NumericCore(string name) =>
            And(
                Not(And(Token('0'), OneOf(TokenSet.Ascii.Digits)))
                    .WithError($"{name} version must not have leading zeros"),
                OneOrMore(OneOf(TokenSet.Ascii.Digits))
            ).As(name);

        MajorVersion = NumericCore("major");
        MinorVersion = NumericCore("minor");
        PatchVersion = NumericCore("patch");

        var identRune = TokenSet.Ascii.Digits | TokenSet.Ascii.Letters | TokenSet.Runes("-");
        PreReleaseIdent = ScanWhile(identRune, minimumCount: 1)
            .As("preReleaseIdent")
            .WithError("Pre-release identifier expected");
        BuildMetadataIdent = ScanWhile(identRune, minimumCount: 1)
            .As("buildIdent")
            .WithError("Build metadata identifier expected");

        PreReleaseSection = And(
            Token('-'),
            PreReleaseIdent,
            ZeroOrMore(And(Token('.'), PreReleaseIdent))
        ).As("preRelease");

        BuildMetadataSection = And(
            Token('+'),
            BuildMetadataIdent,
            ZeroOrMore(And(Token('.'), BuildMetadataIdent))
        ).As("buildMetadata");

        SemVer = And(
            MajorVersion,
            Token('.').WithError("Expected '.' after major version"),
            MinorVersion,
            Token('.').WithError("Expected '.' after minor version"),
            PatchVersion,
            Optional(PreReleaseSection),
            Optional(BuildMetadataSection),
            Eof().WithError("Unexpected text after version")
        ).As("semver");

        SemVer.Compile();
    }
}
