namespace InductorParser.Benchmarks.Json.PegasusParsers;

public static class PegasusJsonOptimizedParser
{
    private static readonly PegasusJsonOptimizedParserGenerated _parser = new();

    public static IJson Parse(string input) => _parser.Parse(input);
}

public static class PegasusJsonWikiParser
{
    private static readonly PegasusJsonWikiParserGenerated _parser = new();

    public static IJson Parse(string input) => _parser.Parse(input);
}
