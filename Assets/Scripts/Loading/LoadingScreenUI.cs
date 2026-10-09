using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LoadingScreen
{
    /// <summary>
    /// The loading screen: a carved stone slab floating in front of the player with the game's title cut into it,
    /// a channel that fills with molten rock as the real loading progresses, the percentage and a short status line.
    ///
    /// It never runs ahead of the real loading: <see cref="SetProgress"/> only ever raises the target, and the bar
    /// glides toward it without passing it, so it reads 100% only once the work is really done.
    ///
    /// Comfort in VR: the panel stays put in the world and only drifts back in front of the player when they look far
    /// away. <see cref="Exit"/> cracks the slab open from the end of the bar, lets it fall apart into shards that
    /// tumble away and crumble to dust (the lettering breaks away with it), then switches the canvas off so nothing of
    /// the UI keeps running.
    /// </summary>
    [DisallowMultipleComponent]
    public class LoadingScreenUI : MonoBehaviour
    {
        const float MaxAnimationStep = 1f / 30f;

        [Header("Parts")]
        public Canvas canvas;
        public CanvasGroup group;
        [Tooltip("Everything that moves together (child of the canvas).")]
        public RectTransform panel;
        [Tooltip("The stone slab (a shard mesh). Its rest rectangle is the whole panel.")]
        public StoneSlabGraphic slab;
        [Tooltip("Hidden template; the title is built from one copy of it per letter.")]
        public Text titleTemplate;
        public Text statusText;
        public Text percentText;
        [Tooltip("The channel the molten fill runs in (position and size only; the slab draws it).")]
        public RectTransform barArea;
        [Tooltip("Glowing ember at the tip of the fill (child of the bar area).")]
        public RectTransform barHead;
        [Tooltip("Soft warm glow along the filled part of the channel.")]
        public Graphic barGlow;
        [Tooltip("Ambient embers drifting around the panel (world space, outside the canvas).")]
        public ParticleSystem embers;
        [Tooltip("Rock dust thrown up while the slab crumbles (world space; filled by script).")]
        public ParticleSystem dust;
        [Tooltip("Small chips of rock that fly off and fall (world space; filled by script).")]
        public ParticleSystem chips;
        public AudioSource sfx;
        [Tooltip("Cracking and crumbling stone; plays once when the slab starts to break.")]
        public AudioClip crumbleClip;

        [Header("Materials (copied at runtime)")]
        [Tooltip("Loading/Stone")]
        public Material stoneMaterial;
        [Tooltip("Loading/StoneText")]
        public Material textMaterial;
        [Tooltip("Loading/UI, additive: glows.")]
        public Material glowMaterial;

        [Header("Title")]
        [Tooltip("Shown as the title. Empty uses the product name from Player Settings.")]
        public string gameTitle = "";
        [Tooltip("Extra space between the title's letters (canvas units).")]
        public float letterSpacing = 22f;

        [Header("Placement")]
        [Tooltip("Distance from the player's eyes (metres).")]
        [Min(0.5f)] public float distance = 2f;
        [Tooltip("Height of the panel centre relative to the eyes (metres).")]
        public float heightOffset = -0.08f;
        [Tooltip("The panel glides back in front of the player when they look further away than this (degrees).")]
        [Range(10f, 90f)] public float followAngle = 40f;

        [Header("Animation")]
        [Min(0f)] public float introDuration = 0.9f;
        [Tooltip("Upper bound on the crumble; it always lasts at least as long as the slowest shard needs.")]
        [Min(0.1f)] public float exitDuration = 1.8f;
        [Tooltip("How quickly the bar catches up with the real progress (seconds).")]
        [Min(0.01f)] public float progressSmoothing = 0.2f;

        /// <summary>Progress the bar shows right now (0..1). Never more than the last value given to SetProgress.</summary>
        public float DisplayedProgress => displayed;

        static readonly int ProgressId = Shader.PropertyToID("_Progress");
        static readonly int AnimTimeId = Shader.PropertyToID("_AnimTime");
        static readonly int BarRectId = Shader.PropertyToID("_BarRect");
        static readonly int ShadowId = Shader.PropertyToID("_Shadow");
        static readonly int EmberLightId = Shader.PropertyToID("_EmberLight");
        static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
        static readonly int GlowId = Shader.PropertyToID("_Glow");
        static readonly int UnderlightId = Shader.PropertyToID("_Underlight");

        /// <summary>One piece of lettering that breaks away on its own.</summary>
        class Piece
        {
            public RectTransform rt;
            public Text text;
            public Material material;
            public Vector2 rest;
            public float baseGlow;
            public float release, life, spin, zSpeed;
            public Vector2 velocity, outward;
            public bool loose;
        }

        readonly List<Text> letters = new List<Text>();
        readonly List<Piece> pieces = new List<Piece>();
        readonly List<Material> ownedMaterials = new List<Material>();
        Material stoneInstance;
        Material glowInstance;
        Transform head;

        float age;
        float target;
        float displayed;
        float velocity;

        string status = "";
        string pendingStatus;
        bool statusDots = true;
        float statusFade = 1f;
        float statusChangeTime = -10f;

        float yaw;
        float yawVelocity;
        float height;
        bool following;
        bool exiting;
        Color percentColor;
        float statusBaseY;
        CanvasGroup headGroup;
        Vector4 barRect;
        Vector2 crackOrigin;
        readonly List<Vector3> trail = new List<Vector3>();

        // ------------------------------------------------------------------------------------------------------
        // API
        // ------------------------------------------------------------------------------------------------------

        public void Show(Transform viewer)
        {
            head = viewer;
            age = 0f;
            target = displayed = velocity = 0f;
            exiting = false;

            ReleaseMaterials();
            stoneInstance = new Material(stoneMaterial) { name = stoneMaterial.name + " (runtime)" };
            glowInstance = new Material(glowMaterial) { name = glowMaterial.name + " (runtime)" };
            ownedMaterials.Add(stoneInstance);
            ownedMaterials.Add(glowInstance);

            // The slab's shards carry their state in extra vertex streams.
            canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2 | AdditionalCanvasShaderChannels.TexCoord3;

            foreach (Graphic g in canvas.GetComponentsInChildren<Graphic>(true))
            {
                if (g == slab) g.material = stoneInstance;
                else if (g is Text) { /* assigned per text below */ }
                else g.material = glowInstance;
            }

            // Bar geometry in slab space (canvas units), shared with the shader.
            Vector3 barCentre = slab.rectTransform.InverseTransformPoint(barArea.position);
            Vector2 half = barArea.rect.size * 0.5f;
            barRect = new Vector4(barCentre.x, barCentre.y, half.x, half.y);
            stoneInstance.SetVector(BarRectId, barRect);
            crackOrigin = new Vector2(barRect.x + barRect.z - 24f, barRect.y);
            slab.Rebuild(crackOrigin);
            slab.Restore();

            BuildTitle();
            foreach (Text t in new[] { statusText, percentText })
                t.material = OwnedTextMaterial();
            percentColor = percentText.color;
            statusText.text = "";
            percentText.text = "0%";
            canvas.gameObject.SetActive(true);
            group.alpha = 0f;
            if (embers != null) embers.Play(true);

            SnapInFront();
            enabled = true;
            UpdateBar(0f);
        }

        /// <summary>Real loading progress, 0..1. The bar glides toward it and never goes past it.</summary>
        public void SetProgress(float value)
        {
            target = Mathf.Max(target, Mathf.Clamp01(value));
        }

        /// <summary>Status line, e.g. "Loading textures". Animated dots are added unless <paramref name="dots"/> is false.</summary>
        public void SetStatus(string text, bool dots = true)
        {
            if (text == status && dots == statusDots || text == pendingStatus)
                return;
            pendingStatus = text;
            statusDots = dots;
        }

        /// <summary>Cracks the slab open, lets it crumble away, then switches the UI off.</summary>
        public IEnumerator Exit()
        {
            exiting = true;
            displayed = target = 1f;
            UpdateBar(age);

            PreparePieces();
            slab.ShardReleased += OnShardReleased;
            if (dust != null) dust.Play(true);
            if (chips != null) chips.Play(true);
            if (embers != null) embers.Stop(true, ParticleSystemStopBehavior.StopEmitting);   // they fade out and switch themselves off

            if (sfx != null && crumbleClip != null)
                sfx.PlayOneShot(crumbleClip);
            LoadingHaptics.Pulse(0.18f, 0.12f);

            float duration = Mathf.Max(exitDuration, slab.Duration + 0.1f);
            bool rumbled = false;
            for (float t = 0f; t < duration; )
            {
                t += Mathf.Min(Time.unscaledDeltaTime, MaxAnimationStep);

                slab.Crumble(t);
                stoneInstance.SetFloat(ShadowId, slab.ShadowFade);
                stoneInstance.SetFloat(EmberLightId, 1f - Smooth(0.55f, 1.6f, t));
                AnimatePieces(t);
                FadeGlows(t);
                EmitTrail(Mathf.Min(Time.unscaledDeltaTime, MaxAnimationStep));

                if (!rumbled && t > slab.firstRelease + 0.25f)
                {
                    rumbled = true;
                    LoadingHaptics.Pulse(0.3f, 0.22f);
                }
                yield return null;
            }

            slab.ShardReleased -= OnShardReleased;
            if (dust != null) dust.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (chips != null) chips.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            canvas.gameObject.SetActive(false);
            enabled = false;
            ReleaseMaterials();
        }

        // ------------------------------------------------------------------------------------------------------
        // Animation
        // ------------------------------------------------------------------------------------------------------

        void Awake()
        {
            statusBaseY = statusText.rectTransform.anchoredPosition.y;
            headGroup = barHead.GetComponent<CanvasGroup>();

            // Nothing shows (or runs) until Show() is called.
            canvas.gameObject.SetActive(false);
            enabled = false;
        }

        void OnDestroy()
        {
            ReleaseMaterials();
        }

        void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, MaxAnimationStep);
            age += dt;

            if (!exiting)
            {
                Follow(dt);

                displayed = Mathf.SmoothDamp(displayed, target, ref velocity, progressSmoothing, 1.5f, dt);
                if (target - displayed < 0.0005f)
                    displayed = target;
            }

            float intro = Mathf.Clamp01(age / Mathf.Max(0.01f, introDuration));
            group.alpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(intro * 1.6f));
            panel.localScale = Vector3.one * Mathf.Lerp(0.96f, 1f, 1f - (1f - intro) * (1f - intro));

            stoneInstance.SetFloat(AnimTimeId, age);
            if (exiting)
                return;

            AnimateTitle();
            UpdateBar(age);
            UpdateStatus();
        }

        Material OwnedTextMaterial()
        {
            var m = new Material(textMaterial) { name = textMaterial.name + " (runtime)" };
            ownedMaterials.Add(m);
            return m;
        }

        void ReleaseMaterials()
        {
            foreach (Material m in ownedMaterials)
                if (m != null) Destroy(m);
            ownedMaterials.Clear();
        }

        void BuildTitle()
        {
            foreach (Text letter in letters)
                Destroy(letter.gameObject);
            letters.Clear();

            string title = string.IsNullOrWhiteSpace(gameTitle) ? Application.productName : gameTitle;
            title = title.Trim().ToUpperInvariant();

            Font font = titleTemplate.font;
            int size = titleTemplate.fontSize;
            FontStyle style = titleTemplate.fontStyle;
            font.RequestCharactersInTexture(title, size, style);

            var advances = new float[title.Length];
            float total = letterSpacing * (title.Length - 1);
            for (int i = 0; i < title.Length; i++)
            {
                advances[i] = font.GetCharacterInfo(title[i], out CharacterInfo info, size, style) ? info.advance : size * 0.3f;
                total += advances[i];
            }

            // One Text per letter: real letter spacing, and each letter can light up (and later break away) on its own.
            RectTransform templateRect = titleTemplate.rectTransform;
            float x = -total * 0.5f;
            for (int i = 0; i < title.Length; i++)
            {
                Text letter = Instantiate(titleTemplate, templateRect.parent);
                letter.name = "Letter " + title[i];
                letter.text = title[i].ToString();
                letter.material = OwnedTextMaterial();
                RectTransform rt = letter.rectTransform;
                rt.sizeDelta = new Vector2(advances[i] + 8f, templateRect.sizeDelta.y);
                rt.anchoredPosition = new Vector2(x + advances[i] * 0.5f, templateRect.anchoredPosition.y);
                letter.gameObject.SetActive(true);
                letters.Add(letter);
                x += advances[i] + letterSpacing;
            }
            titleTemplate.gameObject.SetActive(false);
        }

        void AnimateTitle()
        {
            float fillX = barRect.x - barRect.z + displayed * barRect.z * 2f;
            for (int i = 0; i < letters.Count; i++)
            {
                // Letters light up one after another: a flare, then the inlay settles.
                float u = Mathf.Clamp01((age - 0.15f - i * 0.05f) / 0.55f);
                float ease = u * u * (3f - 2f * u);
                Color c = letters[i].color;
                c.a = ease;
                letters[i].color = c;
                letters[i].material.SetFloat(GlowId, 0.88f * (1f + 0.75f * Mathf.Sin(u * Mathf.PI)));
                // the molten fill lights the lettering from below as it passes underneath
                float behind = fillX - letters[i].rectTransform.anchoredPosition.x;
                letters[i].material.SetFloat(UnderlightId, Smooth(-70f, 150f, behind) * 0.85f);
            }
        }

        void UpdateBar(float time)
        {
            float width = barArea.rect.width;
            float height = barArea.rect.height;
            float filled = displayed * width;

            stoneInstance.SetFloat(ProgressId, displayed);

            float presence = Mathf.Clamp01(filled / (height * 1.5f));
            barHead.anchoredPosition = new Vector2(filled, 0f);
            float flicker = 0.85f + 0.15f * Mathf.PerlinNoise(time * 7f, 0.5f) + 0.08f * Mathf.Sin(time * 23f);
            barHead.localScale = Vector3.one * flicker;
            if (headGroup != null)
                headGroup.alpha = presence;

            if (barGlow != null)
            {
                // spans the filled stretch, a little beyond both ends
                RectTransform g = barGlow.rectTransform;
                float glowWidth = filled + height * 3f;
                g.anchorMin = g.anchorMax = new Vector2(0f, 0.5f);
                g.pivot = new Vector2(0.5f, 0.5f);
                g.sizeDelta = new Vector2(glowWidth, g.sizeDelta.y);
                g.anchoredPosition = new Vector2(filled * 0.5f, 0f);
                SetAlpha(barGlow, presence * (0.20f + 0.05f * Mathf.Sin(time * 4.3f)));
            }

            int percent = Mathf.FloorToInt(displayed * 100f + 1e-4f);
            percentText.text = percent + "%";
            percentText.color = Color.Lerp(percentColor, new Color(1f, 0.92f, 0.78f, percentColor.a), displayed >= 1f ? 0.7f : 0f);
        }

        void UpdateStatus()
        {
            // Cross-fade: the old line sinks away, the new one rises in.
            if (pendingStatus != null)
            {
                statusFade = Mathf.MoveTowards(statusFade, 0f, Time.unscaledDeltaTime / 0.12f);
                if (statusFade <= 0f || status.Length == 0)
                {
                    status = pendingStatus;
                    pendingStatus = null;
                    statusChangeTime = age;
                }
            }
            else
            {
                statusFade = Mathf.MoveTowards(statusFade, 1f, Time.unscaledDeltaTime / 0.2f);
            }

            string dots = "";
            if (statusDots && status.Length > 0)
                dots = new string('.', 1 + (int)((age - statusChangeTime) * 2.5f) % 3);
            statusText.text = status + dots;

            Color c = statusText.color;
            c.a = statusFade * 0.9f;
            statusText.color = c;
            Vector2 p = statusText.rectTransform.anchoredPosition;
            statusText.rectTransform.anchoredPosition = new Vector2(p.x, statusBaseY - 6f * (1f - statusFade));
        }

        // ------------------------------------------------------------------------------------------------------
        // Crumble
        // ------------------------------------------------------------------------------------------------------

        // The lettering breaks away on its own, a little ahead of the stone behind it.
        void PreparePieces()
        {
            pieces.Clear();
            var rnd = new System.Random(5);
            var all = new List<Text>(letters) { statusText, percentText };
            foreach (Text t in all)
            {
                RectTransform rt = t.rectTransform;
                Vector2 rest = rt.anchoredPosition;
                Vector2 centre = rest + Vector2.Scale(rt.sizeDelta, new Vector2(0.5f - rt.pivot.x, 0.5f - rt.pivot.y));
                float dist = Vector2.Distance(centre, crackOrigin);
                float u = Mathf.Clamp01(dist / 1100f);
                Vector2 away = centre - crackOrigin;
                var p = new Piece
                {
                    rt = rt, text = t, material = t.material, rest = rest,
                    baseGlow = t.material.GetFloat(GlowId),
                    release = Mathf.Max(0.04f, slab.firstRelease - 0.2f + slab.releaseSpread * (u * 0.9f + (float)rnd.NextDouble() * 0.1f)),
                    life = Mathf.Lerp(0.42f, 0.62f, (float)rnd.NextDouble()),
                    outward = away.sqrMagnitude > 1f ? away.normalized : Vector2.up,
                };
                p.velocity = p.outward * Mathf.Lerp(4f, 26f, (float)rnd.NextDouble()) + new Vector2(0f, 6f + (float)rnd.NextDouble() * 24f);
                p.spin = ((float)rnd.NextDouble() - 0.5f) * 1.1f;
                p.zSpeed = 20f + (float)rnd.NextDouble() * 50f;
                pieces.Add(p);
            }
        }

        void AnimatePieces(float t)
        {
            foreach (Piece p in pieces)
            {
                float since = t - p.release;
                if (since < 0f)
                {
                    // the inlay flares up as the stone cracks around it
                    float lead = Mathf.Clamp01(1f + since / 0.3f);
                    p.material.SetFloat(GlowId, p.baseGlow * (1f + 0.5f * lead * lead));
                    continue;
                }

                if (!p.loose)
                {
                    p.loose = true;
                    EmitDustAt(p.rt, 0.7f);
                }

                float k = Mathf.Clamp01(since / p.life);
                float dissolve = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(k / 0.85f));
                p.material.SetFloat(DissolveId, dissolve);
                p.material.SetFloat(GlowId, p.baseGlow * (1f - 0.4f * k));

                p.rt.anchoredPosition = p.rest + new Vector2(p.velocity.x * since, p.velocity.y * since - 0.5f * 0.42f * slab.gravity * since * since);
                p.rt.localRotation = Quaternion.Euler(0f, 0f, p.spin * since * Mathf.Rad2Deg);
                Vector3 lp = p.rt.localPosition;
                p.rt.localPosition = new Vector3(lp.x, lp.y, p.zSpeed * since);
                p.rt.localScale = Vector3.one * (1f - 0.12f * dissolve);
            }
        }

        static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // The ember and its glow go out as the stone breaks.
        void FadeGlows(float t)
        {
            float fade = 1f - Smooth(0.15f, 0.7f, t);
            if (headGroup != null)
                headGroup.alpha = fade;
            if (barGlow != null)
                SetAlpha(barGlow, 0.22f * fade);
        }

        void OnShardReleased(StoneSlabGraphic.Release r)
        {
            if (dust == null && chips == null)
                return;

            Transform s = slab.transform;
            float unit = s.lossyScale.x;               // metres per canvas unit
            Vector3 centre = s.TransformPoint(new Vector3(r.center.x, r.center.y, 0f));
            Vector3 outward = s.TransformDirection(new Vector3(r.outward.x, r.outward.y, 0f));
            float width = Mathf.Sqrt(r.area) * unit;   // roughly the shard's width in metres
            float weight = r.coverage;

            if (dust != null)
            {
                int n = Mathf.Clamp(Mathf.RoundToInt(r.area / 1300f * weight) + 2, 2, 11);
                if (weight < 0.05f) n = 0;
                for (int i = 0; i < n; i++)
                    EmitDust(centre + s.TransformDirection(Random.insideUnitCircle * width * 0.5f), outward * Random.Range(0.02f, 0.22f), width * Random.Range(0.25f, 0.7f));
            }

            if (chips != null && weight > 0.05f)
            {
                int n = Mathf.Clamp(Mathf.RoundToInt(r.area / 2600f * weight), 0, 6);
                var emit = new ParticleSystem.EmitParams { applyShapeToPosition = false };
                for (int i = 0; i < n; i++)
                {
                    emit.position = centre + s.TransformDirection(Random.insideUnitCircle * width * 0.4f);
                    emit.velocity = outward * Random.Range(0.15f, 0.6f) + Vector3.up * Random.Range(0.05f, 0.45f) + Random.insideUnitSphere * 0.18f;
                    emit.startSize = Random.Range(0.014f, 0.04f);
                    emit.startLifetime = Random.Range(0.9f, 1.5f);
                    emit.rotation = Random.Range(0f, 360f);
                    emit.angularVelocity = Random.Range(-420f, 420f);
                    float shade = Random.Range(0.72f, 1.1f);
                    emit.startColor = new Color(0.40f * shade, 0.35f * shade, 0.30f * shade, 1f);
                    chips.Emit(emit, 1);
                }
            }
        }

        void EmitDustAt(RectTransform rt, float strength)
        {
            if (dust == null) return;
            Transform s = slab.transform;
            Vector3 world = rt.TransformPoint(rt.rect.center);
            float unit = s.lossyScale.x;
            for (int i = 0; i < 2; i++)
                EmitDust(world + s.TransformDirection(Random.insideUnitCircle * 25f * unit), Vector3.up * Random.Range(0.02f, 0.12f), Random.Range(0.05f, 0.09f) * strength);
        }

        void EmitDust(Vector3 position, Vector3 velocity, float size)
        {
            var emit = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            emit.position = position;
            emit.velocity = velocity + Random.insideUnitSphere * 0.04f + Vector3.up * 0.03f;
            emit.startSize = Mathf.Clamp(size, 0.022f, 0.12f);
            emit.startLifetime = Random.Range(0.9f, 1.7f);
            emit.rotation = Random.Range(0f, 360f);
            float shade = Random.Range(0.8f, 1.15f);
            emit.startColor = new Color(0.46f * shade, 0.41f * shade, 0.36f * shade, Random.Range(0.20f, 0.40f));
            dust.Emit(emit, 1);
        }

        // Dust streams off the shards while they fall.
        void EmitTrail(float dt)
        {
            if (dust == null)
                return;
            trail.Clear();
            slab.CollectFallingCenters(trail);
            Transform s = slab.transform;
            foreach (Vector3 local in trail)
            {
                if (Random.value > dt * 9f)
                    continue;
                EmitDust(s.TransformPoint(local), Vector3.zero, Random.Range(0.025f, 0.06f));
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Placement
        // ------------------------------------------------------------------------------------------------------

        void SnapInFront()
        {
            yaw = FlatYaw(head.forward);
            height = head.position.y + heightOffset;
            yawVelocity = 0f;
            following = false;
            Place();
        }

        void Follow(float dt)
        {
            // While tracking settles in the first moments, stay locked to the view; after that the panel stays put
            // and only glides back when the player looks far away.
            if (age < 0.5f)
            {
                SnapInFront();
                return;
            }

            float headYaw = FlatYaw(head.forward);
            float offset = Mathf.Abs(Mathf.DeltaAngle(yaw, headYaw));
            if (offset > followAngle)
                following = true;
            else if (offset < 3f)
                following = false;

            if (following)
                yaw = Mathf.SmoothDampAngle(yaw, headYaw, ref yawVelocity, 0.35f, Mathf.Infinity, dt);
            height = Mathf.Lerp(height, head.position.y + heightOffset, 1f - Mathf.Exp(-dt * 2f));
            Place();
        }

        void Place()
        {
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 position = head.position + rotation * Vector3.forward * distance;
            position.y = height;
            transform.SetPositionAndRotation(position, rotation);
        }

        static float FlatYaw(Vector3 forward)
        {
            forward.y = 0f;
            return forward.sqrMagnitude < 1e-6f ? 0f : Quaternion.LookRotation(forward).eulerAngles.y;
        }

        // ------------------------------------------------------------------------------------------------------

        static void SetAlpha(Graphic g, float alpha)
        {
            if (g == null)
                return;
            Color c = g.color;
            c.a = alpha;
            g.color = c;
        }
    }
}
