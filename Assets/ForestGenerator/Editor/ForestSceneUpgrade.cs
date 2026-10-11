using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// Brings forest scenes made by an older version of the forest tools up to date when they are opened in the editor
    /// (once per forest, recorded in Forest Generator's setup version):
    ///   1. adds the flashlight (A button, default cookie), and gives the volumetric fog room for its light.
    /// The scene is marked changed when something was added: save it to keep it.
    /// </summary>
    [InitializeOnLoad]
    public static class ForestSceneUpgrade
    {
        public const int CurrentVersion = 1;

        static ForestSceneUpgrade()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            EditorSceneManager.sceneOpened += (scene, mode) => Upgrade(scene);
            EditorApplication.delayCall += UpgradeOpenScenes;
        }

        static void UpgradeOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) Upgrade(SceneManager.GetSceneAt(i));
        }

        public static void Upgrade(Scene scene)
        {
            if (!scene.isLoaded || EditorApplication.isPlayingOrWillChangePlaymode) return;
            bool changed = false;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (ForestGenerator gen in root.GetComponentsInChildren<ForestGenerator>(true))
                {
                    if (gen.setupVersion >= CurrentVersion) continue;
                    if (gen.setupVersion < 1)
                    {
                        if (gen.GetComponent<ForestFlashlight>() == null)
                        {
                            ForestSetup.AddFlashlight(gen.gameObject);
                            Debug.Log("[Havoc Forest] Added the flashlight (A button) to '" + gen.name + "' in " + scene.name + ". Save the scene to keep it.", gen);
                        }
                        // Night: a smaller clear zone than the day look's 0.3 (see Forest Atmosphere > Clear Zone).
                        var atmosphere = gen.GetComponent<ForestAtmosphere>();
                        if (atmosphere != null && Mathf.Approximately(atmosphere.clearZone, 0.3f)) { atmosphere.clearZone = 0.2f; EditorUtility.SetDirty(atmosphere); }

                        // The fog was made with room for the 2 lamp lights only; the flashlight is a third. Its back
                        // scattering also needs to be stronger to show a beam pointing away from the eyes, and the glow
                        // right next to a light softer, or the flashlight blinds the eyes in the fog.
                        var fog = gen.GetComponentInChildren<VRFog.VolumetricFog>(true);
                        if (fog != null && fog.GetComponent<ForestFogVolume>() != null)
                        {
                            if (fog.maxLights == 2) fog.maxLights = 3;
                            if (Mathf.Approximately(fog.anisotropy, 0.25f)) fog.anisotropy = 0.1f;
                            if (Mathf.Approximately(fog.lightCoreRadius, 1.2f)) fog.lightCoreRadius = 3f;   // softer glare next to the flashlight
                            EditorUtility.SetDirty(fog);
                            if (fog.enabled) { fog.enabled = false; fog.enabled = true; }   // re-applies its material
                        }
                    }
                    gen.setupVersion = CurrentVersion;
                    EditorUtility.SetDirty(gen);
                    changed = true;
                }
            if (changed) EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
