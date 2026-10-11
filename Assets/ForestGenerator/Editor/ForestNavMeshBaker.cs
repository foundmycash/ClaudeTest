using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// Bakes a NavMesh for the monster (AI Navigation package):
    ///   1. makes (or updates) a "Forest Monster" agent type in Project Settings > Navigation from the Monster settings:
    ///      radius = half its width, height, step height, steepest slope;
    ///   2. adds a NavMesh Surface to the Forest object and bakes it from the colliders (terrain, trunks, rocks, logs and the
    ///      hidden walls, which fence the NavMesh in; the ceiling is left out);
    ///   3. saves the result next to the forest's other files and checks that every clearing, monster gap and lair can be
    ///      reached from the trailhead.
    /// Re-bake after regenerating the forest or changing the monster's size.
    /// </summary>
    public static class ForestNavMeshBaker
    {
        public const string AgentName = "Forest Monster";

        /// <summary>The agent type id for the monster, created or updated from the profile.</summary>
        public static int EnsureAgentType(ForestMonsterProfile m)
        {
            Object asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset")[0];
            var so = new SerializedObject(asset);
            SerializedProperty settings = so.FindProperty("m_Settings");
            SerializedProperty names = so.FindProperty("m_SettingNames");
            int index = -1;
            for (int i = 0; i < names.arraySize; i++)
                if (names.GetArrayElementAtIndex(i).stringValue == AgentName) index = i;
            if (index < 0)
            {
                var used = new HashSet<int>();
                for (int i = 0; i < settings.arraySize; i++) used.Add(settings.GetArrayElementAtIndex(i).FindPropertyRelative("agentTypeID").intValue);
                int id = ForestRandom.StableHash(AgentName) & 0x7FFFFFFF;
                while (used.Contains(id)) id++;
                settings.InsertArrayElementAtIndex(settings.arraySize);      // a copy of the last one, then made ours
                names.InsertArrayElementAtIndex(names.arraySize);
                index = settings.arraySize - 1;
                names.GetArrayElementAtIndex(index).stringValue = AgentName;
                settings.GetArrayElementAtIndex(index).FindPropertyRelative("agentTypeID").intValue = id;
            }
            SerializedProperty el = settings.GetArrayElementAtIndex(index);
            el.FindPropertyRelative("agentRadius").floatValue = m.width * 0.5f;
            el.FindPropertyRelative("agentHeight").floatValue = m.height;
            el.FindPropertyRelative("agentSlope").floatValue = m.maxSlope;
            el.FindPropertyRelative("agentClimb").floatValue = m.stepHeight;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            return el.FindPropertyRelative("agentTypeID").intValue;
        }

        public static string Bake(ForestGenerator gen)
        {
            if (gen.settings == null || gen.World.data == null) return "Generate the forest first.";
            ForestData data = gen.World.data;
            int agent = EnsureAgentType(gen.settings.monster);

            NavMeshSurface surface = gen.GetComponent<NavMeshSurface>();
            if (surface == null) surface = gen.gameObject.AddComponent<NavMeshSurface>();
            surface.agentTypeID = agent;
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = ~0;
            surface.overrideVoxelSize = true;
            surface.voxelSize = Mathf.Clamp(gen.settings.monster.width * 0.5f / 3f, 0.15f, 0.6f);
            surface.overrideTileSize = true;
            surface.tileSize = 128;
            surface.minRegionArea = 6f;

            // the colliders must exist (they are rebuilt on load)
            gen.World.BuildColliders();
            // The hidden walls stay in: they cut the NavMesh off at the edge, so a NavMesh agent (which does not collide with walls)
            // can never route out over a gentle slope. Only the ceiling is left out (its top would be "ground").
            Transform boundary = gen.transform.Find(ForestGenerator.RootName + "/Boundary/Ceiling");
            bool boundaryActive = boundary != null && boundary.gameObject.activeSelf;
            if (boundary != null) boundary.gameObject.SetActive(false);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                surface.BuildNavMesh();
            }
            finally
            {
                if (boundary != null) boundary.gameObject.SetActive(boundaryActive);
            }
            float seconds = (float)timer.Elapsed.TotalSeconds;

            if (surface.navMeshData != null && gen.saveWithScene)
            {
                string path = gen.SaveFolder + "/MonsterNavMesh.asset";
                if (AssetDatabase.LoadAssetAtPath<NavMeshData>(path) != null) AssetDatabase.DeleteAsset(path);
                surface.navMeshData.name = "MonsterNavMesh";
                AssetDatabase.CreateAsset(surface.navMeshData, path);
                AssetDatabase.SaveAssets();
            }
            data.navMeshHash = data.contentHash;
            EditorUtility.SetDirty(data);
            EditorUtility.SetDirty(surface);
            EditorSceneManager.MarkSceneDirty(gen.gameObject.scene);

            return "Baked the monster NavMesh in " + seconds.ToString("0.0") + " s. " + CheckReachability(gen, agent);
        }

        /// <summary>Paths from the trailhead to every clearing, monster gap and lair, for the monster agent.</summary>
        public static string CheckReachability(ForestGenerator gen, int agent)
        {
            ForestWorld world = gen.World;
            var filter = new NavMeshQueryFilter { agentTypeID = agent, areaMask = NavMesh.AllAreas };
            NavMeshHit start;
            if (!NavMesh.SamplePosition(world.SpawnPose.position, out start, 6f, filter))
                return "The trailhead is not on the NavMesh!";
            var targets = new List<ForestMarker>();
            world.GetMarkers(ForestMarkerType.Clearing, targets);
            world.GetMarkers(ForestMarkerType.Heart, targets);
            world.GetMarkers(ForestMarkerType.MonsterGap, targets);
            world.GetMarkers(ForestMarkerType.MonsterLair, targets);
            world.GetMarkers(ForestMarkerType.DeadEnd, targets);
            int ok = 0;
            var failed = new List<string>();
            var path = new NavMeshPath();
            foreach (ForestMarker m in targets)
            {
                NavMeshHit end;
                if (NavMesh.SamplePosition(m.position, out end, 6f, filter) && NavMesh.CalculatePath(start.position, end.position, filter, path) && path.status == NavMeshPathStatus.PathComplete) ok++;
                else failed.Add(m.type + " at " + m.position.ToString("0"));
            }
            return ok + " of " + targets.Count + " clearings, dead ends, monster gaps and lairs reachable from the trailhead" + (failed.Count > 0 ? ". Not reachable: " + string.Join(", ", failed) : ".");
        }
    }
}
