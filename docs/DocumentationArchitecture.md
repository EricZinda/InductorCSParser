# Documentation Architecture

This doc explains how the documentation site is built: where the content comes from, what turns it into a website, and how it gets published. If you want to change how the docs look or what they include, this is the map.

There are three sources of truth, and the site is just those three things stitched together:

1. Your C# code and its `///` comments, in `src/InductorParser`.
2. The hand-written markdown in `docs/` (the primers, the reference, the architecture docs).
3. The `readme.md`.

The engine that stitches them is [DocFX](https://dotnet.github.io/docfx/), a .NET documentation generator. It's a local tool with its version locked in `docs/docfx/.config/dotnet-tools.json`, so anyone who runs `dotnet tool restore` from `docs/docfx` gets the exact same version. Nothing is installed globally, and nothing docs-related sits at the repo root except `build-docs.sh`.

`build-docs.sh` makes the site. A GitHub Actions workflow deploys it whenever changes land on `master`.

## How the site is built

`build-docs.sh` runs these steps in order.

First it gathers the conceptual content. It copies the curated `docs/` markdown into a working folder (`docs/docfx/docs`) and turns `readme.md` into the site's home page (`docs/docfx/index.md`), fixing up links along the way so they point at the right place once rendered. The list of which `docs/` files are curated lives in the script, so the bug logs and other internal folders stay out of the public site.

Then it builds the API reference from your code. `docfx metadata` runs Roslyn over `InductorParser.csproj`, reads every public type and its `///` comments, and writes one YAML file per type into `docs/docfx/api`. This is the "docs from the code" half.

Then there's a small post-processing step that's our own. It rewrites the generated `api/toc.yml`, the sidebar shown next to every API page, so it groups the public types into task-based sections (Building a Grammar, Starting a Parse, and so on) instead of by namespace. The groups come from one hand-maintained list in the script, and there's a check that every public type lands in some group, so a newly added type can't silently disappear. If one isn't categorized, the build warns and drops it under an "Uncategorized" group rather than leaving it off. There's no separate API index page. The API entry in the top navigation references the `api/` folder (which is what makes DocFX show that sidebar) and lands on the `Rules` class, where a grammar starts.

Finally `docfx build` takes all of it (the conceptual markdown, the readme home page, and the API YAML) and renders one static HTML site into `docs/docfx/_site`. The top navigation (Home / Guides / Reference / Architecture / API) comes from `docs/docfx/toc.yml`, and the whole thing is wired together by `docs/docfx/docfx.json`, which says where the source is, which docs to include, which templates to apply, and settings like turning off the breadcrumb.

So the flow is: code + docs + readme, through DocFX (metadata then build) with our overrides, out to `docs/docfx/_site`.

## The look and feel

The styling and the API-page tweaks are a thin customization layer in `docs/docfx/templates/override`, applied on top of DocFX's built-in "modern" template. DocFX merges template folders by filename, and ours is listed last in `docfx.json`, so our files win:

- `public/main.css` is the skin: fonts, link color, dark code blocks, table styling.
- `partials/namespace.tmpl.partial` drops the Classes/Structs grouping on namespace pages.
- `partials/class.header.tmpl.partial` fixes the inheritance header so a type's own name only shows when it has a real ancestor chain.
- `ManagedReference.extension.js` is a model-transform hook that runs before rendering. It's what strips the empty Parameters / Returns / Property Value sections (the ones that only repeat the signature) and the trivial `object` / `ValueType` / `Enum` inheritance.

## Publishing

Deployment is automatic. The GitHub Actions workflow in `.github/workflows/docs.yml` runs on every push to `master` that touches the docs or the library. It builds the site with `build-docs.sh` and hands `docs/docfx/_site` straight to GitHub's Pages deploy, so the rendered HTML never lands on a branch (there's no `gh-pages` branch). You can also run it by hand from the Actions tab.

One-time setup, already done: the repo's Settings, Pages source is set to "GitHub Actions". To check the site before pushing, run `build-docs.sh --serve` locally.

## What's tracked vs generated

Almost everything under `docs/docfx/` is generated and gitignored: `_site`, `api`, the staged `docs` copy, and `index.md`. The only hand-authored, tracked files are `docfx.json`, `toc.yml`, and the `templates/override` folder, plus `build-docs.sh`, the deploy workflow, and the tool manifest. So if the generated output ever looks wrong, delete it and re-run `build-docs.sh` to rebuild from scratch.
