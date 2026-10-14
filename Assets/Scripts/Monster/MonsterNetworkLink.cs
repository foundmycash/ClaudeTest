using System.Collections;
using Fusion;
using GorillaNet;
using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// Shares the monster between everybody in a Photon Fusion room (Shared mode), so all players see ONE monster that
    /// hunts all of them. Without this component each player's game would run its own monster.
    ///
    /// How it works:
    /// - The monster is a scene NetworkObject with a NetworkTransform. Whoever has its state authority (the room's
    ///   master client at the start, and again after that player leaves) runs the whole AI: senses, chase, speed, the
    ///   NavMesh agent. Everybody else only follows the replicated position; their NavMesh agent, senses and
    ///   decisions are off, but legs and footsteps still animate from the motion they see.
    /// - The authority hunts everybody: its own player (the Gorilla rig) and the other players' avatars. Each avatar
    ///   gets a <see cref="HuntTarget"/> carrying its Photon player number, which is how a chase stays locked to one
    ///   specific player.
    /// - Microphone noise is measured on each player's own headset. A player who is not the authority tells the
    ///   authority "my player made a loud noise" (one tiny message with the sender's number, no audio) and the
    ///   authority applies it like its own. Sight and proximity are computed by the authority from the avatars'
    ///   positions, which every player sends anyway.
    /// - Safe zones are volumes in the scene, so the authority checks them against everybody's position itself.
    /// - A catch is networked: the authority is the one that sees the monster reach somebody. When that is another
    ///   player, the authority ends the chase on its copy of them (HuntTarget.Kill, revived a few seconds later) and
    ///   tells that player's game "you were caught" (one tiny message with their number). Their game then runs the same
    ///   catch as if its own monster had reached them (blackout, respawn, MonsterHunter.AnyTargetCaught).
    ///
    /// If the room is not running (offline, still connecting) nothing here does anything and the monster runs
    /// locally. Add it with Tools > Monster > Make Monster Networked.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MonsterHunter))]
    [AddComponentMenu("Monster/Monster Network Link")]
    public class MonsterNetworkLink : NetworkBehaviour, IStateAuthorityChanged
    {
        MonsterHunter hunter;
        MonsterSenses senses;
        MicrophoneNoiseDetector microphone;
        MonsterScreech screech;
        float nextClaim;
        float nextLocalId;
        bool listening;

        // Seconds a caught remote player stays "dead" for the monster, after the blackout and the respawn have run on their side.
        const float RemoteRespawnMargin = 1.5f;

        public override void Spawned()
        {
            hunter = GetComponent<MonsterHunter>();
            senses = GetComponent<MonsterSenses>();

            ApplyAuthority();

            MonsterHunter.AnyTargetCaught += OnAnyTargetCaught;
            NetworkAvatar.AvatarSpawned += OnAvatarSpawned;
            for (int i = 0; i < NetworkAvatar.Active.Count; i++)
                Adopt(NetworkAvatar.Active[i]);

            // This player's microphone: the authority's senses listen to it directly; anybody else sends it on.
            microphone = MicrophoneNoiseDetector.EnsureExists();
            microphone.NoiseDetected += OnLocalNoise;
            microphone.StartListening();

            // The screech: every game decides and plays its own for its own player (see MonsterScreech), no message needed.
            screech = GetComponent<MonsterScreech>();
            listening = true;

            LabelLocalPlayer();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (listening)
            {
                MonsterHunter.AnyTargetCaught -= OnAnyTargetCaught;
                NetworkAvatar.AvatarSpawned -= OnAvatarSpawned;
                if (microphone != null)
                    microphone.NoiseDetected -= OnLocalNoise;
                listening = false;
            }

            // The room is gone (left, or the scene unloaded): run the monster here again, as offline.
            if (hunter != null)
                hunter.SetAuthority(true);
        }

        /// <summary>Fusion calls this when the monster's state authority moves to another player.</summary>
        public void StateAuthorityChanged()
        {
            ApplyAuthority();
        }

        void ApplyAuthority()
        {
            if (hunter != null && Object != null && Object.IsValid)
                hunter.SetAuthority(Object.HasStateAuthority);
        }

        // Runs on every peer, every frame.
        public override void Render()
        {
            // Nobody owns the monster when its owner leaves (it does not follow the player out of the room); the room's master client takes it over.
            if (Object.StateAuthority == PlayerRef.None && Runner.IsSharedModeMasterClient && Time.unscaledTime >= nextClaim)
            {
                nextClaim = Time.unscaledTime + 1f;
                Object.RequestStateAuthority();
            }

            if (Time.unscaledTime >= nextLocalId)
            {
                nextLocalId = Time.unscaledTime + 1f;
                LabelLocalPlayer();
            }
        }

        // The local player's HuntTarget gets this peer's Photon player number, so the other peers' avatars of it and this one are the same person.
        void LabelLocalPlayer()
        {
            HuntTarget local = HuntTarget.EnsureLocalPlayer();
            if (local != null && Runner != null && local.playerId != Runner.LocalPlayer.PlayerId)
                local.playerId = Runner.LocalPlayer.PlayerId;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Players

        void OnAvatarSpawned(NetworkAvatar avatar)
        {
            Adopt(avatar);
        }

        /// <summary>Makes another player's avatar huntable. Your own avatar is skipped: your player is the Gorilla rig.</summary>
        static void Adopt(NetworkAvatar avatar)
        {
            if (avatar == null || avatar.IsMine)
                return;

            HuntTarget target = avatar.GetComponent<HuntTarget>();
            if (target == null)
                target = avatar.gameObject.AddComponent<HuntTarget>();
            target.head = avatar.head;
            target.bodyRoot = avatar.transform;
            target.isLocalPlayer = false;
            target.playerId = avatar.Owner.PlayerId;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Catches

        // Runs on every peer for every catch of this monster's hunter, but only the authority's monster ever catches anybody.
        void OnAnyTargetCaught(MonsterHunter monster, HuntTarget target)
        {
            // The local player's own catch is handled by CatchBlackout / PlayerRespawn, no message needed.
            if (monster != hunter || target == null || target.isLocalPlayer || target.playerId < 0)
                return;
            if (Object == null || !Object.IsValid || !Object.HasStateAuthority)
                return;

            // The monster is done with them: the chase ends this frame (as for a local catch) and they are not hunted again until
            // their game has blacked out, moved them to a respawn point and faded back in.
            target.Kill();
            StartCoroutine(ReviveAfterRespawn(target));
            RPC_YouWereCaught(target.playerId);
        }

        IEnumerator ReviveAfterRespawn(HuntTarget target)
        {
            CatchBlackout blackout = CatchBlackout.Instance;
            float wait = blackout != null && blackout.enableBlackout ? blackout.blackSeconds + blackout.fadeInSeconds : 0f;
            wait += RemoteRespawnMargin;
            for (float t = 0f; t < wait; t += Time.unscaledDeltaTime)
                yield return null;

            if (target != null)
                target.Revive();
        }

        // Sent by the monster's authority to everybody; only the caught player's game acts on it. The monster's authority is the only
        // source Fusion lets through, so nobody else can "kill" a player this way.
        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        void RPC_YouWereCaught(int caughtPlayerId)
        {
            if (Runner == null || Runner.LocalPlayer.PlayerId != caughtPlayerId)
                return;
            if (Object != null && Object.IsValid && Object.HasStateAuthority)
                return;                 // the authority's own player is caught by its own monster directly

            HuntTarget local = HuntTarget.EnsureLocalPlayer();
            if (local != null && hunter != null)
                hunter.RaiseCaughtRemotely(local);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Screech

        // Only the authority's MonsterScreech decides, so this only fires there.
        void OnScreechTriggered()
        {
            if (Object == null || !Object.IsValid || !Object.HasStateAuthority)
                return;
            RPC_Screech();
        }

        // One tiny message, no audio: every game plays its own copy of the clip from the monster, so each player hears it from where they stand
        // (and not at all beyond the hearing range, where the sound has faded out).
        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        void RPC_Screech()
        {
            if (Object != null && Object.IsValid && Object.HasStateAuthority)
                return;                 // the authority already played it
            if (screech != null)
                screech.PlayRemote();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Sound

        void OnLocalNoise(float levelDb)
        {
            if (Object == null || !Object.IsValid || Object.HasStateAuthority)
                return;                 // the authority's own senses hear its own player
            RPC_PlayerMadeNoise();
        }

        // Only the loudness event travels, never any audio. The sender is identified by Fusion (info.Source), not by anything in the message.
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        void RPC_PlayerMadeNoise(RpcInfo info = default)
        {
            if (senses == null)
                return;
            HuntTarget who = HuntTarget.FindByPlayerId(info.Source.PlayerId);
            if (who != null)
                senses.ReportNoise(who);
        }
    }
}
