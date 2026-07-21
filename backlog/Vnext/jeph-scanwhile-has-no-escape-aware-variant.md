# ScanWhile has no escape-aware variant

`ScanUntil` can scan with escapes. `ScanWhile` can't. That asymmetry is
the gap.

A common token shape is "a sequence of characters in some class, where a
backslash escapes the next character so an otherwise-illegal character can
appear literally." For example, an unquoted term that normally stops at a
colon, but where `\:` keeps the colon in the token:

```
term = ( "\\" anyChar | [^: \t] )+
```

So `foo\:bar` is one token whose text is `foo\:bar`, even though a bare `:`
would end it. The natural InductorParser translation is a character-class
scan:

```csharp
var termChars = ~TokenSet.Runes(": \t");
var term = ScanWhile(termChars, minimumCount: 1);
```

But `ScanWhile` has no notion of an escape. On `foo\:bar` it stops at the
`:` regardless (`:` is outside the class), so the escaped colon is never
consumed. There's no overload that says "scan this class, but when you hit
this rune, consume the next rune too and keep going."

`ScanUntil` already has exactly that, via the
`ScanUntil(TokenSet stopAt, Rune escapeStart, Rule escapeEnd, ...)`
overloads (the shape used for backslash-escaped string-literal bodies like
`"a \" b"`). The until-a-stopper scanner can escape, the while-in-a-class
scanner can't.

Today you either drop down to `OneOrMore(Or(And(Token('\\'), AnyToken()), OneOf(termChars)))`
(slower, allocates a Symbol per piece, loses the single-leaf scan) or
forbid escaped specials inside the run.

Proposal: add an escape-aware `ScanWhile` overload mirroring `ScanUntil`'s:

```csharp
ScanWhile(TokenSet set, Rune escapeStart, Rule escapeEnd, int minimumCount = 1)
```

When the scan hits `escapeStart`, run `escapeEnd` to consume the escape
body and resume the class scan, the same way the `ScanUntil` escape path
already works. The two scanners would then be symmetric on escapes.
