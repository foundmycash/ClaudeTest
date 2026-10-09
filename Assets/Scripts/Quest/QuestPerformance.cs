using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace QuestOptimization
{
    /// <summary>
    /// Turns on the Quest settings that Unity leaves off by default: fixed foveated rendering (the edges of each eye are
    /// shaded at a lower density) and a fixed display refresh rate. It starts itself on an Android (Quest) build, so no
    /// scene has to contain it. Change the two values below and rebuild to tune.
    ///
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

        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        bool applied;

#if UNITY_ANDROID && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            var host = new GameObject("Quest Performance") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            host.AddComponent<QuestPerformance>();
        }
#endif

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
            RequestRefreshRate();
            applied = true;

            display.TryGetDisplayRefreshRate(out float hz);
            Debug.Log($"[QuestPerformance] foveation {display.foveatedRenderingLevel:0.##}, display refresh {hz:0} Hz");
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

        static void RequestRefreshRate()
        {
            if (RefreshRate <= 0f)
                return;

            try
            {
                foreach (float available in OVRPlugin.systemDisplayFrequenciesAvailable)
                {
                    if (Mathf.Abs(available - RefreshRate) < 0.5f)
                    {
                        OVRPlugin.systemDisplayFrequency = available;
                        return;
                    }
                }
                Debug.LogWarning($"[QuestPerformance] This headset does not offer {RefreshRate:0} Hz; keeping the system default.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[QuestPerformance] Could not set the refresh rate: {e.Message}");
            }
        }
    }
}
