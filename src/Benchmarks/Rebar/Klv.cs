// KLV reader for the rebar runner protocol (BurntSushi/rebar). The wire
// format is documented at https://github.com/BurntSushi/rebar/blob/master/KLV.md.
// This is an independent .NET implementation; no rebar source is reused.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InductorParser.Benchmarks.Rebar;

internal sealed class RebarConfig
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string Name { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public List<string> Patterns { get; } = new();
    public bool CaseInsensitive { get; private set; }
    public bool Unicode { get; private set; }
    public string Haystack { get; private set; } = string.Empty;
    public long MaxIters { get; private set; }
    public long MaxWarmupIters { get; private set; }
    public long MaxTimeNanoseconds { get; private set; }
    public long MaxWarmupTimeNanoseconds { get; private set; }

    public static RebarConfig Read(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Read(memory.ToArray());
    }

    public static RebarConfig Read(byte[] raw)
    {
        var config = new RebarConfig();
        int offset = 0;
        while (offset < raw.Length)
        {
            int keyStart = offset;
            int keyEnd = IndexOf(raw, (byte)':', offset);
            if (keyEnd < 0)
                throw new InvalidDataException("Invalid KLV item: missing key separator.");
            string key = DecodeUtf8(raw, keyStart, keyEnd - keyStart, "key");

            int lengthStart = keyEnd + 1;
            int lengthEnd = IndexOf(raw, (byte)':', lengthStart);
            if (lengthEnd < 0)
                throw new InvalidDataException($"Invalid KLV item '{key}': missing length separator.");
            int valueLength = ParseLength(raw, lengthStart, lengthEnd - lengthStart, key);

            int valueStart = lengthEnd + 1;
            int valueEnd = valueStart + valueLength;
            if (valueEnd > raw.Length)
                throw new InvalidDataException($"Invalid KLV item '{key}': value length exceeds input.");
            if (valueEnd >= raw.Length || raw[valueEnd] != (byte)'\n')
                throw new InvalidDataException($"Invalid KLV item '{key}': missing trailing newline.");

            config.Apply(key, raw, valueStart, valueLength);
            offset = valueEnd + 1;
        }

        if (string.IsNullOrEmpty(config.Name))
            throw new InvalidDataException("KLV input didn't include a benchmark name.");
        if (string.IsNullOrEmpty(config.Model))
            throw new InvalidDataException("KLV input didn't include a benchmark model.");

        return config;
    }

    private void Apply(string key, byte[] raw, int start, int length)
    {
        switch (key)
        {
            case "name":
                Name = DecodeUtf8(raw, start, length, key);
                break;
            case "model":
                Model = DecodeUtf8(raw, start, length, key);
                break;
            case "pattern":
                Patterns.Add(DecodeUtf8(raw, start, length, key));
                break;
            case "case-insensitive":
                CaseInsensitive = ParseBool(DecodeUtf8(raw, start, length, key), key);
                break;
            case "unicode":
                Unicode = ParseBool(DecodeUtf8(raw, start, length, key), key);
                break;
            case "haystack":
                Haystack = DecodeUtf8(raw, start, length, key);
                break;
            case "max-iters":
                MaxIters = ParseInt64(DecodeUtf8(raw, start, length, key), key);
                break;
            case "max-warmup-iters":
                MaxWarmupIters = ParseInt64(DecodeUtf8(raw, start, length, key), key);
                break;
            case "max-time":
                MaxTimeNanoseconds = ParseInt64(DecodeUtf8(raw, start, length, key), key);
                break;
            case "max-warmup-time":
                MaxWarmupTimeNanoseconds = ParseInt64(DecodeUtf8(raw, start, length, key), key);
                break;
        }
    }

    private static int IndexOf(byte[] bytes, byte needle, int start)
    {
        for (int index = start; index < bytes.Length; index++)
            if (bytes[index] == needle)
                return index;
        return -1;
    }

    private static int ParseLength(byte[] raw, int start, int length, string key)
    {
        string text = DecodeUtf8(raw, start, length, $"{key} length");
        if (!int.TryParse(text, out int value) || value < 0)
            throw new InvalidDataException($"Invalid KLV item '{key}': length '{text}' isn't a non-negative integer.");
        return value;
    }

    private static bool ParseBool(string text, string key) =>
        text switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidDataException($"Invalid KLV item '{key}': '{text}' isn't a boolean.")
        };

    private static long ParseInt64(string text, string key)
    {
        if (!long.TryParse(text, out long value) || value < 0)
            throw new InvalidDataException($"Invalid KLV item '{key}': '{text}' isn't a non-negative integer.");
        return value;
    }

    private static string DecodeUtf8(byte[] raw, int start, int length, string key)
    {
        try
        {
            return StrictUtf8.GetString(raw, start, length);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"KLV value for '{key}' isn't valid UTF-8.", ex);
        }
    }
}
