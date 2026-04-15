#!/bin/bash
#
# Build the netstandard2.1 InductorParser.dll and run the PlayMode smoke
# test under IL2CPP via Unity's batch mode. This is an IL2CPP tripwire
# test to help ensure the parser works correctly on IL2CPP.
#
# Usage:
#   ./runil2cpptest.sh
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

UNITY=""
UNITY_DATA=""
if [ -x "/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe" ]; then
    UNITY="/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe"
    UNITY_DATA="/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Data"
    IL2CPP_VARIATION="WindowsStandaloneSupport/Variations/win64_player_nondevelopment_il2cpp"
elif [ -x "/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity" ]; then
    UNITY="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
    UNITY_DATA="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents"
    IL2CPP_VARIATION="PlaybackEngines/MacStandaloneSupport/Variations/macos_x64_nondevelopment_il2cpp"
else
    fail "Unity $UNITY_VERSION not found" \
        "Install via Unity Hub, or edit this script to point at your install." \
        "Looked for:" \
        "  /c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe" \
        "  /Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
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

echo "=== Building netstandard2.1 InductorParser.dll ==="
$DOTNET build "$LIBRARY_CSPROJ" -c Release -f netstandard2.1

echo ""
echo "=== Running PlayMode smoke test under IL2CPP (Unity batch mode) ==="
echo "Unity:   $UNITY"
echo "Log:     $UNITY_LOG"
echo "Results: $RESULTS_XML"
echo "(This can take several minutes on a cold Library/ cache.)"

# No -quit: the editor script schedules an async test run and calls
# EditorApplication.Exit from the RunFinished callback.
"$UNITY" -batchmode -nographics \
    -projectPath "$UNITY_PROJECT" \
    -executeMethod InductorParser.Editor.IL2CPPTestRunner.Run \
    -logFile "$UNITY_LOG" || UNITY_EXIT=$?

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
