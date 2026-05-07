- Untitled
# Vendor a UAX #29 grapheme cluster implementation

    - Current state: the lexer uses System.Globalization.StringInfo.GetNextTextElement. On .NET 5+ this follows the runtime's UAX #29-compatible text-element behavior. On .NET Framework, .NET Core 3.x and earlier, and the Mono runtimes Unity ships, it splits real grapheme clusters incorrectly (e.g. Thai "kam" splits into two, the woman-shrugging emoji splits into four). See Microsoft's breaking-change note at https://learn.microsoft.com/en-us/dotnet/core/compatibility/globalization/5.0/uax29-compliant-grapheme-enumeration.
    - Why the NuGet shortcut didn't work: clipperhouse/uax29.net only ships net8.0 TFMs (every published version), so it can't be referenced from a netstandard2.1 library without dropping Unity support.
    - Recommended path: vendor + strip from https://github.com/clipperhouse/uax29.net (MIT). Take Graphemes.Splitter.cs and Graphemes.Dict.cs verbatim, copy only the shared helpers Graphemes.Splitter actually depends on (Buffer/SplitEnumerator base), drop everything words/sentences/byte-stream related. Adapt to a ReadOnlySpan<char> entry point and expose as the NextTokenLength implementation inside the lexer.
    - Cost estimate: half a day if it goes cleanly. Two hours of porting plus an hour wiring up the GraphemeBreakTest.txt conformance suite from unicode.org as test data, plus debugging.
    - Done when: the lexer no longer touches StringInfo; the conformance suite runs green; existing tests still pass.
