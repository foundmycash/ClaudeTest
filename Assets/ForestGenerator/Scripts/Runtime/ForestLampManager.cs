using System.Collections.Generic;
using QuestOptimization;
using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Lights the forest from its streetlights without paying for many real-time lights:
    ///   - the nearest lit lamps (up to 8) are handed to the forest shaders as "lamp light", evaluated per vertex: soft pools
    ///     of light on the ground and on nearby trunks, no shadows, almost free;
    ///   - the very nearest one or two also get a pooled real point light (no shadows), so your own models (URP/Lit
    ///     materials) are lit near the player too;
    ///   - flicker, dropouts and dead lamps are driven here, and the atmosphere's lamp brightness is applied.
    /// For horror events: <see cref="FlickerAll"/>, <see cref="KillLights"/>, <see cref="SetAllStates"/>, <see cref="Nearest"/>.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Havoc/Forest Lamp Manager")]
    public class ForestLampManager : MonoBehaviour
    {
        const int MaxFake = 8;
        static readonly int LampPosId = Shader.PropertyToID("_ForestLampPos");
        static readonly int LampColorId = Shader.PropertyToID("_ForestLampColor");

        static readonly List<ForestLamp> lamps = new List<ForestLamp>();

        /// <summary>Every enabled lamp in the loaded scenes.</summary>
        public static IReadOnlyList<ForestLamp> Lamps { get { return lamps; } }

        public static ForestLampManager Active { get; private set; }

        [Tooltip("Real point lights that follow the nearest lamps (for materials that do not read the forest lamp light). 0 = none. Each costs per-pixel lighting on everything it touches.")]
        [Range(0, 4)] public int realtimeLights = 2;

        [Tooltip("Real point lights on the Quest 2 / Quest 1 (Low tier).")]
        [Range(0, 4)] public int realtimeLightsLowTier = 1;

        [Tooltip("Shadows from the real lights. Expensive on Quest; off by default.")]
        public bool realtimeShadows = false;

        [Tooltip("How many lamps (nearest first) light the forest shaders per vertex. 8 at most.")]
        [Range(0, MaxFake)] public int lampLightSlots = MaxFake;

        [Tooltip("The camera whose position decides which lamps are nearest. Empty = Camera.main (the Scene view camera in the editor).")]
        public Camera targetCamera;

        readonly Vector4[] lampPos = new Vector4[MaxFake];
        readonly Vector4[] lampColor = new Vector4[MaxFake];
        Light[] pool = new Light[0];
        float flickerAllUntil;
        float killedUntil;
        Vector3 killCenter;
        float killRadius;
        readonly List<int> order = new List<int>();
        readonly List<float> distances = new List<float>();
        readonly Dictionary<ForestLamp, float> dropoutUntil = new Dictionary<ForestLamp, float>();

        public static void Register(ForestLamp lamp)
        {
            if (!lamps.Contains(lamp)) lamps.Add(lamp);
        }

        public static void Unregister(ForestLamp lamp)
        {
            lamps.Remove(lamp);
        }

        void OnEnable()
        {
            Active = this;
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            foreach (Light l in pool) if (l != null) DestroyLight(l);
            pool = new Light[0];
            for (int i = 0; i < MaxFake; i++) lampColor[i] = Vector4.zero;
            Shader.SetGlobalVectorArray(LampColorId, lampColor);
        }

        // ---------------------------------------------------------------- events

        /// <summary>Every working lamp flickers for a while (something is near...).</summary>
        public void FlickerAll(float seconds)
        {
            flickerAllUntil = Mathf.Max(flickerAllUntil, Now + seconds);
        }

        /// <summary>Lamps within the radius go dark for a while. A radius of 0 or less = every lamp.</summary>
        public void KillLights(Vector3 center, float radius, float seconds)
        {
            killCenter = center;
            killRadius = radius;
            killedUntil = Now + seconds;
        }

        public static void SetAllStates(ForestLampState state)
        {
            foreach (ForestLamp l in lamps) l.state = state;
        }

        /// <summary>The nearest lamp to a position (any state), or null.</summary>
        public static ForestLamp Nearest(Vector3 position)
        {
            ForestLamp best = null;
            float bd = float.MaxValue;
            foreach (ForestLamp l in lamps)
            {
                float d = (l.LightPosition - position).sqrMagnitude;
                if (d < bd) { bd = d; best = l; }
            }
            return best;
        }

        static float Now
        {
            get { return Application.isPlaying ? Time.time : (float)(System.DateTime.Now.TimeOfDay.TotalSeconds % 10000.0); }
        }

        // ---------------------------------------------------------------- every frame

        void LateUpdate()
        {
            float now = Now;
            float brightness = ForestAtmosphere.LampBrightness;
            float dropouts = ForestAtmosphere.LampDropouts;
            float dt = Application.isPlaying ? Time.deltaTime : 0f;
            bool flickerAll = now < flickerAllUntil;
            bool killing = now < killedUntil;

            // ---- brightness of every lamp
            for (int i = 0; i < lamps.Count; i++)
            {
                ForestLamp lamp = lamps[i];
                float level = Level(lamp, now, flickerAll);
                if (dropouts > 0f && level > 0f && Application.isPlaying)
                {
                    float until;
                    if (dropoutUntil.TryGetValue(lamp, out until) && now < until) level = 0f;
                    else if (Random.value < dropouts * dt) dropoutUntil[lamp] = now + Random.Range(0.08f, 0.5f);
                }
                if (killing && (killRadius <= 0f || (lamp.LightPosition - killCenter).sqrMagnitude < killRadius * killRadius)) level = 0f;
                lamp.Apply(level, brightness);
            }

            // ---- nearest lit lamps first
            Vector3 cam = CameraPosition();
            order.Clear();
            distances.Clear();
            for (int i = 0; i < lamps.Count; i++)
            {
                if (lamps[i].Level <= 0.001f) continue;
                order.Add(i);
                distances.Add((lamps[i].LightPosition - cam).sqrMagnitude);
            }
            // insertion sort: only a handful of lamps, no allocations
            for (int i = 1; i < order.Count; i++)
            {
                int oi = order[i];
                float di = distances[i];
                int j = i - 1;
                while (j >= 0 && distances[j] > di)
                {
                    order[j + 1] = order[j];
                    distances[j + 1] = distances[j];
                    j--;
                }
                order[j + 1] = oi;
                distances[j + 1] = di;
            }

            // ---- the real lights take the nearest ones
            int real = Application.isPlaying ? (IsLowTier ? realtimeLightsLowTier : realtimeLights) : 0;
            EnsurePool(real);
            int used = 0;
            for (int p = 0; p < pool.Length; p++)
            {
                Light l = pool[p];
                if (p < order.Count && p < real)
                {
                    ForestLamp lamp = lamps[order[p]];
                    l.transform.position = lamp.LightPosition;
                    l.color = lamp.color;
                    l.intensity = lamp.intensity * brightness * lamp.Level;
                    l.range = lamp.range;
                    l.shadows = realtimeShadows ? LightShadows.Hard : LightShadows.None;
                    if (!l.enabled) l.enabled = true;
                    used++;
                }
                else if (l.enabled) l.enabled = false;
            }

            // ---- the rest are lamp light for the forest shaders
            int slots = Mathf.Min(MaxFake, lampLightSlots);
            int k = 0;
            for (int i = used; i < order.Count && k < slots; i++, k++)
            {
                ForestLamp lamp = lamps[order[i]];
                Vector3 p = lamp.LightPosition;
                lampPos[k] = new Vector4(p.x, p.y, p.z, 1f / (lamp.range * lamp.range));
                Color c = lamp.CurrentLight;
                lampColor[k] = new Vector4(c.r, c.g, c.b, 0f);
            }
            for (; k < MaxFake; k++) lampColor[k] = Vector4.zero;
            Shader.SetGlobalVectorArray(LampPosId, lampPos);
            Shader.SetGlobalVectorArray(LampColorId, lampColor);
        }

        float Level(ForestLamp lamp, float now, bool flickerAll)
        {
            ForestLampState st = lamp.state;
            if (st == ForestLampState.Dead) return 0f;
            if (st == ForestLampState.Working && !flickerAll)
                return 0.94f + 0.06f * Mathf.PerlinNoise(lamp.Seed, now * 2f);     // a faint hum
            // flickering: mostly on, with stutters and short blackouts
            float t = now * lamp.flickerSpeed;
            float slow = Mathf.PerlinNoise(lamp.Seed * 3.1f, t * 0.7f);
            float fast = Mathf.PerlinNoise(lamp.Seed * 7.7f + 13f, t * 9f);
            if (slow < 0.3f) return fast > 0.6f ? 0.5f : 0f;
            return fast < 0.2f ? 0.25f : 0.85f + 0.15f * fast;
        }

        Vector3 CameraPosition()
        {
            if (targetCamera != null) return targetCamera.transform.position;
#if UNITY_EDITOR
            if (!Application.isPlaying && UnityEditor.SceneView.lastActiveSceneView != null && UnityEditor.SceneView.lastActiveSceneView.camera != null)
                return UnityEditor.SceneView.lastActiveSceneView.camera.transform.position;
#endif
            Camera main = Camera.main;
            return main != null ? main.transform.position : transform.position;
        }

        static bool IsLowTier
        {
            get { return QuestDevice.IsHeadset && QuestDevice.Tier == PerformanceTier.Low; }
        }

        void EnsurePool(int count)
        {
            if (pool.Length == count) return;
            foreach (Light l in pool) if (l != null) DestroyLight(l);
            pool = new Light[count];
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Lamp Light " + i) { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);
                Light l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.ForcePixel;
                l.enabled = false;
                pool[i] = l;
            }
        }

        static void DestroyLight(Light l)
        {
            if (Application.isPlaying) Destroy(l.gameObject);
            else DestroyImmediate(l.gameObject);
        }
    }
}
