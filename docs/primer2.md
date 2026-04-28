
 


, here are the most common ones. 

|                        |                |                    |
| ---------------------- | -------------- | ------------------ |
| AllOf            | Float      | OptionalEndOfLine  |
| AnyToken         | Identifier | OptionalWhitespace |
| AtLeast          | Integer    | FirstOf            |
| AtMost           | Literal    | Peek               |
| BetweenInclusive | NoneOf     | ScanUntil         |
| EndOfLine        | Not        | Token              |
| EndOfLineOrEof   | OneOf      | Whitespace         |
| Eof              | OneOrMore  | ZeroOrMore         |
| Exactly          | Optional   |                    |

build something that confirms a password conforms to a set of rules (from [StackOverflow](https://stackoverflow.com/questions/19605150) ):

- contains at least eight characters
- including at least one number and
- includes both lower and uppercase letters and
- include at least one special characters, #, ?, !.
- cannot be your old password
- cannot contain your username, "password", or "websitename"

The Inductor Parser pattern matches against the characters in a .Net string value using a set of rules. It can "capture"