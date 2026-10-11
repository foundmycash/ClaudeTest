using System;
using System.Linq;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GorillaNet
{
    /// <summary>
    /// Joins a Photon Fusion Shared-mode room as soon as the scene starts, and keeps you in it.
    /// Everybody who uses the same room name AND the same <see cref="region"/> ends up in the same session.
    /// The Photon App ID comes from Fusion's PhotonAppSettings.
    ///
    /// Put it on the GameObject that holds the NetworkRunner and the other runner-side components (AvatarSpawner).
    /// Fusion can't restart a NetworkRunner once it has shut down, so on first use this component copies everything that
    /// sits next to it into an inactive template and removes the originals; every connection attempt then gets a fresh
    /// copy of that template as a child object. If an attempt fails (no network yet, Photon busy) or the connection is
    /// lost later (headset taken off, Wi-Fi hiccup), it throws that copy away and tries again with a growing delay,
    /// so a player never ends up alone in a dead session without knowing it.
    /// </summary>
    public class NetworkLauncher : MonoBehaviour
    {
        public enum LinkState
        {
            Idle,
            Connecting,
            Connected,
            /// <summary>The last attempt failed or the connection was lost; waiting to try again.</summary>
            Retrying,
        }

        [Tooltip("Players using the same room name (and region) are put in the same session.")]
        public string roomName = "GorillaTestRoom";

        [Tooltip("Maximum players in the room (Gorilla Tag uses 10). When it is full the next player goes to '<room>-2', and so on.")]
        public int maxPlayers = 10;

        [Tooltip("Connect from Start(). Turn off to call Connect() yourself, e.g. from a room-code menu.")]
        public bool autoStart = true;

        [Tooltip("Photon region everybody connects to (ussc = Texas, us = Virginia, usw = California, cae, eu, asia, jp, in, au, sa, kr, za ...). " +
                 "Rooms only exist inside one region: with this empty each player picks the region with the best ping, so two friends " +
                 "whose best region differs (e.g. 'ussc' and 'us') end up in different rooms that have the same name and never meet. " +
                 "Pick the region closest to most of your players; ussc is the most central one in the US.")]
        public string region = "ussc";

        [Header("Reconnect")]
        [Tooltip("Seconds to wait before the first retry. Doubles with every failed attempt in a row.")]
        public float retryDelay = 2f;

        [Tooltip("The retry delay never grows beyond this (seconds).")]
        public float maxRetryDelay = 15f;

        [Tooltip("An attempt that hasn't answered after this many seconds is aborted and retried.")]
        public float attemptTimeout = 40f;

        [Header("Offline rig")]
        [Tooltip("Draw your own gorilla (following your head, hands and fingers) while you are not in a room: before the first join, while connecting, with no network and after the connection drops. It is built from the avatar prefab of the AvatarSpawner next to this component. Once you are in a room the networked avatars take over.")]
        public bool showOfflineRig = true;

        [Header("Status text")]
        [Tooltip("Show a small text in front of you while connecting, when the connection fails and when players join or leave.")]
        public bool showStatus = true;

        /// <summary>The runner of the current attempt (null before the first one).</summary>
        public NetworkRunner Runner { get; private set; }

        public LinkState State { get; private set; }

        /// <summary>The session name of the current attempt.</summary>
        public string CurrentRoom { get; private set; }

        /// <summary>Why the last attempt failed or the connection dropped (empty while all is well).</summary>
        public string LastProblem { get; private set; } = "";

        /// <summary>Which attempt this is (1 = the first connection try).</summary>
        public int Attempt { get; private set; }

        /// <summary>While <see cref="State"/> is Retrying: seconds until the next try.</summary>
        public float RetryIn { get; private set; }

        public bool IsConnected => State == LinkState.Connected && Runner != null && Runner.IsRunning;

        GameObject template;
        Task loop;
        TaskCompletionSource<bool> firstJoin;
        bool skipWait;
        OfflineRig offlineRig;

        async void Start()
        {
            // First of all, so the player sees their body from the start, whatever the network does (or doesn't).
            CreateOfflineRig();
            if (!autoStart)
                return;
            try
            {
                await Connect();
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        /// <summary>
        /// Starts joining the room and keeps it joined (see the class comment). The task completes once the room has been
        /// joined for the first time (true), or with false if this component is destroyed before that.
        /// Calling it again while it is already running just returns the same task.
        /// </summary>
        public Task<bool> Connect()
        {
            if (loop != null)
                return firstJoin.Task;

            firstJoin = new TaskCompletionSource<bool>();
            CreateOfflineRig();   // (before BuildTemplate takes the avatar spawner away)
            BuildTemplate();
            if (showStatus)
                NetworkStatusHud.Create(this);
            loop = RunLoop();
            return firstJoin.Task;
        }

        void OnApplicationPause(bool paused)
        {
            // Back from sleep (headset put back on): the connection is probably gone, don't make the player wait for the retry timer.
            if (!paused)
                skipWait = true;
        }

        void OnDestroy()
        {
            if (template != null)
                Destroy(template.transform.parent != null ? template.transform.parent.gameObject : template);
        }

        // ---- Offline rig ----

        void CreateOfflineRig()
        {
            if (!showOfflineRig || offlineRig != null)
                return;
            var spawner = GetComponent<AvatarSpawner>();
            if (spawner == null || spawner.avatarPrefab == null)
            {
                Debug.LogWarning("[NetworkLauncher] No offline rig: there is no AvatarSpawner with an avatar prefab next to the launcher.", this);
                return;
            }
            offlineRig = OfflineRig.Create(this, spawner.avatarPrefab);
        }

        // ---- Runner setup ----

        void BuildTemplate()
        {
            // The holder is inactive, so the copy inside it doesn't wake up (no runner, no launcher). It is a root object of this
            // object's own scene: during the Loading -> main scene hand-over the active scene is still the Loading scene.
            var holder = new GameObject("Network Runner Template");
            holder.SetActive(false);
            SceneManager.MoveGameObjectToScene(holder, gameObject.scene);

            template = Instantiate(gameObject, holder.transform);
            template.name = "Runner";
            DestroyImmediate(template.GetComponent<NetworkLauncher>());   // or every copy would start a launcher of its own

            // The originals have been copied; they would only sit there as a second, never-started runner.
            // (The runner goes last: other Fusion components can depend on it.)
            var runner = GetComponent<NetworkRunner>();
            foreach (var original in GetComponents<Fusion.Behaviour>())
            {
                if (original != runner)
                    Destroy(original);
            }
            if (runner != null)
                Destroy(runner);
        }

        GameObject CreateHost(int attempt, Action<ShutdownReason> onShutdown)
        {
            var host = Instantiate(template, transform);
            host.name = $"Runner (attempt {attempt})";
            host.SetActive(true);

            if (host.GetComponent<NetworkRunner>() == null)
                host.AddComponent<NetworkRunner>();
            if (host.GetComponent<NetworkSceneManagerDefault>() == null)
                host.AddComponent<NetworkSceneManagerDefault>();
            if (host.GetComponent<NetworkObjectProviderDefault>() == null)
                host.AddComponent<SceneObjectKeepingProvider>();

            var runner = host.GetComponent<NetworkRunner>();
            var watcher = host.AddComponent<NetworkRunnerWatcher>();
            watcher.ShutDown += onShutdown;
            watcher.Disconnected += reason =>
            {
                // Fusion doesn't always shut the runner down by itself when the link to Photon is gone; make sure it does.
                Debug.LogWarning($"[NetworkLauncher] Disconnected from the server ({reason}).", this);
                if (runner != null && !runner.IsShutdown)
                    _ = runner.Shutdown(destroyGameObject: false, ShutdownReason.DisconnectedByPluginLogic);
            };
            return host;
        }

        StartGameArgs BuildArgs(NetworkRunner runner, string session)
        {
            runner.ProvideInput = false; // Shared mode: every player simulates their own objects, no input stream needed.

            // Tell Fusion the scene that is already loaded, so NetworkObjects placed in it are registered.
            // (A scene that isn't in the build settings has index -1 and can't be referenced: FromIndex throws for it.)
            var sceneInfo = new NetworkSceneInfo();
            int buildIndex = gameObject.scene.buildIndex;
            if (buildIndex >= 0)
            {
                var sceneRef = SceneRef.FromIndex(buildIndex);
                if (sceneRef.IsValid)
                    sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Additive);
            }

            // Same settings as PhotonAppSettings (App ID ...), except the region is pinned.
            var photon = new FusionAppSettings();
            PhotonAppSettings.Global.AppSettings.CopyTo(photon);
            if (!string.IsNullOrWhiteSpace(region))
                photon.FixedRegion = region.Trim().ToLowerInvariant();

            return new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = session,
                PlayerCount = maxPlayers,
                Scene = sceneInfo,
                SceneManager = runner.GetComponent<NetworkSceneManagerDefault>(),
                ObjectProvider = runner.GetComponent<NetworkObjectProviderDefault>(),
                CustomPhotonAppSettings = photon,
            };
        }

        // ---- Connect / keep connected ----

        async Task RunLoop()
        {
            var cancel = destroyCancellationToken;
            int failuresInARow = 0;
            int roomNumber = 1;

            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    Attempt++;
                    CurrentRoom = roomNumber <= 1 ? roomName : $"{roomName}-{roomNumber}";
                    State = LinkState.Connecting;

                    ShutdownReason? shutdownReason = null;
                    GameObject host = null;
                    NetworkRunner runner = null;
                    bool joined = false;
                    string problem = null;
                    ShutdownReason failReason = ShutdownReason.Ok;
                    try
                    {
                        host = CreateHost(Attempt, reason => shutdownReason = reason);
                        runner = host.GetComponent<NetworkRunner>();
                        Runner = runner;

                        string regionLabel = string.IsNullOrWhiteSpace(region) ? "best region" : $"region '{region.Trim()}'";
                        Debug.Log($"[NetworkLauncher] Joining Shared room '{CurrentRoom}' in {regionLabel} (attempt {Attempt})...", this);

                        Task<StartGameResult> start = runner.StartGame(BuildArgs(runner, CurrentRoom));
                        Task finished = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(attemptTimeout), cancel));
                        if (finished == start)
                        {
                            StartGameResult result = await start;
                            joined = result.Ok;
                            failReason = result.ShutdownReason;
                            problem = $"{result.ShutdownReason} {result.ErrorMessage}".Trim();
                        }
                        else
                        {
                            cancel.ThrowIfCancellationRequested();
                            failReason = ShutdownReason.OperationTimeout;
                            problem = $"no answer after {attemptTimeout:0}s";
                            _ = runner.Shutdown(destroyGameObject: false, ShutdownReason.OperationTimeout);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        failReason = ShutdownReason.Error;
                        problem = e.Message;
                        Debug.LogException(e, this);
                    }

                    if (joined)
                    {
                        failuresInARow = 0;
                        LastProblem = "";
                        State = LinkState.Connected;
                        Debug.Log($"[NetworkLauncher] Joined '{CurrentRoom}' in region '{runner.SessionInfo.Region}' as player {runner.LocalPlayer} " +
                                  $"({runner.ActivePlayers.Count()} in the room).", this);
                        firstJoin.TrySetResult(true);

                        while (runner != null && runner.IsRunning)
                            await Task.Delay(250, cancel);

                        problem = $"connection lost ({(shutdownReason.HasValue ? shutdownReason.Value.ToString() : "no reason given")})";
                        Debug.LogWarning($"[NetworkLauncher] '{CurrentRoom}': {problem}. Rejoining...", this);
                    }
                    else
                    {
                        failuresInARow++;
                        Debug.LogError($"[NetworkLauncher] Could not join '{CurrentRoom}': {problem}", this);
                        if (failReason == ShutdownReason.GameIsFull)
                            roomNumber++;   // the room has all its players: use the next one
                    }

                    LastProblem = problem ?? "";
                    DisposeHost(host, runner);

                    // Wait before the next try: 2 s, 4 s, 8 s ... up to the maximum. A fresh drop (the failure count just reset) retries after the first delay.
                    float wait = Mathf.Min(retryDelay * Mathf.Pow(2f, Mathf.Max(0, failuresInARow - 1)), maxRetryDelay);
                    skipWait = false;
                    State = LinkState.Retrying;
                    for (float left = wait; left > 0f && !skipWait; left -= 0.1f)
                    {
                        RetryIn = left;
                        await Task.Delay(100, cancel);
                    }
                    RetryIn = 0f;
                }
            }
            catch (OperationCanceledException)
            {
                // this component was destroyed
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
            finally
            {
                firstJoin.TrySetResult(false);
            }
        }

        void DisposeHost(GameObject host, NetworkRunner runner)
        {
            if (runner != null && !runner.IsShutdown)
                _ = runner.Shutdown(destroyGameObject: false);
            if (host != null)
                Destroy(host);
            if (Runner == runner)
                Runner = null;
        }
    }
}
