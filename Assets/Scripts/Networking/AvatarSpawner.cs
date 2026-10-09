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
    /// player (editor and development builds only) a second, mirrored avatar of you stands in front of you, so you can
    /// check the avatar and watch it follow your head and hands. It goes away as soon as somebody else joins.
    /// </summary>
    public class AvatarSpawner : SimulationBehaviour, IPlayerJoined, IPlayerLeft
    {
        [Tooltip("NetworkAvatar prefab (Assets/Prefabs/Networking/NetworkedAvatar).")]
        public NetworkObject avatarPrefab;

        [Header("Solo mirror (debug)")]
        [Tooltip("While you are alone in the room, show a mirror image of your avatar in front of you. Editor and development builds only.")]
        public bool soloMirror = true;

        [Tooltip("How far in front of you the mirror stands (meters).")]
        public float mirrorDistance = 2f;

        [Tooltip("How long you have to be alone before the mirror appears (seconds), so it doesn't flash up while the other players are still loading in.")]
        public float mirrorDelay = 1.5f;

        /// <summary>The avatar this peer spawned (null before joining and after leaving).</summary>
        public NetworkObject LocalAvatar { get; private set; }

        /// <summary>The solo mirror avatar, while there is one.</summary>
        public NetworkObject MirrorAvatar { get; private set; }

        float aloneSince = -1f;

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
            Debug.Log($"[AvatarSpawner] Spawned avatar for local player {player}.");
        }

        public void PlayerLeft(PlayerRef player)
        {
            if (player == Runner.LocalPlayer && LocalAvatar != null)
            {
                DespawnMirror();
                Runner.Despawn(LocalAvatar);
                LocalAvatar = null;
            }
        }

        void Update()
        {
            if (Runner == null || !Runner.IsRunning || LocalAvatar == null)
                return;

            bool wanted = soloMirror && Debug.isDebugBuild && Runner.ActivePlayers.Count() == 1;
            if (!wanted)
            {
                aloneSince = -1f;
                DespawnMirror();
            }
            else if (MirrorAvatar == null)
            {
                if (aloneSince < 0f)
                    aloneSince = Time.unscaledTime;
                if (Time.unscaledTime - aloneSince >= mirrorDelay)
                    SpawnMirror();
            }
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
