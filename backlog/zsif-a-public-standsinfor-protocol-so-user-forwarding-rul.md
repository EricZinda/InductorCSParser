# A public StandsInFor protocol so user forwarding rules survive LateBound FlattenType resolution

Came out of the AliasRule transparency fix (July 9, 2026), where the
unnamed alias became a delegator like LateBoundRule.

## The gap

A third-party rule author can build their own forwarding rule today.
Rule.DeclaredFlattenType is protected, the FlattenType and EmitsLeaf
getters are virtual, and the ExternalContractTests project proves
AliasRule itself compiles against just the public + protected surface.
What they can't do is teach LateBoundRule's Compile-time FlattenType
resolver about their rule. ResolveTargetFlattenType walks .Bind(...)
chains through the two built-in stand-ins by concrete type check
(`current is LateBoundRule`, `current is AliasRule alias &&
alias.IsTransparent`). A user-defined transparent rule sitting
between LateBoundRules in a bind chain ends the walk early, and the
final `current.FlattenType` read can then recurse into a LateBoundRule
in the same chain whose ValidateCompiled hasn't run yet. That throws
the misleading "Compile the grammar first" error in the middle of
Compile, or resolves or doesn't depending on walk order.

This is an exotic topology and nobody has hit it. It only matters once
someone writes their own alias-like rule and puts it inside a
recursive bind chain.

## The shape, probably

One virtual property on Rule, something like:

```csharp
public virtual Rule? StandsInFor => null;
```

Non-null means "this rule asserts nothing of its own, resolve through
me to the returned rule". LateBoundRule returns its bound target,
AliasRule returns the inner while transparent (null once .As or
.Delete gives it a policy of its own), and ResolveTargetFlattenType
walks the protocol with its existing visited set instead of
type-checking. AliasRule.IsTransparent and AliasRule.Inner stop being
special internal knowledge.

## The wrinkle to design first

An unbound LateBoundRule is a stand-in with nothing to stand in for.
The resolver currently tells that apart from "concrete rule, stop
walking" so it can throw the helpful "was never bound" error. A
nullable property can't say "I forward, but my target is missing",
since null already means "I'm concrete". Either the protocol needs a
second signal for that state, or LateBoundRule keeps a type check just
for the unbound error while everything else goes through the protocol.

## Why not now

It's public API for a consumer that doesn't exist yet. Parse-time code
never needs the concept (FlattenType and EmitsLeaf forwarding already
make stand-ins look like their targets to every parent), so the
protocol serves exactly one reader, the Compile-time resolver. Waiting
costs nothing: the current type checks are contained in
ResolveTargetFlattenType and swap out cleanly.
