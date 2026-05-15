using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;

namespace InductorParser.Tests.Core;

// Source-bytes sweep. Walks every .cs file in the test project and reports
// any non-ASCII byte (>= 0x80) inside a string literal or char literal that
// isn't wrapped in a Canary(...) call. Non-ASCII inside comments is fine;
// non-ASCII inside a Canary call's arguments is fine (Canary's runtime
// check is the guard).
//
// The detection is a small state machine over the file bytes. The C# spec
// lets us tokenize using ASCII characters only (the structural characters
// for strings, comments, parens, etc. are all in the 0x00..0x7F range and
// can't appear as UTF-8 continuation bytes), so reading the file as raw
// bytes is enough.
[TestFixture]
public class UnicodeLiteralCanaryTests
{
    private static readonly string ThisFilePath = GetThisFilePath();

    private static string GetThisFilePath([CallerFilePath] string callerFilePath = "") => callerFilePath;

    [Test]
    public void AllNonAsciiStringLiteralsAreWrappedInCanary()
    {
        var violations = ScanAll().NonAscii;
        if (violations.Count == 0) return;

        var report = new StringBuilder();
        report.Append("Found ").Append(violations.Count).Append(" non-ASCII byte(s) inside string or char literals outside a Canary(...) call.\n");
        report.Append("Non-ASCII codepoints in test sources must go through Canary(literal, description, codepoints).\n");
        report.Append("If an editor rewrote a glyph in a literal, the canary catches it at runtime.\n\n");
        foreach (var violation in violations)
        {
            report.Append("  ").Append(violation.File)
                  .Append(':').Append(violation.Line)
                  .Append(':').Append(violation.Column)
                  .Append("  byte 0x").Append(violation.ByteValue.ToString("X2"))
                  .Append('\n');
        }
        Assert.Fail(report.ToString());
    }

    // Bans a newline byte inside a raw or verbatim string literal. Such
    // a newline is a real source byte, so git's autocrlf rewrites it:
    // the same literal is LF on a Linux checkout and CRLF on a Windows
    // one, and a test built on it silently exercises a different input
    // per platform. A multi-line test input must be built from
    // single-line literals plus an explicit line break — see
    // TestHelpers.Lines — so the fixture is identical on every checkout.
    [Test]
    public void NoCrossLineStringLiterals()
    {
        var violations = ScanAll().CrossLine;
        if (violations.Count == 0) return;

        var report = new StringBuilder();
        report.Append("Found ").Append(violations.Count).Append(" newline byte(s) inside raw or verbatim string literals.\n");
        report.Append("A literal that crosses lines has platform-dependent newlines (git autocrlf), so a\n");
        report.Append("test built on it isn't deterministic. Build multi-line input with TestHelpers.Lines\n");
        report.Append("(LineBreak, ...) from single-line literals instead.\n\n");
        foreach (var violation in violations)
        {
            report.Append("  ").Append(violation.File)
                  .Append(':').Append(violation.Line)
                  .Append(':').Append(violation.Column)
                  .Append('\n');
        }
        Assert.Fail(report.ToString());
    }

    // Walk every .cs file in the test project once, collecting both the
    // non-ASCII-outside-Canary violations and the cross-line-literal
    // violations so the two tests above share a single pass.
    private static (List<Violation> NonAscii, List<Violation> CrossLine) ScanAll()
    {
        // This file lives at <test-project-root>/Core/UnicodeLiteralCanaryTests.cs.
        // Two parents up is the test project root.
        var testProjectRoot = Path.GetDirectoryName(Path.GetDirectoryName(ThisFilePath))!;

        var nonAscii = new List<Violation>();
        var crossLine = new List<Violation>();
        foreach (var sourceFile in Directory.EnumerateFiles(testProjectRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcludedPath(sourceFile)) continue;
            var bytes = File.ReadAllBytes(sourceFile);
            ScanFile(sourceFile, bytes, nonAscii, crossLine);
        }
        return (nonAscii, crossLine);
    }

    private static bool IsExcludedPath(string sourceFile)
    {
        var sep = Path.DirectorySeparatorChar;
        if (sourceFile.Contains($"{sep}bin{sep}")) return true;
        if (sourceFile.Contains($"{sep}obj{sep}")) return true;
        if (sourceFile.Contains($"{sep}Unity{sep}")) return true;
        return false;
    }

    private readonly record struct Violation(string File, int Line, int Column, int ByteValue);

    private enum ScanState
    {
        Code,
        LineComment,
        BlockComment,
        DoubleQuoteString,
        VerbatimString,
        RawString,
        CharLiteral,
    }

    private static void ScanFile(string path, byte[] bytes, List<Violation> violations, List<Violation> crossLineViolations)
    {
        var state = ScanState.Code;
        int line = 1;
        int column = 1;
        int canaryParenDepth = 0;     // 0 = not in Canary, >0 = depth inside outermost Canary
        int rawStringQuoteCount = 0;   // number of " that opened the current raw string

        int index = 0;
        while (index < bytes.Length)
        {
            byte current = bytes[index];

            switch (state)
            {
                case ScanState.Code:
                    if (current == (byte)'/' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'/')
                    {
                        state = ScanState.LineComment;
                        AdvanceColumn(ref column, 2);
                        index += 2;
                        continue;
                    }
                    if (current == (byte)'/' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'*')
                    {
                        state = ScanState.BlockComment;
                        AdvanceColumn(ref column, 2);
                        index += 2;
                        continue;
                    }
                    if (current == (byte)'@' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'"')
                    {
                        state = ScanState.VerbatimString;
                        AdvanceColumn(ref column, 2);
                        index += 2;
                        continue;
                    }
                    if (current == (byte)'$' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'"')
                    {
                        // $"..." treated as regular double-quote string for byte-level detection.
                        // Interpolation holes are subsumed as part of the string.
                        state = ScanState.DoubleQuoteString;
                        AdvanceColumn(ref column, 2);
                        index += 2;
                        continue;
                    }
                    if (current == (byte)'$' && index + 2 < bytes.Length
                        && bytes[index + 1] == (byte)'@' && bytes[index + 2] == (byte)'"')
                    {
                        state = ScanState.VerbatimString;
                        AdvanceColumn(ref column, 3);
                        index += 3;
                        continue;
                    }
                    if (current == (byte)'@' && index + 2 < bytes.Length
                        && bytes[index + 1] == (byte)'$' && bytes[index + 2] == (byte)'"')
                    {
                        state = ScanState.VerbatimString;
                        AdvanceColumn(ref column, 3);
                        index += 3;
                        continue;
                    }
                    if (current == (byte)'"')
                    {
                        // Raw string: count consecutive quotes. If 3+, it's a raw string.
                        int quoteCount = 0;
                        while (index + quoteCount < bytes.Length && bytes[index + quoteCount] == (byte)'"')
                            quoteCount++;
                        if (quoteCount >= 3)
                        {
                            state = ScanState.RawString;
                            rawStringQuoteCount = quoteCount;
                            AdvanceColumn(ref column, quoteCount);
                            index += quoteCount;
                            continue;
                        }
                        state = ScanState.DoubleQuoteString;
                        AdvanceColumn(ref column, 1);
                        index += 1;
                        continue;
                    }
                    if (current == (byte)'\'')
                    {
                        state = ScanState.CharLiteral;
                        AdvanceColumn(ref column, 1);
                        index += 1;
                        continue;
                    }
                    if (current == (byte)'(' && canaryParenDepth > 0)
                    {
                        canaryParenDepth++;
                    }
                    else if (current == (byte)')' && canaryParenDepth > 0)
                    {
                        canaryParenDepth--;
                    }
                    else if (canaryParenDepth == 0 && IsCanaryCallStart(bytes, index, out int canaryEndIndex))
                    {
                        // Skip past the Canary( and enter canary context.
                        int distance = canaryEndIndex - index;
                        AdvanceColumn(ref column, distance);
                        canaryParenDepth = 1;
                        index = canaryEndIndex;
                        continue;
                    }
                    AdvanceLineOrColumn(current, ref line, ref column);
                    index += 1;
                    continue;

                case ScanState.LineComment:
                    if (current == (byte)'\n')
                    {
                        state = ScanState.Code;
                    }
                    AdvanceLineOrColumn(current, ref line, ref column);
                    index += 1;
                    continue;

                case ScanState.BlockComment:
                    if (current == (byte)'*' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'/')
                    {
                        state = ScanState.Code;
                        AdvanceColumn(ref column, 2);
                        index += 2;
                        continue;
                    }
                    AdvanceLineOrColumn(current, ref line, ref column);
                    index += 1;
                    continue;

                case ScanState.DoubleQuoteString:
                    if (current == (byte)'\\' && index + 1 < bytes.Length)
                    {
                        // Escape sequence: skip the next byte. (e escapes
                        // are byte-level all-ASCII, so we don't need to
                        // interpret them, just consume the backslash and the
                        // following character.)
                        AdvanceLineOrColumn(current, ref line, ref column);
                        AdvanceLineOrColumn(bytes[index + 1], ref line, ref column);
                        index += 2;
                        continue;
                    }
                    if (current == (byte)'"')
                    {
                        state = ScanState.Code;
                        AdvanceColumn(ref column, 1);
                        index += 1;
                        continue;
                    }
                    if (current >= 0x80 && canaryParenDepth == 0)
                    {
                        violations.Add(new Violation(path, line, column, current));
                    }
                    AdvanceLineOrColumn(current, ref line, ref column);
                    index += 1;
                    continue;

                case ScanState.VerbatimString:
                    if (current == (byte)'"')
                    {
                        if (index + 1 < bytes.Length && bytes[index + 1] == (byte)'"')
                        {
                            AdvanceColumn(ref column, 2);
                            index += 2;
                            continue;
                        }
                        state = ScanState.Code;
                        AdvanceColumn(ref column, 1);
                        index += 1;
                        continue;
                    }
                    if (current == (byte)'\n')
                    {
                        crossLineViolations.Add(new Violation(path, line, column, current));
                    }
                    if (current >= 0x80 && canaryParenDepth == 0)
                    {
                        violations.Add(new Violation(path, line, column, current));
                    }
                    AdvanceLineOrColumn(current, ref line, ref column);
                    index += 1;
                    continue;

                case ScanState.RawString:
                    if (current == (byte)'"')
                    {
                        int quoteRun = 0;
                        while (index + quoteRun < bytes.Length && bytes[index + quoteRun] == (byte)'"')
                            quoteRun++;
                        if (quoteRun >= rawStringQuoteCount)
                        {
                            // Closing fence is exactly rawStringQuoteCount quotes;
                            // any extra leading quotes are content. Consume only
                            // the matching number.
                            int leading = quoteRun - rawStringQuoteCount;
                            AdvanceColumn(ref column, quoteRun);
                            index += quoteRun;
                            state = ScanState.Code;
                            rawStringQuoteCount = 0;
                            // Leading extra quotes are content (already passed over).
                            // We don't need to back up because we already advanced past them.
                            // (Spec says the closing fence is the same length as the opening fence.)
                            _ = leading;
                            continue;
                        }
                        AdvanceColumn(ref column, quoteRun);
                        index += quoteRun;
                        continue;
                    }
                    if (current == (byte)'\n')
                    {
                        crossLineViolations.Add(new Violation(path, line, column, current));
                    }
                    if (current >= 0x80 && canaryParenDepth == 0)
                    {
                        violations.Add(new Violation(path, line, column, current));
                    }
                    AdvanceLineOrColumn(current, ref line, ref column);
                    index += 1;
                    continue;

                case ScanState.CharLiteral:
                    if (current == (byte)'\\' && index + 1 < bytes.Length)
                    {
                        AdvanceLineOrColumn(current, ref line, ref column);
                        AdvanceLineOrColumn(bytes[index + 1], ref line, ref column);
                        index += 2;
                        continue;
                    }
                    if (current == (byte)'\'')
                    {
                        state = ScanState.Code;
                        AdvanceColumn(ref column, 1);
                        index += 1;
                        continue;
                    }
                    if (current >= 0x80 && canaryParenDepth == 0)
                    {
                        violations.Add(new Violation(path, line, column, current));
                    }
                    AdvanceLineOrColumn(current, ref line, ref column);
                    index += 1;
                    continue;
            }
        }
    }

    private static bool IsCanaryCallStart(byte[] bytes, int index, out int endIndex)
    {
        endIndex = index;
        const string keyword = "Canary";
        if (index + keyword.Length >= bytes.Length) return false;
        for (int i = 0; i < keyword.Length; i++)
        {
            if (bytes[index + i] != (byte)keyword[i]) return false;
        }
        // Must be a word boundary before: previous byte (if any) can't be
        // an identifier character.
        if (index > 0)
        {
            byte prev = bytes[index - 1];
            if (IsIdentifierByte(prev)) return false;
        }
        // Must be optional whitespace then '(' after the keyword.
        int afterIndex = index + keyword.Length;
        while (afterIndex < bytes.Length && IsAsciiWhitespace(bytes[afterIndex]))
            afterIndex++;
        if (afterIndex >= bytes.Length || bytes[afterIndex] != (byte)'(') return false;
        endIndex = afterIndex + 1;
        return true;
    }

    private static bool IsIdentifierByte(byte b)
    {
        return (b >= (byte)'a' && b <= (byte)'z')
            || (b >= (byte)'A' && b <= (byte)'Z')
            || (b >= (byte)'0' && b <= (byte)'9')
            || b == (byte)'_';
    }

    private static bool IsAsciiWhitespace(byte b)
    {
        return b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r';
    }

    private static void AdvanceLineOrColumn(byte b, ref int line, ref int column)
    {
        if (b == (byte)'\n')
        {
            line++;
            column = 1;
        }
        else
        {
            column++;
        }
    }

    private static void AdvanceColumn(ref int column, int count)
    {
        column += count;
    }
}
