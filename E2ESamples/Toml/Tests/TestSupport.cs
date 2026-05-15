namespace InductorParser.E2ESamples.Toml.Tests;

// Which line break Lines(...) joins with. A test body that takes this
// as a [TestCase] parameter runs once per convention, so one body
// covers both LF and CRLF input. Public because it appears as a
// parameter on public [TestCase] methods.
public enum LineBreak { Lf, Crlf }

// Test-input construction shared across the Toml sample's fixtures.
internal static class TestSupport
{

    // Join `lines` with the chosen line break. Use this instead of a
    // raw or verbatim string literal whenever a test input spans more
    // than one line. An embedded newline in a raw/verbatim literal is
    // a real source byte, and git's autocrlf rewrites it: the same
    // fixture is LF on a Linux checkout and CRLF on a Windows one, so
    // the test silently exercises a different input per platform. The
    // CrossLineStringLiteralCanary test bans those literals for that
    // reason. Lines builds the input from single-line string literals
    // (which autocrlf can't touch) plus an explicit LineBreak, so a
    // fixture means the same thing on every checkout. A test that
    // takes LineBreak as a [TestCase] parameter then covers both line
    // endings from one body.
    //
    // Joins, doesn't append: there's no trailing break. A fixture that
    // needs a trailing newline ends with an explicit "" element, the
    // same way a blank line in the middle is written.
    public static string Lines(LineBreak lineBreak, params string[] lines) =>
        string.Join(lineBreak == LineBreak.Crlf ? "\r\n" : "\n", lines);
}
