using System.Collections.Generic;
using HavocForest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MonsterAI.Editor
{
    /// <summary>
    /// Tools > Monster > Respawn Points > Create 5 From Forest Markers
    ///   Picks 5 open spots (clearings, junctions, the heart, the trailhead) as far apart from each other as possible,
    ///   so they cover the map, and puts a RespawnPoint on each under a "Respawn Points" object. Undoable; save the
    ///   scene afterwards. Move them by hand if you want other places.
    /// Tools > Monster > Respawn Points > Add One At Scene View
    ///   Adds a single point on the ground in front of the Scene view camera.
    /// </summary>
    public static class RespawnPointSetup
    {
        const string ParentName = "Respawn Points";
        const int Count = 5;

        [MenuItem("Tools/Monster/Respawn Points/Create 5 From Forest Markers")]
        static void CreateFive()
        {
            ForestGenerator forest = Object.FindFirstObjectByType<ForestGenerator>();
            ForestWorld world = forest != null ? forest.World : null;
            if (world == null || world.data == null)
            {
                EditorUtility.DisplayDialog("Respawn points", "No generated forest in the open scene. Open the Forest scene (and generate it) first.", "OK");
                return;
            }

            var markers = new List<ForestMarker>();
            world.GetMarkers(ForestMarkerType.Spawn, markers);
            world.GetMarkers(ForestMarkerType.Clearing, markers);
            world.GetMarkers(ForestMarkerType.Heart, markers);
            world.GetMarkers(ForestMarkerType.Junction, markers);
            if (markers.Count < Count)
            {
                EditorUtility.DisplayDialog("Respawn points", "The forest has only " + markers.Count + " usable markers. Use 'Add One At Scene View' to place points by hand.", "OK");
                return;
            }

            GameObject existing = GameObject.Find(ParentName);
            if (existing != null)
            {
                if (!EditorUtility.DisplayDialog("Respawn points", "'" + ParentName + "' already exists. Replace it (your moved points are lost)?", "Replace", "Cancel"))
                    return;
                Undo.DestroyObjectImmediate(existing);
            }

            // Farthest-point sampling: start at the marker farthest from the middle of all of them, then keep taking
            // the marker whose nearest chosen point is the farthest away.
            Vector3 centre = Vector3.zero;
            foreach (ForestMarker m in markers)
                centre += m.position;
            centre /= markers.Count;

            var chosen = new List<ForestMarker>();
            chosen.Add(Farthest(markers, new List<Vector3> { centre }));
            while (chosen.Count < Count)
            {
                var from = new List<Vector3>();
                foreach (ForestMarker c in chosen)
                    from.Add(c.position);
                chosen.Add(Farthest(markers, from));
            }

            var parent = new GameObject(ParentName);
            Undo.RegisterCreatedObjectUndo(parent, "Create respawn points");

            for (int i = 0; i < chosen.Count; i++)
            {
                Vector3 p = chosen[i].position;
                p.y = world.GroundHeight(p);

                Vector3 toCentre = centre - p;
                toCentre.y = 0f;
                Quaternion rot = toCentre.sqrMagnitude > 1f ? Quaternion.LookRotation(toCentre.normalized, Vector3.up) : Quaternion.Euler(0f, chosen[i].yaw, 0f);

                var go = new GameObject("Respawn Point " + (i + 1));
                go.transform.SetParent(parent.transform, false);
                go.transform.SetPositionAndRotation(p, rot);
                go.AddComponent<RespawnPoint>();
            }

            Selection.activeGameObject = parent;
            EditorSceneManager.MarkSceneDirty(parent.scene);
            Debug.Log("Created " + Count + " respawn points under '" + ParentName + "'. Save the scene (Ctrl+S).", parent);
        }

        [MenuItem("Tools/Monster/Respawn Points/Add One At Scene View")]
        static void AddOne()
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                EditorUtility.DisplayDialog("Respawn points", "Open a Scene view first.", "OK");
                return;
            }

            Vector3 pos = view.camera.transform.position + view.camera.transform.forward * 8f;
            Ray ray = new Ray(view.camera.transform.position, view.camera.transform.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, 200f))
                pos = hit.point;

            GameObject parent = GameObject.Find(ParentName);
            if (parent == null)
            {
                parent = new GameObject(ParentName);
                Undo.RegisterCreatedObjectUndo(parent, "Add respawn point");
            }

            var go = new GameObject("Respawn Point " + (parent.transform.childCount + 1));
            go.transform.SetParent(parent.transform, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, view.camera.transform.eulerAngles.y, 0f));
            go.AddComponent<RespawnPoint>();
            Undo.RegisterCreatedObjectUndo(go, "Add respawn point");
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);
        }

        static ForestMarker Farthest(List<ForestMarker> candidates, List<Vector3> from)
        {
            ForestMarker best = candidates[0];
            float bestScore = -1f;
            foreach (ForestMarker m in candidates)
            {
                float nearest = float.MaxValue;
                foreach (Vector3 f in from)
                {
                    Vector3 d = m.position - f;
                    d.y = 0f;
                    nearest = Mathf.Min(nearest, d.magnitude);
                }
                if (nearest > bestScore)
                {
                    bestScore = nearest;
                    best = m;
                }
            }
            return best;
        }
    }
}
