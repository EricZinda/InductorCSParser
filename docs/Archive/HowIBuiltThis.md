- Running linting at checkin time was WAY more effective at fixing verbal ticks than telling claude up front.  Lots always bled through.
- Claude will create infinite tests and documentation. You have to think hard about what you want covered if you plan to actually review it all (as I did)
- I let claude write all the commit messages. These I didn't review.
- Was great for fine tuning that would have taken forever and probably not gotten fixed. Things like better error messages that touched 250 tests.
- Did claude bug hunting. specific focus on security, or unicode, or edge cases
- Did claude app building by rewriting MIT licensed apps
- Did claude performance tuning, ended up removing some

Doing a doc and comment scrub was a TON of work
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

Testing:
- Even when random numbers are used to pick files, Claude seems to find the same bug on different worktrees when starting from different places. picking very specific test criteria like "look for threading bugs" helps, saying "find any bug" and giving it different places to look doesnt. it still seems to migrate back to the ame place from different directions

Using claude to build custom rules found some great issues with internal methods, etc. It was able to find custom rules that others had written on other parsers that couldn't be written as the API was designed initially.

New theory: Claude lets you write all of the code and the text you should’ve written in the first place and the overload is because that’s how we overloading it really is. The reason why my software sucks is that nobody’s wanting to do that

this has really been like talking a a dev as a PM.  Asking questions about code, algorithms. getting them to explain, simplify, etc