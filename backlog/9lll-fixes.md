- Fixes
TryPeekRune decodes
        // the first rune correctly even when it's a supplementary-plane
        // code point that spans two UTF-16 chars (emoji, CJK above
        // U+FFFF) —
        // _expected[0] would hand back just the high surrogate, which isn't
        // a valid rune.
This comment belongs on other rules that do this in the same place too, right?
But describe it with less jargon

        // Pass null; shim will allocate a scratch if inner is Flatten.
This needs to be spelled out more plainly
	
Are there properties, constructors members or methods that aren't used by aything?
	
