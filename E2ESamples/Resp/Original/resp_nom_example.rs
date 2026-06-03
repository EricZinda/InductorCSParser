// A representative RESP (REdis Serialization Protocol, version 2) parser built
// with nom's length_data / length_count combinators. Written from nom's
// documented API to mirror how a nom user would tackle RESP; it is kept here
// for reference only and is NOT compiled by the .NET solution (it's Rust).
//
// nom: https://github.com/rust-bakery/nom  (MIT, (c) 2014-2019 Geoffroy Couprie)
// length_data / length_count live in src/multi/mod.rs. Their doc text, verbatim
// from upstream commit fcc9f16b31804448d5cc71f6474c883ac7cf5624:
//
//   length_data:  "Gets a number from the parser and returns a subslice of the
//                  input of that size."
//   length_count: "Gets a number from the first parser, then applies the second
//                  parser that many times."
//
// Those two combinators are the whole point: the bulk-string payload length and
// the array element count are read from the input and then drive how much more
// input is consumed. That's context-sensitive, so it can't be expressed by
// composing nom's context-free combinators, which is exactly why nom ships
// these as primitives.

use nom::branch::alt;
use nom::bytes::complete::{tag, take_until};
use nom::character::complete::{char, i64 as parse_i64, u64 as parse_u64};
use nom::combinator::map;
use nom::multi::{length_count, length_data};
use nom::sequence::{delimited, preceded, terminated};
use nom::IResult;

#[derive(Debug, PartialEq)]
enum Resp {
    SimpleString(String),
    Error(String),
    Integer(i64),
    BulkString(Vec<u8>),
    Array(Vec<Resp>),
}

const CRLF: &str = "\r\n";

// A line is everything up to the next CRLF, with the CRLF consumed.
fn line(input: &str) -> IResult<&str, &str> {
    terminated(take_until(CRLF), tag(CRLF))(input)
}

fn simple_string(input: &str) -> IResult<&str, Resp> {
    map(preceded(char('+'), line), |s| Resp::SimpleString(s.to_string()))(input)
}

fn error(input: &str) -> IResult<&str, Resp> {
    map(preceded(char('-'), line), |s| Resp::Error(s.to_string()))(input)
}

fn integer(input: &str) -> IResult<&str, Resp> {
    map(delimited(char(':'), parse_i64, tag(CRLF)), Resp::Integer)(input)
}

// Bulk string: '$', then length_data reads the count (terminated by CRLF) and
// returns exactly that many bytes; a trailing CRLF closes it.
fn bulk_string(input: &str) -> IResult<&str, Resp> {
    let length = terminated(parse_u64, tag(CRLF));
    map(
        preceded(char('$'), terminated(length_data(length), tag(CRLF))),
        |bytes: &str| Resp::BulkString(bytes.as_bytes().to_vec()),
    )(input)
}

// Array: '*', then length_count reads the count (terminated by CRLF) and runs
// `value` that many times.
fn array(input: &str) -> IResult<&str, Resp> {
    let count = terminated(parse_u64, tag(CRLF));
    map(preceded(char('*'), length_count(count, value)), Resp::Array)(input)
}

fn value(input: &str) -> IResult<&str, Resp> {
    alt((simple_string, error, integer, bulk_string, array))(input)
}

fn main() {
    assert_eq!(value("+OK\r\n"), Ok(("", Resp::SimpleString("OK".into()))));
    assert_eq!(value(":1000\r\n"), Ok(("", Resp::Integer(1000))));
    assert_eq!(
        value("$5\r\nhello\r\n"),
        Ok(("", Resp::BulkString(b"hello".to_vec())))
    );
    // The point of length_data: a CRLF inside the payload is data, not a
    // terminator, because the length says so.
    assert_eq!(
        value("$6\r\nab\r\ncd\r\n"),
        Ok(("", Resp::BulkString(b"ab\r\ncd".to_vec())))
    );
    assert_eq!(
        value("*2\r\n:1\r\n:2\r\n"),
        Ok(("", Resp::Array(vec![Resp::Integer(1), Resp::Integer(2)])))
    );
    // A length error: the payload is shorter than its declared count.
    assert!(value("$5\r\nhi\r\n").is_err());
}
