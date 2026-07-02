# Lexer.InheritBudgetFrom(null) throws raw NullReferenceException instead of ArgumentNullException

    - `Lexer.InheritBudgetFrom` (src/InductorParser/Lexing/Lexer.cs:157) is `public void InheritBudgetFrom(Lexer parent) => _budget.InheritFrom(parent._budget);`. It dereferences `parent._budget` before `ParseBudget.InheritFrom`'s own null check (src/InductorParser/Lexing/ParseBudget.cs:82, `parent ?? throw new ArgumentNullException(nameof(parent))`) can run, so that check is unreachable for a null `Lexer`. The caller gets a bare NullReferenceException with no parameter name.
    - User-visible consequence: the method's doc comment explicitly targets user-defined rules that build sub-lexers the way WithinToken does, so a null parent is a plausible user mistake (an optional outer lexer that wasn't set, a field read before initialization). The 2026-05-28 null-argument sweep standardized ArgumentNullException across the public surface (commits d52ad58, 476bc91, 37bccb3, and the pattern doc docs/PotentialBugSources/2026-05-28-public-entrypoint-preflight-before-argument-validation.md, which describes exactly this shape: a preflight dereference beating the argument validation). The sibling gaps from the same public-surface commit (13021ee), `TryPeekRune` and `PeekTokenLength`, were found and fixed on 2026-06-02. This one was missed. No test in InductorParser.Tests or InductorParser.ExternalContractTests touches `InheritBudgetFrom` at all.
    - Verify the Bug (Write Test First): in the Lexer public-API validation tests (wherever the TryPeekRune/PeekTokenLength null-and-bounds tests from 2026-06-02 live):
      ```csharp
      var lexer = new Lexer("abc", oneRunePerToken: true);
      Assert.Throws<ArgumentNullException>(() => lexer.InheritBudgetFrom(null!));
      ```
      Run: `./test.sh recursive --filter "FullyQualifiedName~InheritBudgetFrom"`. Pre-fix it fails because the thrown exception is NullReferenceException.
    - Fix: validate the parameter before touching it, matching the sweep's idiom, e.g. reshape the expression body to `_budget.InheritFrom((parent ?? throw new ArgumentNullException(nameof(parent)))._budget);` or expand to a block with the check first (the block form reads better).
    - Verify the Fix: the new test passes, full `./test.sh recursive` green.
