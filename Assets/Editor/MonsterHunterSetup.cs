using System.Text;
using FIMSpace.FProceduralAnimation;
using Fusion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace MonsterAI.Editor
{
    /// <summary>
    /// Menu commands for the monster's hunting AI:
    ///   Tools > Monster > Set Up Hunting AI On Selected Monster   adds and wires every component (undoable; save the scene afterwards)
    ///   GameObject > Monster > Safe Zone                          creates a safe-zone volume in front of the Scene view camera
    /// </summary>
    public static class MonsterHunterSetup
    {
        const string ForestAgentName = "Forest Monster";

        [MenuItem("Tools/Monster/Set Up Hunting AI On Selected Monster")]
        static void SetUpSelected()
        {
            GameObject monster = Selection.activeGameObject != null ? Selection.activeGameObject : FindMonster();
            if (monster == null)
            {
                EditorUtility.DisplayDialog("Monster hunting AI", "Select the Monster object in the Hierarchy first (the one with the NavMesh Agent), then run this again.", "OK");
                return;
            }

            string report = Apply(monster);
            Debug.Log("Monster hunting AI set up on '" + monster.name + "':\n" + report, monster);
            EditorUtility.DisplayDialog("Monster hunting AI", report + "\nSave the scene (Ctrl+S) to keep it.", "OK");
        }

        [MenuItem("Tools/Monster/Set Up Hunting AI On Selected Monster", true)]
        static bool CanSetUp()
        {
            return !Application.isPlaying;
        }

        [MenuItem("Tools/Monster/Make Monster Networked (Fusion)")]
        static void NetworkSelected()
        {
            GameObject monster = Selection.activeGameObject != null ? Selection.activeGameObject : FindMonster();
            if (monster == null || monster.GetComponent<MonsterHunter>() == null)
            {
                EditorUtility.DisplayDialog("Monster network", "Select the Monster object (with the hunting AI already set up) first.", "OK");
                return;
            }

            string report = AddNetwork(monster);
            Debug.Log("Monster made networked:\n" + report, monster);
            EditorUtility.DisplayDialog("Monster network", report + "\nSave the scene (Ctrl+S) to keep it.", "OK");
        }

        [MenuItem("Tools/Monster/Make Monster Networked (Fusion)", true)]
        static bool CanNetwork()
        {
            return !Application.isPlaying;
        }

        /// <summary>
        /// Makes the monster a shared scene object: NetworkObject (master client owns it) + NetworkTransform + MonsterNetworkLink.
        /// Undoable, marks the scene dirty, does not save.
        /// </summary>
        public static string AddNetwork(GameObject monster)
        {
            var report = new StringBuilder();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Make monster networked");

            NetworkObject networkObject = monster.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                networkObject = Undo.AddComponent<NetworkObject>(monster);
                report.AppendLine("- Added a Network Object.");
            }

            // The master client of the room owns the monster, and ownership moves to the new master client when it leaves.
            var so = new SerializedObject(networkObject);
            SerializedProperty flags = so.FindProperty("Flags");
            if (flags != null)
            {
                int wanted = flags.intValue | (int)NetworkObjectFlags.MasterClientObject;
                wanted &= ~(int)NetworkObjectFlags.DestroyWhenStateAuthorityLeaves;
                if (wanted != flags.intValue)
                {
                    flags.intValue = wanted;
                    so.ApplyModifiedProperties();
                    report.AppendLine("- Network Object: owned by the room's master client, not destroyed when its owner leaves.");
                }
            }
            else
            {
                report.AppendLine("- Could not find the Flags of the Network Object; tick \"Is Master Client Object\" on it yourself.");
            }

            NetworkTransform networkTransform = monster.GetComponent<NetworkTransform>();
            if (networkTransform == null)
            {
                networkTransform = Undo.AddComponent<NetworkTransform>(monster);
                report.AppendLine("- Added a Network Transform (sends the monster's position and rotation).");
            }

            // The NavMesh agent moves the monster every frame, not inside Fusion's network tick. With Shared Mode Interpolation on, the
            // owner's own copy keeps being pulled back to the last network position and the monster crawls. (The player avatars use
            // the same setting for the same reason: ConfigFlags bit 0 = "Disable Shared Mode Interpolation".) Other players still
            // see it interpolated.
            var transformObject = new SerializedObject(networkTransform);
            SerializedProperty configFlags = transformObject.FindProperty("ConfigFlags");
            if (configFlags != null && (configFlags.intValue & 1) == 0)
            {
                configFlags.intValue |= 1;
                transformObject.ApplyModifiedProperties();
                report.AppendLine("- Network Transform: Shared Mode Interpolation off for the owner (the agent moves it outside the network tick).");
            }

            AddIfMissing<MonsterNetworkLink>(monster, report);

            if (report.Length == 0)
                report.AppendLine("Everything was already set up.");
            if (!Application.isPlaying && monster.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(monster.scene);
            return report.ToString();
        }

        /// <summary>Adds and wires the hunting components on this object. Undoable, marks the scene dirty, does not save. Returns what it did.</summary>
        public static string Apply(GameObject monster)
        {
            var report = new StringBuilder();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Set up monster hunting AI");

            NavMeshAgent agent = monster.GetComponent<NavMeshAgent>();
            if (agent == null)
            {
                agent = Undo.AddComponent<NavMeshAgent>(monster);
                report.AppendLine("- Added a NavMesh Agent.");
            }

            Undo.RecordObject(agent, "Monster agent");
            int forestType;
            if (TryFindAgentType(ForestAgentName, out forestType) && agent.agentTypeID != forestType)
            {
                agent.agentTypeID = forestType;
                report.AppendLine("- NavMesh Agent type set to \"" + ForestAgentName + "\" (the forest NavMesh is baked for it; the agent was on another type and could not path).");
            }
            agent.autoBraking = false;
            agent.updateRotation = false;
            EditorUtility.SetDirty(agent);

            AddIfMissing<MonsterBody>(monster, report);
            AddIfMissing<MonsterSenses>(monster, report);
            AddIfMissing<MonsterHunter>(monster, report);
            AddIfMissing<MonsterFootsteps>(monster, report);
            if (monster.GetComponent<LegsAnimator>() != null)
                AddIfMissing<MonsterLegsLink>(monster, report);
            else
                report.AppendLine("- No Legs Animator on the monster: footsteps use the distance timing.");

            if (monster.GetComponent<MonsterGaze>() == null)
                report.AppendLine("- (Optional) add Monster Gaze to make the head follow players it can see.");

            if (report.Length == 0)
                report.AppendLine("Everything was already set up.");

            if (!Application.isPlaying && monster.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(monster.scene);
            return report.ToString();
        }

        static void AddIfMissing<T>(GameObject go, StringBuilder report) where T : Component
        {
            if (go.GetComponent<T>() != null)
                return;
            Undo.AddComponent<T>(go);
            report.AppendLine("- Added " + typeof(T).Name + ".");
        }

        static bool TryFindAgentType(string agentName, out int id)
        {
            int count = NavMesh.GetSettingsCount();
            for (int i = 0; i < count; i++)
            {
                NavMeshBuildSettings s = NavMesh.GetSettingsByIndex(i);
                if (NavMesh.GetSettingsNameFromID(s.agentTypeID) == agentName)
                {
                    id = s.agentTypeID;
                    return true;
                }
            }
            id = 0;
            return false;
        }

        static GameObject FindMonster()
        {
            MonsterGaze gaze = Object.FindFirstObjectByType<MonsterGaze>();
            if (gaze != null)
                return gaze.gameObject;
            MonsterHunter hunter = Object.FindFirstObjectByType<MonsterHunter>();
            if (hunter != null)
                return hunter.gameObject;
            return GameObject.Find("Monster");
        }

        [MenuItem("GameObject/Monster/Safe Zone", false, 10)]
        static void CreateSafeZone(MenuCommand command)
        {
            var go = new GameObject("Safe Zone");
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);

            SceneView view = SceneView.lastActiveSceneView;
            if (view != null && command.context == null)
            {
                Vector3 p = view.pivot;
                go.transform.position = new Vector3(p.x, p.y, p.z);
            }

            Undo.RegisterCreatedObjectUndo(go, "Create Safe Zone");
            Undo.AddComponent<SafeZone>(go);       // Reset() adds the trigger box
            Selection.activeGameObject = go;
        }
    }
}
