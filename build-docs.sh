#!/usr/bin/env bash
# build-docs.sh: build the browsable DocFX documentation site locally.
#
# It generates API reference from the InductorParser XML doc comments,
# folds in the curated docs/ markdown and the readme (as the home page),
# and writes a static site to docfx/_site.
#
# Usage:
#   ./build-docs.sh            # build once, output in docfx/_site
#   ./build-docs.sh --serve    # build, then serve at http://localhost:8080
#
# After a successful build, run ./publish-docs.sh to push docfx/_site to
# the gh-pages branch that GitHub Pages serves.

set -euo pipefail

# Run from the repo root regardless of where the script is invoked.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

# WSL has no native `dotnet` but can call the Windows SDK via interop.
# Fall back to dotnet.exe when dotnet isn't on PATH. On Linux and Git Bash
# on Windows, `dotnet` is present and this stays "dotnet". Same pattern as
# test.sh.
dotnet_command=dotnet
if ! command -v dotnet >/dev/null 2>&1; then
    dotnet_command=dotnet.exe
fi

# Absolute GitHub locations used to rewrite links that point outside the
# docs set so they stay clickable on the published site.
blob_base="https://github.com/EricZinda/InductorCSParser/blob/master"
raw_base="https://raw.githubusercontent.com/EricZinda/InductorCSParser/master"

# The curated docs that belong on the public site. Everything else in docs/
# (BugSearchLog/, PotentialBugSources/, Archive/) is internal and stays out.
# The capitalized Primer3/Primer4 match the real filenames.
curated_docs=(
    primer1.md
    primer2.md
    Primer3.md
    Primer4.md
    tutorial-peek.md
    InductorParserReference.md
    Recipes.md
    InductorParserDesignDecisions.md
    CodeArchitecture.md
    ErrorArchitecture.md
    TestArchitecture.md
    Terminology.md
    UnicodeModel.md
    UnicodeGotchas.md
    UnicodeInternalsArchitecture.md
    MappingPositionsAfterNormalization.md
)

echo "=== Restoring DocFX (local tool) ==="
"$dotnet_command" tool restore

echo "=== Staging curated docs into docfx/docs ==="
# Copy the curated docs flat into docfx/docs so their mutual links (all flat,
# e.g. [ref](InductorParserReference.md)) keep resolving and DocFX rewrites
# them to .html. Rewrite parent-relative links (../E2ESamples, ../src, ...)
# to absolute GitHub blob URLs, since those targets aren't part of the site.
rm -rf docfx/docs
mkdir -p docfx/docs
for doc in "${curated_docs[@]}"; do
    sed "s#](\.\./#](${blob_base}/#g" "docs/${doc}" > "docfx/docs/${doc}"
done

echo "=== Generating home page from readme.md ==="
# Use the readme as the site home so it stays the single source of truth.
# Keep its docs/ links relative (they resolve to docfx/docs above). Point
# the benchmark chart image at the raw URL so it renders inline, and the
# other src/ links at GitHub blob URLs so they stay clickable.
sed \
    -e "s#](src/Benchmarks/performance-chart.jpg)#](${raw_base}/src/Benchmarks/performance-chart.jpg)#g" \
    -e "s#](src/#](${blob_base}/src/#g" \
    readme.md > docfx/index.md

echo "=== Cleaning previous output ==="
# DocFX doesn't delete stale files from a prior run, so wipe the generated API
# metadata and the rendered site before rebuilding. Without this, a doc or type
# removed from the inputs would linger in _site and get published.
rm -rf docfx/api docfx/_site

echo "=== Generating API metadata ==="
"$dotnet_command" docfx metadata docfx/docfx.json

echo "=== Building the categorized API index ==="
# The metadata step writes docfx/api/toc.yml grouped by namespace, with every
# type entry indented two spaces ("  - uid:") and namespace entries at column 0.
# From that we build one combined API page (no per-namespace pages):
#   - api/index.md: every public type grouped into task-based sections
#   - api/toc.yml:  a flat sidebar of those same types (no namespace grouping)
api_dir="docfx/api"
all_type_uids=$(grep -E '^  - uid: ' "$api_dir/toc.yml" | sed -E 's/^  - uid: //')

# Editorial grouping for the API index. Lines starting with "# " are section
# headings; every other non-blank line is a type uid placed under the current
# heading, in this order. This is the one hand-maintained list in the docs, so
# the completeness check below appends (and warns about) any public type that's
# missing rather than letting a newly added type silently drop off the index.
api_index_layout='# Building a Grammar
InductorParser.Rules
InductorParser.TokenSet
InductorParser.TokenSet.Ascii
InductorParser.LateBoundRule
InductorParser.SyntaxTree.FlattenType
# Starting a Parse
InductorParser.ParseOptions
InductorParser.ParseCancellation
InductorParser.Tracing.TraceLevel
# Parse Results
InductorParser.ParseResult
InductorParser.ParseOutcome
InductorParser.SyntaxTree.SourcePosition
InductorParser.SyntaxTree.SourceRange
InductorParser.SyntaxTree.Symbol
InductorParser.SyntaxTree.SymbolExtensions
InductorParser.SyntaxTree.SymbolId
InductorParser.SyntaxTree.ParseContext
# Writing a Rule
InductorParser.Rule
InductorParser.Lexing.Lexer
InductorParser.Lexing.Token
InductorParser.Lexing.Lexer.Probe
InductorParser.Lexing.Lexer.Transaction
InductorParser.INormalizationReporter
InductorParser.Lexing.GraphemeHelpers
InductorParser.Lexing.RuneHelpers
InductorParser.Invariant
InductorParser.InductorParserBugException
# Infrastructure
InductorParser.SyntaxTree.SymbolRanges
InductorParser.InvariantInterpolatedStringHandler
InductorParser.Tracing.TraceInterpolatedStringHandler'

# Render the grouped index page.
{
    printf '# API Reference\n'
    while IFS= read -r line; do
        case "$line" in
            '# '*) printf '\n## %s\n\n' "${line#\# }" ;;
            '')    ;;
            *)     printf -- '- <xref:%s>\n' "$line" ;;
        esac
    done <<< "$api_index_layout"
} > "$api_dir/index.md"

# Completeness check: warn (don't silently drop) if a public type is missing
# from the layout, or if the layout names a type that no longer exists.
layout_uids=$(printf '%s\n' "$api_index_layout" | grep -Ev '^(#|$)')
missing=$(comm -23 <(printf '%s\n' "$all_type_uids" | sort -u) <(printf '%s\n' "$layout_uids" | sort -u))
stale=$(comm -13 <(printf '%s\n' "$all_type_uids" | sort -u) <(printf '%s\n' "$layout_uids" | sort -u))
if [ -n "$missing" ]; then
    echo "WARNING: public types missing from the API index layout (appended under Uncategorized):" >&2
    printf '  %s\n' $missing >&2
    {
        printf '\n## Uncategorized\n\n'
        for uid in $missing; do printf -- '- <xref:%s>\n' "$uid"; done
    } >> "$api_dir/index.md"
fi
if [ -n "$stale" ]; then
    echo "WARNING: API index layout names types that no longer exist:" >&2
    printf '  %s\n' $stale >&2
fi

# Sidebar grouped into the same task-based sections as the index page. Each
# "# Heading" becomes a collapsible group; each uid becomes a type link under it.
{
    printf '### YamlMime:TableOfContent\n'
    printf 'items:\n'
    while IFS= read -r line; do
        case "$line" in
            '# '*) printf -- '- name: %s\n  items:\n' "${line#\# }" ;;
            '')    ;;
            *)     printf -- '  - uid: %s\n' "$line" ;;
        esac
    done <<< "$api_index_layout"
    # Keep the sidebar complete: any type missing from the layout (already
    # warned about above) goes under its own group rather than vanishing.
    if [ -n "$missing" ]; then
        printf -- '- name: Uncategorized\n  items:\n'
        for uid in $missing; do printf -- '  - uid: %s\n' "$uid"; done
    fi
} > "$api_dir/toc.yml"

echo "=== Building the site ==="
# DocFX prints "Invalid file link" warnings for any broken cross-reference.
# Scan the output for those after a build to keep the site link-clean.
"$dotnet_command" docfx build docfx/docfx.json

if [ "${1:-}" = "--serve" ]; then
    echo "=== Serving at http://localhost:8080 (Ctrl+C to stop) ==="
    "$dotnet_command" docfx serve docfx/_site
fi
