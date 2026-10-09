using System.Threading.Tasks;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GorillaNet
{
    /// <summary>
    /// Joins a Photon Fusion Shared-mode room as soon as the scene starts.
    /// Put it on a GameObject that also has a <see cref="NetworkRunner"/>. Everybody who uses the same
    /// room name ends up in the same session. The Photon App ID comes from Fusion's PhotonAppSettings.
    /// </summary>
    [RequireComponent(typeof(NetworkRunner))]
    public class NetworkLauncher : MonoBehaviour
    {
        [Tooltip("Players using the same room name are put in the same session.")]
        public string roomName = "GorillaTestRoom";

        [Tooltip("Maximum players in the room (Gorilla Tag uses 10).")]
        public int maxPlayers = 10;

        [Tooltip("Connect from Start(). Turn off to call Connect() yourself, e.g. from a room-code menu.")]
        public bool autoStart = true;

        public NetworkRunner Runner { get; private set; }
        public bool IsConnected => Runner != null && Runner.IsRunning;

        bool starting;

        async void Start()
        {
            if (autoStart)
                await Connect();
        }

        public async Task Connect()
        {
            if (starting || IsConnected)
                return;
            starting = true;

            Runner = GetComponent<NetworkRunner>();
            Runner.ProvideInput = false; // Shared mode: every player simulates their own objects, no input stream needed.

            var sceneManager = GetComponent<NetworkSceneManagerDefault>();
            if (sceneManager == null)
                sceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>();

            var objectProvider = GetComponent<NetworkObjectProviderDefault>();
            if (objectProvider == null)
                objectProvider = gameObject.AddComponent<NetworkObjectProviderDefault>();

            // Tell Fusion the scene that is already loaded, so NetworkObjects placed in it are registered.
            // (A scene that isn't in the build settings has index -1 and can't be referenced: FromIndex throws for it.)
            var sceneInfo = new NetworkSceneInfo();
            int buildIndex = SceneManager.GetActiveScene().buildIndex;
            if (buildIndex >= 0)
            {
                var sceneRef = SceneRef.FromIndex(buildIndex);
                if (sceneRef.IsValid)
                    sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Additive);
            }

            Debug.Log($"[NetworkLauncher] Joining Shared room '{roomName}'...");
            StartGameResult result = await Runner.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = roomName,
                PlayerCount = maxPlayers,
                Scene = sceneInfo,
                SceneManager = sceneManager,
                ObjectProvider = objectProvider,
            });

            starting = false;
            if (result.Ok)
                Debug.Log($"[NetworkLauncher] Joined '{roomName}' as player {Runner.LocalPlayer}.");
            else
                Debug.LogError($"[NetworkLauncher] Could not join '{roomName}': {result.ShutdownReason} {result.ErrorMessage}");
        }
    }
}
