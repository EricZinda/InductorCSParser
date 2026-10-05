using System;
using InductorParser;
using InductorParser.Lexing;
using static InductorParser.Rules;

// Child process for HostGlobalizationChildProcessTests. The tests
// launch this program with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT or
// DOTNET_SYSTEM_GLOBALIZATION_USENLS in its environment, which no
// in-process test can arrange because the globalization mode is fixed
// at process start. The single argument picks the scenario, the
// outcome goes to stdout as ASCII marker lines, and the NUnit side
// owns every assertion. Exit 0 means the scenario machinery ran (a
// thrown host-globalization check is a reported outcome, not a
// failure), 1 means an unexpected exception, 2 means a bad scenario
// argument.

if (args.Length != 1)
{
    Console.Error.WriteLine("Expected exactly one scenario argument.");
    return 2;
}

// The e-acute strings are built from numeric char values, never string
// literals, because the decomposed and precomposed forms render
// identically in an editor, and a tool that normalized this source
// file would silently break the scenarios.
string precomposedEAcute = ((char)0x00E9).ToString();
string decomposedEAcute = new string(new[] { (char)0x0065, (char)0x0301 });

try
{
    switch (args[0])
    {
        case "default":
            break;
        case "override":
            UnicodeEnvironment.AllowNonstandardRuntimeNormalization = true;
            break;
        case "bundled":
            UnicodeEnvironment.Implementation = UnicodeImplementation.Bundled;
            break;
        case "segmentation-only":
            // Compile(null) turns normalization off, so this grammar
            // never consults the host-globalization check no matter
            // what the environment variables say.
            var segmentationRule = And(Literal("ab"), Token(';')).Compile(null);
            ReportParse(segmentationRule.Parse("ab;"));
            return 0;
        default:
            Console.Error.WriteLine($"Unknown scenario: {args[0]}");
            return 2;
    }

    // The normalizing scenario, shared by every case that falls
    // through the switch: a default FormC compile with a precomposed
    // e-acute literal, parsed against decomposed input. On a normally
    // configured host the two match. Under invariant globalization the
    // compile throws unless a remedy was applied first, and with the
    // override applied the parse runs but the decomposed input no
    // longer matches, which is exactly the pre-check behavior the
    // override deliberately buys back.
    try
    {
        var rule = And(Literal(precomposedEAcute), Token(';')).Compile();
        ReportParse(rule.Parse(decomposedEAcute + ";"));
    }
    catch (InvalidOperationException exception)
    {
        Console.WriteLine("OUTCOME:CHECK_THREW");
        Console.WriteLine("MESSAGE-BEGIN");
        Console.WriteLine(exception.Message);
        Console.WriteLine("MESSAGE-END");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

void ReportParse(ParseResult result)
{
    Console.WriteLine("OUTCOME:PARSED");
    Console.WriteLine($"PARSE-SUCCESS:{result.Success}");
    Console.WriteLine($"ACTIVE:{UnicodeEnvironment.ActiveImplementation}");
}
