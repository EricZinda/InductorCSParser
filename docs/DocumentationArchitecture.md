# Documentation Architecture

This doc explains how the documentation site is built: where the content comes from, what turns it into a website, and how it gets published. If you want to change how the docs look or what they include, this is the map.

There are three sources of truth, and the site is just those three things stitched together:

1. Your C# code and its `///` comments, in `src/InductorParser`.
2. The hand-written markdown in `docs/` (the primers, the reference, the architecture docs).
3. The `readme.md`.

The engine that stitches them is [DocFX](https://dotnet.github.io/docfx/), a .NET documentation generator. We pin it as a local tool in `.config/dotnet-tools.json`, so anyone who runs `dotnet tool restore` gets the exact same version. Nothing is installed globally.

Two scripts do everything. `build-docs.sh` makes the site, `publish-docs.sh` puts it online.

## How the site is built

`build-docs.sh` runs these steps in order.

First it gathers the conceptual content. It copies the curated `docs/` markdown into a working folder (`docfx/docs`) and turns `readme.md` into the site's home page (`docfx/index.md`), fixing up links along the way so they point at the right place once rendered. The list of which `docs/` files are curated lives in the script, so the bug logs and other internal folders stay out of the public site.

Then it builds the API reference from your code. `docfx metadata` runs Roslyn over `InductorParser.csproj`, reads every public type and its `///` comments, and writes one YAML file per type into `docfx/api`. This is the "docs from the code" half.

Then there's a small post-processing step that's our own. It takes that generated API data and writes two files: `api/index.md`, the single categorized API page (Building a Grammar, Starting a Parse, and so on), and `api/toc.yml`, the matching grouped sidebar. Both come from one hand-maintained list of categories in the script, and there's a check that every public type lands in some category, so a newly added type can't silently disappear. If one isn't categorized, the build warns and drops it under an "Uncategorized" group rather than leaving it off.

Finally `docfx build` takes all of it (the conceptual markdown, the readme home page, and the API YAML) and renders one static HTML site into `docfx/_site`. The top navigation (Home / Guides / Reference / Architecture / API) comes from `docfx/toc.yml`, and the whole thing is wired together by `docfx/docfx.json`, which says where the source is, which docs to include, which templates to apply, and settings like turning off the breadcrumb.

So the flow is: code + docs + readme, through DocFX (metadata then build) with our overrides, out to `docfx/_site`.

## The look and feel

The styling and the API-page tweaks are a thin customization layer in `docfx/templates/override`, applied on top of DocFX's built-in "modern" template. DocFX merges template folders by filename, and ours is listed last in `docfx.json`, so our files win:

- `public/main.css` is the skin: fonts, link color, dark code blocks, table styling.
- `partials/namespace.tmpl.partial` drops the Classes/Structs grouping on namespace pages.
- `partials/class.header.tmpl.partial` fixes the inheritance header so a type's own name only shows when it has a real ancestor chain.
- `ManagedReference.extension.js` is a model-transform hook that runs before rendering. It's what strips the empty Parameters / Returns / Property Value sections (the ones that only repeat the signature) and the trivial `object` / `ValueType` / `Enum` inheritance.

## Publishing

`publish-docs.sh` is separate and simple. It takes the already-built `docfx/_site` and pushes it to a `gh-pages` branch using a throwaway git worktree. It drops in a `.nojekyll` file, so GitHub doesn't run Jekyll over DocFX's underscore-prefixed asset folders, and a `.gitattributes`, so line endings don't churn on every publish. GitHub Pages serves that branch at the public URL. The rendered HTML never lands on `master`.

One time only, the repo's Settings, Pages source has to be set to deploy from the `gh-pages` branch root. After that, every `publish-docs.sh` just updates it.

## What's tracked vs generated

Almost everything under `docfx/` is generated and gitignored: `_site`, `api`, the staged `docs` copy, and `index.md`. The only hand-authored, tracked files are `docfx.json`, `toc.yml`, and the `templates/override` folder, plus the two scripts and the tool manifest. So if the generated output ever looks wrong, delete it and re-run `build-docs.sh` to rebuild from scratch.
