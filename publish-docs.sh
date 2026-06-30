#!/usr/bin/env bash
# publish-docs.sh: publish the built DocFX site to the gh-pages branch.
#
# GitHub Pages serves the site from the root of the gh-pages branch. This
# script syncs docfx/_site into a throwaway worktree checked out to
# gh-pages, commits, and pushes. The generated HTML never lands on master.
#
# Run ./build-docs.sh first, then ./publish-docs.sh.
#
# One-time setup: in the repo's Settings -> Pages, set the source to
# "Deploy from a branch" -> gh-pages -> / (root). The site then lives at
# https://ericzinda.github.io/InductorCSParser/.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

site_dir="docfx/_site"
branch="gh-pages"

if [ ! -d "$site_dir" ]; then
    echo "No $site_dir found. Run ./build-docs.sh first." >&2
    exit 1
fi

worktree_dir="$(mktemp -d)"

# Always clean up the worktree, even on failure.
cleanup() {
    git worktree remove --force "$worktree_dir" >/dev/null 2>&1 || true
    rm -rf "$worktree_dir" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "=== Checking out $branch into a temporary worktree ==="
if git ls-remote --exit-code --heads origin "$branch" >/dev/null 2>&1; then
    git fetch origin "$branch"
    # Reset the worktree to the remote tip so we publish on top of it.
    git worktree add -B "$branch" "$worktree_dir" "origin/$branch"
else
    # First publish: create gh-pages as an orphan branch with no history.
    echo "Branch $branch does not exist on origin; creating it."
    git worktree add --detach "$worktree_dir"
    git -C "$worktree_dir" checkout --orphan "$branch"
    git -C "$worktree_dir" reset --hard
fi

echo "=== Syncing $site_dir into the worktree ==="
# Drop everything except the .git pointer, then copy the freshly built site.
find "$worktree_dir" -mindepth 1 -maxdepth 1 ! -name '.git' -exec rm -rf {} +
cp -r "$site_dir/." "$worktree_dir/"
# .nojekyll stops GitHub Pages' Jekyll pass from hiding DocFX's _-prefixed
# asset folders, which would otherwise render the site unstyled.
touch "$worktree_dir/.nojekyll"
# The generated site ships with LF line endings. Mark everything as binary
# so Git never converts line endings, regardless of the user's autocrlf
# setting. Without this, each publish re-touches every HTML/JS/CSS file and
# produces a huge no-op diff.
printf '* -text\n' > "$worktree_dir/.gitattributes"

echo "=== Committing and pushing ==="
git -C "$worktree_dir" add -A
if git -C "$worktree_dir" diff --cached --quiet; then
    echo "No documentation changes to publish."
else
    git -C "$worktree_dir" commit -m "Publish docs site"
    git -C "$worktree_dir" push origin "$branch"
    echo "Published. Live at https://ericzinda.github.io/InductorCSParser/"
fi
