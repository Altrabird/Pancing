#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Pancing.EditorTools
{
    /// <summary>
    /// One-click builds. Adds a "Pancing" menu to the Unity menu bar.
    ///
    /// Because the game builds itself at runtime (see GameLauncher), the only
    /// thing a build needs is one empty scene in the Build Settings — this tool
    /// creates and registers it, so there is nothing to set up by hand and nothing
    /// that can drift out of step with the code.
    ///
    /// Outputs, relative to the project folder:
    ///   Builds/Windows/Pancing.exe   (+ data folder) — for PCs and laptops
    ///   Builds/Android/Pancing.apk   — sideload onto a phone or tablet
    /// </summary>
    public static class BuildTool
    {
        private const string SceneDir = "Assets/Pancing/Scenes";
        private const string ScenePath = SceneDir + "/Main.unity";

        private const string ProductName = "Pancing";
        private const string PackageId = "com.altrabird.pancing";

        [MenuItem("Pancing/Build/Windows (.exe)", false, 1)]
        public static void BuildWindows()
        {
            EnsureScene();
            ApplySettings();
            Run(BuildTarget.StandaloneWindows64, "Builds/Windows/Pancing.exe");
        }

        [MenuItem("Pancing/Build/Android (.apk)", false, 2)]
        public static void BuildAndroid()
        {
            EnsureScene();
            ApplySettings();
            ConfigureAndroid();
            Run(BuildTarget.Android, "Builds/Android/Pancing.apk");
        }

        [MenuItem("Pancing/Build/Both — Windows + Android", false, 3)]
        public static void BuildBoth()
        {
            BuildWindows();
            BuildAndroid();
        }

        [MenuItem("Pancing/Setup/Create Main scene + add to Build Settings", false, 20)]
        public static void EnsureScene()
        {
            if (!Directory.Exists(SceneDir)) Directory.CreateDirectory(SceneDir);

            if (!File.Exists(ScenePath))
            {
                EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
                Debug.Log("[Pancing] Created empty startup scene: " + ScenePath);
            }

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Pancing/Setup/Re-export data from the JS reference", false, 21)]
        public static void ExportDataHint()
        {
            EditorUtility.DisplayDialog(
                "Re-export data",
                "The species, gear and spot tables are generated from the JavaScript " +
                "reference build so the two cannot drift.\n\n" +
                "From the repository root, run:\n\n" +
                "    node shared/tools/export-data.mjs\n\n" +
                "It rewrites shared/data/*.json and copies them into " +
                "Assets/Pancing/Resources/.",
                "OK");
        }

        [MenuItem("Pancing/Clear saved game", false, 40)]
        public static void ClearSave()
        {
            if (!EditorUtility.DisplayDialog("Clear saved game",
                    "Delete the local save — level, money, gear and the record book?",
                    "Delete", "Cancel")) return;
            PlayerPrefs.DeleteKey(Pancing.Sim.PlayerState.SaveKey);
            PlayerPrefs.Save();
            Debug.Log("[Pancing] Save cleared.");
        }

        /* --- settings ---------------------------------------------------------- */

        private static void ApplySettings()
        {
            PlayerSettings.productName = ProductName;
            PlayerSettings.companyName = "Altrabird";

            // Colour space and a sane default quality. Linear makes the water's
            // Fresnel term behave the way the shader assumes it does.
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // The game builds its own UI and never uses the splash-screen logo slot;
            // leaving it on just delays the first frame.
            PlayerSettings.SplashScreen.show = false;
        }

        private static void ConfigureAndroid()
        {
            PlayerSettings.SetApplicationIdentifier(
                UnityEditor.Build.NamedBuildTarget.Android, PackageId);

            // API 25 is the lowest Unity 6 still accepts — Android 7.1, so phones back to
            // about 2016, which is the realistic floor for a hand-me-down school device.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7 | AndroidArchitecture.ARM64;
            // IL2CPP because 64-bit ARM requires it, and Play Store uploads require
            // 64-bit. Sideloaded APKs do not, but there is no reason to ship worse.
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);

            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

            // An .apk, not an .aab: this gets sideloaded onto school tablets over
            // USB or a shared folder, not uploaded to Play.
            EditorUserBuildSettings.buildAppBundle = false;
        }

        /* --- the build --------------------------------------------------------- */

        /// <summary>
        /// What the thing actually weighs, in MB.
        ///
        /// Neither obvious answer is right on its own. `report.summary.totalSize`
        /// counts Android's staged uncompressed payload and calls a 35 MB apk
        /// "410 MB"; the output file's own length calls a Windows build "0.6 MB",
        /// because that is just the launcher stub next to a Pancing_Data folder
        /// holding everything else. So: one file for a packaged build, the whole
        /// folder for a loose one.
        /// </summary>
        private static double SizeOnDisk(string outputPath)
        {
            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            bool packaged = ext == ".apk" || ext == ".aab";

            long bytes = 0;
            if (packaged)
            {
                if (File.Exists(outputPath)) bytes = new FileInfo(outputPath).Length;
            }
            else
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        // Burst's debug symbols are stamped "DoNotShip" and are not
                        // part of what a player downloads.
                        if (f.Contains("BurstDebugInformation")) continue;
                        bytes += new FileInfo(f).Length;
                    }
                }
            }
            return bytes / (1024.0 * 1024.0);
        }

        private static void Run(BuildTarget target, string outputPath)
        {
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = target,
                options = BuildOptions.None,
            };

            Debug.Log($"[Pancing] Building {target} → {outputPath}");
            BuildReport report = BuildPipeline.BuildPlayer(options);

            if (report.summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[Pancing] {target} build OK — {SizeOnDisk(outputPath):0.0} MB at {outputPath}");
            }
            else
            {
                Debug.LogError($"[Pancing] {target} build {report.summary.result}: " +
                               $"{report.summary.totalErrors} error(s).");
            }
        }
    }
}
#endif
