# CLS-compliance audit on the InductorParser public API

Where this came up: the Nunycode E2E sample under
`E2ESamples/Nunycode/`. Upstream's `Original/AssemblyInfo.cs`
has `[assembly: System.CLSCompliant(true)]`. Once the rewrite
added a `public static readonly Rule Domain` field to
`IdnGrammar`, the C# compiler emitted CS3003 ("Type of
'IdnGrammar.Domain' is not CLS-compliant") because the <!-- style-lint-ok: quotes the CS3003 message verbatim -->
`InductorParser` assembly doesn't declare itself CLS-compliant.
The fix for the sample was to drop the attribute, but that
sidesteps the underlying question: should InductorParser declare
CLS compliance so other-language consumers (VB.NET, F#, IronPython,
PowerShell hosts) can use it without per-call attribute decoration?

How CS3003 fires: the C# compiler walks every public member of an
assembly marked `[assembly: CLSCompliant(true)]` and verifies that
every type it references is itself CLS-compliant. A type counts
as compliant if its declaring assembly is marked compliant, or if
the type has `[CLSCompliant(true)]` individually. Without a
claim on the InductorParser assembly, every type defined there
reads as "compliance unknown," which the rule treats as
"not compliant" for the purposes of consumers that claim
compliance themselves.

What an audit would entail:

- Confirm InductorParser's public API stays inside the CLS subset.
  Common things that aren't in the subset:
    - Unsigned integer types in public signatures (`sbyte`,
      `ushort`, `uint`, `ulong`).
    - Pointer types in public API.
    - Identifiers that differ only by case.
    - Some operator overload shapes.
    - Generic constraints involving non-CLS types.
- Mark `[assembly: CLSCompliant(true)]` on
  `src/InductorParser/InductorParser.csproj` and `[CLSCompliant(false)]`
  on any individual member that has to stay outside the subset (with
  a comment explaining why).
- Add a CI check that the assembly stays CLS-compliant once it's
  declared so. The build will fail on CS3003 if a new member
  introduces a non-compliant type.

Why this matters: cross-language consumability is the explicit
purpose of CLS. The InductorParser library is the kind of
infrastructure piece a polyglot project would want to call from
multiple .NET languages. Declaring compliance is a low-cost
promise IF the API already stays inside the subset, and a small
refactor IF a few members trip it.

What to do, in increasing-effort order:

- Quick survey. Grep `public` declarations in
  `src/InductorParser/` for unsigned types, pointer types,
  case-only identifier collisions. Estimate how many real changes
  an audit would require. Cost: an hour.
- Full audit + declaration. Mark the assembly compliant, fix
  whichever members trip the checks, document any individual
  `[CLSCompliant(false)]` exceptions. Cost: half a day to a day
  depending on what the survey turns up.
- Don't bother. Stay silent on CLS compliance. Consumers who care
  can wrap the library themselves with `[CLSCompliant(false)]` at
  their own boundary. This is the default until somebody actually
  wants to consume InductorParser from VB.NET or F#.

The Nunycode sample picked the third option for itself (dropped
upstream's `AssemblyInfo.cs` rather than fight the warning). That
solves the sample's local problem but defers the library-level
question.

Done when: either (a) `InductorParser.csproj` declares
`[assembly: CLSCompliant(true)]` and all CS3003 warnings clear
across consumers, or (b) we explicitly decide not to make the
promise and document the decision in the library's README so
future samples don't keep rediscovering the friction.
