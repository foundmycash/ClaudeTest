using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace QuestOptimization
{
    /// <summary>
    /// Sets the Quest settings that Unity leaves off by default and fits the quality to the headset:
    ///   * fixed foveated rendering (the edges of each eye are shaded at a lower density) and a fixed display refresh rate,
    ///   * per headset (see <see cref="QuestDevice"/>): the Quest 2 gets 2x MSAA instead of 4x, no real-time sun shadows and a
    ///     slightly lower level of detail, and the cheaper cave surface (see <see cref="CaveDetail"/>).
    /// The Quest 3 keeps the authored quality. The Performance Lab (hold both grips and the left menu button) shows the frame
    /// times and changes all of this live, and remembers what you chose.
    ///
    /// It starts itself on an Android (Quest) build, so no scene has to contain it. The values below are the starting points.
    /// Check the result with `adb logcat -s Unity` (look for "[QuestPerformance]").
    /// </summary>
    public sealed class QuestPerformance : MonoBehaviour
    {
        /// <summary>Foveated rendering strength, 0 (off) to 1 (high). Set it to 0 if the edges of the view look wrong.</summary>
        public static readonly float FoveationLevel = 1f;

        /// <summary>
        /// Display refresh rate to request, in Hz (Quest offers 72, 80, 90 and 120 depending on the model). 0 keeps the
        /// system default, which the player can raise to 90 or 120 in the headset settings.
        /// </summary>
        public static readonly float RefreshRate = 72f;

        /// <summary>The Quest 2's MSAA: Unity's advice for tile based headsets is 2x (4x doubles the multisample memory traffic).</summary>
        public static readonly int LowTierMsaa = 2;

        /// <summary>
        /// Real-time shadow distance on the Quest 2 (0 = no shadows). The shadow pass draws the scene a second time and, with a
        /// distance of 0, URP finds nothing to cast and skips it. The cave is lit by its lamps and has no use for sun shadows.
        /// </summary>
        public static readonly float LowTierShadowDistance = 0f;

        /// <summary>Quality Settings LOD bias on the Quest 2 (1 = authored, lower = lower detail objects switch in sooner).</summary>
        public static readonly float LowTierLodBias = 0.75f;

        public static QuestPerformance Instance { get; private set; }

        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        bool applied;

#if (UNITY_ANDROID && !UNITY_EDITOR)
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            var host = new GameObject("Quest Performance") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            host.AddComponent<QuestPerformance>();
        }
#endif

        /// <summary>Starts it by hand (the editor, tests).</summary>
        public static QuestPerformance Ensure()
        {
            if (Instance != null)
                return Instance;
            var host = new GameObject("Quest Performance") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            return host.AddComponent<QuestPerformance>();
        }

        void Awake()
        {
            Instance = this;
            Debug.Log($"[QuestPerformance] {QuestDevice.Description} -> performance tier {QuestDevice.Tier}");
            ApplyTierSettings();
            PerformanceLab.Create();
        }

        IEnumerator Start()
        {
            // The XR display normally runs before the first scene loads; give it a moment in case it does not.
            for (float waited = 0f; !TryGetDisplay(out _); waited += Time.unscaledDeltaTime)
            {
                if (waited > 15f)
                    yield break;
                yield return null;
            }

            Apply();
        }

        // The refresh rate request can be dropped while the app is in the background, so ask again on coming back.
        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && applied)
                Apply();
        }

        void Apply()
        {
            if (!TryGetDisplay(out XRDisplaySubsystem display))
                return;

            display.foveatedRenderingLevel = FoveationLevel;
            RequestRefreshRate(RefreshRate);
            applied = true;

            // (The physics step is deliberately left alone: the Gorilla locomotion moves the rig through its transform every frame and the
            // rig's Rigidbody interpolates, so matching the step to the refresh rate made the movement sticky.)
            display.TryGetDisplayRefreshRate(out float hz);
            Debug.Log($"[QuestPerformance] foveation {display.foveatedRenderingLevel:0.##}, display refresh {hz:0} Hz, physics step {Time.fixedDeltaTime * 1000f:0.0} ms");
            PerformanceLab.ApplySavedChoices();
        }

        /// <summary>The per headset quality.</summary>
        public static void ApplyTierSettings()
        {
            if (QuestDevice.Tier != PerformanceTier.Low)
                return;

            CaveDetail.Set(true);

            QualitySettings.lodBias = Mathf.Min(QualitySettings.lodBias, LowTierLodBias);

            // These are asset values: in the editor they would change the asset on disk, so only a build does it.
            if (Application.isEditor)
                return;
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
                return;
            pipeline.msaaSampleCount = LowTierMsaa;
            pipeline.shadowDistance = LowTierShadowDistance;
        }

        bool TryGetDisplay(out XRDisplaySubsystem display)
        {
            SubsystemManager.GetSubsystems(displays);
            foreach (XRDisplaySubsystem candidate in displays)
            {
                if (candidate.running)
                {
                    display = candidate;
                    return true;
                }
            }
            display = null;
            return false;
        }

        /// <summary>The running XR display, or null.</summary>
        public static XRDisplaySubsystem GetRunningDisplay()
        {
            var list = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(list);
            foreach (XRDisplaySubsystem candidate in list)
            {
                if (candidate.running)
                    return candidate;
            }
            return null;
        }

        /// <summary>The refresh rates this headset offers (empty when that cannot be asked).</summary>
        public static float[] AvailableRefreshRates()
        {
            try
            {
                return OVRPlugin.systemDisplayFrequenciesAvailable;
            }
            catch (System.Exception)
            {
                return new float[0];
            }
        }

        public static void RequestRefreshRate(float hz)
        {
            if (hz <= 0f)
                return;

            try
            {
                foreach (float available in OVRPlugin.systemDisplayFrequenciesAvailable)
                {
                    if (Mathf.Abs(available - hz) < 0.5f)
                    {
                        OVRPlugin.systemDisplayFrequency = available;
                        return;
                    }
                }
                Debug.LogWarning($"[QuestPerformance] This headset does not offer {hz:0} Hz; keeping the system default.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[QuestPerformance] Could not set the refresh rate: {e.Message}");
            }
        }
    }
}
