using UnityEngine;

namespace QuestOptimization
{
    /// <summary>Which headset the game runs on, as far as performance goes.</summary>
    public enum HeadsetClass
    {
        /// <summary>The editor, a PC build: the authored quality.</summary>
        Desktop,
        /// <summary>An Android headset that is not a Quest we know.</summary>
        Unknown,
        Quest1,
        Quest2,
        QuestPro,
        Quest3,
        Quest3S,
    }

    /// <summary>How much the headset can take. Decides MSAA, shadows, level of detail, the cave surface and so on (see QuestPerformance).</summary>
    public enum PerformanceTier
    {
        /// <summary>Quest 3 / 3S and PC: the authored quality.</summary>
        High,
        /// <summary>Quest Pro and headsets we do not know.</summary>
        Medium,
        /// <summary>Quest 2 and Quest 1: a Snapdragon XR2 Gen 1 GPU (Adreno 650) at the same time as a screen with more pixels than it can shade comfortably.</summary>
        Low,
    }

    /// <summary>
    /// Finds out which headset this is. Meta's own API is asked first (OVRPlugin); if that does not answer (it needs the Meta
    /// runtime to be up), the GPU name and the amount of memory tell: Adreno 650 is a Quest 2 (6 GB) or a Quest Pro (12 GB),
    /// Adreno 740 is a Quest 3 / 3S.
    /// </summary>
    public static class QuestDevice
    {
        static HeadsetClass? detected;
        static string description = "";

        public static HeadsetClass Class
        {
            get
            {
                if (!detected.HasValue)
                    detected = Detect();
                return detected.Value;
            }
        }

        /// <summary>A short line for logs and the on-headset panel: the model and how it was recognised.</summary>
        public static string Description
        {
            get
            {
                if (!detected.HasValue)
                    detected = Detect();
                return description;
            }
        }

        public static PerformanceTier Tier
        {
            get
            {
                switch (Class)
                {
                    case HeadsetClass.Quest1:
                    case HeadsetClass.Quest2:
                        return PerformanceTier.Low;
                    case HeadsetClass.QuestPro:
                    case HeadsetClass.Unknown:
                        return PerformanceTier.Medium;
                    default:
                        return PerformanceTier.High;
                }
            }
        }

        /// <summary>True on a standalone headset (anything that is not the editor or a PC build).</summary>
        public static bool IsHeadset => Class != HeadsetClass.Desktop;

        /// <summary>Pretend to be another headset (the editor and tests, a PC cannot be a Quest). Clears with <see cref="Reset"/>.</summary>
        public static void Override(HeadsetClass headset)
        {
            detected = headset;
            description = headset + " (forced)";
        }

        public static void Reset() => detected = null;

        static HeadsetClass Detect()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            HeadsetClass fromMeta = FromOvr();
            if (fromMeta != HeadsetClass.Unknown)
            {
                description = $"{fromMeta} (OVRPlugin), {SystemInfo.graphicsDeviceName}, {SystemInfo.systemMemorySize} MB";
                return fromMeta;
            }

            HeadsetClass fromGpu = FromGpu();
            description = $"{fromGpu} (from the GPU), {SystemInfo.graphicsDeviceName}, {SystemInfo.systemMemorySize} MB, model '{SystemInfo.deviceModel}'";
            return fromGpu;
#else
            description = "Desktop";
            return HeadsetClass.Desktop;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static HeadsetClass FromOvr()
        {
            try
            {
                switch (OVRPlugin.GetSystemHeadsetType())
                {
                    case OVRPlugin.SystemHeadset.Oculus_Quest: return HeadsetClass.Quest1;
                    case OVRPlugin.SystemHeadset.Oculus_Quest_2: return HeadsetClass.Quest2;
                    case OVRPlugin.SystemHeadset.Meta_Quest_Pro: return HeadsetClass.QuestPro;
                    case OVRPlugin.SystemHeadset.Meta_Quest_3: return HeadsetClass.Quest3;
                    case OVRPlugin.SystemHeadset.Meta_Quest_3S: return HeadsetClass.Quest3S;
                }
            }
            catch (System.Exception)
            {
                // the Meta plugin is not available: fall through to the GPU
            }
            return HeadsetClass.Unknown;
        }

        static HeadsetClass FromGpu()
        {
            string gpu = SystemInfo.graphicsDeviceName ?? "";
            if (!gpu.Contains("Adreno"))
                return HeadsetClass.Unknown;
            if (gpu.Contains("540")) return HeadsetClass.Quest1;
            if (gpu.Contains("650")) return SystemInfo.systemMemorySize > 9000 ? HeadsetClass.QuestPro : HeadsetClass.Quest2;
            if (gpu.Contains("740") || gpu.Contains("750") || gpu.Contains("8")) return HeadsetClass.Quest3;
            return HeadsetClass.Unknown;
        }
#endif
    }
}
