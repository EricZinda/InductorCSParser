using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace InductorParser.Editor
{
    // Batch-mode entry point that flips the Standalone scripting backend
    // to IL2CPP and runs the PlayMode smoke tests on a Standalone Player.
    // This is the "does the library work under IL2CPP" tripwire described
    // in docs/TestArchitecture.md and in backlog/k900.
    //
    // Running PlayMode tests via the built-in `-runTests -testPlatform
    // StandaloneWindows64` flag would also work, but it assumes the
    // scripting backend is already set to IL2CPP in ProjectSettings.asset.
    // The scaffold in this repo deliberately doesn't ship a committed
    // ProjectSettings.asset so Unity generates a default on first open,
    // which means the scripting backend starts as Mono. We set it here at
    // run time so the committed state is "whatever Unity chose by
    // default" and the IL2CPP configuration lives in code where the test
    // expects it.
    //
    // Invoked via:
    //   Unity -batchmode -projectPath src/InductorParser.Tests/Unity/
    //         -executeMethod InductorParser.Editor.IL2CPPTestRunner.Run
    //         -logFile ...
    // DON'T pass -quit. The method schedules an async test run. Unity
    // must stay alive until the RunFinished callback fires and calls
    // EditorApplication.Exit with the result code.
    public static class IL2CPPTestRunner
    {
        // NUnit XML results file, anchored to the repo root. The Unity
        // project lives at <repo>/src/InductorParser.Tests/Unity/, so
        // the repo root is three parents above Application.dataPath
        // (which is <repo>/src/InductorParser.Tests/Unity/Assets).
        // Deriving from Application.dataPath avoids relying on Unity's
        // batch-mode working directory, which isn't guaranteed.
        private static string ResolveResultsPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var testsDir = Directory.GetParent(projectRoot)!.FullName;
            var srcDir = Directory.GetParent(testsDir)!.FullName;
            var repoRoot = Directory.GetParent(srcDir)!.FullName;
            return Path.Combine(repoRoot, "test-results", "il2cpp-playmode-results.xml");
        }

        public static void Run()
        {
            try
            {
                ConfigureIL2CPPStandalone();
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                api.RegisterCallbacks(new ExitOnRunFinished(ResolveResultsPath()));
                api.Execute(new ExecutionSettings(new Filter
                {
                    testMode = TestMode.PlayMode,
                    targetPlatform = PickStandaloneTarget()
                }));
            }
            catch (Exception e)
            {
                Debug.LogError($"IL2CPPTestRunner.Run threw: {e}");
                EditorApplication.Exit(2);
            }
        }

        private static void ConfigureIL2CPPStandalone()
        {
            var standalone = NamedBuildTarget.Standalone;
            PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(standalone, ApiCompatibilityLevel.NET_Standard);
            AssetDatabase.SaveAssets();
        }

        private static BuildTarget PickStandaloneTarget()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    return BuildTarget.StandaloneWindows64;
                case RuntimePlatform.OSXEditor:
                    return BuildTarget.StandaloneOSX;
                case RuntimePlatform.LinuxEditor:
                    return BuildTarget.StandaloneLinux64;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported editor platform for IL2CPP smoke test: {Application.platform}");
            }
        }

        private sealed class ExitOnRunFinished : ICallbacks
        {
            private readonly string _resultsPath;

            public ExitOnRunFinished(string resultsPath)
            {
                _resultsPath = resultsPath;
            }

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                try
                {
                    WriteResults(result);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to write IL2CPP test results: {e}");
                }

                var exitCode = result.FailCount > 0 ? 1 : 0;
                Debug.Log($"IL2CPP PlayMode tests finished. Pass={result.PassCount} " +
                          $"Fail={result.FailCount} Skip={result.SkipCount}. Exiting {exitCode}.");
                EditorApplication.Exit(exitCode);
            }

            private void WriteResults(ITestResultAdaptor result)
            {
                var path = Path.GetFullPath(_resultsPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var writer = new StreamWriter(path);
                writer.Write(result.ToXml().OuterXml);
            }
        }
    }
}
