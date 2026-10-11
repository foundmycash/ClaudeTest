using UnityEngine;

namespace HavocCave
{
    /// <summary>
    /// Put this next to a Light (point, spot or directional) and the cave lightmapper bakes that light into the cave the next
    /// time a cave is generated. It works on lights inside your own prop prefabs too, because props are spawned before the bake.
    /// By default the Light is switched off when the game starts, so only the baked light shows (see Baked Only).
    /// </summary>
    [AddComponentMenu("Havoc/Cave Baked Light")]
    [RequireComponent(typeof(Light))]
    [DisallowMultipleComponent]
    public class CaveBakedLight : MonoBehaviour
    {
        [Tooltip("On: the Light component is switched off when the game starts, so this light only exists as baked light and costs nothing at runtime. " +
                 "The bake still reads its colour, intensity, range and angle. Off: it also stays a real-time light on top of the baked one.")]
        public bool bakedOnly = true;

        [Tooltip("Size of the light source in metres. Bigger = softer shadows. Spot and point lights only.")]
        [Min(0f)] public float radius = 0.1f;

        [Tooltip("On: the light fades with the square of the distance, like a real lamp. Off: a flat fade across the whole range, which lights everything more evenly.")]
        public bool realisticFalloff = true;

        [Tooltip("Multiplies the light's intensity for the bake only.")]
        [Min(0f)] public float bakeIntensityScale = 1f;

        [Tooltip("The bake can leave this light out without removing the component.")]
        public bool includeInBake = true;

        void Awake()
        {
            // Only in play mode: in the editor the Light stays on so you can still see and tune it in the Scene view.
            if (!Application.isPlaying || !bakedOnly) return;
            Light l = GetComponent<Light>();
            if (l != null) l.enabled = false;
        }
    }
}
