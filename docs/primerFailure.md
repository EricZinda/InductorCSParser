# Inductor Parser Primer: Parsing Errors

The other primers assume the input matches your grammar. This one is about the other case: what the parser tells you when the input doesn't match, how to attach your own messages to the rules that fail, and how to reshape or localize the default text. The examples reuse the INI grammar from [Primer 2: Parsing and Processing](primer2.md).

`Parse()` returns a `ParseResult`, and when a parse fails it tells you where the problem is:

```CSharp
var result = config.Parse("[server]\nport oops\n");
if (!result.Success)
    Console.WriteLine(result.ErrorMessage);
```

That input tried to use `port oops` as a key/value pair without an `=` sign. The output is:

```
Unexpected 'o' at line 2, column 6.
```

The default message points at the error with a one-based line and column, counting the column in graphemes so it lines up with the characters a person sees. When a tool is consuming the position instead of a person, you can read it off the result object. `result.ErrorLine` and `result.ErrorCharColumn` are zero-based (the Language Server Protocol convention), with the column counted in chars. Those are the conventions an editor or LSP client expects. The line count uses every terminator the parser treats as ending a line (the full Unicode set, not just `\n`, `\r\n` or lone `\r`), so it matches an editor on ordinary input and diverges only on the rarer terminators. Several other position units are available, covered in the Unicode section of [Primer 2: Parsing and Processing](primer2.md).

The default is a generic error that only says something went wrong. To report a more grammar-specific error, attach `.WithError(...)` to the rule that's most likely to be where the user went wrong:

```CSharp
var keyValue = And(
    key,
    Optional(InlineWhitespace()),
    Token('=').WithError("Expected '=' after the setting name"),
    Optional(InlineWhitespace()),
    value,
    Optional(InlineWhitespace()),
    EndOfLine())
    .As("keyValue");
```

If `Token('=')` is the deepest failure when a parse fails (the rule that got furthest before giving up), `result.ErrorMessage` uses your custom string instead of the default. Re-running the same `[server]\nport oops\n` input now reports:

```
Expected '=' after the setting name at line 2, column 6.
```

Your custom text flows through the template, `WithErrorTemplate`, which by default appends the failure position, so a custom message has the same position as the default. To change that, set `WithErrorTemplate` on `ParseOptions` to add whatever you want (or nothing at all), or use whatever position indicators you choose.

The example above attaches `.WithError(...)` to a `Token('=')` that's constructed right there in the `And(...)`, so the message is bound to that one caller. But what if the rule you want to decorate is being used in several places? Setting `.WithError("...")` on the shared rule means it will be used everywhere that rule is shared.

`AliasRule` is designed for this scenario. It runs the same inner rule but gives it a new identity, its own `.WithError(...)` slot, and its own `FlattenType`:

```CSharp
// shared, no error message
var comma = Token(',').Flatten(FlattenType.Delete);  

// At the one caller that wants a message:
new AliasRule(comma).WithError("expected ',' after citation key")
```

The alias defaults to `FlattenType.Flatten`, so it contributes no tree node, which is what you want for a delimiter. The shared `comma` stays untouched everywhere else it's referenced. 

`.WithError` covers the rules you can predict will fail. For the rest, the parser uses default messages. These are stored as templates on `ParseOptions` and include `{name}`-style placeholders to insert text from the runtime state like position of the error. 

Going back to the basic grammar (the version before we attached `.WithError`), suppose you want the catch-all rendered in French:

```CSharp
var options = new ParseOptions
{
    PositionalErrorTemplate = "Erreur à la ligne {lineNumber}, colonne {tokenColumnNumber}: caractère '{character}' inattendu.",
    EndOfInputErrorTemplate = "Fin d'entrée inattendue.",
};

var result = config.Parse("[server]\nport oops\n", options);
Console.WriteLine(result.ErrorMessage);
```

Output:

```
Erreur à la ligne 2, colonne 6: caractère 'o' inattendu.
```

This allows you to localize the parser errors, even the default ones.

This primer covers the everyday cases. For the full model of how the parser decides which failure to report and where it lands, including how `.WithError` messages are ranked and when a deeper failure shadows them, see [ErrorArchitecture.md](ErrorArchitecture.md).
