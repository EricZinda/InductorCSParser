#!/bin/bash
#
# Build the netstandard2.1 InductorParser.dll and run the full
# InductorParser.Tests suite under IL2CPP via Unity's batch mode. The
# .NET test sources live in src/InductorParser.Tests/{Core,Rules,E2EExamples}/
# and are the single source of truth; this script copies them into the
# Unity PlayMode folder before Unity runs so the IL2CPP test pass exercises
# the same coverage that dotnet test does.
#
# Usage:
#   ./runil2cpptest.sh
#
# On Windows this runs from Git Bash or WSL, same as test.sh.
#
# Requires:
#   - dotnet SDK
#   - Unity 6000.3.13f1 (matches Unity/ProjectSettings/ProjectVersion.txt)
#   - Unity's IL2CPP build support module for the host platform
#   - Unity not open on the Unity/ project at the same time
#
# Results land in test-results/il2cpp-playmode-results.xml at the repo
# root; Unity log lands in test-results/il2cpp-log.txt. Exits non-zero on
# test failure or if a required component is missing.

set -e

SCRIPT_DIR="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
REPO_ROOT="$(readlink -f "$SCRIPT_DIR/../..")"
UNITY_PROJECT="$SCRIPT_DIR/Unity"
LIBRARY_CSPROJ="$REPO_ROOT/src/InductorParser/InductorParser.csproj"
RESULTS_DIR="$REPO_ROOT/test-results"
RESULTS_XML="$RESULTS_DIR/il2cpp-playmode-results.xml"
UNITY_LOG="$RESULTS_DIR/il2cpp-log.txt"
SYNCED_TESTS_DIR="$UNITY_PROJECT/Assets/Tests/PlayMode/Synced"

UNITY_VERSION="6000.3.13f1"

fail() {
    echo ""
    echo "*** $1 ***"
    shift
    for line in "$@"; do
        echo "    $line"
    done
    exit 1
}

# --- dotnet ---

DOTNET="dotnet"
if ! command -v dotnet &> /dev/null; then
    if command -v dotnet.exe &> /dev/null; then
        DOTNET="dotnet.exe"
    else
        fail "dotnet SDK not found" \
            "Install the .NET SDK: https://dotnet.microsoft.com/download"
    fi
fi

# --- Unity editor ---
#
# Git Bash mounts Windows drives at /c, WSL mounts them at /mnt/c, so
# probe both. WSL also doesn't auto-convert POSIX path arguments for
# Windows executables the way Git Bash does, so every path handed to
# Unity.exe or dotnet.exe goes through to_windows_path: wslpath -w
# where it exists (WSL), unchanged everywhere else.

to_windows_path() {
    if command -v wslpath &> /dev/null; then
        wslpath -w "$1"
    else
        echo "$1"
    fi
}

UNITY=""
UNITY_DATA=""
for drive_root in "/c" "/mnt/c"; do
    candidate="$drive_root/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe"
    if [ -x "$candidate" ]; then
        UNITY="$candidate"
        UNITY_DATA="$drive_root/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Data"
        IL2CPP_VARIATION="WindowsStandaloneSupport/Variations/win64_player_nondevelopment_il2cpp"
        break
    fi
done
if [ -z "$UNITY" ] && [ -x "/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity" ]; then
    UNITY="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
    UNITY_DATA="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents"
    IL2CPP_VARIATION="PlaybackEngines/MacStandaloneSupport/Variations/macos_x64_nondevelopment_il2cpp"
fi
if [ -z "$UNITY" ]; then
    fail "Unity $UNITY_VERSION not found" \
        "Install via Unity Hub, or edit this script to point at your install." \
        "Looked for:" \
        "  /c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe (Git Bash)" \
        "  /mnt/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe (WSL)" \
        "  /Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity (macOS)"
fi

# --- IL2CPP build support module ---
#
# Unity's IL2CPP backend ships as a separate Hub module. The Editor alone
# compiles but rejects the player build with "Currently selected scripting
# backend (IL2CPP) is not installed". Catch that here instead of after a
# multi-minute Unity startup.

if [ ! -d "$UNITY_DATA/il2cpp" ]; then
    fail "IL2CPP compiler not installed in Unity $UNITY_VERSION" \
        "Missing: $UNITY_DATA/il2cpp" \
        "Install via Unity Hub: Installs -> $UNITY_VERSION -> Add modules ->" \
        "  check Windows Build Support (IL2CPP) or the matching module for your OS."
fi

if [ ! -d "$UNITY_DATA/PlaybackEngines/$IL2CPP_VARIATION" ]; then
    fail "IL2CPP Standalone player variation missing" \
        "Expected: $UNITY_DATA/PlaybackEngines/$IL2CPP_VARIATION" \
        "The IL2CPP module is partially installed. Re-add it via Unity Hub."
fi

# --- run ---

mkdir -p "$RESULTS_DIR"
rm -f "$RESULTS_XML" "$UNITY_LOG"
# wslpath -w can only convert paths that exist, and the log file is
# handed to Unity in Windows form below, so create it empty up front.
touch "$UNITY_LOG"

echo "=== Building netstandard2.1 InductorParser.dll ==="
# dotnet.exe is the Windows SDK reached through WSL interop, so it needs
# the csproj path in Windows form. A native dotnet takes the POSIX path.
LIBRARY_CSPROJ_ARGUMENT="$LIBRARY_CSPROJ"
if [ "$DOTNET" = "dotnet.exe" ]; then
    LIBRARY_CSPROJ_ARGUMENT="$(to_windows_path "$LIBRARY_CSPROJ")"
fi
$DOTNET build "$LIBRARY_CSPROJ_ARGUMENT" -c Release -f netstandard2.1

# Mirror the .NET test sources into Unity. The .NET test project
# (src/InductorParser.Tests/) is the single source of truth for test
# coverage; syncteststounity.sh copies a snapshot into the Unity PlayMode
# asmdef's scope so Unity's test runner discovers and runs the same NUnit
# tests under IL2CPP. The destination is .gitignored (Unity/.gitignore)
# and cleaned each run. syncteststounity.sh is also safe to run on its own
# when iterating in the Unity Editor's Test Runner window.
echo ""
echo "=== Syncing test sources into Unity PlayMode ==="
"$SCRIPT_DIR/syncteststounity.sh"

echo ""
echo "=== Running PlayMode smoke test under IL2CPP (Unity batch mode) ==="
echo "Unity:   $UNITY"
echo "Log:     $UNITY_LOG"
echo "Results: $RESULTS_XML"
echo "(This can take several minutes on a cold Library/ cache.)"

# No -quit: the editor script schedules an async test run and calls
# EditorApplication.Exit from the RunFinished callback.
"$UNITY" -batchmode -nographics \
    -projectPath "$(to_windows_path "$UNITY_PROJECT")" \
    -executeMethod InductorParser.Editor.IL2CPPTestRunner.Run \
    -logFile "$(to_windows_path "$UNITY_LOG")" || UNITY_EXIT=$?

UNITY_EXIT=${UNITY_EXIT:-0}

if [ ! -f "$RESULTS_XML" ]; then
    fail "IL2CPP test FAILED (no results file)" \
        "Unity exit=$UNITY_EXIT. Check $UNITY_LOG for details."
fi

if [ $UNITY_EXIT -ne 0 ]; then
    fail "IL2CPP tests FAILED (Unity exit=$UNITY_EXIT)" \
        "See $RESULTS_XML and $UNITY_LOG."
fi

echo "IL2CPP smoke test PASSED."
exit 0
