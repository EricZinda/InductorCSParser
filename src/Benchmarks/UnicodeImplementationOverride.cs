using System;
using System.Runtime.CompilerServices;
using global::InductorParser.Lexing;

namespace InductorParser.Benchmarks;

// Lets a benchmark run force the parser's Unicode implementation from the
// command line:
//
//   INDUCTORPARSER_UNICODE_IMPLEMENTATION=Bundled Benchmarks.exe --filter "*Json_InductorParser*"   (style-lint-ok: the value is the UnicodeImplementation.Bundled enum name)
//
// UnicodeEnvironment.Implementation is process-wide and freezes on the
// first segmentation or normalization query, and BenchmarkDotNet runs
// each benchmark in a child process that inherits this environment
// variable. A module initializer runs when the assembly loads, before
// any grammar is built, so it lands inside the window where the setting
// can still be changed, in the host process and in every child. When
// the variable is unset the setting stays Automatic, which in the
// net8.0 assembly means Runtime.
internal static class UnicodeImplementationOverride
{
    [ModuleInitializer]
    internal static void Apply()
    {
        string? value = Environment.GetEnvironmentVariable("INDUCTORPARSER_UNICODE_IMPLEMENTATION");
        if (string.IsNullOrEmpty(value)) return;
        UnicodeEnvironment.Implementation =
            Enum.Parse<UnicodeImplementation>(value, ignoreCase: true);
    }
}
