# What "specific to the rule" could look like

Each rule already knows what it was looking for:

    LiteralRule            _expected      "expected 'maj'"
    GraphemeRule           _expected      "expected 'a'"
    LiteralIgnoreAsciiCase _expected      "expected 'select' (case-insensitive)"
    OneOfRule              _setRendered   "expected one of [A-Z,a-z]"
    NoneOfRule             _setRendered   "expected anything except [...]"
    EofRule                (none)         "expected end of input"
    AnyTokenRule           (none)         "expected any character"
    ScanUntilRule          terminator     "expected to find ';' before end of input"
    BetweenInclusive       min,max        "expected at least 2 of ..."

`AndRule` and `OrRule` don't usually win the deepest-failure slot (some child of them does), so their own defaults matter less.
