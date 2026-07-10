#!/usr/bin/env bash
# test.sh: run the state-machine engine's test suite.
#
# Usage:
#   ./test.sh                                              # whole suite
#   ./test.sh --filter "FullyQualifiedName~Atom_fragment"  # one fixture
#
# All arguments pass through to dotnet test. The run sets
# INDUCTOR_DEFAULT_ENGINE=statemachine so every Rule.Parse call in the
# test project routes through the state-machine evaluator (see
# InductorParser.Tests/TestArchitecture.md). The cross-engine compare
# fixtures call rule.ParseRecursive directly for their recursive
# baseline, so they stay real comparisons under the flipped default.

set -euo pipefail

# Run from ExperimentalSrc/ regardless of where the script is invoked,
# so the project path below resolves.
script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_root"

# WSL has no native `dotnet` but can call the Windows SDK via interop.
# Fall back to dotnet.exe when dotnet isn't on PATH. On Linux CI and Git
# Bash on Windows, `dotnet` is present, so this stays "dotnet" and nothing
# changes for them.
dotnet_command=dotnet
if ! command -v dotnet >/dev/null 2>&1; then
    dotnet_command=dotnet.exe
fi

INDUCTOR_DEFAULT_ENGINE=statemachine "$dotnet_command" test InductorParser.Tests/InductorParser.StateMachine.Tests.csproj --nologo "$@"
