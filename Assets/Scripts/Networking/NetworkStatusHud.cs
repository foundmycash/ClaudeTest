using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GorillaNet
{
    /// <summary>
    /// A small text floating in front of you that tells what the lobby connection is doing, so a headset player can see
    /// why friends don't show up without a log cable: "Connecting...", "Can't reach the lobby: ... Trying again in 4 s",
    /// and, for a few seconds after joining and whenever somebody joins or leaves: room, region, player count, ping.
    /// Compare the room and region on two headsets: friends only meet when both are identical.
    /// Created by <see cref="NetworkLauncher"/> (its "Show Status" option); nothing to set up in the scene.
    /// </summary>
    public class NetworkStatusHud : MonoBehaviour
    {
        const float DistanceFromHead = 1.5f;
        const float HeightBelowEyes = 0.32f;
        const float FollowSmoothTime = 0.18f;
        const float ShowAfterJoin = 9f;
        const float ShowAfterChange = 5f;
        const float FadeTime = 1.0f;

        static readonly int ZTestModeId = Shader.PropertyToID("unity_GUIZTestMode");

        public static NetworkStatusHud Create(NetworkLauncher launcher)
        {
            var go = new GameObject("Network Status");
            SceneManager.MoveGameObjectToScene(go, launcher.gameObject.scene);
            var hud = go.AddComponent<NetworkStatusHud>();
            hud.launcher = launcher;
            return hud;
        }

        NetworkLauncher launcher;
        TextMeshPro label;
        Camera cam;
        Vector3 velocity;
        bool placed;

        NetworkLauncher.LinkState lastState = NetworkLauncher.LinkState.Idle;
        int lastPlayerCount = -1;
        float visibleUntil;
        float nextPollTime;
        string cachedText = "";
        int playerCount;

        void Awake()
        {
            label = gameObject.AddComponent<TextMeshPro>();
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = 0.55f;
            label.color = Color.white;
            label.outlineWidth = 0.22f;
            label.outlineColor = new Color32(0, 0, 0, 255);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.raycastTarget = false;

            // Drawn over the world, so a wall or a floor between you and the text doesn't hide it. (TMP's own isOverlay flag does
            // nothing for this 3D label; the shader's depth test is the global "unity_GUIZTestMode", a material can override it.)
            Material material = label.fontMaterial;
            material.SetFloat(ZTestModeId, (float)CompareFunction.Always);
            material.renderQueue = (int)RenderQueue.Overlay;
            label.rectTransform.sizeDelta = new Vector2(2f, 0.6f);
            label.text = "";
            label.alpha = 0f;
        }

        void Update()
        {
            if (launcher == null)
            {
                Destroy(gameObject);
                return;
            }

            if (Time.unscaledTime >= nextPollTime)
            {
                // Four times a second while there is something to read, once a second once it has faded out.
                bool faded = launcher.State == NetworkLauncher.LinkState.Connected && Time.unscaledTime > visibleUntil + FadeTime;
                nextPollTime = Time.unscaledTime + (faded ? 1f : 0.25f);
                Refresh();
            }

            float alpha = 1f;
            if (launcher.State == NetworkLauncher.LinkState.Connected)
                alpha = Mathf.Clamp01((visibleUntil - Time.unscaledTime) / FadeTime);
            label.alpha = Mathf.MoveTowards(label.alpha, alpha, Time.unscaledDeltaTime * 4f);
            label.enabled = label.alpha > 0.01f && cachedText.Length > 0;
        }

        void Refresh()
        {
            var state = launcher.State;
            var runner = launcher.Runner;

            if (state == NetworkLauncher.LinkState.Connected && runner != null && runner.IsRunning)
            {
                int count = 0;
                foreach (var _ in runner.ActivePlayers)
                    count++;
                playerCount = count;

                if (lastState != NetworkLauncher.LinkState.Connected)
                    visibleUntil = Time.unscaledTime + ShowAfterJoin;
                else if (count != lastPlayerCount)
                    visibleUntil = Mathf.Max(visibleUntil, Time.unscaledTime + ShowAfterChange);
                lastPlayerCount = count;

                string region = runner.SessionInfo.IsValid ? runner.SessionInfo.Region : "?";
                int ping = Mathf.RoundToInt((float)(runner.GetPlayerRtt(runner.LocalPlayer) * 1000.0));
                cachedText = $"Lobby {launcher.CurrentRoom}  -  region {region}\n" +
                             $"{count} player{(count == 1 ? "" : "s")} in the lobby  -  {ping} ms";
            }
            else
            {
                lastPlayerCount = -1;
                if (state == NetworkLauncher.LinkState.Retrying)
                {
                    string why = string.IsNullOrEmpty(launcher.LastProblem) ? "no connection" : launcher.LastProblem;
                    cachedText = $"Can't reach the lobby: {why}\nTrying again in {Mathf.CeilToInt(launcher.RetryIn)} s";
                }
                else if (state == NetworkLauncher.LinkState.Connecting)
                {
                    cachedText = launcher.Attempt > 1
                        ? $"Connecting to the lobby...  (try {launcher.Attempt})"
                        : "Connecting to the lobby...";
                }
                else
                {
                    cachedText = "";
                }
            }

            lastState = state;
            if (label.text != cachedText)
                label.text = cachedText;
        }

        void LateUpdate()
        {
            if (cam == null || !cam.isActiveAndEnabled)
                cam = Camera.main;
            if (cam == null)
                return;

            // Hangs in front of you, level with the horizon (looking up or down doesn't carry it along) and turns toward you.
            Transform head = cam.transform;
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 0.01f)
                flat = Vector3.ProjectOnPlane(head.up, Vector3.up);
            flat.Normalize();
            Vector3 target = head.position + flat * DistanceFromHead + Vector3.down * HeightBelowEyes;

            if (!placed)
            {
                transform.position = target;
                placed = true;
            }
            else
            {
                transform.position = Vector3.SmoothDamp(transform.position, target, ref velocity, FollowSmoothTime, Mathf.Infinity, Time.unscaledDeltaTime);
            }

            Vector3 away = transform.position - head.position;
            if (away.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }
}
