using System.IO;
using System.Text;

namespace InductorParser.Tests;

// Shared helpers for trace-format tests. Every rule's test file pins its
// trace output verbatim via Assert.That(sink.ToString(), Is.EqualTo(...)),
// and the comparison only works if (a) the sink uses a platform-neutral
// newline and (b) the expected-string builder matches that newline. These
// two helpers are the convention.
internal static class TraceTestHelpers
{
    // StringWriter with newline forced to "\n" so expected-output strings
    // don't drift when the test runs on a platform whose
    // Environment.NewLine differs from the one the author wrote against.
    // Without this, a \r\n vs \n mismatch turns every verbatim assertion
    // into a byte-level failure.
    public static StringWriter NewSink() => new StringWriter { NewLine = "\n" };

    // Concatenate lines with "\n" separators plus a trailing "\n",
    // matching what TextWriter.WriteLine produces when NewLine is "\n".
    // The trailing "\n" matters: the last trace emission ends with a
    // newline, so the sink's final character is always "\n".
    public static string Lines(params string[] lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines) { sb.Append(line); sb.Append('\n'); }
        return sb.ToString();
    }
}
