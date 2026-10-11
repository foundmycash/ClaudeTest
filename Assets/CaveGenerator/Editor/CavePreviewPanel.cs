using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HavocCave.EditorTools
{
    /// <summary>
    /// The preview shown in the Cave Generator inspector and in the Scene view: a quick low-detail model of the cave
    /// that is rebuilt on a background thread whenever the seed or any setting changes, before anything is generated.
    /// </summary>
    public sealed class CavePreviewPanel
    {
        const double SettleDelay = 0.2;           // seconds without changes before a rebuild starts
        const float PreviewTriangles = 16000f;    // how detailed the preview model is
        const string PrefLive = "HavocCave.PreviewLive";
        const string PrefScene = "HavocCave.PreviewScene";
        const string PrefMode = "HavocCave.PreviewMode";

        readonly CaveGenerator gen;
        readonly Action repaint;

        public CavePreviewData Data { get; private set; }
        public bool Busy { get { return task != null; } }
        public string Error { get; private set; }

        string key = "";
        double dirtyAt;
        double nextCheck;
        bool dirty;
        CancellationTokenSource cts;
        Task<CavePreviewData> task;

        Mesh mesh;
        Material material;
        Texture2D mapTexture;
        int mapW, mapH;
        bool mapStale = true;
        float mapYMin, mapYMax;
        static Texture2D legend;

        PreviewRenderUtility pru;
        float yaw = 28f, pitch = 50f, zoom = 1f;

        public bool Live
        {
            get { return EditorPrefs.GetBool(PrefLive, true); }
            set { EditorPrefs.SetBool(PrefLive, value); if (value) key = ""; }
        }

        public bool ShowInScene
        {
            get { return EditorPrefs.GetBool(PrefScene, true); }
            set { EditorPrefs.SetBool(PrefScene, value); }
        }

        /// <summary>0 = 3D view, 1 = top-down map.</summary>
        public int Mode
        {
            get { return EditorPrefs.GetInt(PrefMode, 0); }
            set { EditorPrefs.SetInt(PrefMode, value); }
        }

        public CavePreviewPanel(CaveGenerator generator, Action repaintCallback)
        {
            gen = generator;
            repaint = repaintCallback;
            EditorApplication.update += Tick;
        }

        public void Dispose()
        {
            EditorApplication.update -= Tick;
            if (cts != null) cts.Cancel();
            task = null;
            if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            if (mapTexture != null) UnityEngine.Object.DestroyImmediate(mapTexture);
            if (pru != null) pru.Cleanup();
            mesh = null; material = null; mapTexture = null; pru = null;
        }

        // ---------------------------------------------------------------- keeping the preview up to date

        static string MakeKey(CaveGenerator g)
        {
            return JsonUtility.ToJson(g.settings) + "|" + JsonUtility.ToJson(g.spawnCave) + "|" + g.seed + "|" + g.caveSize + "|" + g.tunnelScale + "|" + g.polyCount + "|" + g.usePolyCount;
        }

        void Tick()
        {
            if (gen == null) return;
            double now = EditorApplication.timeSinceStartup;

            if (task != null && task.IsCompleted) Finish();
            if (now < nextCheck) return;
            nextCheck = now + 0.05;
            if (!Live) return;

            string k = MakeKey(gen);
            if (k != key)
            {
                key = k;
                dirty = true;
                dirtyAt = now;
            }
            if (dirty && now - dirtyAt >= SettleDelay)
            {
                dirty = false;
                StartBuild();
            }
        }

        void StartBuild()
        {
            if (cts != null) cts.Cancel();
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            // Everything the worker thread needs is read here, on the main thread.
            CaveSettings effective = gen.BuildEffectiveSettings();
            int seed = gen.seed;
            bool usePoly = gen.usePolyCount;
            float poly = gen.polyCount;
            CaveSpawnCave spawn = gen.spawnCave.Clone();
            Error = null;
            task = Task.Run(() => CavePreviewBuilder.Build(effective, seed, usePoly, poly, PreviewTriangles, token, spawn), token);
            if (repaint != null) repaint();
        }

        void Finish()
        {
            Task<CavePreviewData> done = task;
            task = null;
            if (done.IsCanceled) return;
            if (done.IsFaulted)
            {
                Error = done.Exception.GetBaseException().Message;
                Debug.LogWarning("Cave preview failed: " + done.Exception.GetBaseException());
                if (repaint != null) repaint();
                return;
            }
            CavePreviewData d = done.Result;
            if (d == null) return;
            Apply(d);
            if (repaint != null) repaint();
            SceneView.RepaintAll();
        }

        /// <summary>Turns finished preview data into a mesh. Also used by tests.</summary>
        public void Apply(CavePreviewData d)
        {
            Data = d;
            if (mesh == null)
            {
                mesh = new Mesh { name = "CavePreview", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            }
            mesh.Clear();
            mesh.vertices = d.vertices;
            mesh.colors32 = d.colors;
            mesh.triangles = d.triangles;
            mesh.RecalculateBounds();
            mapStale = true;
        }

        Material GetMaterial()
        {
            if (material != null) return material;
            Shader sh = Shader.Find("Hidden/Havoc/CavePreview");
            if (sh == null)
            {
                string[] guids = AssetDatabase.FindAssets("CavePreview t:Shader");
                for (int i = 0; i < guids.Length && sh == null; i++)
                    sh = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guids[i]));
            }
            if (sh == null) sh = Shader.Find("Hidden/Internal-Colored");
            material = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            return material;
        }

        // ---------------------------------------------------------------- Scene view

        public void DrawScene()
        {
            if (gen == null || Data == null || mesh == null || !ShowInScene) return;
            if (Event.current.type != EventType.Repaint) return;
            if (gen.HasGeneratedCave) return;             // the real thing is there: no ghost on top of it

            Matrix4x4 m = gen.transform.localToWorldMatrix * Matrix4x4.Translate(-Data.spawnFloor);
            Graphics.DrawMesh(mesh, m, GetMaterial(), 0, Camera.current);

            Handles.color = new Color(0.3f, 0.95f, 0.5f);
            Handles.DrawWireDisc(gen.transform.position + Vector3.up * 0.05f, Vector3.up, 0.6f);
            Handles.Label(gen.transform.position + Vector3.up * 2.2f, "Cave preview: press Generate to build it");
        }

        // ---------------------------------------------------------------- inspector

        static GUIStyle overlayStyle;

        static GUIStyle OverlayStyle
        {
            get
            {
                if (overlayStyle == null)
                {
                    overlayStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                    overlayStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
                }
                return overlayStyle;
            }
        }

        public void DrawInspector()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Preview", EditorStyles.boldLabel, GUILayout.Width(52));
            int mode = GUILayout.Toolbar(Mode, new[] { "3D View", "Top-Down Map" }, GUILayout.Height(20));
            if (mode != Mode) Mode = mode;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            bool live = EditorGUILayout.ToggleLeft(new GUIContent("Live", "Rebuild the preview whenever the seed or a setting changes."), Live, GUILayout.Width(50));
            if (live != Live) Live = live;
            bool inScene = EditorGUILayout.ToggleLeft(new GUIContent("Show in Scene view", "Draw the preview in the Scene view at this object's position until the real cave is generated."), ShowInScene, GUILayout.Width(140));
            if (inScene != ShowInScene) { ShowInScene = inScene; SceneView.RepaintAll(); }
            GUILayout.FlexibleSpace();
            if (Mode == 0)
            {
                if (GUILayout.Button("Top", EditorStyles.miniButtonLeft, GUILayout.Width(40))) { yaw = 0f; pitch = 89f; zoom = 1f; }
                if (GUILayout.Button("Angle", EditorStyles.miniButtonMid, GUILayout.Width(44))) { yaw = 28f; pitch = 50f; zoom = 1f; }
                if (GUILayout.Button("Side", EditorStyles.miniButtonRight, GUILayout.Width(40))) { yaw = 20f; pitch = 8f; zoom = 1f; }
            }
            EditorGUILayout.EndHorizontal();

            Rect r = GUILayoutUtility.GetRect(10f, 300f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(r, new Color(0.11f, 0.12f, 0.15f));

            if (Data == null || mesh == null)
            {
                string msg = Error != null ? "Preview failed: " + Error
                           : (!Live ? "Live preview is off. Turn on Live to see the cave before you generate it."
                                    : "Building preview...");
                GUI.Label(new Rect(r.x + 10f, r.y + r.height * 0.5f - 12f, r.width - 20f, 40f), msg, OverlayStyle);
            }
            else if (Mode == 0)
            {
                Handle3D(r);
                if (Event.current.type == EventType.Repaint)
                {
                    Texture t = Render3D(ref pru, r, mesh, GetMaterial(), (Data.boundsMin + Data.boundsMax) * 0.5f,
                                         (Data.boundsMax - Data.boundsMin).magnitude * 0.5f, yaw, pitch, zoom);
                    if (t != null) GUI.DrawTexture(r, t, ScaleMode.StretchToFill, false);
                }
                GUI.Label(new Rect(r.x + 6f, r.y + 3f, r.width - 12f, 16f), "Drag to turn it, scroll to zoom. Roofs are hidden so you can see inside.", OverlayStyle);
            }
            else
            {
                DrawMap(r);
            }
            if (Busy && Data != null) GUI.Label(new Rect(r.xMax - 80f, r.y + 3f, 76f, 16f), "Updating...", OverlayStyle);

            EditorGUILayout.Space(2f);
            if (Data != null) EditorGUILayout.LabelField(Describe(Data), EditorStyles.miniLabel);
            if (Mode == 1) DrawLegend();
            EditorGUILayout.EndVertical();
        }

        void Handle3D(Rect r)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; e.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        yaw += e.delta.x * 0.45f;
                        pitch = Mathf.Clamp(pitch + e.delta.y * 0.45f, 3f, 89f);
                        e.Use();
                        if (repaint != null) repaint();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
                case EventType.ScrollWheel:
                    if (r.Contains(e.mousePosition))
                    {
                        zoom = Mathf.Clamp(zoom * (1f + e.delta.y * 0.04f), 0.15f, 3f);
                        e.Use();
                        if (repaint != null) repaint();
                    }
                    break;
            }
            EditorGUIUtility.AddCursorRect(r, MouseCursor.Orbit);
        }

        /// <summary>
        /// Renders the preview mesh into a render texture with a camera orbiting around target. Returns null if the
        /// render could not be made. Public so it can be tested without a window.
        /// </summary>
        public static Texture Render3D(ref PreviewRenderUtility utility, Rect r, Mesh previewMesh, Material mat, Vector3 target, float radius,
                                       float yaw, float pitch, float zoom)
        {
            if (previewMesh == null || mat == null || r.width < 8f || r.height < 8f) return null;
            if (utility == null)
            {
                utility = new PreviewRenderUtility();
                utility.cameraFieldOfView = 38f;
                utility.camera.nearClipPlane = 0.5f;
                utility.camera.farClipPlane = 2000f;
                utility.camera.clearFlags = CameraClearFlags.SolidColor;
                utility.camera.backgroundColor = new Color(0.11f, 0.12f, 0.15f);
            }
            utility.BeginPreview(r, GUIStyle.none);
            float dist = Mathf.Max(8f, radius) / Mathf.Tan(utility.cameraFieldOfView * 0.5f * Mathf.Deg2Rad) * 0.95f * zoom;
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            utility.camera.transform.position = target - rot * Vector3.forward * dist;
            utility.camera.transform.rotation = rot;
            utility.DrawMesh(previewMesh, Matrix4x4.identity, mat, 0);
            utility.Render(true, true);
            return utility.EndPreview();
        }

        // ---------------------------------------------------------------- map

        void DrawMap(Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            int w = Mathf.Clamp(Mathf.RoundToInt(r.width / 32f) * 32, 128, 1024);
            int h = Mathf.Clamp(Mathf.RoundToInt(r.height / 32f) * 32, 128, 1024);
            if (mapTexture == null || mapW != w || mapH != h)
            {
                if (mapTexture != null) UnityEngine.Object.DestroyImmediate(mapTexture);
                mapTexture = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                mapW = w; mapH = h;
                mapStale = true;
            }
            if (mapStale)
            {
                mapTexture.SetPixels32(CaveMapRenderer.Render(Data.layout, w, h, out mapYMin, out mapYMax));
                mapTexture.Apply(false);
                mapStale = false;
            }
            GUI.DrawTexture(r, mapTexture, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(r.x + 6f, r.y + 3f, r.width - 12f, 16f), "Seen from above. Grid lines are 20 m apart. Colour = floor height.", OverlayStyle);
        }

        void DrawLegend()
        {
            if (legend == null)
            {
                legend = new Texture2D(64, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                for (int i = 0; i < 64; i++) legend.SetPixel(i, 0, CaveMapRenderer.LegendColor(i / 63f));
                legend.Apply(false);
            }
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(string.Format("{0:0} m", mapYMin - Data.spawnFloor.y), EditorStyles.miniLabel, GUILayout.Width(44));
            Rect bar = GUILayoutUtility.GetRect(10f, 8f, GUILayout.ExpandWidth(true));
            bar.y += 3f;
            GUI.DrawTexture(bar, legend, ScaleMode.StretchToFill, false);
            GUILayout.Label(string.Format("{0:+0;-0;0} m", mapYMax - Data.spawnFloor.y), EditorStyles.miniLabel, GUILayout.Width(44));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Green dot = where you spawn. Red ring = pit that drops down. Blue ring = chimney that goes up. Heights are relative to the spawn floor.", OverlayStyle);
        }

        // ---------------------------------------------------------------- text

        public static string Describe(CavePreviewData d)
        {
            int[] kinds = new int[7];
            foreach (CaveRoom rm in d.layout.rooms) kinds[rm.kind]++;
            var sb = new StringBuilder();
            sb.Append(string.Format("{0:0} m of tunnel on {1} paths, {2} rooms", d.tunnelMeters, d.tunnels, d.rooms));
            var parts = new StringBuilder();
            Append(parts, kinds[(int)CaveRoomKind.Hall], "hall", "halls");
            Append(parts, kinds[(int)CaveRoomKind.Cathedral], "cathedral", "cathedrals");
            Append(parts, kinds[(int)CaveRoomKind.Cavern], "lumpy cavern", "lumpy caverns");
            Append(parts, d.shafts, "shaft", "shafts");
            Append(parts, d.loops, "loop", "loops");
            Append(parts, d.formations, "formation", "formations");
            if (parts.Length > 0) sb.Append(" (").Append(parts).Append(")");
            sb.Append(string.Format(". Height range {0:0} m.", d.yMax - d.yMin));
            sb.Append(string.Format("\nGenerate will make about {0:N0} triangles (voxel {1:0.00} m){2}. This preview is a rough model: the real cave is much more detailed.",
                RoundTo(d.estimatedTriangles, 100f), d.voxelSize, d.polyLimited ? ", more than Poly Count because it would fall apart below that" : ""));
            return sb.ToString();
        }

        static float RoundTo(float v, float step)
        {
            return Mathf.Round(v / step) * step;
        }

        static void Append(StringBuilder sb, int n, string one, string many)
        {
            if (n <= 0) return;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(n).Append(' ').Append(n == 1 ? one : many);
        }
    }
}
