# An unnamed Alias dissolves a named composite inner but keeps a named leaf inner

Found by a first-principles review of AliasRule (July 8, 2026). The
existing test suite doesn't catch it.

## The behavior

Wrap a named rule in a plain `Alias(...)` without naming the alias. The
alias's FlattenType stays Flatten, and AliasRule's own header says a
Flatten alias is transparent: the inner's content surfaces in the
parent with no alias identity. Transparent should mean the tree looks
the same as if the alias weren't there. It doesn't:

```csharp
var word = OneOrMore(OneOf(TokenSet.Letters)).As("word");

// Control, no alias: Find(word) locates the named composite.
And(word, Eof()).Parse("abc").Find(word);          // found

// Same grammar through a "transparent" alias: the word node is
// gone, its letter leaves spilled directly into the parent.
And(Alias(word), Eof()).Parse("abc").Find(word);   // null

// But a named leaf inner survives the same wrapper intact.
var letter = OneOf(TokenSet.Letters).As("letter");
And(Alias(letter), Eof()).Parse("a").Find(letter); // found
```

The composite case and the leaf case can't both be right.

## Why it happens

In AliasRule.TryParseRule, the content-build block after a successful
inner parse runs the same branch whether the alias is emitting its own
Preserve composite or flattening away into the caller's list. When the
inner handed back its own Preserve Symbol, the branch adds a leaf
intact but lifts a composite's children and drops the composite's own
node. That lift is right for exactly one case, a Preserve alias, where
the file's tenet says the alias substitutes its identity onto the inner
("a composite inner's children appear directly under the alias"). When
the alias itself is flattening away there is no alias identity to
substitute, so the inner should surface whole, the way AndRule, OrRule,
and BetweenInclusiveRule all add a Preserve child's Symbol intact when
they flatten themselves away.

## The fix, probably

In the composite arm, branch on effectiveFlattenType: a Preserve alias
lifts the children (identity substitution), a Flatten alias adds
innerSymbol intact (transparency).

One decision to make first: the leaf case under a Flatten alias
currently keeps the inner's leaf, id and all, which matches the
transparency reading. If the intended semantics is instead "an alias
always replaces the inner's identity" (a reading the Rules.Alias doc
supports), then the leaf branch is the wrong one and should stop
surfacing the inner's id. Whichever way it lands, the two cases have
to agree, and the AliasRule header comment should say which reading
won.

## Mitigating

An unnamed Alias is an odd construction. Alias exists to be `.As(...)`ed,
which flips it to Preserve, and AliasedAs does both in one step. So real
grammars mostly never hit this. It still contradicts the file's own
transparency comment and behaves differently for leaf vs composite
inners, which is the tell that one branch is wrong.
