# Need to code/comment review all tests and docs still

Done:
src/InductorParser/*
readme.md
docs/primer1.md
docs/primer2.md
docs/primer3.md
docs/primer4.md
docs/primerFailure.md
docs/tutorial-peek.md
docs/UnicodeGotchas.md
docs/UnicodeInternalsArchitecure.md
docs/CodeArchitecture.md
docs/ErrorArchitecture.md
docs/MappingPositionsAfterNormalization.md
docs/TestArchitecture.md
docs/Terminology.md

Reference:
    Building a Grammar
    Starting a Parse
    Parse Results
    Writing a Rule

Fixes:
.NET's segmentation and normalization don't necessarily use the same version of Unicode data.
what does this mean?


By default, the parser throws an exception if .NET is configured to skip Unicode normalization or use Windows NLS instead of ICU.
For normalization and grapheme segmentation