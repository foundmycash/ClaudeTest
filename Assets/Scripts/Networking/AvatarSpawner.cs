using System.Linq;
using Fusion;
using UnityEngine;

namespace GorillaNet
{
    /// <summary>
    /// Gives every player a <see cref="NetworkAvatar"/>. Put it on the same GameObject as the NetworkRunner (next to
    /// <see cref="NetworkLauncher"/>). In Shared mode each player spawns their own avatar once they've joined the room.
    /// When a player leaves, Fusion removes their avatar for everybody because the prefab's NetworkObject has
    /// "Destroy When State Authority Leaves" on.
    ///
    /// You never see your own avatar, so alone in a room there would be nothing to look at. While you are the only
    /// player (the editor, development builds, and release builds too while <see cref="soloViewInReleaseBuilds"/> is on)
    /// your avatar is drawn, so you can check it and watch it follow your head and hands: either on you, exactly where
    /// the rig is (<see cref="SoloStyle.Self"/>), or as a mirror image in front of you (<see cref="SoloStyle.Mirror"/>).
    /// It goes back to hidden / the mirror goes away as soon as somebody else joins.
    /// (Before you are in a room at all, the <see cref="OfflineRig"/> draws you instead.)
    /// </summary>
    public class AvatarSpawner : SimulationBehaviour, IPlayerJoined, IPlayerLeft
    {
        public enum SoloStyle
        {
            /// <summary>Your own avatar is drawn where you are, flush with the rig.</summary>
            Self,
            /// <summary>A second, hue-shifted avatar stands in a mirror in front of you and copies you mirrored.</summary>
            Mirror,
        }

        [Tooltip("NetworkAvatar prefab (Assets/Prefabs/Networking/NetworkedAvatar).")]
        public NetworkObject avatarPrefab;

        [Header("Solo view (debug)")]
        [Tooltip("While you are alone in the room, draw your avatar so there is something to look at.")]
        public bool soloMirror = true;

        [Tooltip("Also do that in release (non-development) builds. Without it the solo view exists only in the editor and development builds, so a release build shows nobody while you are alone in the room. Turn off before shipping if you don't want players to see themselves.")]
        public bool soloViewInReleaseBuilds = true;

        [Tooltip("Self: your own avatar, on you. Mirror: a mirror image of it in front of you.")]
        public SoloStyle soloStyle = SoloStyle.Self;

        [Tooltip("Mirror style only: how far in front of you the mirror stands (meters). Your reflection appears the same distance behind it, so twice this far from you.")]
        public float mirrorDistance = 2f;

        [Tooltip("How long you have to be alone before the avatar appears (seconds), so it doesn't flash up while the other players are still loading in.")]
        public float mirrorDelay = 1.5f;

        /// <summary>The avatar this peer spawned (null before joining and after leaving).</summary>
        public NetworkObject LocalAvatar { get; private set; }

        /// <summary>The solo mirror avatar, while there is one.</summary>
        public NetworkObject MirrorAvatar { get; private set; }

        /// <summary>The solo view (your own avatar drawn while you are alone in the room) is switched on for this kind of build.</summary>
        public bool SoloViewEnabled => soloMirror && (Debug.isDebugBuild || soloViewInReleaseBuilds);

        /// <summary>True while your own avatar, or its mirror image, is drawn (the solo view is showing).</summary>
        public bool OwnAvatarDrawn => MirrorAvatar != null || (localAvatarView != null && localAvatarView.ShowOwnAvatar);

        float aloneSince = -1f;
        NetworkAvatar localAvatarView;

        public void PlayerJoined(PlayerRef player)
        {
            // Called on every peer for every player in the room; only spawn our own.
            if (player != Runner.LocalPlayer || LocalAvatar != null)
                return;

            if (avatarPrefab == null)
            {
                Debug.LogError("[AvatarSpawner] No avatar prefab assigned.", this);
                return;
            }

            LocalAvatar = Runner.Spawn(avatarPrefab, Vector3.zero, Quaternion.identity, player);
            localAvatarView = LocalAvatar.GetComponent<NetworkAvatar>();
            Debug.Log($"[AvatarSpawner] Spawned avatar for local player {player}.");
        }

        public void PlayerLeft(PlayerRef player)
        {
            if (player == Runner.LocalPlayer && LocalAvatar != null)
            {
                DespawnMirror();
                Runner.Despawn(LocalAvatar);
                LocalAvatar = null;
                localAvatarView = null;
            }
        }

        void Update()
        {
            if (Runner == null || !Runner.IsRunning || LocalAvatar == null)
                return;

            bool alone = SoloViewEnabled && Runner.ActivePlayers.Count() == 1;
            if (!alone)
            {
                aloneSince = -1f;
                DespawnMirror();
                SetOwnAvatarVisible(false);
                return;
            }

            if (aloneSince < 0f)
                aloneSince = Time.unscaledTime;
            if (Time.unscaledTime - aloneSince < mirrorDelay)
                return;

            SetOwnAvatarVisible(soloStyle == SoloStyle.Self);
            if (soloStyle == SoloStyle.Mirror)
            {
                if (MirrorAvatar == null)
                    SpawnMirror();
            }
            else
            {
                DespawnMirror();
            }
        }

        void SetOwnAvatarVisible(bool visible)
        {
            if (localAvatarView != null)
                localAvatarView.ShowOwnAvatar = visible;
        }

        void SpawnMirror()
        {
            MirrorAvatar = Runner.Spawn(avatarPrefab, Vector3.zero, Quaternion.identity, Runner.LocalPlayer, (runner, obj) =>
            {
                obj.name += " (solo mirror)";
                var avatar = obj.GetComponent<NetworkAvatar>();
                avatar.showOwnAvatar = true;
                avatar.hueShift = 0.5f;
                avatar.SetPoseSource(new MirroredPoseSource(NetworkAvatar.PoseSourceFactory(runner), mirrorDistance));
            });
            Debug.Log("[AvatarSpawner] You are alone in the room: spawned a mirror avatar in front of you (it disappears when somebody joins).");
        }

        void DespawnMirror()
        {
            if (MirrorAvatar == null)
                return;
            if (Runner != null && Runner.IsRunning)
                Runner.Despawn(MirrorAvatar);
            MirrorAvatar = null;
        }
    }
}
