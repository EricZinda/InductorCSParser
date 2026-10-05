# Building and Publishing

This is the maintainer's view of the repo: what the build produces, where the version number comes from, how the docs site gets published, and how a release reaches nuget.org and GitHub Releases. If you just want to use the library, the [readme](readme.md) has the one-line install and the primers.

# The pieces

There are two solutions, split on purpose so a plain library build never has to restore benchmark packages.

```
InductorParser.sln                              the library, its test projects, and the E2ESamples
src/Benchmarks/Benchmarks.sln                   the JSON benchmark harness (BenchmarkDotNet, Parlot, Pidgin, ...)
```

The library itself is `src/InductorParser/InductorParser.csproj`. It has no NuGet dependencies and builds two targets:

```
netstandard2.1   what Unity's Mono and IL2CPP backends load. Defines INDUCTORPARSER_USE_BUNDLED_UNICODE,
                 so UnicodeImplementation.Automatic means the library's built-in segmenter and normalizer,
                 because the StringInfo and string.Normalize those runtimes ship are too old to trust.
net8.0           what dotnet test and ordinary .NET consumers use. Automatic means the runtime's own
                 StringInfo and string.Normalize.
```

Both builds emit an XML doc file next to the DLL so IntelliSense shows the `///` docs to anyone referencing the assembly. The project has `TreatWarningsAsErrors` on, so a new warning fails the build rather than piling up. After a netstandard2.1 build, the csproj copies the DLL into the Unity IL2CPP test project under `src/InductorParser.Tests/Unity/Assets/Plugins/`, which is how that test project picks up the latest library without any manual step.

# Building and testing

A release build of the library on its own:

```
dotnet build -c Release src/InductorParser/InductorParser.csproj
```

The output lands in `src/InductorParser/bin/Release/<target>/`. Building the solution at the root builds the tests and the E2ESamples too.

Tests go through `test.sh`, which wraps `dotnet test` with a quiet logger (the Unicode conformance fixtures expand to about 16,000 cases that would otherwise flood the console as skipped) and passes any extra arguments straight through:

```
./test.sh                                       everyday suite
./test.sh --framework net8.0                    one runtime
./test.sh --all                                 everything, including the Unity IL2CPP pass (slow, needs network and Unity)
```

[docs/TestArchitecture.md](docs/TestArchitecture.md) explains what the suites cover and how the Unity pass works.

# Where the version number comes from

The version is `Major.Minor.Build`. Major and Minor are typed by hand in the csproj (`VersionMajor` and `VersionMinor`) and bumped when a release calls for it. Build isn't stored anywhere: an MSBuild target runs `git rev-list --count HEAD` at build time and uses the commit count. That number comes straight from the shared `.git` database, so every worktree computes the same value for the same commit, there's no counter file to conflict on merge, and rebuilding the same commit always gives the same version. Without git (a build from a source tarball, say) the build number falls back to 0.

The three version attributes on the DLL get different values on purpose:

```
AssemblyVersion        Major.Minor.0.0     what .NET binding compares, so it doesn't move every commit
FileVersion            Major.Minor.Build   what Explorer shows in file properties
InformationalVersion   Major.Minor.Build+<short commit hash>, e.g. 1.0.1059+d745f88
```

The NuGet package takes the same `Major.Minor.Build` as FileVersion, so a package and the DLL inside it always agree.

One consequence worth knowing: because Build is the commit count, you can't choose it. The commit that bumps Major or Minor is itself one more commit, so the first build after that change is `Major.Minor.<count+1>`.

# The docs site

The site at https://ericzinda.github.io/InductorCSParser/ is built by DocFX from three sources: the `///` comments in the library (rendered as the API reference), the hand-written markdown in `docs/`, and the readme, which becomes the home page. `build-docs.sh` does the whole thing locally:

```
./build-docs.sh            build into docs/docfx/_site
./build-docs.sh --serve    build, then preview at http://localhost:8080
```

DocFX is a local dotnet tool whose version is locked in `docs/docfx/.config/dotnet-tools.json`, so the script's `dotnet tool restore` gives everyone the same version and nothing is installed globally. The script stages the curated docs into `docs/docfx/docs/`, rewrites any links that point outside the site (into `src/`, `E2ESamples/`, or the unstaged `docs/` subfolders) to GitHub URLs, copies the benchmark chart in as a site resource, and writes a task-based API sidebar from a hand-maintained list in the script. A public type missing from that list is appended under Uncategorized with a warning rather than dropped. [docs/DocumentationArchitecture.md](docs/DocumentationArchitecture.md) has the details.

Publishing is automatic. The `.github/workflows/docs.yml` workflow runs `build-docs.sh` on every push to `master` that touches the library, `docs/`, the readme, or the build script, and deploys the result to GitHub Pages. There's no gh-pages branch, the site deploys straight from the build artifact.

# The NuGet package

`dotnet pack` builds the package from the same csproj:

```
dotnet pack -c Release src/InductorParser/InductorParser.csproj -o out
```

That produces `out/InductorParser.<version>.nupkg` containing both target frameworks with their XML doc files, plus the LICENSE and readme from the repo root so nuget.org can show them on the package page. The package id, description, tags, license, and project and repository URLs all live in the csproj. nuget.org can't resolve relative links in a package readme, which is why every link in `readme.md` is an absolute URL: docs pages point at the published site, the benchmark chart link at the site's copy, and everything else at GitHub. nuget.org also only renders images hosted on an allowlist of domains, so the chart image itself and the version badge come from `raw.githubusercontent.com` and `img.shields.io`, which are on the list, rather than from the docs site, which isn't.

# Cutting a release

A release is a tag. Pushing a tag of the form `v<Major>.<Minor>.<Build>` runs `.github/workflows/release.yml`, which packs the library, pushes the package to nuget.org, and creates a GitHub Release with two assets: the `.nupkg`, and `InductorParser-<version>-netstandard2.1.zip` holding the DLL, its XML doc file, and the LICENSE for Unity users, who can't pull from NuGet directly.

Because the build number is the commit count, the tag has to name the version the tagged commit actually builds as. From an up-to-date `master` checkout:

```
git tag v1.0.$(git rev-list --count HEAD)
git push origin --tags
```

The workflow checks that the tag matches the version it computed and fails with the expected tag name if they differ, so the tag, the Release, and the package version can't drift apart. If you want a new Major or Minor, bump them in the csproj, commit, push to master, and then tag.

Publishing uses nuget.org's Trusted Publishing rather than a stored API key. The job asks GitHub for a short-lived OIDC token, the `NuGet/login` action trades it to nuget.org for a temporary API key that lives one hour and works once, and the push uses that. nuget.org only accepts the trade when a policy on the account names this exact repo and workflow file. The one-time setup is a Trusted Publishing policy on nuget.org (owner `EricZinda`, repository `InductorCSParser`, workflow file `release.yml`, scoped to the `InductorParser` package id, allowed to push new packages) and a `NUGET_USER` repo secret holding the nuget.org profile name. The GitHub Release uses the workflow's own token and needs nothing.

Before the first real tag, run the workflow by hand from the Actions tab (or `gh workflow run release.yml`). That's a dry run: it packs, zips, and performs the token exchange, which proves the policy accepts this repo, then uploads the package and zip as a workflow artifact to inspect. The push and the Release are skipped, so no version number is used up. That matters because nuget.org never deletes a published version, it can only be unlisted.

# Benchmarks

The JSON benchmark harness in `src/Benchmarks` is a separate solution. Running it regenerates the performance chart that the readme and docs site show:

```
dotnet run -c Release --project src/Benchmarks/Benchmarks.csproj -- --spot-check
dotnet run -c Release --project src/Benchmarks/Benchmarks.csproj -- --filter *Json* --exporters GitHub
```

Run the spot-check first. It round-trips every parser's output back to the exact input bytes, so a parser that silently stopped early can't post a fast time. The benchmark run then rewrites `src/Benchmarks/performance-chart.jpg` and `performance-chart.html`, both of which are tracked, with the run date in the chart title. The full table in [src/Benchmarks/README.md](src/Benchmarks/README.md) is a hand-maintained regression baseline and doesn't update on its own.
