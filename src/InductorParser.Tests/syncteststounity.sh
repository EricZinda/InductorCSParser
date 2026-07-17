#!/bin/bash
#
# Copy the .NET test sources from src/InductorParser.Tests/{Core,Rules,
# E2EExamples}/ plus every root-level helper .cs file (CanaryHelper,
# TestHelpers, NormalizationExamples, the matrix helpers, ...) into
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
# All root-level .cs files are shared helpers the synced tests use.
# Copying them wholesale means a new helper added at the root can't
# break the Unity compile by being missing from a hand-kept list here.
cp "$SCRIPT_DIR/"*.cs "$SYNCED_TESTS_DIR/"

# PrologGrammarTests reads its .pl / .htn fixture corpus from
# AppContext.BaseDirectory + E2EExamples/PrologFixtures at run time
# (dotnet test gets them via the csproj CopyToOutputDirectory entry).
# Under the Unity test runner BaseDirectory is the Unity project root,
# so the fixtures are copied there. The directory sits outside Assets/
# on purpose: it's data for File.ReadAllText, not an asset for Unity to
# import, and it's .gitignored like Synced/.
FIXTURES_DIR="$UNITY_PROJECT/E2EExamples/PrologFixtures"
rm -rf "$UNITY_PROJECT/E2EExamples"
mkdir -p "$FIXTURES_DIR"
cp "$SCRIPT_DIR/E2EExamples/PrologFixtures/"*.pl "$FIXTURES_DIR/"
cp "$SCRIPT_DIR/E2EExamples/PrologFixtures/"*.htn "$FIXTURES_DIR/"

echo "Synced $(find "$SYNCED_TESTS_DIR" -name '*.cs' | wc -l) .cs files to:"
echo "  $SYNCED_TESTS_DIR"
echo "Synced $(find "$FIXTURES_DIR" -type f | wc -l) Prolog fixture files to:"
echo "  $FIXTURES_DIR"
