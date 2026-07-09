# TokenSet.EnumerateMultiRuneGraphemes returns the internal array, so a cast can corrupt shared sets

    - Where this came up: a first-principles bug hunt on 2026-07-08. TokenSet's class doc says "The struct is immutable," but `EnumerateMultiRuneGraphemes()` returns the internal `string[]` directly as `IEnumerable<string>` (the `_multiRuneGraphemes ?? Array.Empty<string>()` return in TokenSet.cs). A caller can cast the result back to `string[]` and write through it.
    - Why it matters: the set operators share grapheme arrays between sets on their fast paths (`MergeMultiRuneGraphemeUnion` and `MergeMultiRuneGraphemeDifference` return `a` or `b` unchanged when one side contributes nothing), so mutating one set's array corrupts every set that shares it, including a built-in like `TokenSet.LineTerminators` when it was an operand. The same array feeds the binary search in `ContainsToken`, plus `Equals` and `GetHashCode`, so one write breaks membership, equality, and hashing in sets that look unrelated to the caller.
    - Repro (verified 2026-07-08 on this branch):

        ```csharp
        var mine  = TokenSet.Graphemes("\r\n");
        var union = mine | TokenSet.Runes(",");           // union shares mine's grapheme array
        Console.WriteLine(union.ContainsToken("\r\n"));   // True
        ((string[])mine.EnumerateMultiRuneGraphemes())[0] = "zz";
        Console.WriteLine(union.ContainsToken("\r\n"));   // False. union is corrupted.
        ```

    - The codebase's own pattern elsewhere is the opposite: Symbol wraps children with `AsReadOnly` exactly so `Symbol.Children` can't be cast back to a mutable type, and ParseResult does the same for its symbol list. The span-shaped accessor `MultiRuneGraphemes` is already safe (`ReadOnlySpan<string>` doesn't permit element writes).
    - Possible fix: have `EnumerateMultiRuneGraphemes()` yield the entries (or wrap the array in `Array.AsReadOnly`) so the concrete array never escapes. Yielding keeps the current lazy shape. Either way the cast stops compiling into a working write.
    - Done when: casting the return value to `string[]` can't reach the set's internal state, and a regression test asserts a set (and a set sharing its array via an operator) still matches after a caller tries the cast-and-write.
