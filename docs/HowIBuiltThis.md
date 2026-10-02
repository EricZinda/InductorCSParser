# How I built this
I needed a PEG parser for a C# project I'm building so I wanted to port the C++ parser I had already open sourced to .NET. Since it was April 2026, I thought this would be a good experiment to understand the capabilities of LLMs as they stood then.

I was part of the leadership team that delivered the .NET Framework 1.0, alongside [Scott Gellock](https://www.linkedin.com/in/sgellock/) and [Destry Hood](https://www.linkedin.com/in/destry-hood-336790112/) and I managed teams that delivered several other framework components afterwards. I have a lot of experience designing, building and shipping this type of product. So, I was excited to go down that same road using an LLM to get a real understanding of their strengths and weaknesses in a domain I understood deeply.

## What I did
At the point where I consider it V1 done, I had worked for ~62 mostly 8ish hour days to produce this. Let's call that 3 months of full-time work. Without Claude, I would have delivered something in that amount of time, I am sure. However, what Claude enabled was a much bigger scope of project than I would have tackled myself in that same time. For example:

- I would not have attempted to make a "Unicode-first" PEG parser ([Unicode Internals Architecture](UnicodeInternalsArchitecture.md) and [Unicode Gotchas](UnicodeGotchas.md) show what that meant). That's just too much spec to read and understand. Claude was a good teacher (assuming I asked for citations) and that enabled me to tackle Unicode which I think is hugely valuable. <!-- style-lint-ok: author's own voice -->
- I would not have built a performance harness (the [Rebar harness](../ExperimentalSrc/BenchMarks/Rebar/README.md) and the [JSON benchmarks](../ExperimentalSrc/BenchMarks/README.md)). Yes, I would have had a few simple benchmarks for smoke testing performance, but no comparisons to other frameworks, no detailed testing across Regex expressions, etc. <!-- style-lint-ok: author's own voice -->
- I would not have done *nearly* the amount of app-building using the framework. Claude could take an open source project that used a parser and rewrite it using mine and show me the results and the pros and cons. Much of this code I threw away and just kept the learnings, but a few I kept in the project (the [E2ESamples](../E2ESamples/) folder, for example [Newsboat](../E2ESamples/Newsboat/), [TOML](../E2ESamples/Toml/), and [PEP 508](../E2ESamples/Pep508/README.md)). <!-- style-lint-ok: author's own voice -->
- I would not have done *nearly* the amount of automated and ad hoc testing. Claude is an absolute monster for finding bugs and writing tests (the [BugSearchLog](BugSearchLog/) has every bug hunt). I simply would not have had the patience for it. <!-- style-lint-ok: author's own voice -->
- I was able to do sweeping changes that I would have just lived with because Claude made the cost much cheaper ([move to the latest Unicode](UnicodeGotchas.md#token-segmentation-across-runtimes), [change the fundamental unit the lexer tokenizes](Archive/ExpandRuneSet.md), rename small things that had a lot of dumb costs to change)
- I was able to do throwaway tests and proof of concepts for a myriad of things I just wouldn't have had time to investigate. Is X any faster than Y? What do other parsers do for this problem? What if we made this part private? etc.

### Technology
I used VS Code and Claude for most of the project, interacting with Claude in the Anthropic Add-in they ship for it. I found OpenAI's Codex to not be as reliable (so I didn't use it outside of testing it occasionally) until about Sept 2026 which was probably GPT 5.5. From that point on, I used Claude and Codex interchangeably, finding Codex to be much faster but running out of tokens quicker.

I used Git and 6 Git worktrees for day to day development, often having all 6 with a different Claude instance implementing something different simultaneously.

I used the $100 a month Claude subscription and it worked well for me doing 40 hour weeks in this mode, except for a short patch in the middle of the coding phase where I upped to the $200 subscription because I was running out of tokens.

### Workflow Across Project Phases
Let's start with what I didn't do: vibe coding. This was not a "tell Claude what I want and ignore the code" project. I *very much* cared and deeply reviewed what the framework looked like, its usability, performance and implementation details. <!-- style-lint-ok: author's own voice -->

I acted as (from a Microsoft job title perspective) the Program Manager, Dev Lead and Test Lead for the project. Claude wrote 99% of the code and comments, and probably 75% of the documentation (rough estimates). I code/doc reviewed *all* of it, and we often iterated. I treated the project as my own code and design. I cared about every line.

*Design Phase*: At the start of the project I worked with Claude like I'd work with the developer of an area I was responsible for Program Managing. I laid out requirements, design goals, API sketches ("be like my C++ PEG Parsing framework") and then read through Claude's proposed plan and design. We iterated a ton until we had design docs that I agreed with ([Code Architecture](CodeArchitecture.md), [Error Architecture](ErrorArchitecture.md), [Test Architecture](TestArchitecture.md), and [Unicode Internals Architecture](UnicodeInternalsArchitecture.md) are the ones that survived to ship).

*Implementation Phase*: I mostly had my dev lead hat on here and assigned Claude coding and test automation tasks and exhaustively reviewed what it wrote. Again, much iteration on every commit here.

*Code Complete/Ad Hoc Testing Phase*: I had Claude write [extensive tests](../src/InductorParser.Tests/) for each feature it wrote, but after coding was done, I had it do ad hoc testing too. Here, I'd have Claude [find bugs](BugSearchLog/) and fix them, trying as many different ways as I could to have it poke and prod the code from different directions: security, performance, usability, etc. Again, I would review the bugs, and code review the fixes. I'd make sure the test suites filled in coverage for areas where bugs exposed test gaps.

*Documentation*: This happened all along the way. Any of the documents that use the first person "I" were written by me, usually with Claude suggesting edits (the [readme](../readme.md) and this document, for example). All the others were in reverse: Claude wrote what I asked, and I reviewed and gave edits (the [API reference](InductorParserReference.md) and [Error Reporting Architecture](ErrorArchitecture.md), for example).

I treated code and documentation as "I have to sign off on this as if I wrote it", and reviewed it at that level of detail. I had to agree with design decisions, agree with and be able to explain and justify every line of code and documentation, etc. For tests, I still reviewed the code but cared more about "does this test what it is supposed to test" than code quality per se.

I let Claude write all the commit messages. These I didn't review.

## Post Mortem
What I'll do the same next time:

- Building detailed design documents by iterating with Claude *before* any code was written reduced surprises and flushed out architectural issues early
- Coding big changes/additions in phases made the review process more tenable and enabled catching more directional issues early
- Code reviewing every line of code across the parser and tests found issues early before they got baked in as standard practice or created a shaky foundation that would just get worse
- Verifying claims made by Claude by asking for citations, including links and the direct quote that licensed the claim often forced a backoff or clarification of the original claim
- For polish or redesigns that I might not have otherwise done: Doing sweeping changes (in phases) or small changes that affect lots of areas was trivial and done well by Claude and made getting things "just right" a breeze.
- Using Claude to do supporting projects that I wouldn't have done otherwise helped deliver much higher quality: performance test harness and suites, "app building" using the library
- Having Claude do ad hoc testing and verification (in addition to the automated tests) found lots of issues: ensuring that all docs are accurate vis-a-vis the code or external claims, ensuring tests actually test what they say they test, reviewing the finished code across all the standard axes: performance, globalization, localization, functional correctness
- Giving Claude a high level [test architecture](TestArchitecture.md) (file organization, test harness, etc) and then having it generate coverage as we went covered a lot of ground quickly and caught regressions
- Let Claude pick places to perf optimize, and use a perf harness (the [JSON benchmarks](../ExperimentalSrc/BenchMarks/README.md)) to check its work. Claude was very good at estimating what would be slow and doing a fix and showing it improved the system. Not perfect, but very good. Letting it guide the perf optimizations worked well, and having the perf harness to validate the wins was key.


What I'll do from the start next time:

- Linting the code and docs against the large list of style rules came on late and was way more effective than just telling Claude how to write (which it'd follow kinda)
- Using git worktrees to allow 6 Claudes to work simultaneously worked well because Claude is *so* good at resolving merges (downside was git history is littered with merges). This didn't start until well into the project
- Clamping down hard, early, on comments, or asking for no code comments (and writing them myself) might have been easier than reviewing the volume that Claude created. Ditto for docs: having the high level guidance be about being brief and holding to that would have created less text to comb through and reduce later. Much of my feedback to Claude on docs it wrote was "simplify and less jargon"
- Don't allow something to be started unless I fully understand and buy off on it. I allowed Claude to implement a couple of speculative performance optimizations that were fragile and complicated, thinking "I'll spend time really understanding these later, just make sure the code works". Maybe they were correct, but I never understood them well enough. I ended up cutting them, but I worked around their impacts in the code way too long. Claude is good at enabling feature creep.
- Claude will create infinite tests and documentation. You have to think hard about what you want covered if you plan to actually review it all (as I did)


### Docs Details
I spent a lot of time trying to get Claude to write in the best way I know, i.e. *my* style. I had it analyze my writing from my blog and projects and condense it into a style guide for itself and use that as its "voice". I also doggedly added items to claude.md to remove annoying style ticks it still retained. I found that this helped a little, but best was to run a "lint" pass before every commit that checked all of these rules. It almost always found violations.

The whole thing is in [SystemClaude.md](Archive/SystemClaude.md), a snapshot of the global claude.md as it stood at the end of the project. Here are a few examples to illustrate what is in it:

> Vocabulary: Never use "earn" / "earns" / "earned" / "earning" figuratively in prose or code comments to say a feature or case justifies its existence ("the case where forced earns its place", "the other two operators earn their spot", "earns its keep"). State the justification directly instead ("the case where forced is the right tool", "the other two operators are worth having because hand-enumerating the result goes stale"). The literal sense (earning money) stays. <!-- style-lint-ok: quotes the rule itself -->
>
> Sentence structure: Never open a sentence with a verbless topic label, a noun phrase dropped in front of the content like an inline section heading ("Start side, two parts: the head rune must be...", "Body side: every converted piece must land in...", "Fast path: the loop skips the check."). This is telegraphic style, it names the topic without saying anything about it, and Claude writes this shape often. Break the habit. Give the label a verb and make it a real sentence ("The start-side check has two parts: ...", "On the body side, every converted piece must land in...", "The fast path skips the check."). The comma-spliced count ("Start side, two parts:") gets the same treatment, fold the count into the sentence.

Even with claude.md and linting, I still found lots of jargon and writing I didn't like. I iterated a *ton* on the documentation. I still think I ended up with way more extensive documentation than I would have had without Claude, but it was by no means "free".

Claude is extremely prolific with comments and documentation. I ended up drastically cutting documentation and comments back as I'd review the docs in the final passes. I should have controlled this better up front: every sentence that gets written has to be reviewed. Honestly, for code comments, I wonder if I should tell Claude to not write any until asked as a way to control the volume of them. <!-- style-lint-ok: author's own voice -->


### Design Details
As a person working solo on a project, Claude is a good sounding board. I'd often have discussions about different approaches to designing the API or architecture. Claude would be great at pushing back on them with factual problems or suggesting alternatives. It was *key* to require it to back up any facts or opinions it had with real quotes from internet sources and their links. This would often (10-20% of the time?) make it back down or change opinion in some way.

Once I had the architecture of the app pinned down, Claude was great at suggesting features that I wouldn't have otherwise done, mostly because I wouldn't have thought of them or didn't want to spend the time implementing them. A couple of examples: <!-- style-lint-ok: author's own voice -->

- A broader-than-timeout [budget system](InductorParserReference.md#catastrophic-backtracking-and-timeouts) was a great idea.
- The [algorithm for mapping normalized positions to original text](MappingPositionsAfterNormalization.md) was Claude's, and is a novel idea as far as I can tell. However, it took me iterating with Claude for 2 weeks (!) before I believed that it was provably valid. That document is the result.

For Claude's more complicated or new-to-me ideas, I found the best way to build trust and prove that algorithms are correct, especially for areas like Unicode that have huge specifications, is to ask Claude to:
1) give citations that have links and verbatim quotes to support facts it states (e.g. "Unicode allows x when y"). I usually asked it to put the citations in the comments or docs it wrote.
2) prove the logic chain, step by step, and challenge it all along the way

Claude was *very good* at estimating what would be slow and designing code right the first time or doing a fix and showing it improved the system. Not perfect, but very good.



### Code Details
The check I did on code review was to explain and justify/poke holes in the code and comments that Claude wrote. If I didn't understand it or agree with it, I pair programmed it with Claude until I did. A few times this took a LOT of time, including a couple of optimizations that I had to cut because I really couldn't get my head around what it was doing. Maybe that's a failing on my part or maybe Claude was wrong.

Claude let me compress the exploration and multiple rewrites of the project into a much shorter time. Every new framework or product I've ever worked on needed a few rewrites of the core architecture as we learned more about the nature of the problem and this was no different. Claude made it *really* fast to completely revector an area when it was time. Some examples:

- Try supporting both a Rune based and Grapheme based parser, and then settle on Grapheme ([the design rationale](Archive/ExpandRuneSet.md))
- Try supporting a bunch of detailed perf improvements and then cut them because of complexity and maintenance overhead
- Try various different ways of modelling the [TokenSet](../src/InductorParser/TokenSet.cs) class to make it safe and usable
- Change Compile to [auto-convert grammar literals](UnicodeInternalsArchitecture.md#normalization)
- Was great for fine tuning that would have taken forever and probably not gotten fixed. Things like better error messages or member renames that touched 250 tests.



### Testing Details
This might be where I got the biggest wins because I didn't (for the most part) care *how* it tested as long as my review made me confident it worked and tested what it was supposed to. I cared about test organization, naming, and high level architecture, but let the code be whatever it was for the most part. This means the review/iterate bottleneck was much larger and the Claude multiplier here was much larger.

In my opinion, this project has incredible test coverage. But it was also a TON of work to review, even though I treated the code quality with a lighter bar than the core framework.  Honestly, it's probably the level of testing and work every project should have, but tough to find a human that would be willing to write them all. Especially on an open source project. <!-- style-lint-ok: author's own voice -->

I kept iterating on ad-hoc testing until the current models were finding really "won't fix" bugs consistently. I'd have each of the 6 instances focus on something different. Sometimes a specific file, sometimes a particular area. You can look in the [BugSearchLog](BugSearchLog/) for Claude's summary of every single one. It was really good at it and almost always found good bugs, some of which prompted rewrites of key areas.

Learning: Even when random numbers are used to pick files to start from, Claude seems to find the same bug on different worktrees when starting from different places in the same codebase. Picking very specific test criteria like "look for threading bugs" helps, saying "find any bug" and giving it different places to look doesn't. It still seems to migrate back to the same place from different directions.

I had Claude exhaustively check docs for broken links and accuracy, add citations to any claims, etc. It was very good at this and allowed for much more detailed linking than I would have done since it is such a manual process.

### Supporting Projects Details
In every framework I've been a part of building, we always do extensive app building, performance tuning and stress testing to focus on usability, finding bugs, flushing out the system. For this project I didn't do stress testing, but found Claude to be *spectacular* on the other two:

- Did Claude app building by having it rewrite MIT licensed apps from the Internet using this parser (the ones I kept are in [E2ESamples](../E2ESamples/))
- Did Claude performance tuning, by having it reuse an open source performance harness with existing data and tests designed for parser performance testing (the [JSON benchmarks](../ExperimentalSrc/BenchMarks/README.md))

Using Claude to build [custom rules](InductorParserReference.md#user-defined-rules) (the [LookbehindRule](../src/InductorParser.ExternalContractTests/LookbehindRule.cs) ported from LPeg is one that stayed) found some great issues with internal methods, etc. It was able to find custom rules that others had written on other parsers that couldn't be written as the API was designed initially.

It was invaluable to get the usability and performance data from this work, and it was just the kind of detailed, tedious work that would make me want to skip it outside of the most important stuff. With Claude I went much deeper on both than I would have otherwise gone.
