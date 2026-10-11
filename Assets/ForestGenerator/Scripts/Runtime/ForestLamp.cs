using UnityEngine;

namespace HavocForest
{
    public enum ForestLampState
    {
        Working,
        Flickering,
        Dead
    }

    /// <summary>
    /// An old streetlight. It has no Light of its own: the Forest Lamp Manager lights the forest around the lamps nearest the
    /// camera (cheap per-vertex lamp light for the forest shaders, plus a small pool of real point lights for everything
    /// else) and drives the glass and halo brightness. Put it on your own streetlight prefab to make it a forest lamp.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Havoc/Forest Lamp")]
    public class ForestLamp : MonoBehaviour
    {
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
        static readonly int GlowId = Shader.PropertyToID("_GlowColor");

        public ForestLampState state = ForestLampState.Working;

        [Tooltip("Light colour. The generator sets warm or green from the Streetlights settings.")]
        public Color color = new Color(1f, 0.82f, 0.6f);

        [Tooltip("Brightness of the light it throws.")]
        [Min(0f)] public float intensity = 1.6f;

        [Tooltip("How far the light reaches, in metres.")]
        [Min(0.5f)] public float range = 11f;

        [Tooltip("Where the light comes from (the lamp head). Empty = 6 m above this object.")]
        public Transform lightPoint;

        [Tooltip("Renderers that glow with the lamp (the glass). Their material needs an _EmissionColor property.")]
        public Renderer[] glass = new Renderer[0];

        [Tooltip("The halo renderer (Havoc/Forest Lamp Glow).")]
        public Renderer halo;

        [Tooltip("How bright the glass glows compared with the light it throws.")]
        [Min(0f)] public float glassBrightness = 3f;

        [Tooltip("How fast it flickers when Flickering.")]
        [Range(0.2f, 4f)] public float flickerSpeed = 1f;

        /// <summary>0..1 how bright it is this frame (after flicker and dropouts), set by the manager.</summary>
        public float Level { get; private set; } = 1f;

        /// <summary>The colour and brightness of the light it throws this frame (linear).</summary>
        public Color CurrentLight { get; private set; }

        public Vector3 LightPosition
        {
            get { return lightPoint != null ? lightPoint.position : transform.position + transform.up * 6f; }
        }

        /// <summary>A stable random number per lamp, for flicker phase.</summary>
        public float Seed { get; private set; }

        MaterialPropertyBlock block;
        float shownLevel = -1f;
        Color shownColor;

        void OnEnable()
        {
            Vector3 p = transform.position;
            Seed = Mathf.Repeat(p.x * 0.731f + p.z * 1.379f, 97f);
            ForestLampManager.Register(this);
        }

        void OnDisable()
        {
            ForestLampManager.Unregister(this);
        }

        public void SetState(ForestLampState newState)
        {
            state = newState;
        }

        /// <summary>Called by the manager every frame with the lamp's brightness (0..1) and the atmosphere's lamp multiplier.</summary>
        public void Apply(float level, float brightness)
        {
            Level = level;
            CurrentLight = color.linear * (intensity * brightness * level);
            if (Mathf.Abs(level - shownLevel) < 0.01f && shownColor == color) return;    // nothing visible changed
            shownLevel = level;
            shownColor = color;
            if (block == null) block = new MaterialPropertyBlock();

            Color glow = color.linear * (glassBrightness * brightness * level);
            if (glass != null)
                foreach (Renderer r in glass)
                {
                    if (r == null) continue;
                    r.GetPropertyBlock(block);
                    block.SetColor(EmissionId, glow);
                    r.SetPropertyBlock(block);
                }
            if (halo != null)
            {
                halo.GetPropertyBlock(block);
                block.SetColor(GlowId, color.linear * (0.55f * brightness * level));
                halo.SetPropertyBlock(block);
                halo.enabled = level > 0.01f;
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = color;
            Gizmos.DrawWireSphere(LightPosition, range);
        }
    }
}
