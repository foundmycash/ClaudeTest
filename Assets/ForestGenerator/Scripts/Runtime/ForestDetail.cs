using QuestOptimization;
using UnityEngine;

namespace HavocForest
{
    public enum ForestDetailMode
    {
        [Tooltip("Lite on Quest 2 / Quest 1 (Low tier), Full everywhere else.")] Auto,
        Full,
        Lite
    }

    /// <summary>
    /// Switches the forest shaders between their full look and a cheaper one for the Quest 2 (the same idea as CaveDetail):
    /// the lite ground blends the 2 strongest surfaces per pixel instead of 3, skips the normal maps and the distant
    /// anti-tiling sample. The keyword is global, so it covers every forest material without touching them.
    /// </summary>
    public static class ForestDetail
    {
        public const string Keyword = "_FOREST_LITE";

        public static bool Lite { get; private set; }

        public static void Set(bool lite)
        {
            Lite = lite;
            if (lite) Shader.EnableKeyword(Keyword);
            else Shader.DisableKeyword(Keyword);
        }

        public static void Apply(ForestDetailMode mode)
        {
            switch (mode)
            {
                case ForestDetailMode.Full: Set(false); break;
                case ForestDetailMode.Lite: Set(true); break;
                default: Set(TierDefault); break;
            }
        }

        /// <summary>What this headset's tier calls for.</summary>
        public static bool TierDefault
        {
            get { return QuestDevice.IsHeadset && QuestDevice.Tier == PerformanceTier.Low; }
        }
    }
}
