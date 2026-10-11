using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HavocForest.EditorTools
{
    public struct ForestIssue
    {
        public MessageType type;
        public string text;

        public ForestIssue(MessageType type, string text)
        {
            this.type = type;
            this.text = text;
        }
    }

    /// <summary>Checks a Forest Generator's setup and reports problems before (and after) generating. Shown in its inspector.</summary>
    public static class ForestValidator
    {
        public static List<ForestIssue> Validate(ForestGenerator gen)
        {
            var issues = new List<ForestIssue>();
            ForestSettings s = gen.settings;
            if (s == null)
            {
                issues.Add(new ForestIssue(MessageType.Error, "No Forest Settings assigned. Tools > Havoc Forest > Create Default Assets makes one."));
                return issues;
            }

            // ---- ground
            ForestGroundLayers layers = gen.groundLayers;
            if (layers == null) issues.Add(new ForestIssue(MessageType.Error, "No Ground Layers assigned: the terrain has no material."));
            else
            {
                if (layers.material == null) issues.Add(new ForestIssue(MessageType.Error, "The Ground Layers asset has no material."));
                else if (layers.material.shader == null || layers.material.shader.name != "Havoc/Forest Ground")
                    issues.Add(new ForestIssue(MessageType.Warning, "The ground material does not use the Havoc/Forest Ground shader, so the generated blend maps are not shown."));
                if (layers.albedoArray == null || layers.normalArray == null)
                    issues.Add(new ForestIssue(MessageType.Warning, "The ground texture arrays are not built. Press Build Ground Texture Arrays below."));
                if (layers.layers != null)
                    for (int i = 0; i < layers.layers.Length; i++)
                        if (layers.layers[i] != null && layers.layers[i].albedo == null)
                            issues.Add(new ForestIssue(MessageType.Warning, "Ground surface '" + layers.layers[i].name + "' has no colour texture (it shows as plain grey)."));
            }
            if (s.ground.rockSlopeEnd <= s.ground.rockSlopeStart)
                issues.Add(new ForestIssue(MessageType.Warning, "Ground: Rock Slope End should be steeper than Rock Slope Start."));

            // ---- monster clearance
            ForestMonsterProfile m = s.monster;
            float corridor = m.CorridorHalfWidth * 2f;
            if (s.trails.mainWidth < corridor)
                issues.Add(new ForestIssue(MessageType.Info, "Main trails are widened from " + s.trails.mainWidth.ToString("0.0") + " m to " + corridor.ToString("0.0") + " m to fit the monster (width + margins)."));
            if (s.trails.pathWidth < corridor)
                issues.Add(new ForestIssue(MessageType.Info, "Connecting paths are widened from " + s.trails.pathWidth.ToString("0.0") + " m to " + corridor.ToString("0.0") + " m to fit the monster."));
            if (s.clearings.radius.x < m.MinClearingRadius)
                issues.Add(new ForestIssue(MessageType.Info, "Clearings are made at least " + m.MinClearingRadius.ToString("0.0") + " m in radius so the monster can turn around in them."));
            if (s.trails.playerOnlyPaths && s.trails.playerOnlyWidth >= m.width)
                issues.Add(new ForestIssue(MessageType.Warning, "Player-only paths (" + s.trails.playerOnlyWidth.ToString("0.0") + " m) are as wide as the monster (" + m.width.ToString("0.0") + " m): it could squeeze along them. Make them narrower."));

            // ---- zones
            if (s.zones.zones == null || !s.zones.zones.Any(z => z != null && z.enabled && z.weight > 0f))
                issues.Add(new ForestIssue(MessageType.Warning, "No zone is enabled: the whole forest gets the first zone's look."));

            // ---- the models
            ValidateLibrary(gen.assets, s, issues);

            // ---- size
            float area = s.world.playableSize.x * s.world.playableSize.y;
            float clearingArea = s.clearings.count * s.clearings.spacing * s.clearings.spacing;
            if (clearingArea > area * 0.9f)
                issues.Add(new ForestIssue(MessageType.Warning, "The clearings probably do not all fit: " + s.clearings.count + " clearings " + s.clearings.spacing + " m apart need more room than the playable area."));

            // ---- atmosphere
            ForestAtmosphere atm = gen.GetComponent<ForestAtmosphere>();
            if (atm == null) issues.Add(new ForestIssue(MessageType.Info, "No Forest Atmosphere on this object: fog and light are left as the scene has them."));
            else
            {
                if (atm.calm == null) issues.Add(new ForestIssue(MessageType.Warning, "Forest Atmosphere has no Calm profile."));
                if (atm.sun == null) issues.Add(new ForestIssue(MessageType.Info, "Forest Atmosphere has no sun light assigned."));
                if (atm.fogMode == ForestFogMode.Volumetric && !atm.VolumetricActive)
                    issues.Add(new ForestIssue(MessageType.Warning, "Fog Mode is Volumetric but the Volumetric Fog box is missing or switched off: Unity fog is used instead."));
                if (atm.VolumetricActive && !atm.volumetricFog.useSceneDepth)
                    issues.Add(new ForestIssue(MessageType.Warning, "Use Scene Depth is off on the forest's Volumetric Fog: the fog cannot stop at trees and the ground, so everything looks fully fogged. Turn it back on."));
                if (atm.VolumetricActive && atm.volumetricFog.maxLights < 3 && gen.GetComponent<ForestFlashlight>() != null)
                    issues.Add(new ForestIssue(MessageType.Info, "The Volumetric Fog's Max Lights is " + atm.volumetricFog.maxLights + ": with the flashlight and 2 lamp lights on, one of them does not glow in the fog. 3 lets all of them."));
            }

            // ---- the flashlight
            ForestFlashlight torch = gen.GetComponent<ForestFlashlight>();
            if (torch != null && torch.cookie != null)
            {
                for (int q = 0; q < QualitySettings.count; q++)
                {
                    RenderPipelineAsset rp = QualitySettings.GetRenderPipelineAssetAt(q);
                    if (rp == null) rp = GraphicsSettings.defaultRenderPipeline;
                    if (rp == null) continue;
                    SerializedProperty cookies = new SerializedObject(rp).FindProperty("m_SupportsLightCookies");
                    if (cookies != null && !cookies.boolValue)
                        issues.Add(new ForestIssue(MessageType.Warning, "Light Cookies are off in " + rp.name + " (quality level " + QualitySettings.names[q] + "): the flashlight's beam pattern does not show there. Turn on Lighting > Light Cookies in that asset."));
                }
            }

            // ---- the player
            var player = Object.FindAnyObjectByType<GorillaLocomotion.Player>();
            if (player != null && (player.locomotionEnabledLayers.value & (1 << s.terrain.layer)) == 0)
                issues.Add(new ForestIssue(MessageType.Error, "The Gorilla Player's Locomotion Enabled Layers do not include the forest's layer (" + LayerMask.LayerToName(s.terrain.layer) + "): players would fall through the ground."));
            if (gen.movePlayerToSpawn == null)
                issues.Add(new ForestIssue(MessageType.Info, "Move Player To Spawn is empty: the player rig is not moved to the trailhead when you generate."));

            // ---- the monster's NavMesh
            ForestData data = gen.World != null ? gen.World.data : null;
            var surface = gen.GetComponent<Unity.AI.Navigation.NavMeshSurface>();
            if (data != null && surface == null)
                issues.Add(new ForestIssue(MessageType.Info, "No monster NavMesh yet. Press Bake Monster NavMesh when you need pathfinding."));
            else if (data != null && surface != null && (surface.navMeshData == null || data.navMeshHash != data.contentHash))
                issues.Add(new ForestIssue(MessageType.Warning, "The monster NavMesh is out of date (the forest was regenerated). Press Bake Monster NavMesh."));
            else if (surface != null && UnityEngine.AI.NavMesh.GetSettingsByID(surface.agentTypeID).agentTypeID == -1)
                issues.Add(new ForestIssue(MessageType.Warning, "The '" + ForestNavMeshBaker.AgentName + "' agent type is missing from Project Settings > Navigation. Press Bake Monster NavMesh once to create it."));

            if (Object.FindObjectsByType<ForestWorld>(FindObjectsSortMode.None).Length > 1)
                issues.Add(new ForestIssue(MessageType.Warning, "More than one forest in the open scenes: only one can show its ground at a time."));

            // ---- builds
            string scenePath = gen.gameObject.scene.path;
            if (!string.IsNullOrEmpty(scenePath) && !EditorBuildSettings.scenes.Any(sc => sc.path == scenePath && sc.enabled))
                issues.Add(new ForestIssue(MessageType.Info, "This scene is not in the Build Settings. Unity strips fog from builds unless a scene in the build uses it, so add it before testing on Quest."));

            // ---- results of the last run
            if (gen.LastWarnings != null)
                foreach (string w in gen.LastWarnings) issues.Add(new ForestIssue(MessageType.Warning, w));
            return issues;
        }

        /// <summary>Checks every category and prefab of an asset library against what the forest (and Quest) needs.</summary>
        public static void ValidateLibrary(ForestAssetLibrary lib, ForestSettings settings, List<ForestIssue> issues)
        {
            if (lib == null)
            {
                issues.Add(new ForestIssue(MessageType.Warning, "No Asset Library assigned: only the ground is generated (no trees, rocks or props)."));
                return;
            }
            if (lib.categories == null) return;
            foreach (ForestAssetCategory cat in lib.categories)
            {
                if (cat == null || !cat.enabled) continue;
                string where = "'" + cat.name + "': ";
                if (!cat.HasUsableVariant)
                {
                    issues.Add(new ForestIssue(MessageType.Warning, where + "no prefab assigned (or all weights are 0), so nothing is placed."));
                    continue;
                }
                if (cat.slope.y < cat.slope.x) issues.Add(new ForestIssue(MessageType.Warning, where + "the slope range is upside down."));
                foreach (ForestAssetVariant v in cat.variants)
                {
                    if (v == null || v.prefab == null) continue;
                    ForestPrefabInfo info = ForestPrefabInfo.Get(v.prefab);
                    string w = "'" + v.prefab.name + "' (" + cat.name + "): ";
                    if (!info.HasMeshes && !info.needsGameObject)
                    {
                        issues.Add(new ForestIssue(MessageType.Error, w + "has no MeshFilter + MeshRenderer to draw."));
                        continue;
                    }
                    if (v.scale.y < v.scale.x) issues.Add(new ForestIssue(MessageType.Warning, w + "the scale range is upside down."));

                    if (cat.render == ForestRenderMode.Instanced && !info.instancingReady)
                        issues.Add(new ForestIssue(MessageType.Warning, w + "cannot be GPU-instanced: " + info.instancingProblem + ". Tick Enable GPU Instancing on its materials, or it is placed as GameObjects."));
                    else if (cat.render == ForestRenderMode.Auto && info.HasMeshes && !info.instancingReady && !info.needsGameObject)
                        issues.Add(new ForestIssue(MessageType.Info, w + "will be placed as GameObjects (one draw call each) because " + info.instancingProblem + "."));
                    if (cat.render == ForestRenderMode.Auto && info.needsGameObject && cat.placement == ForestPlacement.Scatter && cat.density > 0.2f)
                        issues.Add(new ForestIssue(MessageType.Info, w + "has " + info.needsGameObjectReason + ", so every copy is a real GameObject. Fine for a few, expensive for many."));

                    // pivot: the base should sit at the pivot
                    float height = info.bounds.size.y;
                    if (info.bounds.min.y > Mathf.Max(0.15f, height * 0.1f))
                        issues.Add(new ForestIssue(MessageType.Warning, w + "its bottom is " + info.bounds.min.y.ToString("0.00") + " m above its pivot, so it floats. Put the pivot at the base."));
                    else if (info.bounds.min.y < -height * 0.35f && cat.kind != ForestAssetKind.GroundCover)
                        issues.Add(new ForestIssue(MessageType.Warning, w + "its pivot is well above its base (half of it would be underground). Put the pivot at the base."));

                    // Quest budgets for the first level of detail
                    int tris = 0;
                    if (info.lods.Count > 0)
                        foreach (ForestPrefabInfo.Part p in info.lods[0].parts) tris += (int)(p.mesh.GetIndexCount(p.submesh) / 3);
                    int budget = cat.kind == ForestAssetKind.Tree ? 4000 : cat.kind == ForestAssetKind.GroundCover ? 300 : 1500;
                    if (tris > budget)
                        issues.Add(new ForestIssue(MessageType.Warning, w + tris.ToString("N0") + " triangles at full detail; for Quest aim under " + budget.ToString("N0") + " for this kind of object."));
                    if (info.lods.Count < 2 && (cat.kind == ForestAssetKind.Tree || cat.kind == ForestAssetKind.Obstacle) && tris > 300)
                        issues.Add(new ForestIssue(MessageType.Info, w + "has no LOD Group, so it is always drawn at full detail. Add lower levels for Quest."));

                    // streetlights: they need the lamp component, and their arm has to clear the monster
                    if (cat.placement == ForestPlacement.Streetlight)
                    {
                        if (v.prefab.GetComponentInChildren<ForestLamp>(true) == null)
                            issues.Add(new ForestIssue(MessageType.Warning, w + "has no Forest Lamp component, so it gives no light. Add one (and point it at the glass and halo)."));
                        if (settings != null && info.bounds.max.z > 0.5f && info.bounds.max.y < settings.monster.OverheadClearance)
                            issues.Add(new ForestIssue(MessageType.Warning, w + "its arm reaches over the trail at " + info.bounds.max.y.ToString("0.0") + " m, lower than the monster needs (" + settings.monster.OverheadClearance.ToString("0.0") + " m)."));
                    }

                    // collision
                    if (cat.colliders && info.colliders.Count == 0 && (cat.kind == ForestAssetKind.Tree || cat.kind == ForestAssetKind.Obstacle || cat.placement == ForestPlacement.Landmark))
                        issues.Add(new ForestIssue(MessageType.Warning, w + "has no colliders: players and the monster walk through it. Add a Capsule (trunk) or convex Mesh Collider to the prefab."));
                    if (cat.colliders && info.colliders.Count > 0 && cat.kind == ForestAssetKind.GroundCover)
                        issues.Add(new ForestIssue(MessageType.Info, w + "ground cover with colliders costs physics for every copy; turn Colliders off for this category if they are not needed."));
                    foreach (ForestPrefabInfo.ColliderShape c in info.colliders)
                        if (c.type == ForestPrefabInfo.ShapeType.Mesh && !c.convex && c.mesh != null && c.mesh.GetIndexCount(0) / 3 > 2000)
                        {
                            issues.Add(new ForestIssue(MessageType.Info, w + "uses a detailed non-convex Mesh Collider; a convex or primitive collider is much cheaper."));
                            break;
                        }

                    // alpha clipping (leaf cards) is costly on Quest
                    if (info.lods.Count > 0)
                        foreach (ForestPrefabInfo.Part p in info.lods[0].parts)
                            if (p.material != null && (p.material.IsKeywordEnabled("_ALPHATEST_ON") || (p.material.renderQueue >= 2450 && p.material.renderQueue < 3000)))
                            {
                                issues.Add(new ForestIssue(MessageType.Info, w + "uses alpha clipping (material '" + p.material.name + "'): many overlapping cutout leaves are one of the most expensive things on Quest."));
                                break;
                            }
                }
            }
        }
    }
}
