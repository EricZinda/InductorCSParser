When you build grammars in the Inductor Parser you don't need to worry about the encoding complexities of Unicode, you build rules around the characters you care about and the engine ensures that:

1) The text stream is normalized into a form that is canonical. Invalid Unicode throws.
2) Characters in your rules are encoded in the same canonical form so they match properly. Rules in non-canonical form throw.
3) Tokens given to your rules are always characters the user (and you!) perceives 

It is designed so you can safely write grammars over Unicode text without having to be a Unicode expert. 

Let's imagine we're building a parser for a French program that will launch other programs, one after the other.  The syntax we want is:

```
exécuter ProgramName
exécuter ProgramName
...
```
Where `ProgramName` can be any Unicode string without whitespace.

The grammar is simple:
```
launchStatement = AllOf(Literal("exécuter"), 
                        InlineWhitespace(), 
                        OneOrMore(NoneOf(TokenSet.AnyWhitespace)).As("program"),
                        EndOfLine(eofIsEol:true));
document = OneOrMore(launchStatement);
```
