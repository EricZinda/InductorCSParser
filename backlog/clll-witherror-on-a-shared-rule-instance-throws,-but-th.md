# WithError on a shared rule instance throws, but the failure mode looks like a TypeInitializationException

- Surfaced by: the CLDR plural-rules sample (E2ESamples/CldrPlural). The grammar reuses one shared `value` rule (digit+) across four different positions where the consumer wants four different "expected digit after X" error messages:
    - after `..` in a range: "Expected digit after '..' in range"
    - after `mod` in an expr: "Expected digit after 'mod'"
    - after `%` in an expr: "Expected digit after '%'"
    - after `is` in an is-relation: "Expected value after 'is'"
- The natural way to spell each call site is `value.WithError("...")`. That works for the first call, but each subsequent call throws `InvalidOperationException` at static initialization: `".WithError(\"after mod\") can't be applied to this rule: it already has the error message \"after ..\""`. The error message itself is good — it even names the workaround ("build a factory function that returns a fresh rule each call"). The problem is two-fold:
    - The failure mode lands as `TypeInitializationException` from the test runner, with the real `InvalidOperationException` one level deeper in `InnerException`. Most NUnit test output formats only show the top-line message, so the actual diagnostic is buried. The reader sees "type initializer threw an exception" first and has to dig.
    - The "build a factory" suggestion is the obvious fix conceptually, but the natural shape — a fresh `ScanWhile(TokenSet.Ascii.Digits).As("value").Preserve()` per call — runs into the duplicate-name check at Compile time ("Two reachable rules share the name 'value'"). So the user discovers the WithError-is-set-once rule, fixes it the obvious way, and then trips the OTHER set-once rule (names are unique). The working pattern (an `And(inner).WithError(message)` wrapper that flattens out at tree-flatten time so the inner Symbol surfaces directly) is the third thing tried, and it's not obvious from the WithError error message.
- Current workaround in the sample: a local `Expect(inner, error)` helper inside the static constructor:
    ```csharp
    static Rule Expect(Rule inner, string error) =>
        And(inner).WithError(error);
    ```
- Proposed change: one of three options, pick whichever is least invasive.
    1. The simplest: include the wrapper pattern in the WithError error message itself. The current message says "build a factory function that returns a fresh rule each call". Change it to something like: "WithError is per-rule-instance and set-once. To attach a different error to the same shared rule at multiple call sites, wrap it: `And(sharedRule).WithError(message)`. The And wrapper is fresh per call, so it can carry its own error, and its default Flatten policy keeps the inner Symbol's tree shape."
    2. Stronger: a `Rule.WithErrorAtCallSite(message)` factory that builds a fresh wrapper rule for you. The user-visible call site reads exactly like `value.WithError(message)` but builds a fresh wrapper internally.
    3. Strongest: make WithError on a shared rule a no-op-with-deprecation-warning, and silently substitute the wrapper shape. That's invasive but the most caller-friendly: it makes the natural-looking code do the right thing.
- Done when: a grammar can write `value.WithError("first")` and `value.WithError("second")` on the same shared rule reference and get two different error messages at the two call sites, OR the error message points the caller at the exact wrapper pattern they need, by code example, not by an abstract "build a factory" hint.
