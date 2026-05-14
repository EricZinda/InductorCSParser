# Pattern_White_Space TokenSet factory is missing

- Surfaced by: the CLDR plural-rules sample (E2ESamples/CldrPlural). The UTS #35 plural-rule spec says "Whitespace (defined as Unicode Pattern_White_Space) can occur between or around any of the above tokens". Pattern_White_Space is one of the standard Unicode character classes (defined in PropList.txt): eleven specific code points.
    - U+0009..U+000D (TAB, LF, VT, FF, CR)
    - U+0020 (SPACE)
    - U+0085 (NEXT LINE)
    - U+200E, U+200F (LEFT-TO-RIGHT MARK, RIGHT-TO-LEFT MARK)
    - U+2028, U+2029 (LINE SEPARATOR, PARAGRAPH SEPARATOR)
- The current implementation in the sample spells this out by hand:
    ```csharp
    var patternWhiteSpace =
          TokenSet.Range(0x0009, 0x000D)
        | TokenSet.Single(0x0020)
        | TokenSet.Single(0x0085)
        | TokenSet.Single(0x200E)
        | TokenSet.Single(0x200F)
        | TokenSet.Single(0x2028)
        | TokenSet.Single(0x2029);
    ```
- Several real-world grammars need this exact set. Off the top of my head: every CLDR LDML grammar (PluralRules, NumberFormat, MessageFormat), every UTS #18 regex character-class parser, every Unicode-identifier-syntax (UAX #31) grammar. The set is small and stable (the Unicode consortium maintains it as part of PropList.txt). It also doesn't change between Unicode versions in any practical way.
- The library already exposes `TokenSet.Ascii.Letters`, `TokenSet.Ascii.Digits`, `TokenSet.InlineWhitespace`, etc. as pre-built sets. A `TokenSet.PatternWhiteSpace` would fit the same shelf.
- While we're there, the related Unicode-property sets that show up regularly in real grammars are worth pre-building too:
    - `TokenSet.PatternSyntax` (PropList.txt's Pattern_Syntax class, used everywhere identifiers need to be distinguished from punctuation)
    - `TokenSet.IdentifierStart` (UAX #31's XID_Start)
    - `TokenSet.IdentifierContinue` (UAX #31's XID_Continue)
- Done when: `TokenSet.PatternWhiteSpace` is a pre-built static, the CLDR plural sample uses it (deleting the hand-rolled eleven-line definition), and there's a Doc-comments-with-spec-link xml comment on the field so IntelliSense surfaces the Unicode property name. A unit test pins all eleven code points in and at least one nearby code point (U+0086, U+200D, U+200E, ...) out.
