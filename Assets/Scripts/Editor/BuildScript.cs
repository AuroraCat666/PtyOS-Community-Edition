#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    public static class BuildScript
    {
        public static void BuildAPK()
        {
            string[] scenes = {
                "Assets/Scenes/EntryScene.unity",
                "Assets/Scenes/MainScene.unity",
                "Assets/Scenes/PlayingScene.unity",
                "Assets/Scenes/LevelOver 1.unity",
                "Assets/Scenes/NetworkTest.unity",
                "Assets/Scenes/LoadingScene.unity",
                "Assets/Scenes/ChartSelectorScene.unity",
                "Assets/Scenes/SettingsScene.unity",
                "Assets/Scenes/AboutScene.unity",
                "Assets/Scenes/LoginScene.unity",
                "Assets/Scenes/BeatmapSelectScene.unity",
                "Assets/Scenes/CharacterAdjustScene.unity",
                "Assets/Scenes/DSPScene.unity",
                "Assets/Scenes/DelayCorrectionScene.unity",
                "Assets/Scenes/WahtThe.unity"
            };

            string path = "Build/Android/PtyOS_CE.apk";

            PlayerSettings.Android.keystorePass = "Totorowldox";
            PlayerSettings.Android.keyaliasName = "greenball233_rpgr";
            PlayerSettings.Android.keyaliasPass = "Kagari939!!!";

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log("APK build succeeded: " + path);
            }
            else
            {
                Debug.LogError("APK build failed: " + report.summary.totalErrors + " errors");
                EditorApplication.Exit(1);
            }
        }
    }
}
#endif