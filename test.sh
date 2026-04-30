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

engine="${1:-recursive}"
shift || true

run_one() {
    local label="$1"
    shift
    echo "=== Running tests under engine: $label ==="
    "$@"
}

case "$engine" in
    recursive|rec|r)
        run_one "recursive" dotnet test --nologo "$@"
        ;;
    statemachine|sm|s)
        INDUCTOR_DEFAULT_ENGINE=statemachine run_one "statemachine" dotnet test --nologo "$@"
        ;;
    both|all)
        run_one "recursive" dotnet test --nologo "$@"
        INDUCTOR_DEFAULT_ENGINE=statemachine run_one "statemachine" dotnet test --nologo "$@"
        ;;
    *)
        echo "Unknown engine: $engine" >&2
        echo "Use one of: recursive | statemachine | both" >&2
        exit 2
        ;;
esac
