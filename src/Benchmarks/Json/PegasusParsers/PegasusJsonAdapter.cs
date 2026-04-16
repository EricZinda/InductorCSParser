namespace InductorParser.Benchmarks.Json.PegasusParsers;

public static class PegasusJsonParser
{
    private static readonly PegasusJsonParserGenerated _parser = new();

    public static IJson Parse(string input) => _parser.Parse(input);
}
