using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace QuestOptimization
{
    /// <summary>
    /// A panel in the headset that shows how the frame is doing and lets you change what costs most while you look at it:
    /// resolution, MSAA, foveation, the cave surface, shadows, refresh rate. What you pick is saved on
    /// the headset and applied again the next time the game starts (X on the left controller undoes all of it).
    ///
    ///   Open / close   hold BOTH GRIPS and the LEFT MENU button for 1.5 seconds
    ///   Move           right thumbstick up / down picks a line, left / right changes it
    ///   Reset a line   A button (right controller)        Reset everything   X button (left controller)
    ///
    /// The frame times are Unity's FrameTimingManager: CPU is the whole main + render thread time of a frame, GPU the time the GPU
    /// took (the "GPU" line says n/a when the headset's GPU does not report it). A frame is "late" when it took more than 1.2
    /// refresh intervals. Nothing runs while the panel is closed, apart from checking the three buttons ten times a second.
    ///
    /// In the editor: F10 opens it, the arrow keys move.
    /// </summary>
    public sealed class PerformanceLab : MonoBehaviour
    {
        const string PrefsPrefix = "QuestPerf.";
        const float HoldSeconds = 1.5f;
        const float PanelDistance = 1.25f;

        public static PerformanceLab Instance { get; private set; }

        public static void Create()
        {
            if (Instance != null)
                return;
            var go = new GameObject("Performance Lab") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<PerformanceLab>();
        }

        // ---- the lines of the panel ----

        sealed class Row
        {
            public string name;
            public string key;                       // PlayerPrefs key (without prefix)
            public Func<string> read;                // what it is now
            public Action<int> change;               // -1 / +1
            public Action reset;                     // back to what the headset's tier says
            public Func<bool> available = () => true;
        }

        readonly List<Row> rows = new List<Row>();
        int selected;
        bool open;

        TextMeshPro label;
        readonly StringBuilder text = new StringBuilder(1024);
        float holdTime;
        float nextPoll;
        float nextInput;
        float nextText;
        bool inputWasNeutral = true;
        Transform headTransform;

        // frame statistics
        FrameTiming[] timings = new FrameTiming[1];
        double cpuMs, gpuMs;
        float frameSum, frameMax;
        int frameCount, lateFrames, lateFramesWindow;
        float windowStart;
        float fps, worstFrameMs, lateRate;
        long gcBytesAtWindowStart;
        float gcKbPerSecond;

        void Awake()
        {
            BuildRows();
            windowStart = Time.unscaledTime;
            gcBytesAtWindowStart = GC.GetTotalMemory(false);
        }

        static UniversalRenderPipelineAsset Pipeline => GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

        void BuildRows()
        {
            rows.Add(new Row
            {
                name = "Resolution scale", key = "RenderScale",
                read = () => Pipeline != null ? $"{Pipeline.renderScale:0.00}  ({EyeSize()})" : "n/a",
                change = d => { if (Pipeline != null) Pipeline.renderScale = Mathf.Clamp(Mathf.Round((Pipeline.renderScale + d * 0.05f) * 20f) / 20f, 0.5f, 1.3f); },
                reset = () => { if (Pipeline != null) Pipeline.renderScale = defaultRenderScale; },
            });
            rows.Add(new Row
            {
                name = "MSAA", key = "Msaa",
                read = () => Pipeline != null ? $"{Pipeline.msaaSampleCount}x" : "n/a",
                change = d => { if (Pipeline != null) Pipeline.msaaSampleCount = Mathf.Clamp(d > 0 ? Pipeline.msaaSampleCount * 2 : Pipeline.msaaSampleCount / 2, 1, 4); },
                reset = () => { if (Pipeline != null) Pipeline.msaaSampleCount = defaultMsaa; },
            });
            rows.Add(new Row
            {
                name = "Foveation (edges shaded coarser)", key = "Foveation",
                read = () => { var dsp = QuestPerformance.GetRunningDisplay(); return dsp != null ? $"{dsp.foveatedRenderingLevel:0.00}" : "n/a"; },
                change = d => { var dsp = QuestPerformance.GetRunningDisplay(); if (dsp != null) dsp.foveatedRenderingLevel = Mathf.Clamp01(Mathf.Round((dsp.foveatedRenderingLevel + d * 0.25f) * 4f) / 4f); },
                reset = () => { var dsp = QuestPerformance.GetRunningDisplay(); if (dsp != null) dsp.foveatedRenderingLevel = QuestPerformance.FoveationLevel; },
            });
            rows.Add(new Row
            {
                name = "Cave surface", key = "CaveDetail",
                read = () => CaveDetailText(),
                change = d => CycleCaveDetail(d),
                reset = () => SetCaveDetail(-1),
            });
            rows.Add(new Row
            {
                name = "Sun shadows", key = "Shadows",
                read = () => Pipeline != null ? (Pipeline.shadowDistance > 0f ? "on" : "off") : "n/a",
                change = d => { if (Pipeline != null) Pipeline.shadowDistance = Pipeline.shadowDistance > 0f ? 0f : (defaultShadowDistance > 0f ? defaultShadowDistance : 50f); },
                reset = () => { if (Pipeline != null) Pipeline.shadowDistance = defaultShadowDistance; },
            });
            rows.Add(new Row
            {
                name = "Refresh rate (Hz)", key = "Refresh",
                read = () => { var dsp = QuestPerformance.GetRunningDisplay(); return dsp != null && dsp.TryGetDisplayRefreshRate(out float hz) ? $"{hz:0}" : "n/a"; },
                change = d => ChangeRefresh(d),
                reset = () => QuestPerformance.RequestRefreshRate(QuestPerformance.RefreshRate),
            });
            rows.Add(new Row
            {
                name = "Anisotropic filtering", key = "Aniso",
                read = () => QualitySettings.anisotropicFiltering.ToString(),
                change = d => QualitySettings.anisotropicFiltering = QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable ? AnisotropicFiltering.Enable : AnisotropicFiltering.Disable,
                reset = () => QualitySettings.anisotropicFiltering = defaultAniso,
            });
        }

        // what the tier set, so "reset" can go back
        float defaultRenderScale;
        int defaultMsaa;
        float defaultShadowDistance;
        AnisotropicFiltering defaultAniso;

        void Start()
        {
            // Called after QuestPerformance.Awake has applied the tier: these are the defaults of this headset.
            var pipeline = Pipeline;
            defaultRenderScale = pipeline != null ? pipeline.renderScale : 1f;
            defaultMsaa = pipeline != null ? pipeline.msaaSampleCount : 1;
            defaultShadowDistance = pipeline != null ? pipeline.shadowDistance : 0f;
            defaultAniso = QualitySettings.anisotropicFiltering;
        }

        // ---- cave surface line ----

        // -1 = follow the tier, 0 = full, 1 = lite
        int caveDetailChoice = -1;

        string CaveDetailText()
        {
            return (CaveDetail.Lite ? "lite (2 projections, no normal maps)" : "full") + (caveDetailChoice < 0 ? "  [auto]" : "");
        }

        void CycleCaveDetail(int d)
        {
            int next = caveDetailChoice + d;
            if (next < -1) next = 1;
            if (next > 1) next = -1;
            SetCaveDetail(next);
        }

        void SetCaveDetail(int choice)
        {
            caveDetailChoice = choice;
            CaveDetail.Set(choice < 0 ? CaveDetail.TierDefault : choice == 1);
        }

        void ChangeRefresh(int d)
        {
            float[] rates = QuestPerformance.AvailableRefreshRates();
            if (rates.Length == 0)
                return;
            var dsp = QuestPerformance.GetRunningDisplay();
            float current = 0f;
            dsp?.TryGetDisplayRefreshRate(out current);
            Array.Sort(rates);
            int index = 0;
            for (int i = 0; i < rates.Length; i++)
                if (Mathf.Abs(rates[i] - current) < 0.5f) index = i;
            index = Mathf.Clamp(index + d, 0, rates.Length - 1);
            QuestPerformance.RequestRefreshRate(rates[index]);
        }

        static string EyeSize()
        {
            return $"{XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} per eye";
        }

        // ---- saved choices ----

        void SaveRow(Row row)
        {
            PlayerPrefs.SetString(PrefsPrefix + row.key, row.read());
            PlayerPrefs.SetInt(PrefsPrefix + "Any", 1);
            PlayerPrefs.Save();
        }

        /// <summary>Puts back what was chosen in the panel on an earlier run. Called by QuestPerformance once the XR display is up.</summary>
        public static void ApplySavedChoices()
        {
            if (Instance == null || PlayerPrefs.GetInt(PrefsPrefix + "Any", 0) == 0)
                return;
            Instance.RestoreSaved();
        }

        void RestoreSaved()
        {
            var pipeline = Pipeline;
            if (pipeline != null)
            {
                if (TryRead("RenderScale", out string scale) && float.TryParse(FirstWord(scale), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s))
                    pipeline.renderScale = s;
                if (TryRead("Msaa", out string msaa) && int.TryParse(msaa.TrimEnd('x'), out int m))
                    pipeline.msaaSampleCount = m;
                if (TryRead("Shadows", out string shadows))
                    pipeline.shadowDistance = shadows == "on" ? (defaultShadowDistance > 0f ? defaultShadowDistance : 50f) : 0f;
            }
            var display = QuestPerformance.GetRunningDisplay();
            if (display != null && TryRead("Foveation", out string fov) && float.TryParse(fov, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                display.foveatedRenderingLevel = f;
            if (TryRead("Refresh", out string hz) && float.TryParse(hz, out float rate))
                QuestPerformance.RequestRefreshRate(rate);
            if (TryRead("Aniso", out string aniso) && Enum.TryParse(aniso, out AnisotropicFiltering a))
                QualitySettings.anisotropicFiltering = a;
            if (TryRead("CaveDetail", out string cave))
                SetCaveDetail(cave.EndsWith("[auto]") ? -1 : cave.StartsWith("lite") ? 1 : 0);
            Debug.Log("[PerformanceLab] applied the settings you saved on this headset (X on the left controller in the panel resets them).");
        }

        static bool TryRead(string key, out string value)
        {
            value = PlayerPrefs.GetString(PrefsPrefix + key, "");
            return value.Length > 0;
        }

        static string FirstWord(string s)
        {
            int space = s.IndexOf(' ');
            return space < 0 ? s : s.Substring(0, space);
        }

        void ResetAll()
        {
            foreach (Row row in rows)
                row.reset();
            caveDetailChoice = -1;
            foreach (Row row in rows)
                PlayerPrefs.DeleteKey(PrefsPrefix + row.key);
            PlayerPrefs.DeleteKey(PrefsPrefix + "Any");
            PlayerPrefs.Save();
        }

        // ---- opening, input ----

        static XRNode Hand(bool left) => left ? XRNode.LeftHand : XRNode.RightHand;

        static bool Button(bool left, InputFeatureUsage<bool> usage)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(Hand(left));
            return device.isValid && device.TryGetFeatureValue(usage, out bool value) && value;
        }

        static Vector2 Stick(bool left)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(Hand(left));
            return device.isValid && device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 value) ? value : Vector2.zero;
        }

        void Update()
        {
            if (!open)
            {
                if (Time.unscaledTime >= nextPoll)
                {
                    nextPoll = Time.unscaledTime + 0.1f;
                    bool combo = Button(true, CommonUsages.menuButton) && Button(true, CommonUsages.gripButton) && Button(false, CommonUsages.gripButton);
#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
                    combo |= UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.f10Key.isPressed;
#endif
                    holdTime = combo ? holdTime + 0.1f : 0f;
                    if (holdTime >= HoldSeconds)
                        SetOpen(true);
                }
                return;
            }

            UpdateStats();
            HandleInput();
            if (Time.unscaledTime >= nextText)
            {
                nextText = Time.unscaledTime + 0.25f;
                RefreshText();
            }
            Follow();
        }

        public bool IsOpen => open;
        public void Open() => SetOpen(true);
        public void Close() => SetOpen(false);
        /// <summary>The text of the panel, for tests.</summary>
        public string PanelText { get { RefreshText(); return label != null ? label.text : ""; } }

        void SetOpen(bool value)
        {
            open = value;
            holdTime = 0f;
            if (value)
            {
                EnsureLabel();
                label.gameObject.SetActive(true);
                PlaceInFront();
                windowStart = Time.unscaledTime;
                frameSum = frameMax = 0f; frameCount = lateFrames = 0;
                RefreshText();
            }
            else if (label != null)
            {
                label.gameObject.SetActive(false);
            }
        }

        void HandleInput()
        {
            Vector2 stick = Stick(false);
            bool toggleClose = Button(true, CommonUsages.menuButton) && Button(true, CommonUsages.gripButton) && Button(false, CommonUsages.gripButton);
            bool resetAll = Button(true, CommonUsages.primaryButton);
            bool resetRow = Button(false, CommonUsages.primaryButton);
            bool left = false, right = false, up = false, down = false;

#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                up |= kb.upArrowKey.isPressed; down |= kb.downArrowKey.isPressed;
                left |= kb.leftArrowKey.isPressed; right |= kb.rightArrowKey.isPressed;
                resetRow |= kb.rKey.isPressed; resetAll |= kb.tKey.isPressed;
                toggleClose |= kb.f11Key.isPressed;
            }
#endif
            up |= stick.y > 0.6f; down |= stick.y < -0.6f;
            right |= stick.x > 0.6f; left |= stick.x < -0.6f;

            if (toggleClose)
            {
                holdTime += Time.unscaledDeltaTime;
                if (holdTime >= HoldSeconds)
                    SetOpen(false);
                return;
            }
            holdTime = 0f;

            bool any = up || down || left || right || resetAll || resetRow;
            if (!any) { inputWasNeutral = true; return; }
            // first press at once, then repeat slowly
            if (!inputWasNeutral && Time.unscaledTime < nextInput) return;
            nextInput = Time.unscaledTime + (inputWasNeutral ? 0.45f : 0.18f);
            inputWasNeutral = false;

            if (resetAll) { ResetAll(); RefreshText(); return; }
            Row row = rows[selected];
            if (up) selected = NextAvailable(-1);
            else if (down) selected = NextAvailable(+1);
            else if (resetRow) { row.reset(); SaveRow(row); }
            else if (left || right)
            {
                if (row.available()) { row.change(right ? 1 : -1); SaveRow(row); }
            }
            RefreshText();
        }

        int NextAvailable(int direction)
        {
            int index = selected;
            for (int i = 0; i < rows.Count; i++)
            {
                index = (index + direction + rows.Count) % rows.Count;
                if (rows[index].available())
                    return index;
            }
            return selected;
        }

        // ---- statistics ----

        void UpdateStats()
        {
            float dt = Time.unscaledDeltaTime;
            frameSum += dt;
            frameMax = Mathf.Max(frameMax, dt);
            frameCount++;

            float refresh = 72f;
            var display = QuestPerformance.GetRunningDisplay();
            if (display != null && display.TryGetDisplayRefreshRate(out float hz) && hz > 1f)
                refresh = hz;
            if (dt > 1.2f / refresh)
                lateFrames++;

            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
            {
                cpuMs = timings[0].cpuFrameTime;
                gpuMs = timings[0].gpuFrameTime;
            }

            float window = Time.unscaledTime - windowStart;
            if (window >= 1f)
            {
                fps = frameCount / window;
                worstFrameMs = frameMax * 1000f;
                lateRate = lateFrames / window;
                long gc = GC.GetTotalMemory(false);
                gcKbPerSecond = Mathf.Max(0f, (gc - gcBytesAtWindowStart) / 1024f / window);
                gcBytesAtWindowStart = gc;
                windowStart = Time.unscaledTime;
                frameSum = frameMax = 0f; frameCount = lateFrames = 0;
            }
        }

        void RefreshText()
        {
            text.Clear();
            text.Append("<b>PERFORMANCE LAB</b>   ").Append(QuestDevice.Class).Append("  tier ").Append(QuestDevice.Tier).Append('\n');
            text.Append("fps ").Append(fps.ToString("0.0")).Append("   worst frame ").Append(worstFrameMs.ToString("0.0")).Append(" ms   late frames/s ").Append(lateRate.ToString("0.0")).Append('\n');
            text.Append("CPU ").Append(cpuMs > 0 ? cpuMs.ToString("0.0") + " ms" : "n/a")
                .Append("   GPU ").Append(gpuMs > 0 ? gpuMs.ToString("0.0") + " ms" : "n/a")
                .Append("   garbage ").Append(gcKbPerSecond.ToString("0")).Append(" KB/s\n");
            text.Append('\n');
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                if (!row.available()) continue;
                text.Append(i == selected ? "<color=#ffd24a>> " : "   ").Append(row.name).Append(":  ").Append(row.read()).Append(i == selected ? "</color>" : "").Append('\n');
            }
            text.Append("\nstick: move / change    A: reset line    X: reset all    hold grips + menu: close");
            label.text = text.ToString();
        }

        // ---- the panel itself ----

        void EnsureLabel()
        {
            if (label != null)
                return;
            var go = new GameObject("Performance Lab Panel") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            label = go.AddComponent<TextMeshPro>();
            label.alignment = TextAlignmentOptions.TopLeft;
            label.fontSize = 0.34f;
            label.color = Color.white;
            label.richText = true;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.raycastTarget = false;
            label.rectTransform.sizeDelta = new Vector2(1.5f, 0.9f);
            label.outlineWidth = 0.2f;
            label.outlineColor = new Color32(0, 0, 0, 255);
            Material material = label.fontMaterial;
            material.SetFloat(Shader.PropertyToID("unity_GUIZTestMode"), (float)CompareFunction.Always);
            material.renderQueue = (int)RenderQueue.Overlay;
        }

        void PlaceInFront()
        {
            Camera cam = Camera.main;
            if (cam == null || label == null)
                return;
            headTransform = cam.transform;
            Vector3 flat = Vector3.ProjectOnPlane(headTransform.forward, Vector3.up);
            if (flat.sqrMagnitude < 0.01f) flat = Vector3.forward;
            flat.Normalize();
            Vector3 pos = headTransform.position + flat * PanelDistance + Vector3.up * -0.05f;
            label.transform.position = pos;
            label.transform.rotation = Quaternion.LookRotation(pos - headTransform.position, Vector3.up);
        }

        // the panel stays where it was put; if the player walks off or turns away it comes back in front
        void Follow()
        {
            Camera cam = Camera.main;
            if (cam == null || label == null)
                return;
            Vector3 toLabel = label.transform.position - cam.transform.position;
            float distance = new Vector2(toLabel.x, toLabel.z).magnitude;
            float angle = Vector3.Angle(Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up), Vector3.ProjectOnPlane(toLabel, Vector3.up));
            if (distance > 3f || angle > 100f)
                PlaceInFront();
        }
    }
}
