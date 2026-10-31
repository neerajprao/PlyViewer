// Loads a Gaussian-splat .ply at runtime (UnitySplats) and lets you fly through it with FlyCamera.
//
// The file comes from (first match):
//   1. Command line   -ply /path/to/scene.ply      (built player)
//   2. Editor         the file chosen with "PLY Viewer > Open .ply File..." or the view-ply script
//   3. plyPath field  set in the Inspector
//
// Keys: W A S D move, mouse look, Space/Ctrl up/down, Shift faster, scroll speed,
//       R reset view, P save this view as the file's start view, F flip upside down,
//       T tilt 90 degrees, H help, Esc release mouse.
// -screenshot /path/view.png saves one frame after loading, then quits (for checks).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Gsplat;
using UnityEngine;

namespace PlyView
{
    public class PlyViewer : MonoBehaviour
    {
        [Header("References")]
        public GsplatRenderer splatRenderer;
        public FlyCamera flyCamera;

        [Header("File")]
        [Tooltip("Absolute path of the .ply to load when Play starts (optional; the PLY Viewer menu " +
                 "and view-ply override it)")]
        public string plyPath = "";

        /// <summary>SessionState key the Editor uses to pass the chosen file into Play mode.</summary>
        public const string EditorPathKey = "PlyViewer.PlayPath";
        [Tooltip("Uncompressed keeps exact colours and shapes; Spark uses less GPU memory")]
        public CompressionMode compression = CompressionMode.Uncompressed;

        enum State { Empty, Loading, Ready, Error }

        State m_state = State.Empty;
        string m_message = "";
        bool m_showHelp = true;
        float m_fps;
        string m_screenshotPath = "";

        GsplatAsset m_asset;
        PlyInfo m_info;
        Quaternion m_upright = Quaternion.identity;
        bool m_flipped;
        int m_tiltSteps;
        Vector3 m_startPos;
        Vector3 m_startFwd = Vector3.forward;
        string m_toast = "";
        float m_toastUntil;

        /// <summary>Start view chosen with P, remembered per file in PlayerPrefs.</summary>
        [Serializable]
        class SavedView
        {
            public Vector3 position;
            public Vector3 forward;
            public bool flipped;
            public int tiltSteps;
            public float speed;
        }

        string ViewKey => "PlyViewer.view:" + Path.GetFullPath(plyPath.Trim());

        void Awake()
        {
            if (splatRenderer == null) splatRenderer = FindAnyObjectByType<GsplatRenderer>();
            if (flyCamera == null) flyCamera = FindAnyObjectByType<FlyCamera>();
            // Splats can take the whole GPU; 60 fps leaves headroom for macOS itself.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
        }

        void Start()
        {
#if UNITY_EDITOR
            string chosen = UnityEditor.SessionState.GetString(EditorPathKey, "");
            if (chosen.Length > 0) plyPath = chosen;
#endif
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-ply") plyPath = args[i + 1];
                else if (args[i] == "-screenshot") m_screenshotPath = args[i + 1];
            }

            if (string.IsNullOrWhiteSpace(plyPath))
            {
                m_state = State.Empty;
                m_message = "No .ply file chosen.\n\nIn the Editor: menu PLY Viewer > Open .ply File...\n" +
                            "From Terminal: ./view-ply /path/to/scene.ply";
                if (flyCamera != null) flyCamera.inputEnabled = false;
                return;
            }
            StartCoroutine(Load(plyPath.Trim()));
        }

        void OnDestroy()
        {
            if (splatRenderer != null && splatRenderer.GsplatAsset == m_asset)
                splatRenderer.GsplatAsset = null;
            if (m_asset != null) Destroy(m_asset);
        }

        // ------------------------------------------------------------- loading --

        IEnumerator Load(string path)
        {
            if (!File.Exists(path))
            {
                Fail($"File not found:\n{path}");
                yield break;
            }

            m_state = State.Loading;
            m_message = $"Loading {Path.GetFileName(path)} ({new FileInfo(path).Length / (1024 * 1024)} MB)...";
            yield return null; // let the message draw before the blocking decode
            yield return null;

            GsplatAsset asset;
            try
            {
                // RUB is the standard 3DGS convention; orientation is corrected below.
                asset = GsplatRuntimeLoader.LoadFile(path, compression, SourceCoordinates.RUB);
            }
            catch (Exception e)
            {
                Fail($"Could not load {Path.GetFileName(path)}:\n{e.Message}");
                yield break;
            }

            m_asset = asset;
            splatRenderer.GsplatAsset = asset;
            m_info = PlyInfo.Read(path);
            m_upright = UprightRotation(m_info);
            SavedView saved = LoadSavedView();
            if (saved != null)
            {
                m_flipped = saved.flipped;
                m_tiltSteps = saved.tiltSteps;
            }
            Debug.Log($"[PlyViewer] Loaded {path}: {asset.SplatCount:N0} splats" +
                      (m_info?.verticalAxis != null ? $", vertical axis {m_info.verticalAxis}" : ""));

            Place();
            m_state = State.Ready;
            if (flyCamera != null) flyCamera.inputEnabled = true;
            if (!string.IsNullOrEmpty(m_screenshotPath)) StartCoroutine(ScreenshotAndQuit(m_screenshotPath));
        }

        /// <summary>Rotation that stands the scene upright. Brush writes the scene's up direction
        /// into the PLY header; otherwise use the usual 3DGS convention (y points down), the same
        /// 180 degree turn UnitySplats applies when importing a PLY in the Editor.</summary>
        static Quaternion UprightRotation(PlyInfo info)
        {
            if (info?.verticalAxis is Vector3 v)
            {
                var upLocal = new Vector3(v.x, v.y, -v.z); // file -> RUB-loaded local frame
                return Quaternion.FromToRotation(upLocal, Vector3.up);
            }
            return Quaternion.Euler(0f, 0f, 180f);
        }

        Quaternion SceneRotation() =>
            Quaternion.Euler(90f * m_tiltSteps, 0f, 0f) * Quaternion.Euler(0f, 0f, m_flipped ? 180f : 0f) * m_upright;

        /// <summary>Orient the scene and choose a start view: an empty spot inside the scene with the
        /// most splats in front of it (where a camera could have stood), or, if there is none
        /// (e.g. a small object), outside the scene looking at its middle.</summary>
        void Place()
        {
            Transform root = splatRenderer.transform;
            Quaternion rot = SceneRotation();
            root.SetPositionAndRotation(Vector3.zero, rot);

            m_startFwd = Vector3.forward;
            float radius;
            if (m_info != null && m_info.samples.Count >= 100)
            {
                var pts = new List<Vector3>(StartSearchSamples);
                int step = Mathf.Max(1, m_info.samples.Count / StartSearchSamples);
                for (int i = 0; i < m_info.samples.Count; i += step)
                {
                    Vector3 s = m_info.samples[i];
                    pts.Add(rot * new Vector3(s.x, s.y, -s.z));
                }
                Vector3 lo = Percentile(pts, 0.1f), mid = Percentile(pts, 0.5f), hi = Percentile(pts, 0.9f);
                radius = Mathf.Max(0.01f, 0.5f * (hi - lo).magnitude);
                if (!FindOpenView(pts, lo, hi, radius, out m_startPos, out m_startFwd))
                {
                    m_startFwd = Vector3.forward;
                    m_startPos = mid - m_startFwd * radius * 1.8f;
                }
            }
            else
            {
                Bounds b = splatRenderer.Bounds;
                radius = Mathf.Max(0.01f, b.extents.magnitude);
                m_startPos = rot * b.center - m_startFwd * radius * 1.8f;
            }

            if (flyCamera != null)
            {
                flyCamera.baseSpeed = Mathf.Clamp(radius * 0.25f, 0.02f, 50f);
                var cam = flyCamera.GetComponent<Camera>();
                cam.nearClipPlane = Mathf.Clamp(radius * 0.002f, 0.001f, 0.1f);
                cam.farClipPlane = Mathf.Max(100f, radius * 40f);
            }

            // A view saved with P wins, as long as it was saved with the same flip/tilt.
            SavedView saved = LoadSavedView();
            if (saved != null && saved.flipped == m_flipped && saved.tiltSteps == m_tiltSteps)
            {
                m_startPos = saved.position;
                m_startFwd = saved.forward;
                if (flyCamera != null && saved.speed > 0f) flyCamera.baseSpeed = saved.speed;
            }
            ResetView();
        }

        SavedView LoadSavedView()
        {
            string json = PlayerPrefs.GetString(ViewKey, "");
            return json.Length > 0 ? JsonUtility.FromJson<SavedView>(json) : null;
        }

        void SaveCurrentView()
        {
            if (flyCamera == null) return;
            Transform t = flyCamera.transform;
            var view = new SavedView
            {
                position = t.position, forward = t.forward, flipped = m_flipped,
                tiltSteps = m_tiltSteps, speed = flyCamera.baseSpeed,
            };
            PlayerPrefs.SetString(ViewKey, JsonUtility.ToJson(view));
            PlayerPrefs.Save();
            m_startPos = view.position;
            m_startFwd = view.forward;
            Toast("Start view saved - this file will open here, and R comes back here");
        }

        void Toast(string text)
        {
            m_toast = text;
            m_toastUntil = Time.unscaledTime + 3f;
        }

        const int StartSearchSamples = 20000;

        /// <summary>Grid search over the scene's core box: candidate spots with almost no splats
        /// nearby, scored by how many splats lie in a 30 degree cone in front at a viewing distance.</summary>
        static bool FindOpenView(List<Vector3> pts, Vector3 lo, Vector3 hi, float radius,
                                 out Vector3 bestPos, out Vector3 bestFwd)
        {
            bestPos = Vector3.zero;
            bestFwd = Vector3.forward;
            int bestScore = 0;
            float clear2 = 0.01f * radius * radius;          // empty within 0.1 r
            float near = 0.15f * radius, far = 2f * radius;
            int maxNearby = Mathf.Max(1, pts.Count / 500);
            var dirs = new Vector3[8];
            for (int k = 0; k < 8; k++)
                dirs[k] = Quaternion.Euler(0f, 45f * k, 0f) * Vector3.forward;
            var score = new int[8];

            for (int ix = 0; ix < 7; ix++)
            for (int iy = 0; iy < 3; iy++)
            for (int iz = 0; iz < 7; iz++)
            {
                var c = new Vector3(Mathf.Lerp(lo.x, hi.x, ix / 6f), Mathf.Lerp(lo.y, hi.y, iy / 2f),
                                    Mathf.Lerp(lo.z, hi.z, iz / 6f));
                int nearby = 0;
                Array.Clear(score, 0, 8);
                foreach (Vector3 p in pts)
                {
                    Vector3 d = p - c;
                    float d2 = d.sqrMagnitude;
                    if (d2 < clear2 && ++nearby > maxNearby) break;
                    float dist = Mathf.Sqrt(d2);
                    if (dist < near || dist > far) continue;
                    for (int k = 0; k < 8; k++)
                        if (Vector3.Dot(d, dirs[k]) > 0.87f * dist) score[k]++;
                }
                if (nearby > maxNearby) continue;
                for (int k = 0; k < 8; k++)
                {
                    if (score[k] <= bestScore) continue;
                    bestScore = score[k];
                    bestPos = c;
                    bestFwd = dirs[k];
                }
            }
            return bestScore > 0;
        }

        static Vector3 Percentile(List<Vector3> pts, float q)
        {
            int n = pts.Count, k = Mathf.Clamp(Mathf.RoundToInt(q * (n - 1)), 0, n - 1);
            var xs = new float[n];
            var ys = new float[n];
            var zs = new float[n];
            for (int i = 0; i < n; i++) { xs[i] = pts[i].x; ys[i] = pts[i].y; zs[i] = pts[i].z; }
            Array.Sort(xs);
            Array.Sort(ys);
            Array.Sort(zs);
            return new Vector3(xs[k], ys[k], zs[k]);
        }

        void ResetView()
        {
            if (flyCamera != null) flyCamera.SetPose(m_startPos, m_startFwd);
        }

        void Fail(string message)
        {
            m_state = State.Error;
            m_message = message;
            Debug.LogError("[PlyViewer] " + message.Replace('\n', ' '));
            if (flyCamera != null) flyCamera.ReleaseMouse();
        }

        IEnumerator ScreenshotAndQuit(string path)
        {
            yield return new WaitForSecondsRealtime(2f); // let the GPU sort settle
            ScreenCapture.CaptureScreenshot(path);
            yield return new WaitForSecondsRealtime(1f);
            Debug.Log("[PlyViewer] Saved screenshot " + path);
            Application.Quit();
        }

        // ----------------------------------------------------------------- UI --

        void Update()
        {
            m_fps = Mathf.Lerp(m_fps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
            if (m_state != State.Ready) return;
            if (KeyDown(KeyCode.H)) m_showHelp = !m_showHelp;
            if (KeyDown(KeyCode.R)) ResetView();
            if (KeyDown(KeyCode.P)) SaveCurrentView();
            if (KeyDown(KeyCode.F)) { m_flipped = !m_flipped; Place(); }
            if (KeyDown(KeyCode.T)) { m_tiltSteps = (m_tiltSteps + 1) % 4; Place(); }
        }

        static bool KeyDown(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null) return false;
            switch (key)
            {
                case KeyCode.H: return k.hKey.wasPressedThisFrame;
                case KeyCode.R: return k.rKey.wasPressedThisFrame;
                case KeyCode.P: return k.pKey.wasPressedThisFrame;
                case KeyCode.F: return k.fKey.wasPressedThisFrame;
                case KeyCode.T: return k.tKey.wasPressedThisFrame;
            }
            return false;
#else
            return Input.GetKeyDown(key);
#endif
        }

        void OnGUI()
        {
            GUI.skin.label.fontSize = 14;
            switch (m_state)
            {
                case State.Empty:
                case State.Loading:
                case State.Error:
                    var rect = new Rect(Screen.width / 2f - 300, Screen.height / 2f - 60, 600, 120);
                    GUI.Box(rect, m_state == State.Error ? "Something went wrong" : "PLY Viewer");
                    GUI.Label(new Rect(rect.x + 16, rect.y + 28, rect.width - 32, rect.height - 36), m_message);
                    break;
                case State.Ready:
                    DrawHud();
                    break;
            }
        }

        void DrawHud()
        {
            if (Time.unscaledTime < m_toastUntil)
                GUI.Box(new Rect(Screen.width / 2f - 260, Screen.height - 60, 520, 30), m_toast);

            string title = $"{Path.GetFileName(plyPath)}   {m_asset.SplatCount:N0} splats   {m_fps:0} fps";
            if (!m_showHelp)
            {
                GUI.Label(new Rect(12, 8, 700, 24), title + "   (H for help)");
                return;
            }
            const string help =
                "Click the view to capture the mouse, Esc to release\n" +
                "W A S D  move        Mouse  look        Space / Ctrl  up / down\n" +
                "Shift  faster        Scroll  speed      R  reset view\n" +
                "P  save this view as the start view\n" +
                "F  flip upside down     T  tilt 90°     H  hide help";
            GUI.Box(new Rect(8, 8, 560, 132), GUIContent.none);
            GUI.Label(new Rect(18, 12, 540, 24), title);
            GUI.Label(new Rect(18, 36, 540, 104), help);
        }
    }
}
