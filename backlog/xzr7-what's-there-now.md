# What's there now

The default messages live in one place: `ParseOptions.cs` lines 108 to 181. Six templated strings (positional, EOF, plus four budget aborts). The two failure-path ones are:

    PositionalErrorTemplate  "Parse failed at offset {charIndex}: unexpected '{character}'."
    EndOfInputErrorTemplate  "Unexpected end of input."

Every rule that fails calls `lexer.RecordFailure(pos, ErrorMessage)` (Lexer.cs:438) passing its own `_errorMessage` (the `.WithError(...)` text, or null). On failure, `Rule.BuildErrorMessage` (Rule.cs:934-968) picks: user's WithError if set, otherwise the EOF template if the failure is at end of input, otherwise the positional template.

So the actual default a user sees on a normal failure is always one of two sentences. The rule that failed doesn't get a say.
