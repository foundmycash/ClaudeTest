using UnityEngine;

namespace QuestOptimization
{
    /// <summary>
    /// Switches the cave surface (Havoc/Cave Blend) between its full look and a cheaper one for the Quest 2:
    /// the lite surface projects the rock and dirt textures from two sides instead of three and leaves out the normal maps
    /// (4 texture reads per pixel instead of 6, or 12 with normal maps). The lamp light, the baked lightmap and the blend
    /// between rock and dirt are the same. The keyword is global, so it covers every cave material without touching them.
    /// </summary>
    public static class CaveDetail
    {
        const string Keyword = "_CAVE_LITE";

        public static bool Lite { get; private set; }

        public static void Set(bool lite)
        {
            Lite = lite;
            if (lite)
                Shader.EnableKeyword(Keyword);
            else
                Shader.DisableKeyword(Keyword);
        }

        /// <summary>What this headset's tier calls for.</summary>
        public static bool TierDefault => QuestDevice.IsHeadset && QuestDevice.Tier == PerformanceTier.Low;
    }
}
