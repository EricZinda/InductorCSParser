
# Inductor Parser Tutorial: Peek
Let's build something that confirms a password conforms to a set of rules (from [StackOverflow](https://stackoverflow.com/questions/19605150) ):

- contains at least eight characters
- including at least one number and
- includes both lower and uppercase letters and
- include at least one special characters, #, ?, !.
- cannot be your old password
- cannot contain your username, "password", or "websitename"

The best marked answer at the time of this writing lists several variants, the last of which is:

```Regex
"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&])[A-Za-z\d@$!%*?&]{8,10}$"
```

But in order to actually meet the OP's requirements it had several gaps:

- Missing the username, website, previous password, and `"password"` substring checks.
- Capped the password length at 10. The OP said "at least 8" with no upper bound.
- Restricted the body to only the required-set characters. The OP said what *must* appear, not what *may* appear.
- Used `\d`, which in .NET regex matches all Unicode decimal digits (Arabic-Indic, Devanagari, etc.), while `[a-z]` and `[A-Z]` are ASCII-only. 

Here's the fixed version in C# regex, formatted to be a bit more readable:
```CSharp
var originalPassword = ... get password ...;
var username = ... get username ...;
var websitename = ... get websitename ...;

string pattern = $@"^(?!{Regex.Escape(originalPassword)}$)" +
                 $@"(?!.*{Regex.Escape(username)})" +
                 $@"(?!.*password)" +
                 $@"(?!.*{Regex.Escape(websitename)})" +
                 $@"(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[#?!])" +
                 $@".{{8,}}$";

bool isValid = Regex.IsMatch(input, pattern);
```

To do this in Inductor Parser, we can start by thinking about how to scan a string until we hit something specific. Inductor Parser has a rule for this: `ScanUntil`. To scan a string until you hit a number you'd say:
```CSharp
ScanUntil(TokenSet.Range('0', '9'))
```
`ScanUntil` fails if it reaches end-of-input without ever matching its stopper, so on its own it already answers "did the input contain a digit?" (success means yes, failure means no). To make it more readable for how we're using it, we can wrap it in our own rule:

```CSharp
Rule Contains(TokenSet options) =>
    ScanUntil(options);

// Scan for one number
Contains(TokenSet.Range('0', '9'))

// Scan for one upper case ASCII
Contains(TokenSet.Range('A', 'Z'))

// Scan for one lower case ASCII
Contains(TokenSet.Range('a', 'z'))

// Scan for one special character
Contains(TokenSet.Runes("#?!"))
```
Those rules succeed if they find at least one of the characters we specify, but they also *consume* the body up to that character as they go. So running them one after the other wouldn't check the whole password each time, only what's left after the previous rule succeeded.

The `Peek` rule is designed for just this case. Like `Not` it checks if something is upcoming, but doesn't *consume* it. So, we can simply `Peek` at each rule so they each get to look at the entire password:

```CSharp
Rule Contains(TokenSet options) =>
    Peek(ScanUntil(options));
```
And then we have to make this real C# by combining them into a single rule:
```CSharp
Rule Contains(TokenSet options) =>
    Peek(ScanUntil(options));

var rule = And(Contains(TokenSet.Range('0', '9')),
                 Contains(TokenSet.Range('A', 'Z')),
                 Contains(TokenSet.Range('a', 'z')),
                 Contains(TokenSet.Runes("#?!")));
```
The next two aren't character based checks, they look for whole strings:
- cannot contain your username, "password", or "websitename"
- cannot be your old password

`ScanUntil` supports the first one too, using a `Rule` overload. We can make another rule for that and use it:

```CSharp
Rule Contains(Rule rule) =>
    Peek(ScanUntil(rule));

var username = ... get username ...;
var websitename = ... get website name ...;
And(
    Not(Contains(Literal(username))),
    Not(Contains(Literal("password"))),
    Not(Contains(Literal(websitename)))
);
```
The original spec said it also can't *be* the original password, which is less strong than "contains" but we'll go with it:

```CSharp
var originalPassword = ... get original password ...;
Not(And(Literal(originalPassword), Eof()))
```
Note that we have to consume the original password *and* `Eof` otherwise it would mean "starts with".  `Eof` guarantees we hit the end of the string.

So now we have:
```CSharp
Rule Contains(Rule rule) =>
    Peek(ScanUntil(rule));

Rule Contains(TokenSet options) =>
    Peek(ScanUntil(options));

And(
    Contains(TokenSet.Range('0', '9')),
    Contains(TokenSet.Range('A', 'Z')),
    Contains(TokenSet.Range('a', 'z')),
    Contains(TokenSet.Runes("#?!")),
    Not(Contains(Literal(username))),
    Not(Contains(Literal("password"))),
    Not(Contains(Literal(websitename))),
    Not(And(Literal(originalPassword), Eof()))
);

```
But none of these actually *consume* any input. They only check properties of the password without advancing through it, which means we still have no length check. We can fix that by adding a final consuming rule that also enforces the minimum length:
- contains at least eight characters

```CSharp
var originalPassword = ... get password ...;
var username = ... get username ...;
var websitename = ... get websitename ...;

Rule Contains(Rule rule) =>
    Peek(ScanUntil(rule));

Rule Contains(TokenSet options) =>
    Peek(ScanUntil(options));

var pattern = 
    And(
        Contains(TokenSet.Range('0', '9')),
        Contains(TokenSet.Range('A', 'Z')),
        Contains(TokenSet.Range('a', 'z')),
        Contains(TokenSet.Runes("#?!")),
        Not(Contains(Literal(username))),
        Not(Contains(Literal("password"))),
        Not(Contains(Literal(websitename))),
        Not(And(Literal(originalPassword), Eof())),
        AtLeast(8, AnyToken())
    );

bool isValid = pattern.Parse(input).Success;
```
Compare that to the fixed version of the top suggested Regex solution from the StackOverflow post:

```CSharp
var originalPassword = ... get password ...;
var username = ... get username ...;
var websitename = ... get websitename ...;

string pattern = $@"^(?!{Regex.Escape(originalPassword)}$)" +
                 $@"(?!.*{Regex.Escape(username)})" +
                 $@"(?!.*password)" +
                 $@"(?!.*{Regex.Escape(websitename)})" +
                 $@"(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[#?!])" +
                 $@".{{8,}}$";

bool isValid = Regex.IsMatch(input, pattern);
```

