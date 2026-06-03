// Three fixtures, matching the other E2E samples' layout:
//   * GoldenInputs   valid RESP values of every type round-trip to the
//                    expected RespValue, including the cases that only the
//                    custom rules make possible: a bulk-string payload that
//                    contains CRLF, and nested arrays.
//   * RejectInputs   a truncated payload, a length/CRLF mismatch, an array
//                    short of its declared count, a bad type byte, trailing
//                    data, and empty input each fail with a sensible position.
//   * CustomRuleCore exercises LengthDataRule and LengthCountRule directly on a
//                    netstring-style shape, independent of the RESP grammar.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using InductorParser;
using static InductorParser.Rules;
using RespSample.Rewrite;

namespace RespSample.Tests;

[TestFixture]
public class GoldenInputs
{
    [Test]
    public void Simple_string()
    {
        Assert.That(RespParser.Parse("+OK\r\n"), Is.EqualTo(new RespSimpleString("OK")));
    }

    [Test]
    public void Empty_simple_string()
    {
        Assert.That(RespParser.Parse("+\r\n"), Is.EqualTo(new RespSimpleString("")));
    }

    [Test]
    public void Error()
    {
        Assert.That(RespParser.Parse("-ERR unknown command\r\n"),
            Is.EqualTo(new RespError("ERR unknown command")));
    }

    [TestCase(":1000\r\n", 1000L)]
    [TestCase(":0\r\n", 0L)]
    [TestCase(":-5\r\n", -5L)]
    [TestCase(":+42\r\n", 42L)]
    public void Integer(string input, long expected)
    {
        Assert.That(RespParser.Parse(input), Is.EqualTo(new RespInteger(expected)));
    }

    [Test]
    public void Bulk_string()
    {
        Assert.That(RespParser.Parse("$5\r\nhello\r\n"), Is.EqualTo(new RespBulkString("hello")));
    }

    [Test]
    public void Empty_bulk_string_is_not_null()
    {
        Assert.That(RespParser.Parse("$0\r\n\r\n"), Is.EqualTo(new RespBulkString("")));
    }

    [Test]
    public void Null_bulk_string()
    {
        Assert.That(RespParser.Parse("$-1\r\n"), Is.EqualTo(new RespBulkString(null)));
    }

    [Test]
    public void Bulk_string_payload_can_contain_crlf()
    {
        // The whole reason a bulk string declares its length: the payload is
        // length-counted, so a CRLF inside it is data, not a terminator. No
        // context-free grammar can do this; the length tells the custom rule
        // exactly how far to read. "ab\r\ncd" is six characters.
        Assert.That(RespParser.Parse("$6\r\nab\r\ncd\r\n"), Is.EqualTo(new RespBulkString("ab\r\ncd")));
    }

    [Test]
    public void Array_of_integers()
    {
        var value = RespParser.Parse("*2\r\n:1\r\n:2\r\n");
        var array = (RespArray)value;
        Assert.That(array.Items, Is.EqualTo(new RespValue[] { new RespInteger(1), new RespInteger(2) }));
    }

    [Test]
    public void Empty_array_is_not_null()
    {
        var array = (RespArray)RespParser.Parse("*0\r\n");
        Assert.That(array.Items, Is.Not.Null);
        Assert.That(array.Items!, Is.Empty);
    }

    [Test]
    public void Null_array()
    {
        var array = (RespArray)RespParser.Parse("*-1\r\n");
        Assert.That(array.Items, Is.Null);
    }

    [Test]
    public void Array_of_bulk_strings_is_the_canonical_redis_command()
    {
        // What a Redis client actually sends for `LLEN mylist`.
        var array = (RespArray)RespParser.Parse("*2\r\n$4\r\nLLEN\r\n$6\r\nmylist\r\n");
        Assert.That(array.Items, Is.EqualTo(new RespValue[]
        {
            new RespBulkString("LLEN"),
            new RespBulkString("mylist"),
        }));
    }

    [Test]
    public void Nested_arrays()
    {
        var outer = (RespArray)RespParser.Parse("*2\r\n*1\r\n:1\r\n+OK\r\n");
        Assert.That(outer.Items!.Count, Is.EqualTo(2));
        var inner = (RespArray)outer.Items[0];
        Assert.That(inner.Items, Is.EqualTo(new RespValue[] { new RespInteger(1) }));
        Assert.That(outer.Items[1], Is.EqualTo(new RespSimpleString("OK")));
    }
}

[TestFixture]
public class RejectInputs
{
    [Test]
    public void Bulk_string_payload_shorter_than_declared()
    {
        // Declares 5 chars; "hi\r\n" is only four before end-of-input.
        const string input = "$5\r\nhi\r\n";
        Assert.That(RespParser.TryParse(input, out _, out var error), Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(input.Length));
        Assert.That(error.Message, Is.EqualTo("bulk string payload doesn't match its declared length"));
    }

    [Test]
    public void Bulk_string_length_too_small_leaves_junk_before_crlf()
    {
        // Declares 2 chars, so the payload is "he"; the trailing CRLF check
        // then lands on "llo", which isn't CRLF.
        const string input = "$2\r\nhello\r\n";
        Assert.That(RespParser.TryParse(input, out _, out var error), Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(6));
        Assert.That(error.Message, Is.EqualTo("expected CRLF after the bulk string payload"));
    }

    [Test]
    public void Array_shorter_than_its_declared_count()
    {
        // Declares two elements; only one follows.
        const string input = "*2\r\n:1\r\n";
        Assert.That(RespParser.TryParse(input, out _, out var error), Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(input.Length));
    }

    [Test]
    public void Unknown_type_byte()
    {
        Assert.That(RespParser.TryParse("?nope\r\n", out _, out var error), Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(0));
        Assert.That(error.Message, Is.EqualTo("expected a RESP value starting with one of + - : $ *"));
    }

    [Test]
    public void Trailing_data_after_a_complete_value()
    {
        const string input = "+OK\r\n:1\r\n";
        Assert.That(RespParser.TryParse(input, out _, out var error), Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(5));
        Assert.That(error.Message, Is.EqualTo("unexpected trailing data after the RESP value"));
    }

    [Test]
    public void Empty_input()
    {
        Assert.That(RespParser.TryParse("", out _, out var error), Is.False);
        Assert.That(error!.CharIndex, Is.EqualTo(0));
    }
}

[TestFixture]
public class CustomRuleCore
{
    // A netstring-style "length:payload" shape, the smallest thing that shows
    // length_data on its own: read the count, a ':' separator, then exactly
    // that many characters. Built fresh per test so each compiles its grammar.
    private static Rule LengthData() =>
        new LengthDataRule(OneOrMore(OneOf(TokenSet.Ascii.Digits)), Literal(":"));

    // "count,item item item": read the count, a ',' separator, then run an
    // any-character item that many times.
    // .As makes the root Preserve so its Symbol (whose children are the
    // matched items) reaches result.Tree; an unnamed LengthCountRule is Flatten
    // by default and its items would land in result.Symbols instead.
    private static Rule LengthCount() =>
        new LengthCountRule(OneOrMore(OneOf(TokenSet.Ascii.Digits)), Literal(","), AnyToken())
            .As("counted");

    [Test]
    public void Length_data_reads_exactly_count_chars()
    {
        var result = LengthData().Parse("5:hello");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void Length_data_count_rule_can_delete_framing()
    {
        // The count is the Symbol's ToString(), not the consumed characters, so
        // a count rule can Delete framing it doesn't want in the number. Here
        // it reads "#5" but the '#' is Delete (Token's default), so ToString()
        // is "5". A raw-substring count would choke on the '#'.
        var rule = new LengthDataRule(
            And(Token('#'), OneOrMore(OneOf(TokenSet.Ascii.Digits))),
            Literal(":"));
        var result = rule.Parse("#5:hello");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo("hello"));
    }

    [Test]
    public void Length_data_zero_is_an_empty_payload()
    {
        var result = LengthData().Parse("0:");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.ToString(), Is.EqualTo(""));
    }

    [Test]
    public void Length_data_truncated_payload_fails()
    {
        var result = LengthData().Parse("5:hi");
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Length_data_rejects_a_length_that_splits_a_character()
    {
        // U+1F600 is two UTF-16 code units; a declared length of 1 would end
        // in the middle of it. The rule rejects rather than emit half a char.
        var result = LengthData().Parse("1:\U0001F600");
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Length_count_runs_the_item_count_times()
    {
        var result = LengthCount().Parse("3,abc");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(3));
    }

    [Test]
    public void Length_count_zero_matches_nothing()
    {
        var result = LengthCount().Parse("0,");
        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Tree!.Children.Count, Is.EqualTo(0));
    }

    [Test]
    public void Length_count_too_few_items_fails()
    {
        var result = LengthCount().Parse("3,ab");
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void Length_data_rejects_a_null_count_rule()
    {
        Assert.Throws<ArgumentNullException>(() => new LengthDataRule(null!, Literal(":")));
    }

    [Test]
    public void Length_count_rejects_a_null_item_rule()
    {
        Assert.Throws<ArgumentNullException>(
            () => new LengthCountRule(OneOrMore(OneOf(TokenSet.Ascii.Digits)), Literal(","), null!));
    }
}
