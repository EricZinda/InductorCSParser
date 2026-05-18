using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;

namespace InductorParser.E2ESamples.Toml.Tests;

// Bans a newline byte inside a raw or verbatim string literal anywhere
// in the Toml sample's sources. Such a newline is a real source byte,
// so git's autocrlf rewrites it: the same literal is LF on a Linux
// checkout and CRLF on a Windows one, and a test built on it silently
// exercises a different input per platform (this is exactly how the
// CRLF comment-scanning bug stayed hidden). A multi-line test input
// must instead be built from single-line literals plus an explicit
// line break — see TestSupport.Lines — so the fixture is identical on
// every checkout, and parameterizing the test over LineBreak covers
// LF and CRLF deliberately.
//
// Byte-level scan: the C# spec lets structural characters (quotes,
// comment markers) be tokenized as ASCII only, so reading raw bytes is
// enough to tell code from comment from string literal. Mirrors the
// scanner shape of InductorParser.Tests' UnicodeLiteralCanaryTests.
[TestFixture]
public class CrossLineStringLiteralTests
{
    private static string GetThisFilePath([CallerFilePath] string callerFilePath = "") => callerFilePath;

    [Test]
    public void NoStringLiteralCrossesALine()
    {
        // This file lives at <toml-project-root>/Tests/CrossLineStringLiteralTests.cs.
        // Two parents up is the Toml sample root.
        var tomlRoot = Path.GetDirectoryName(Path.GetDirectoryName(GetThisFilePath()))!;
        var separator = Path.DirectorySeparatorChar;

        var violations = new List<string>();
        foreach (var sourceFile in Directory.EnumerateFiles(tomlRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (sourceFile.Contains($"{separator}bin{separator}")
                || sourceFile.Contains($"{separator}obj{separator}"))
                continue;
            ScanFile(sourceFile, File.ReadAllBytes(sourceFile), violations);
        }

        if (violations.Count == 0) return;

        var report = new StringBuilder();
        report.Append("Found ").Append(violations.Count).Append(" newline byte(s) inside raw or verbatim string literals.\n");
        report.Append("A literal that crosses lines has platform-dependent newlines (git autocrlf), so a\n");
        report.Append("test built on it isn't deterministic. Build multi-line input with TestSupport.Lines\n");
        report.Append("(LineBreak, ...) from single-line literals instead.\n\n");
        foreach (var violation in violations)
            report.Append("  ").Append(violation).Append('\n');
        Assert.Fail(report.ToString());
    }

    private enum State { Code, LineComment, BlockComment, QuotedString, Verbatim, Raw, CharLiteral }

    private static void ScanFile(string path, byte[] bytes, List<string> violations)
    {
        var state = State.Code;
        int line = 1;
        int column = 1;
        int rawQuoteCount = 0;

        int index = 0;
        while (index < bytes.Length)
        {
            byte current = bytes[index];
            switch (state)
            {
                case State.Code:
                    if (current == (byte)'/' && Peek(bytes, index) == (byte)'/')
                    { state = State.LineComment; index += 2; column += 2; continue; }
                    if (current == (byte)'/' && Peek(bytes, index) == (byte)'*')
                    { state = State.BlockComment; index += 2; column += 2; continue; }
                    // $@"..." and @$"..." both open a verbatim string.
                    if ((current == (byte)'$' || current == (byte)'@')
                        && index + 2 < bytes.Length
                        && bytes[index + 1] == (current == (byte)'$' ? (byte)'@' : (byte)'$')
                        && bytes[index + 2] == (byte)'"')
                    { state = State.Verbatim; index += 3; column += 3; continue; }
                    if (current == (byte)'@' && Peek(bytes, index) == (byte)'"')
                    { state = State.Verbatim; index += 2; column += 2; continue; }
                    if (current == (byte)'$' && Peek(bytes, index) == (byte)'"')
                    { state = State.QuotedString; index += 2; column += 2; continue; }
                    if (current == (byte)'"')
                    {
                        int quoteCount = 0;
                        while (index + quoteCount < bytes.Length && bytes[index + quoteCount] == (byte)'"')
                            quoteCount++;
                        if (quoteCount >= 3)
                        { state = State.Raw; rawQuoteCount = quoteCount; index += quoteCount; column += quoteCount; continue; }
                        state = State.QuotedString; index += 1; column += 1; continue;
                    }
                    if (current == (byte)'\'')
                    { state = State.CharLiteral; index += 1; column += 1; continue; }
                    Advance(current, ref line, ref column); index += 1; continue;

                case State.LineComment:
                    if (current == (byte)'\n') state = State.Code;
                    Advance(current, ref line, ref column); index += 1; continue;

                case State.BlockComment:
                    if (current == (byte)'*' && Peek(bytes, index) == (byte)'/')
                    { state = State.Code; index += 2; column += 2; continue; }
                    Advance(current, ref line, ref column); index += 1; continue;

                case State.QuotedString:
                    // A regular string can't contain a raw newline (C#
                    // rejects it), so no cross-line check is needed here.
                    if (current == (byte)'\\' && index + 1 < bytes.Length)
                    {
                        Advance(current, ref line, ref column);
                        Advance(bytes[index + 1], ref line, ref column);
                        index += 2; continue;
                    }
                    if (current == (byte)'"') { state = State.Code; index += 1; column += 1; continue; }
                    Advance(current, ref line, ref column); index += 1; continue;

                case State.CharLiteral:
                    if (current == (byte)'\\' && index + 1 < bytes.Length)
                    {
                        Advance(current, ref line, ref column);
                        Advance(bytes[index + 1], ref line, ref column);
                        index += 2; continue;
                    }
                    if (current == (byte)'\'') { state = State.Code; index += 1; column += 1; continue; }
                    Advance(current, ref line, ref column); index += 1; continue;

                case State.Verbatim:
                    if (current == (byte)'"')
                    {
                        if (Peek(bytes, index) == (byte)'"') { index += 2; column += 2; continue; }
                        state = State.Code; index += 1; column += 1; continue;
                    }
                    if (current == (byte)'\n')
                        violations.Add($"{path}:{line}:{column}");
                    Advance(current, ref line, ref column); index += 1; continue;

                case State.Raw:
                    if (current == (byte)'"')
                    {
                        int quoteRun = 0;
                        while (index + quoteRun < bytes.Length && bytes[index + quoteRun] == (byte)'"')
                            quoteRun++;
                        if (quoteRun >= rawQuoteCount) { state = State.Code; rawQuoteCount = 0; }
                        index += quoteRun; column += quoteRun; continue;
                    }
                    if (current == (byte)'\n')
                        violations.Add($"{path}:{line}:{column}");
                    Advance(current, ref line, ref column); index += 1; continue;
            }
        }
    }

    private static byte Peek(byte[] bytes, int index) =>
        index + 1 < bytes.Length ? bytes[index + 1] : (byte)0;

    private static void Advance(byte b, ref int line, ref int column)
    {
        if (b == (byte)'\n') { line++; column = 1; }
        else column++;
    }
}
