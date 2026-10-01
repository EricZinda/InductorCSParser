
# How I built this
I needed a PEG parser for a C# project I'm building so I wanted to port the C++ parser I had already open sourced to .Net. Since it was April 2026, I thought this would be a good experiment to understand the capabilities of LLMs as they stood then.

I was part of the leadership team that delivered the .Net Framework 1.0, alongside [Scott Gellock](https://www.linkedin.com/in/sgellock/) and [Destry Hood](https://www.linkedin.com/in/destry-hood-336790112/) and I managed teams that delivered several other framework components afterwards. I have a lot of experience designing, building and shipping this type of product. So, I was excited to go down that same road using an LLM to get a real understanding of their strengths and weaknesses in a domain I understood deeply.

## What I did

### Technology
I used VS Code and Claude for most of the project, interacting with Claude in the Anthropic Add-in they ship for it. I found OpenAI's Codex to not be as reliable (so I didn't use it outside of testing it occasionally) until about Sept 2026 which was probably GPT 5.5. At that point I used Claude and Codex interchangeably, finding Codex to be much faster but run out of tokens quicker.

I used Git and 6 Git worktrees for day to day development.  Often having all 6 with a different Claude instance implementing something different. 

I used the $100 a month Claude subscription and it worked well for me doing 40 hour weeks in this mode, except for a short patch in the middle of the coding phase where I upped to the $200 subscription because I was running out of tokens.

### Workflow Across Project Phases
Let's start with what I didn't do: vibe coding. This was not a "tell claude what I want and ignore the code" project. I *very much* cared and deeply reviewed what the framework looked like, its usability, performance and implementation details. <!-- style-lint-ok: author's own voice -->

I acted as (from a Microsoft Job Title perspective) the Program Manager, Dev Lead and Test Lead for the project. Claude wrote 99% of the code and comments, and probably 75% of the documentation (rough estimates). I code/doc reviewed *all* of it, and we often iterated. I treated the project as my own code and design. I cared about every line.

*Design Phase*: At the start of the project I worked with Claude like I'd work with the developer of an area I was responsible for Program Managing.  I laid out requirements, design goals, API sketches ("be like my C++ PEG Parsing framework") and then read through Claude's proposed plan and design. We iterated a ton until we had design docs that I agreed with.

*Implementation Phase*: I mostly had my dev lead hat on here and assigned Claude coding tasks and exhaustively reviewed what it wrote. Again, much iteration on every commit here.

*Code Complete/Ad Hoc Testing Phase*: I had Claude write extensive tests for each feature it wrote, but after coding was done I had it do ad hoc testing too. Here, I'd have Claude find bugs and fix them, trying as many different ways as I could to have it poke and prod the code from different directions: security, performance, usability, etc. Again, I would review the bugs, and code review the fixes. I'd make sure the test suites filled in coverage for areas where bugs exposed test gaps.

*Documentation*: This happened all along the way. Any of the documents that use the first person "I" were written by me, usually with Claude suggesting edits. All the others were in reverse: Claude wrote what I asked, and I reviewed and gave edits.

I treated code and documentation as "I have to sign off on this as if I wrote it", and reviewed it at that level of detail. I had to agree with design decisions, agree with and be able to explain and justify every line of code and documentation, etc. For Tests, I still reviewed the code but cared more about "does this test what it is supposed to test" than code quality per se.

I let claude write all the commit messages. These I didn't review.

## Takeaways
### Docs
I spent a lot of time trying to get Claude to write in the best way I know, i.e. *my* style. I had it analyze my reasonably extensive writing from my blog and projects and condense it into a style guide for itself and use that as its "voice". I also doggedly added items to claude.md to remove annoying style ticks it still retained. I found that this helped a little, but best was to run a "lint" pass before every commit that checked all of these rules. It almost always found violations.

Here are a few examples to illustrate what was in claude.md:

> Vocabulary: Never use "earn" / "earns" / "earned" / "earning" figuratively in prose or code comments to say a feature or case justifies its existence ("the case where forced earns its place", "the other two operators earn their spot", "earns its keep"). State the justification directly instead ("the case where forced is the right tool", "the other two operators are worth having because hand-enumerating the result goes stale"). The literal sense (earning money) stays. <!-- style-lint-ok: quotes the rule itself -->
>
> Sentence structure: Never open a sentence with a verbless topic label, a noun phrase dropped in front of the content like an inline section heading ("Start side, two parts: the head rune must be...", "Body side: every converted piece must land in...", "Fast path: the loop skips the check."). This is telegraphic style, it names the topic without saying anything about it, and Claude writes this shape often. Break the habit. Give the label a verb and make it a real sentence ("The start-side check has two parts: ...", "On the body side, every converted piece must land in...", "The fast path skips the check."). The comma-spliced count ("Start side, two parts:") gets the same treatment, fold the count into the sentence.

Even with claude.md and linting, I still found lots of jargon and writing I didn't like. I iterated a *ton* on the documentation. I still think I ended up with way more extensive documentation that I would have had without Claude, but it was by no means "free".

Claude is extremely prolific with comments and documentation. I ended up drastically cutting documentation and comments back as I'd review the docs in the final passes. I should have controlled this better up front: every sentence that gets written has to be reviewed. Honestly, for code comments, I wonder If I should tell Claude to not write any until asked as a way to control the volume of them. <!-- style-lint-ok: author's own voice -->


### Design
As a person working solo on a project, Claude is a good sounding board. I'd often have discussions about different approaches to designing the API or architecture. Claude would be great at pushing back on them with factual problems or suggesting alternatives. It was *key* to require it to back up any facts or opinions it had with real quotes from internet sources and their links. This would often (10-20% of the time?) make it back down or change opinion in some way.

Once I had the architecture of the app pinned down, Claude was great a suggesting features that I wouldn't have otherwise done, mostly because I wouldn't have thought of them or didn't want to spend the time implementing them. A couple of examples: <!-- style-lint-ok: author's own voice -->

- A broader-than-timeout budget system was a great idea.
- The algorithm for mapping normalized positions to original text was Claude's, and is a novel idea as far as I can tell. However, it took my iterating with Claude for 2 weeks (!) before I believed that it was provably valid.

For Claude's more complicated or new-to-me ideas, I found the best way to build trust and prove that algorithms are correct, expecially for areas like Unicode that have huge specifications, is to ask Claude to:
1) give citations that have links and verbatim quotes to support facts it states (e.g. "Unicode allows x when y"). These are usually put in the comments or docs it writes. 
2) prove the logic chain, step by step, and challenge it all along the way

Claude was *very good* at estimating what would be slow and designing code right the first time or doing a fix and showing it improved the system. Not perfect, but very good.



### Code 
The check I did on code review was to explain and justify/poke holes in the code and comments that Claude wrote. If I didn't understand it or agree with it, I pair programmed it with Claude until I did. A few times this took a LOT of time, including a couple of optimizations that I had to cut because I really couldn't get my head around what it was doing.  Maybe that's a failing on my part or maybe claude was wrong.

Claude let me compress the exploration and multiple rewrites of the project into a much shorter time. Every new framework or product I've ever worked on needed a few iterations as we learned more about the nature of the problem and this was no different. Claude made it *really* fast to completely revector an area when it was time. Some examples:

- Try supporting both a Rune based and Grapheme based parser, and then settle on Grapheme
- Try supporting a bunch of detailed perf improvements and then cut them because of complexity and maintance overhead
- Try various different ways of modelling the TokenSet class to make it safe and usable
- Change Compile to auto-convert grammar literals
- Was great for fine tuning that would have taken forever and probably not gotten fixed. Things 
like better error messages or member renames that touched 250 tests.



### Testing
This might be where I got the biggest wins because I didn't (for the most part) care *how* it tested as long as my review made me confident it worked and tested what it was supposed to. I cared about test organization, naming, and high level architecture, but let the code be whatever it was for the most part. This means the review/iterate bottleneck was much larger and the Claude multiplier here was much larger.

In my opinion, this project has incredible test coverage. But it was also a TON of work to review, even though I treated the code quality with a lighter bar than the core framework.  Honestly, it's probably the level of testing and work every project should have, but tough to find a human that would be willing to write them all. Especially on an open source project. <!-- style-lint-ok: author's own voice -->

- Did claude bug hunting. specific focus on security, or unicode, or edge cases

- Even when random numbers are used to pick files, Claude seems to find the same bug on different worktrees when starting from different places. picking very specific test criteria like "look for threading bugs" helps, saying "find any bug" and giving it different places to look doesnt. it still seems to migrate back to the ame place from different directions


### Supporting Projects
In every framework I've been a part of building, we always do extensive App building, performance tuning and stress testing to focus on usability, finding bugs, flushing out the system. For this project I didn't do stress testing, but found Claude to be *spectacular* on the other two:

- Did claude app building by having it rewrite MIT licensed apps from the Internet using this parser
- Did claude performance tuning, by having it reuse an open source performance harness with existing data and tests designed for parser performance testing

Using claude to build custom rules found some great issues with internal methods, etc. It was able to find custom rules that others had written on other parsers that couldn't be written as the API was designed initially.

It was invaluable to get the usability and performance data from this work, and it was just the kind of detailed, tedious work that would make me want to skip it outside of the most important stuff. With Claude I went much deeper on both than I would have otherwise gone.




## 

- The amount of text and comments I have to review is enormous.
- Incredible test coverage. But a TON of work to review.  Honestly, that's probably the level of testsing and work every project should have but tough to find a human that would be willing to write them all. <!-- style-lint-ok: author's own voice -->
- style: lots of work changing the voice to one I liked. For example, simplifying Unicode jargon. Getting rid of verbal ticks.
- The check I had to do on code review was to explain the code that was written and my comments. If I didn't understand it (or agree with it), I pair programmed it with claude until I did. A few times this took a LOT of time, including a couple of optimizations that I had to cut because I really couldn't get my head around what it was doing.  Maybe thats a failing on my part or maybe claude was wrong.

- Running linting at checkin time was WAY more effective at fixing verbal ticks than telling claude up front.  Lots always bled through.
    - Fixing all of the verbal ticks is really expensive later, you need the lint to be good up front so they don't get introduced
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