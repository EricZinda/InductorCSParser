#!/usr/bin/env bash
# test.sh: run the test suite under either parsing engine.
#
# Usage:
#   ./test.sh                  # recursive engine (default)
#   ./test.sh recursive        # recursive engine (explicit)
#   ./test.sh statemachine     # state-machine engine
#   ./test.sh both             # both engines, sequentially
#
# Extra arguments after the engine name pass through to dotnet test, so
# you can target a single fixture:
#   ./test.sh statemachine --filter "FullyQualifiedName~Atom_fragment"

set -euo pipefail

# WSL has no native `dotnet` but can call the Windows SDK via interop.
# Fall back to dotnet.exe when dotnet isn't on PATH. On Linux CI and Git
# Bash on Windows, `dotnet` is present, so this stays "dotnet" and nothing
# changes for them.
dotnet_command=dotnet
if ! command -v dotnet >/dev/null 2>&1; then
    dotnet_command=dotnet.exe
fi

engine="${1:-recursive}"
shift || true

# The [Explicit] UnicodeConformance fixture expands to ~10,000 discovered
# cases. They never run by default, but the console logger lists every one
# as skipped, which buries the real summary. The logger reports skipped
# tests at "warning" level, so minimal still prints them; quiet drops them
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

run_one() {
    local label="$1"
    shift
    echo "=== Running tests under engine: $label ==="
    # Suppress the ~10,000 per-case skip lines from the [Explicit]
    # UnicodeConformance fixture. The quiet console logger above handles this
    # when dotnet honors it, but dotnet.exe driven from WSL ignores the logger
    # setting and runs at default verbosity, listing every explicit-skipped
    # case. Those lines all carry the fixture's [Explicit] reason text, so
    # filter on it as a backstop. PIPESTATUS keeps dotnet's real exit code
    # instead of grep's.
    set +e
    "$@" 2>&1 | grep -v 'UAX #29 conformance suite'
    local status=${PIPESTATUS[0]}
    set -e
    return "$status"
}

case "$engine" in
    recursive|rec|r)
        run_one "recursive" "$dotnet_command" test --nologo "${logger_arguments[@]}" "$@"
        ;;
    statemachine|sm|s)
        INDUCTOR_DEFAULT_ENGINE=statemachine run_one "statemachine" "$dotnet_command" test --nologo "${logger_arguments[@]}" "$@"
        ;;
    both|all)
        run_one "recursive" "$dotnet_command" test --nologo "${logger_arguments[@]}" "$@"
        INDUCTOR_DEFAULT_ENGINE=statemachine run_one "statemachine" "$dotnet_command" test --nologo "${logger_arguments[@]}" "$@"
        ;;
    *)
        echo "Unknown engine: $engine" >&2
        echo "Use one of: recursive | statemachine | both" >&2
        exit 2
        ;;
esac
