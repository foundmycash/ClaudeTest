using System.Collections.Generic;
using System.IO;
using GorillaNet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// Brings the game's player and multiplayer into a forest scene (Tools > Havoc Forest > Add Player And Multiplayer):
    ///   - the Gorilla Rig prefab (Assets/Scenes/Gorilla Rig.prefab, the same rig as in ClaudeTest), unless the scene already
    ///     has an instance of it (an older plain copy of a rig is replaced);
    ///   - the Network object (Fusion's Network Runner, Network Launcher, Avatar Spawner), copied from the ClaudeTest scene
    ///     with all its settings, but in its own room, so players in the forest never share a session with ClaudeTest;
    /// both under a "System" object, as in ClaudeTest. The rig is put on the trailhead and becomes the generator's spawn
    /// target, and the scene is added to the Build Settings (the Loading scene loads it by name). One Undo step reverts
    /// everything; nothing is saved.
    /// Nothing else is needed in the scene: the offline rig, the network status label, hand contact feedback, Quest
    /// performance settings and the wake-up intro create themselves at runtime.
    /// </summary>
    public static class ForestGameSetup
    {
        public const string RigPrefabGuid = "59a32eba32a8bba4eba11b5d6775cc69";       // Assets/Scenes/Gorilla Rig.prefab
        public const string NetworkSourceScene = "Assets/Scenes/ClaudeTest.unity";
        const string AvatarPrefabPath = "Assets/Prefabs/Networking/NetworkedAvatar.prefab";
        public const string ForestRoomName = "HavocForest";
        const string SystemName = "System";

        [MenuItem("Tools/Havoc Forest/Add Player And Multiplayer")]
        static void Menu()
        {
            Scene scene = SceneManager.GetActiveScene();
            string result = Run(scene);
            Debug.Log("[Havoc Forest] " + result);
            EditorUtility.DisplayDialog("Havoc Forest", result + "\n\nSave the scene (Ctrl+S) to keep it. Edit > Undo takes it all back.", "OK");
        }

        [MenuItem("Tools/Havoc Forest/Add Player And Multiplayer", true)]
        static bool MenuValid()
        {
            return !Application.isPlaying && FindGenerator(SceneManager.GetActiveScene()) != null;
        }

        /// <summary>Adds (or completes) the player rig and the multiplayer objects in a forest scene. Returns what was done.</summary>
        public static string Run(Scene scene)
        {
            ForestGenerator gen = FindGenerator(scene);
            if (gen == null) return "This scene has no Forest Generator.";

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Add Player And Multiplayer");
            var done = new List<string>();

            // ---- the rig
            GameObject prefab = RigPrefab();
            GameObject rig = FindRig(scene);
            if (rig != null && prefab != null && !IsInstanceOf(rig, prefab))
            {
                done.Add("replaced the old rig '" + rig.name + "' with the Gorilla Rig prefab");
                Undo.DestroyObjectImmediate(rig);
                rig = null;
            }
            if (rig == null)
            {
                if (prefab != null) rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                else rig = ForestSetup.CopyRig(scene);     // no prefab: a copy of ClaudeTest's rig, else the stock prefab
                if (rig == null) return "No player rig found: neither Assets/Scenes/Gorilla Rig.prefab nor a rig in ClaudeTest.";
                Undo.RegisterCreatedObjectUndo(rig, "Add Player");
                done.Add("added the Gorilla Rig" + (prefab != null ? " prefab" : " (a copy, the prefab is missing)"));
            }
            else done.Add("kept the Gorilla Rig already in the scene");

            GameObject system = FindOrCreateRoot(scene, SystemName);
            if (rig.transform.parent != system.transform) Undo.SetTransformParent(rig.transform, system.transform, "Add Player");

            // ---- multiplayer
            NetworkLauncher launcher = FindInScene<NetworkLauncher>(scene);
            if (launcher == null)
            {
                GameObject network = CopyNetworkFromClaudeTest(scene) ?? CreateNetwork(scene);
                Undo.RegisterCreatedObjectUndo(network, "Add Multiplayer");
                network.name = "Network";
                Undo.SetTransformParent(network.transform, system.transform, "Add Multiplayer");
                network.transform.SetSiblingIndex(0);
                launcher = network.GetComponent<NetworkLauncher>();
                launcher.roomName = ForestRoomName;
                done.Add("added multiplayer (room '" + ForestRoomName + "', region " + launcher.region + ")");
            }
            else done.Add("kept the multiplayer already in the scene (room '" + launcher.roomName + "')");

            // ---- the forest uses this rig
            Undo.RecordObject(gen, "Add Player");
            gen.movePlayerToSpawn = rig.transform;
            if (gen.World != null && gen.World.data != null)
            {
                Pose pose = gen.World.SpawnPose;
                Undo.RecordObject(rig.transform, "Add Player");
                rig.transform.SetPositionAndRotation(pose.position + Vector3.up * gen.spawnHeightOffset, pose.rotation);
                done.Add("put the rig on the trailhead");
            }
            else done.Add("the forest is not generated yet: Generate puts the rig on the trailhead");

            if (!string.IsNullOrEmpty(scene.path) && !InBuild(scene.path))
            {
                ForestSetup.AddToBuildSettings(scene.path);
                done.Add("added " + Path.GetFileNameWithoutExtension(scene.path) + " to the Build Settings");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = system;
            return "Player and multiplayer: " + string.Join("; ", done) + ".";
        }

        static ForestGenerator FindGenerator(Scene scene)
        {
            return scene.IsValid() && scene.isLoaded ? FindInScene<ForestGenerator>(scene) : null;
        }

        static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T c = root.GetComponentInChildren<T>(true);
                if (c != null) return c;
            }
            return null;
        }

        static GameObject RigPrefab()
        {
            string path = AssetDatabase.GUIDToAssetPath(RigPrefabGuid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        /// <summary>The rig that holds the scene's Gorilla Player: its prefab instance, else its top object (below System).</summary>
        static GameObject FindRig(Scene scene)
        {
            var player = FindInScene<GorillaLocomotion.Player>(scene);
            if (player == null) return null;
            GameObject instance = PrefabUtility.GetOutermostPrefabInstanceRoot(player.gameObject);
            if (instance != null) return instance;
            Transform t = player.transform;
            while (t.parent != null && t.parent.name != SystemName) t = t.parent;
            return t.gameObject;
        }

        static bool IsInstanceOf(GameObject instance, GameObject prefab)
        {
            return PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance) == AssetDatabase.GetAssetPath(prefab);
        }

        static GameObject FindOrCreateRoot(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == name) return root;
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);
            Undo.RegisterCreatedObjectUndo(go, "Add Player");
            return go;
        }

        /// <summary>A copy of ClaudeTest's Network object, with every setting the way it is there.</summary>
        static GameObject CopyNetworkFromClaudeTest(Scene target)
        {
            if (!File.Exists(NetworkSourceScene) || target.path == NetworkSourceScene) return null;
            Scene src = SceneManager.GetSceneByPath(NetworkSourceScene);
            bool opened = false;
            if (!src.isLoaded)
            {
                src = EditorSceneManager.OpenScene(NetworkSourceScene, OpenSceneMode.Additive);
                opened = true;
            }
            try
            {
                NetworkLauncher source = FindInScene<NetworkLauncher>(src);
                if (source == null) return null;
                GameObject copy = Object.Instantiate(source.gameObject);
                copy.transform.SetParent(null, false);
                SceneManager.MoveGameObjectToScene(copy, target);
                return copy;
            }
            finally
            {
                if (opened) EditorSceneManager.CloseScene(src, true);
            }
        }

        /// <summary>The Network object from scratch, when ClaudeTest has none to copy.</summary>
        static GameObject CreateNetwork(Scene target)
        {
            var go = new GameObject("Network");
            SceneManager.MoveGameObjectToScene(go, target);
            var spawner = go.AddComponent<AvatarSpawner>();
            var avatar = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefabPath);
            if (avatar != null) spawner.avatarPrefab = avatar.GetComponent<Fusion.NetworkObject>();
            go.AddComponent<NetworkLauncher>();
            go.AddComponent<Fusion.NetworkRunner>();
            return go;
        }

        static bool InBuild(string path)
        {
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                if (s.path == path && s.enabled) return true;
            return false;
        }
    }
}
