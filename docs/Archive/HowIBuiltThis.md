- Did claude bug hunting. specific focus on security, or unicode, or edge cases
- Did claude app building by rewriting MIT licensed apps
- Did claude performance tuning, ended up removing some

Linting rules about text was way better than saying up front
some things like commands with ";" NEVER WENT AWAY <!-- style-lint-ok: literal ";" character reference -->

Multiple large refactorings, including one where I removed all performance optimizations where the code was just too complicated and not worth it. Claude came up with some perf optimizations that I liked and accepted, but the ended up being very hard for me to validate reponsibly. Instead of just vibe-coding them, I cut them. I should have cut them much earlier

Claude is pretty bad at explaining things simply, and slowly grows comments on code over time. I needed aggressive management of this and didn't do it soon enough.

Claude was very good at estimating what would be slow and doing a fix and showing it improved the system. Not perfect, but very good. 

Snuck in: Lots of complexity got added for optimizations that eventually needed to be stripped

Some suggestions were good: A broader-than-timeout budget system was a great idea.

I ended up allowing it to do pure vibecoding of the statemachine parser as a prototype to see how far it could go and how much faster it could be, but I didn't ship it.

Learning: Don’t let Claude write the comments for anything, write them themselves to make them actually readable and convince myself I understand it

Claude Let me compress the exploration and multiple rewrites of the project into a much shorter time:
- Try supporting both a Rune based and Grapheme based parser, and then settle on Grapheme
- Try support a bunch of detailed perf improvements and then cut them because of complexity and maintance overhead
- Try various different ways of modelling TokenSet to make it safe and usable
- Form-aware Compile auto-converts grammar literals
- Self-contained Symbol.SourceRange and Symbol.SourceText
- AllOf/FirstOf → And/Or rename
