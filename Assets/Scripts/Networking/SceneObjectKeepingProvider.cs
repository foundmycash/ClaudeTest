using Fusion;

namespace GorillaNet
{
    /// <summary>
    /// Fusion's default object provider destroys network objects that were placed in the scene (the grab cube) when a
    /// runner shuts down. That is fine for a game that reloads its scene afterwards, but <see cref="NetworkLauncher"/> rejoins
    /// the lobby with a new runner without reloading anything, and the cube would be gone for good.
    /// This provider switches scene objects off instead; the next runner finds them again (inactive objects included)
    /// and registers them.
    /// </summary>
    public class SceneObjectKeepingProvider : NetworkObjectProviderDefault
    {
        protected override void DestroySceneObject(NetworkRunner runner, NetworkSceneObjectId sceneObjectId, NetworkObject instance)
        {
            if (instance != null)
                instance.gameObject.SetActive(false);
        }
    }
}
