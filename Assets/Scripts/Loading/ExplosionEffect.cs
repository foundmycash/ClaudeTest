using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoadingScreen
{
    /// <summary>
    /// The dynamite's explosion: a flash, a light that lights up the room and the fog, a fireball that bursts open and
    /// then surges toward the player until the camera is inside it, plus a few smoke puffs, sparks and chunks of debris.
    ///
    /// Cost is kept low on purpose: the fireball is one sphere mesh shaded by Loading/Fireball (no particles), and the
    /// particle systems emit a few dozen particles in total, once. Everything is switched off with <see cref="Stop"/>
    /// as soon as the full-screen fire hides it.
    ///
    /// <see cref="EngulfAmount"/> tells how close the fireball's surface is to the viewer (0 = far, 1 = the camera is
    /// inside); LoadingSequence uses it to bring in the full-screen fire at exactly the right moment.
    /// </summary>
    [DisallowMultipleComponent]
    public class ExplosionEffect : MonoBehaviour
    {
        const float MaxAnimationStep = 1f / 30f;

        [Header("Parts")]
        [Tooltip("Sphere (radius 0.5) with a Loading/Fireball material.")]
        public Renderer fireball;
        public Light blastLight;
        public ParticleSystem flash;
        public ParticleSystem smoke;
        public ParticleSystem sparks;
        public ParticleSystem debris;

        [Header("Fireball")]
        [Tooltip("Radius of the first burst, reached within a few hundredths of a second (metres).")]
        [Min(0.1f)] public float burstRadius = 0.85f;

        [Tooltip("Seconds from the detonation until the fireball's surface reaches the viewer.")]
        [Min(0.2f)] public float timeToEngulf = 0.62f;

        [Tooltip("How far the centre of the fireball travels toward the viewer while it surges (0 = stays at the wall).")]
        [Range(0f, 0.8f)] public float centreTravel = 0.35f;

        [Tooltip("The full-screen fire starts creeping in when the fireball's surface is this close (metres).")]
        [Min(0.1f)] public float engulfStartDistance = 1.3f;

        [Header("Light")]
        [Min(0f)] public float lightPeakIntensity = 16f;
        [Min(0f)] public float lightRange = 14f;
        public Color lightFlashColor = new Color(1f, 0.92f, 0.75f);
        public Color lightFireColor = new Color(1f, 0.5f, 0.18f);

        /// <summary>0 while the fireball is far away, 1 once the viewer is inside it.</summary>
        public float EngulfAmount { get; private set; }

        /// <summary>How strongly the approaching fire lights up the view (for the full-screen glow).</summary>
        public float GlowAmount { get; private set; }

        /// <summary>True once the viewer is inside the fireball (or it should have been by now).</summary>
        public bool Engulfed { get; private set; }

        static readonly int AgeId = Shader.PropertyToID("_Age");
        static readonly int HeatId = Shader.PropertyToID("_Heat");
        static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        static readonly int CullId = Shader.PropertyToID("_Cull");

        // The vertex shader pushes the surface in by up to this fraction of the radius (_Displacement / 2 + margin).
        const float SurfaceInset = 0.82f;

        Material fireballMaterial;
        Coroutine running;

        void Awake()
        {
            if (fireball != null)
            {
                fireballMaterial = new Material(fireball.sharedMaterial) { name = fireball.sharedMaterial.name + " (runtime)" };
                fireball.sharedMaterial = fireballMaterial;
                fireball.shadowCastingMode = ShadowCastingMode.Off;
                fireball.receiveShadows = false;
            }
        }

        void OnDestroy()
        {
            if (fireballMaterial != null)
                Destroy(fireballMaterial);
        }

        /// <summary>
        /// Shows every part once, for two frames, at a spot the camera looks at but cannot see (behind the wall), so
        /// shaders and particle buffers are ready before the real explosion. Lights stay off.
        /// </summary>
        public IEnumerator Prewarm(Vector3 hiddenPosition)
        {
            gameObject.SetActive(true);
            transform.position = hiddenPosition;
            if (blastLight != null) blastLight.enabled = false;
            SetFireball(hiddenPosition, 0.5f, 0f, 1f);

            foreach (ParticleSystem ps in Systems())
                ps.Play(true);

            yield return null;
            yield return null;

            foreach (ParticleSystem ps in Systems())
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            gameObject.SetActive(false);
        }

        /// <summary>Blows up at <paramref name="position"/>. <paramref name="outward"/> points away from the wall.</summary>
        public void Detonate(Vector3 position, Vector3 outward, Transform viewer)
        {
            gameObject.SetActive(true);
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(outward));
            EngulfAmount = 0f;
            GlowAmount = 0f;
            Engulfed = false;

            foreach (ParticleSystem ps in Systems())
            {
                ps.Clear(true);
                ps.Play(true);
            }

            if (running != null)
                StopCoroutine(running);
            running = StartCoroutine(Animate(viewer));
        }

        /// <summary>Switches everything off (used once the full-screen fire hides the explosion).</summary>
        public void Stop()
        {
            if (running != null)
                StopCoroutine(running);
            running = null;
            gameObject.SetActive(false);
        }

        IEnumerator Animate(Transform viewer)
        {
            Vector3 origin = transform.position;
            if (blastLight != null)
            {
                blastLight.enabled = true;
                blastLight.intensity = 0f;
            }

            float t = 0f;
            while (true)
            {
                t += Mathf.Min(Time.deltaTime, MaxAnimationStep);
                Vector3 head = viewer.position;

                // Burst: the ball pops open almost instantly, then keeps billowing.
                float burst = burstRadius * (1f - Mathf.Exp(-t / 0.05f));

                // Surge: after a short beat the blast front accelerates toward the viewer and swallows them exactly at
                // timeToEngulf, wherever they stand.
                float surge = Mathf.Clamp01((t - 0.12f) / (timeToEngulf - 0.12f));
                float travel = centreTravel * surge * surge * (3f - 2f * surge);
                Vector3 centre = Vector3.Lerp(origin, head, travel);
                float toHead = Vector3.Distance(centre, head);
                float engulfRadius = (toHead + 0.25f) / SurfaceInset;
                float radius = Mathf.Max(burst, Mathf.Lerp(burst, engulfRadius, Mathf.Pow(surge, 2.2f)));
                if (t > timeToEngulf)
                    radius = Mathf.Max(radius, engulfRadius) + (t - timeToEngulf) * 3f;

                float heat = 0.62f + 0.7f * Mathf.Exp(-t / 0.6f);
                SetFireball(centre, radius, t, heat);
                bool inside = toHead < radius * SurfaceInset;
                fireballMaterial.SetFloat(CullId, (float)(inside ? CullMode.Front : CullMode.Back));

                // How close the surface is: drives the full-screen fire.
                float surfaceDistance = toHead - radius * SurfaceInset;
                EngulfAmount = 1f - Mathf.Clamp01((surfaceDistance - 0.05f) / engulfStartDistance);
                GlowAmount = 0.35f * (1f - Mathf.Clamp01(surfaceDistance / 3.5f));
                if (EngulfAmount >= 1f || t > timeToEngulf + 0.3f)
                    Engulfed = true;

                if (blastLight != null)
                {
                    float attack = Mathf.Clamp01(t / 0.03f);
                    float flicker = 1f + 0.12f * (Mathf.PerlinNoise(t * 18f, 0.3f) - 0.5f);
                    blastLight.intensity = lightPeakIntensity * attack * (0.35f + 0.65f * Mathf.Exp(-t / 0.25f)) * flicker;
                    blastLight.range = lightRange + radius * 2f;
                    blastLight.color = Color.Lerp(lightFlashColor, lightFireColor, Mathf.Clamp01(t / 0.3f));
                    blastLight.transform.position = centre + transform.forward * 0.2f;
                }

                yield return null;
            }
        }

        void SetFireball(Vector3 centre, float radius, float age, float heat)
        {
            if (fireball == null || fireballMaterial == null)
                return;

            Transform ft = fireball.transform;
            ft.position = centre;
            ft.localScale = Vector3.one * (radius * 2f);   // the mesh has a radius of 0.5

            fireballMaterial.SetFloat(AgeId, age);
            fireballMaterial.SetFloat(HeatId, heat);
            fireballMaterial.SetFloat(OpacityId, 1f);
        }

        IEnumerable<ParticleSystem> Systems()
        {
            if (flash != null) yield return flash;
            if (smoke != null) yield return smoke;
            if (sparks != null) yield return sparks;
            if (debris != null) yield return debris;
        }
    }
}
