// Editor side of the PLY Viewer.
//   PLY Viewer > Open .ply File...   choose a file, then the viewer scene opens and Play starts
//   view-ply /path/to/scene.ply      the script writes Library/PlyViewer/open-request.txt; this
//                                    class polls for it and does the same as the menu
//   PLY Viewer > Create Viewer Scene rebuilds Assets/Scenes/PlyViewer.unity

using System.IO;
using Gsplat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlyView.EditorTools
{
    [InitializeOnLoad]
    public static class PlyViewerEditor
    {
        const string ScenePath = "Assets/Scenes/PlyViewer.unity";
        const string RequestFile = "Library/PlyViewer/open-request.txt";
        const string PendingKey = "PlyViewer.PendingPath";
        const string LastDirKey = "PlyViewer.LastDir";

        static double s_nextPoll;

        static PlyViewerEditor()
        {
            EditorApplication.update += Poll;
        }

        [MenuItem("PLY Viewer/Open .ply File...", priority = 1)]
        static void OpenFileMenu()
        {
            string dir = EditorPrefs.GetString(LastDirKey, System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.Desktop));
            string path = EditorUtility.OpenFilePanel("Open a Gaussian splat .ply", dir, "ply");
            if (string.IsNullOrEmpty(path)) return;
            EditorPrefs.SetString(LastDirKey, Path.GetDirectoryName(path));
            Open(path);
        }

        /// <summary>Load `path` into the viewer scene and start Play mode.</summary>
        public static void Open(string path)
        {
            // Leave Play mode first; Poll() finishes the job once the Editor is back in Edit mode.
            SessionState.SetString(PendingKey, path);
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < s_nextPoll) return;
            s_nextPoll = EditorApplication.timeSinceStartup + 0.5;

            if (File.Exists(RequestFile))
            {
                string requested = File.ReadAllText(RequestFile).Trim();
                File.Delete(RequestFile);
                if (requested.Length > 0) Open(requested);
            }

            // isPlaying stays true until Play mode has really ended, unlike isPlayingOrWillChangePlaymode.
            string pending = SessionState.GetString(PendingKey, "");
            if (pending.Length == 0 || EditorApplication.isPlaying ||
                EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            SessionState.EraseString(PendingKey);
            try
            {
                LoadAndPlay(pending);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[PlyViewer] Could not open " + pending + ": " + e.Message);
            }
        }

        static void LoadAndPlay(string path)
        {
            if (!File.Exists(path))
            {
                EditorUtility.DisplayDialog("PLY Viewer", "File not found:\n" + path, "OK");
                return;
            }
            if (SceneManager.GetActiveScene().path != ScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                if (File.Exists(ScenePath)) EditorSceneManager.OpenScene(ScenePath);
                else CreateViewerScene();
            }

            if (Object.FindAnyObjectByType<PlyViewer>() == null)
                CreateViewerScene();

            // Handed to Play mode in memory, so the scene file never stores a machine-specific path.
            SessionState.SetString(PlyViewer.EditorPathKey, path);
            Debug.Log("[PlyViewer] Opening " + path);
            EditorApplication.EnterPlaymode();
        }

        [MenuItem("PLY Viewer/Create Viewer Scene", priority = 20)]
        public static void CreateViewerScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.06f);
            cam.fieldOfView = 60f;
            cam.allowMSAA = false;
            var fly = camGo.AddComponent<FlyCamera>();

            var splat = new GameObject("Splat").AddComponent<GsplatRenderer>();

            var viewer = new GameObject("PLY Viewer").AddComponent<PlyViewer>();
            viewer.splatRenderer = splat;
            viewer.flyCamera = fly;

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[PlyViewer] Created " + ScenePath);
        }
    }
}
