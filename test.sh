#!/usr/bin/env bash
# test.sh: run the test suite.
#
# Usage:
#   ./test.sh                                              # everyday suite
#   ./test.sh --all                                        # everything (slow, needs network + Unity)
#   ./test.sh --filter "FullyQualifiedName~Atom_fragment"  # one fixture
#
# All arguments pass through to dotnet test.

set -euo pipefail

# Run from the repo root regardless of where the script is invoked. Same
# pattern as build-docs.sh.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

# --all: absolutely everything. The everyday suite, then every
# [Explicit] suite, then the Unity IL2CPP pass, stopping at the first
# failure (set -e). Expect it to take a long time (the conformance
# corpora, the deep campaigns, and a full Unity player build), and it
# needs network access (the RequiresNetwork tests download UCD files
# from unicode.org) plus Unity installed (runil2cpptest.sh
# preflight-checks the exact version). The [Explicit] suites are
# selected by category because NUnit only runs an [Explicit] test when
# the filter picks it out by name or category.
# ExplicitTestConventionTests in the test project keeps the category
# list complete: an [Explicit] test outside the categories named here
# fails the everyday suite.
if [ "${1:-}" = "--all" ]; then
    shift
    "$repo_root/test.sh" "$@"
    "$repo_root/test.sh" --filter "TestCategory=UnicodeConformance|TestCategory=DeepCampaign|TestCategory=RequiresNetwork" "$@"
    "$repo_root/src/InductorParser.Tests/runil2cpptest.sh"
    exit 0
fi

# WSL has no native `dotnet` but can call the Windows SDK via interop.
# Fall back to dotnet.exe when dotnet isn't on PATH. On Linux CI and Git
# Bash on Windows, `dotnet` is present, so this stays "dotnet" and nothing
# changes for them.
dotnet_command=dotnet
if ! command -v dotnet >/dev/null 2>&1; then
    dotnet_command=dotnet.exe
fi

# The [Explicit] UnicodeConformance fixtures expand to ~16,000 discovered
# cases. They never run by default, but the console logger lists every one
# as skipped, which buries the real summary. The logger reports skipped
# tests at "warning" level, so minimal still prints them. Quiet drops them
# and shows only failures (errors) plus the pass/fail summary. Only add it
# when the caller hasn't asked for their own --logger, so an explicit
# `--logger "console;verbosity=detailed"` still wins.
logger_arguments=(--logger "console;verbosity=quiet")
for argument in "$@"; do
    if [ "$argument" = "--logger" ]; then
        logger_arguments=()
        break
    fi
done

# Suppress the ~10,000 per-case skip lines from the [Explicit]
# UnicodeConformance fixture. The quiet console logger above handles this
# when dotnet honors it, but dotnet.exe driven from WSL ignores the logger
# setting and runs at default verbosity, listing every explicit-skipped
# case. Those lines all include the fixture's [Explicit] reason text, so
# filter on it as a backstop. PIPESTATUS keeps dotnet's real exit code
# instead of grep's.
set +e
"$dotnet_command" test --nologo "${logger_arguments[@]}" "$@" 2>&1 | grep -v 'UAX #29 conformance suite'
status=${PIPESTATUS[0]}
set -e
exit "$status"
