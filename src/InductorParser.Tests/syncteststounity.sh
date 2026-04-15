#!/bin/bash
#
# Copy the .NET test sources from src/InductorParser.Tests/{Core,Rules,
# E2EExamples}/ plus TraceTestHelpers.cs and UnicodeExamples.cs into
# src/InductorParser.Tests/Unity/Assets/Tests/PlayMode/Synced/ so Unity's
# PlayMode asmdef picks them up.
#
# The .NET test project is the single source of truth for test coverage.
# The Synced/ tree under the Unity project is .gitignored and cleaned on
# each run so a deleted test can't linger there.
#
# Usage:
#   ./syncteststounity.sh
#
# Run this once after a fresh clone (or any time you add/remove/rename a
# test) before opening the Unity project in the Editor, so the Test
# Runner window sees the full suite. runil2cpptest.sh also invokes this
# script automatically before batch-mode runs.

set -e

SCRIPT_DIR="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
UNITY_PROJECT="$SCRIPT_DIR/Unity"
SYNCED_TESTS_DIR="$UNITY_PROJECT/Assets/Tests/PlayMode/Synced"

rm -rf "$SYNCED_TESTS_DIR"
mkdir -p "$SYNCED_TESTS_DIR/Core"
mkdir -p "$SYNCED_TESTS_DIR/Rules"
mkdir -p "$SYNCED_TESTS_DIR/E2EExamples"
cp "$SCRIPT_DIR/Core/"*.cs "$SYNCED_TESTS_DIR/Core/"
cp "$SCRIPT_DIR/Rules/"*.cs "$SYNCED_TESTS_DIR/Rules/"
cp "$SCRIPT_DIR/E2EExamples/"*.cs "$SYNCED_TESTS_DIR/E2EExamples/"
cp "$SCRIPT_DIR/TraceTestHelpers.cs" "$SYNCED_TESTS_DIR/"
cp "$SCRIPT_DIR/UnicodeExamples.cs" "$SYNCED_TESTS_DIR/"

echo "Synced $(find "$SYNCED_TESTS_DIR" -name '*.cs' | wc -l) .cs files to:"
echo "  $SYNCED_TESTS_DIR"
