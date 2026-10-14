using System;
using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// The monster screeches when a player comes inside its hearing range (<see cref="MonsterSenses.HearingRange"/>).
    /// The screech is a 3D sound that fades out exactly at that radius, so the range in which the monster can hear is
    /// the range in which its screech can be heard: every player inside it hears the screech, nobody outside does.
    ///
    /// It screeches once when somebody walks into the range (not again until <see cref="cooldownSeconds"/> have passed,
    /// and not at all in the first seconds after the scene loads), and optionally keeps screeching every
    /// <see cref="repeatWhileInsideSeconds"/> while somebody stays inside. It only plays a sound: it does not start a
    /// chase (that is sight, proximity and noise, see <see cref="MonsterSenses"/>).
    ///
    /// In a shared room only the player whose game runs the monster decides when to screech; the
    /// <see cref="MonsterNetworkLink"/> tells everybody else's game to play it too, and each game plays it from the
    /// monster's position, so each player hears it from where they stand. Added to the monster by itself.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-40)]
    [AddComponentMenu("Monster/Monster Screech")]
    public class MonsterScreech : MonoBehaviour
    {
        const string ResourcePath = "Monster/Screech";

        [Tooltip("The screech. Empty = Assets/Resources/Monster/Screech (a copy of Assets/Audio/Screech.wav).")]
        public AudioClip clip;

        [Range(0f, 1f)] public float volume = 1f;

        [Tooltip("Up to this many metres the screech is at full volume. From here it fades out linearly to nothing at the hearing range.")]
        [Min(0.1f)] public float fullVolumeDistance = 10f;

        [Tooltip("Use the project's audio spatializer (Meta XR Audio here) for proper 3D direction on headphones, if one is set up.")]
        public bool useSpatializer = true;

        [Tooltip("Seconds before it may screech again, however often players come and go.")]
        [Min(0f)] public float cooldownSeconds = 20f;

        [Tooltip("0 = screech once when somebody enters. Above 0: screech again this often (seconds) while somebody stays inside the range.")]
        [Min(0f)] public float repeatWhileInsideSeconds = 0f;

        [Tooltip("Seconds between range checks (just distances).")]
        [Range(0.05f, 1f)] public float checkInterval = 0.1f;

        /// <summary>Raised on the game that decided to screech (never on the games that only play it). The network link sends it on.</summary>
        public event Action Triggered;

        public bool IsPlaying => source != null && source.isPlaying;
        public int Screeches { get; private set; }

        MonsterSenses senses;
        MonsterHunter hunter;
        AudioSource source;
        float enabledAt;
        float nextCheck;
        float nextAllowed;
        bool wasInside;
        bool warnedNoClip;

        void Awake()
        {
            senses = GetComponent<MonsterSenses>();
            hunter = GetComponent<MonsterHunter>();
        }

        void OnEnable()
        {
            enabledAt = Time.time;
            wasInside = false;
        }

        void Update()
        {
            if (Time.time < nextCheck)
                return;
            nextCheck = Time.time + checkInterval;

            // Every game decides for its own player (the monster's position is replicated), so nobody depends on the authority or a message.
            if (senses == null)
                return;

            bool inside = AnyoneInRange();
            bool graceOver = Time.time - enabledAt >= senses.startupGraceSeconds;

            if (inside && graceOver && Time.time >= nextAllowed)
            {
                bool entered = !wasInside;
                bool repeat = wasInside && repeatWhileInsideSeconds > 0f;
                if (entered || repeat)
                    Trigger();
            }

            // Somebody who is inside while the monster is still in its start-up grace counts as "already inside": no screech until they leave and come back.
            wasInside = inside;
        }

        bool AnyoneInRange()
        {
            float range = senses.HearingRange;
            float limit = range * range;
            Vector3 here = transform.position;

            var all = HuntTarget.Active;
            for (int i = 0; i < all.Count; i++)
            {
                HuntTarget t = all[i];
                if (t.isLocalPlayer && (t.HeadPosition - here).sqrMagnitude <= limit && t.IsHuntable)
                    return true;
            }
            return false;
        }

        void Trigger()
        {
            nextAllowed = Time.time + Mathf.Max(cooldownSeconds, repeatWhileInsideSeconds);
            if (!Play())
                return;
            Triggered?.Invoke();
        }

        /// <summary>Plays the screech here, from the monster. Used by the network link on every player's game other than the one that decided.</summary>
        public bool PlayRemote()
        {
            return Play();
        }

        /// <summary>Plays the screech now, on this game only (no cooldown, no network).</summary>
        [ContextMenu("Test: Screech now")]
        public bool Play()
        {
            if (clip == null)
                clip = Resources.Load<AudioClip>(ResourcePath);
            if (clip == null)
            {
                if (!warnedNoClip)
                {
                    warnedNoClip = true;
                    Debug.LogWarning("MonsterScreech on '" + name + "': no clip assigned and Resources/" + ResourcePath + " was not found, so there is no screech.", this);
                }
                return false;
            }

            EnsureSource();
            float range = senses != null ? senses.HearingRange : MonsterSenses.DefaultHearingRange;
            source.maxDistance = Mathf.Max(range, 1f);
            source.minDistance = Mathf.Clamp(fullVolumeDistance, 0.1f, source.maxDistance * 0.9f);
            source.volume = volume;
            source.clip = clip;
            source.Play();
            Screeches++;
            return true;
        }

        void EnsureSource()
        {
            if (source != null)
                return;

            var go = new GameObject("Screech Voice");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * 2f;           // about where the head is
            source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;          // silent exactly at the hearing range
            source.dopplerLevel = 0f;
            bool spatialize = useSpatializer && !string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName());
            source.spatialize = spatialize;
            source.spatializePostEffects = spatialize;
        }
    }
}
