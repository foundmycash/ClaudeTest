using System.Linq;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GorillaNet
{
    /// <summary>
    /// Your own gorilla while you are not in a room: before the first join, while connecting, with no network, and after the
    /// connection is lost. It follows your head, hands and fingers exactly like the networked avatar does, but nothing about it
    /// is networked, so it needs no server (and nobody else sees it).
    ///
    /// It is built from the same prefab as the networked avatar (the avatar prefab on the <see cref="AvatarSpawner"/>), with
    /// the Fusion components taken off the copy, so changes you make to the avatar prefab (model, finger angles ...) show up
    /// here too. Created by <see cref="NetworkLauncher"/> (its "Show Offline Rig" option); nothing to set up in the scene.
    ///
    /// Once you are in a room the networked avatars take over:
    /// * somebody else is in the room: you don't see your own body (they see it), so this one goes away;
    /// * you are alone: this one stays until the solo view of the <see cref="AvatarSpawner"/> has drawn your networked avatar
    ///   (so there is no gap), and goes away for good if the solo view is switched off.
    /// </summary>
    public class OfflineRig : MonoBehaviour
    {
        /// <summary>
        /// The player number whose color the offline rig wears: 1 is the first player in a room, so the rig looks the same
        /// as the solo view of an empty room and the switch between the two isn't noticed.
        /// </summary>
        const int ColorPlayerNumber = 1;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        [Header("Fingers: PC debug (editor and PC builds only, does nothing on the Quest)")]
        [Tooltip("Same keys and sliders as on the networked avatar. Left hand: 1 = trigger, 2 = grip, 3 = X. Right hand: 8 = trigger, 9 = grip, 0 = A.")]
        public bool pcFingerDebug = true;
        public HandInput debugLeftHand;
        public HandInput debugRightHand;

        /// <summary>The offline body is drawn right now.</summary>
        public bool IsShowing { get; private set; }

        /// <summary>The body model with its fitting script (null before it is built).</summary>
        public AvatarBodyRig BodyRig { get; private set; }

        /// <summary>The launcher this rig belongs to.</summary>
        public NetworkLauncher Launcher => launcher;

        NetworkLauncher launcher;
        IAvatarPoseSource poseSource = new GorillaRigPoseSource();
        Transform head, leftHand, rightHand;
        Renderer[] renderers;
        bool? applied;   // what the renderers are set to (null: not yet set)

        NetworkRunner cachedRunner;
        AvatarSpawner cachedSpawner;

        /// <summary>
        /// Builds the offline rig from <paramref name="avatarPrefab"/> (the networked avatar prefab) in the launcher's scene.
        /// Returns null if the prefab is missing or doesn't look like an avatar.
        /// </summary>
        public static OfflineRig Create(NetworkLauncher launcher, NetworkObject avatarPrefab)
        {
            if (avatarPrefab == null || avatarPrefab.GetComponent<NetworkAvatar>() == null)
                return null;

            var go = new GameObject("Offline Rig");
            SceneManager.MoveGameObjectToScene(go, launcher.gameObject.scene);
            var rig = go.AddComponent<OfflineRig>();
            rig.launcher = launcher;
            if (!rig.Build(avatarPrefab))
            {
                Destroy(go);
                return null;
            }
            return rig;
        }

        /// <summary>Drive the rig from somewhere else than the VR rig (tests, bots). The default reads the Gorilla rig.</summary>
        public void SetPoseSource(IAvatarPoseSource source)
        {
            poseSource = source ?? new GorillaRigPoseSource();
        }

        bool Build(NetworkObject prefab)
        {
            // The copy is made under an inactive object, so none of its components wake up (Awake / OnEnable wait until it is
            // active in the hierarchy). That way Fusion never sees the NetworkObject, and the Fusion parts are gone before it runs.
            var holder = new GameObject("Offline Rig (building)");
            holder.SetActive(false);
            GameObject body = Instantiate(prefab.gameObject, holder.transform);
            body.name = "Offline Avatar";

            var avatar = body.GetComponent<NetworkAvatar>();
            head = avatar.head;
            leftHand = avatar.leftHand;
            rightHand = avatar.rightHand;
            pcFingerDebug = avatar.pcFingerDebug;

            var block = new MaterialPropertyBlock();
            Color color = Color.Lerp(Color.white,
                NetworkAvatar.ColorForPlayerId(ColorPlayerNumber, avatar.colorSaturation, avatar.colorValue),
                avatar.tintStrength);
            block.SetColor(BaseColorId, color);
            block.SetColor(ColorId, color);
            foreach (var r in avatar.tintedRenderers)
            {
                if (r != null)
                    r.SetPropertyBlock(block);
            }

            // The parts that depend on a NetworkObject first, the NetworkObject last.
            foreach (var networked in body.GetComponentsInChildren<NetworkBehaviour>(true))
                DestroyImmediate(networked);
            DestroyImmediate(body.GetComponent<NetworkObject>());

            BodyRig = body.GetComponentInChildren<AvatarBodyRig>(true);
            if (BodyRig == null || head == null || leftHand == null || rightHand == null)
            {
                Debug.LogWarning("[OfflineRig] The avatar prefab has no body rig or no head / hands: no offline rig.", this);
                Destroy(holder);
                return false;
            }
            BodyRig.headTarget = head;
            BodyRig.leftHandTarget = leftHand;
            BodyRig.rightHandTarget = rightHand;

            renderers = body.GetComponentsInChildren<Renderer>(true);
            body.transform.SetParent(transform, false);   // wakes up here
            Destroy(holder);
            SetDrawn(false);                               // nothing shows until there is a pose to show
            return true;
        }

        // After the Gorilla rig moved this frame (Player.Update), before the body fitting (AvatarBodyRig, order 50).
        void LateUpdate()
        {
            if (launcher == null)
            {
                Destroy(gameObject);
                return;
            }

            Pose headPose = default, leftPose = default, rightPose = default;
            bool show = ShouldShow() && poseSource.TryGetPoses(out headPose, out leftPose, out rightPose);
            SetDrawn(show);
            if (!show)
                return;

            head.SetPositionAndRotation(headPose.position, headPose.rotation);
            leftHand.SetPositionAndRotation(leftPose.position, leftPose.rotation);
            rightHand.SetPositionAndRotation(rightPose.position, rightPose.rotation);

            HandInput left = default, right = default;
            if (poseSource is IAvatarHandInputSource source)
                source.TryGetHandInput(out left, out right);
            NetworkAvatar.ApplyPcFingerDebug(pcFingerDebug, debugLeftHand, debugRightHand, ref left, ref right);
            BodyRig.SetHandInput(left, right);
        }

        bool ShouldShow()
        {
            if (!launcher.IsConnected)
                return true;

            // In a room: the networked avatars take over (see the class comment).
            NetworkRunner runner = launcher.Runner;
            if (runner != cachedRunner)
            {
                cachedRunner = runner;
                cachedSpawner = runner != null ? runner.GetComponent<AvatarSpawner>() : null;
            }
            if (cachedSpawner == null || !cachedSpawner.SoloViewEnabled)
                return false;
            if (runner.ActivePlayers.Count() > 1)
                return false;
            return !cachedSpawner.OwnAvatarDrawn;
        }

        // Turning the renderers off (not the object) is what puts the body rig to sleep, and it fits the torso from scratch when they come back on.
        void SetDrawn(bool drawn)
        {
            if (renderers == null || applied == drawn)
                return;
            applied = drawn;
            IsShowing = drawn;
            foreach (var r in renderers)
            {
                if (r != null)
                    r.enabled = drawn;
            }
        }
    }
}
