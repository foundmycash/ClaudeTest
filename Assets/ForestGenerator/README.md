# Havoc Forest: procedural horror forest for Quest

A forest generator built for this project (Unity 6000.3, URP 17.3, Gorilla locomotion, Quest 2 / 3). It builds a
complete, explorable, seed-reproducible forest in the editor, saves it with the scene, and draws it cheaply on Quest.

Everything lives in `Assets/ForestGenerator` (code in the `HavocForest` namespace). Generated output goes to
`Assets/ForestGeneratorSaved/<Scene>_<id>/`. The demo scene is `Assets/Scenes/Forest.unity`.

---

## 1. Quick start

1. Open `Assets/Scenes/Forest.unity` (or make a fresh one: **Tools > Havoc Forest > Create Forest Scene**).
2. Select the **Forest** object. Its inspector shows **Generate**, **New Seed** and **Clear**, the stats of the current forest,
   a list of checks, and the Forest Settings / Ground Layers inline.
3. Press **Add This Scene To Build Settings** if the button shows. (With Fog Mode = Unity Fog this also matters for
   the fog: Unity strips fog from builds unless a scene in the build uses it.)
4. When you need monster pathfinding, press **Bake Monster NavMesh**. Re-bake after every regenerate (the checks list
   tells you when it is out of date).
5. Save the scene (Ctrl+S).

### Player and multiplayer

**Tools > Havoc Forest > Add Player And Multiplayer** (with the forest scene open) puts the game's player and
multiplayer into it, under a **System** object as in ClaudeTest:
- **Gorilla Rig**: an instance of `Assets/Scenes/Gorilla Rig.prefab`, the same rig as in ClaudeTest (locomotion
  settings, hand grabbers, camera). Edit the prefab and both scenes get the change. A rig that is not that prefab is
  replaced; an instance already in the scene is kept.
- **Network**: a copy of ClaudeTest's Network object (Fusion's Network Runner, Network Launcher, Avatar Spawner) with
  all its settings, except the room: **HavocForest** instead of GorillaTestRoom, so forest players never land in a
  session with players in ClaudeTest (or with an older build). Same region (ussc), same avatars, offline rig and
  status label. Change the room on Network > Network Launcher > Room Name.
- The rig is put on the trailhead and becomes the generator's **Move Player To Spawn** target (Generate moves it there
  again), and the scene is added to the Build Settings.

It is one Undo step and saves nothing: save the scene afterwards. Running it again keeps what is there and only puts
the rig back on the trailhead. **Create Forest Scene** does the same for a new scene. The forest itself needs no syncing: it is baked into the
scene, so every player loads the same one. Every player starts on the trailhead (the same spot).

The game boots into the **Loading** scene, whose cinematic (Loading Sequence > Main Scene) now loads **Forest**.
ClaudeTest is still in the project and the Build Settings, but nothing loads it any more; untick it in the Build
Settings to leave it out of builds.

---

## 2. What is on the Forest object

| Component | Job |
|---|---|
| **Forest Generator** | Seed, settings, Generate / Clear, saving, debug gizmos. Editor-time tool; also runs at runtime if **Generate On Start** is on. |
| **Forest World** | The forest at runtime: hands the ground maps to the shaders, picks the Quest detail level, rebuilds the colliders of instanced objects on load, answers queries (ground height, dread, markers, nearest trail). |
| **Forest Instanced Renderer** | Draws trees, rocks, undergrowth... with GPU instancing, per camera, with culling and LODs. |
| **Forest Atmosphere** | Fog (volumetric or Unity fog), ambient (sky) light, moon or sun; blends Calm to Dread with depth; event overrides. Its child **Volumetric Fog** is the fog box (see 6, Volumetric fog). |
| **Forest Lamp Manager** | Lights the forest from the streetlights (cheap lamp light + a small pool of real lights), flicker, events. |
| **Forest Boundary Guard** | Last-resort containment: puts a player who got out back on the nearest trail. |
| **Forest Flashlight** | The player's flashlight: spot light with a cookie, A button, on the head or a hand (see 6, Night, light sources and the flashlight). |
| **NavMesh Surface** | Added by Bake Monster NavMesh (AI Navigation package). |

Generated children of the Forest object:
- `__GeneratedForest/Terrain`: terrain chunks (MeshRenderer + MeshCollider).
- `__GeneratedForest/Props`: real GameObjects for categories that cannot be instanced (the streetlights).
- `__GeneratedForest/Boundary`: the hidden wall colliders and the ceiling.
- `__ForestColliders` (not saved, rebuilt on load): collider-only copies of the instanced objects' colliders.

Generating again replaces all of it, frees what it made, and overwrites the saved files in place (references stay
valid). **Clear** removes the forest and its saved files.

---

## 3. Architecture

The generator is a pipeline of separate steps (`Scripts/Generation`). Each step uses its own random stream, so
changing one thing (say, the bushes' density) does not reshuffle everything else.

| Step | Class | What it does |
|---|---|---|
| Shape & base terrain | `ForestShape`, `ForestBuilder.BaseHeights` | Rounded, wobbly playable outline; rolling hills, ridges, bumps; the raised boundary band (cliffs, rocky inclines, dense-woodland banks, blended around the edge). |
| Layout | `ForestLayoutBuilder`, `ForestTrailRouter`, `ForestTrailField`, `ForestZoneMap` | Trailhead, clearings, dead ends; spanning-tree backbone + loop links + spurs; A* routes on a cost grid (avoids slopes, meanders along noise valleys, runs straight in "sightline" regions, merges with earlier trails); depth ("dread") along the trail network; landmark sites; warped-Voronoi zones. |
| Levelling | `ForestBuilder.ShapeTerrain` | Trails flat across and following the smoothed ground; clearings flattened. |
| Markers | `ForestMarkerBuilder` | Spawn, clearings, heart, junctions, dead ends, landmarks, long sightlines (reveal points), blind corners, ambient-sound spots. |
| Ground blend maps | `ForestGroundPainter.PaintBlend` | 8 surface weights per texel: zone mix, patches, rock on slopes, clearings, gravel trail cores with packed-dirt ragged edges, leaves drifting in. |
| Scatter | `ForestScatter` | Places the asset library: streetlights, landmarks, boundary rocks, obstacles, trees, bend thickets, undergrowth, ground cover; monster gaps; clearance check. |
| Ground marks & light map | `ForestGroundPainter.ApplyStamps / PaintLight` | Roots, needles, leaves, moss around placed objects; sky occlusion and dappled canopy shade baked into the ground light map. |
| Meshes | `ForestTerrainMesher` | Terrain cut into chunks (one culling unit each). |

`ForestBuilder.Build` does all of this without creating Unity objects; `ForestGenerator` turns the result into meshes,
textures, GameObjects and the saved `ForestData` asset (binary: heights, layout, markers, placed objects, chunk meshes).

### Why these choices (Quest)
- **Terrain is a generated chunked mesh, not Unity Terrain.** URP's terrain shader draws 4 surfaces per pass, so 8
  would draw the whole terrain twice. Here one material blends all 8 in one pass. Cost: Unity's terrain sculpt and
  paint brushes do not apply.
- **One ground material** (`Havoc/Forest Ground`) with two **texture arrays** (colour + height, normals) and two blend
  maps. Each pixel samples only the strongest 3 surfaces (2 on Quest 2), with height-based blending for natural edges,
  per-surface rotation, large-scale colour variation and a distant second sample against visible tiling.
- **GPU instancing by hand** (`Graphics.RenderMeshInstanced`). URP's GPU Resident Drawer needs Forward+, and Quest
  uses Forward. About 20,000 objects come out at about 50-60 draw calls from a typical viewpoint.
- **Opaque low-poly placeholders**, no alpha-cut leaves (on Quest those cost overdraw and early depth rejection).
- **Lighting without many real lights:** the sun has no real-time shadows on Quest 2; canopy shade and dapples are
  baked into the ground light map. Streetlights light the forest through per-vertex "lamp light" in the forest shaders,
  plus 1-2 pooled real point lights for other materials.
- **Fog is the project's Volumetric Fog** (Assets/Scripts/VolumetricFog) by default, with Unity's built-in
  exponential-squared fog as a nearly free fallback (Forest Atmosphere > Fog Mode). Either way the camera's far plane
  is pulled in to where the fog is opaque, and the renderer skips everything behind the fog.

---

## 4. Configuration (Forest Settings asset)

Everything that shapes the forest is in one asset (`Settings/ForestSettings.asset`), shown inline in the Forest
Generator's inspector. Every field has a tooltip. Highlights:

- **World**: playable size (default 300 x 300 m), corner radius, edge wobble.
- **Terrain**: vertex spacing (1.5 m), chunk size, hills, ridges, bumps, layer (must be in the Gorilla Player's
  Locomotion Enabled Layers; the checks warn if not).
- **Trails**: main / path width (never narrower than the monster corridor), loops, dead ends, optional **player-only
  escape paths** (off by default), wander, straight-run frequency (sightlines), slope avoidance, gravel share, edge
  raggedness.
- **Clearings**: count, radius (never below the monster's turning space), spacing, irregularity, landmark count.
- **Zones**: the kinds of woodland (Old Conifer Stand, Autumn Mixed Wood, Birch Grove, Thicket, Dead Stand, Rocky
  Outcrop): weight, where they prefer to be (near the trailhead or deep in), tree and undergrowth density, multipliers
  per asset tag, ground surface mix.
- **Ground**: blend-map and light-map resolution, patch size and contrast, rock slopes, clearing grass, leaves on trails.
- **Boundary**: band width, rise height, edge types, containment (wall inset, thickness, ceiling clearance).
- **Monster**: height, width, turning radius, step height, max slope, margins (see section 7).
- **Scatter**: global density, ground-cover density, spacing scale, bend thickets, monster gaps, canopy overhang.
- **Streetlights**: per 100 m of trail, off-trail share, spacing, green / dead / flickering shares, decay with depth,
  colours, range, intensity.
- **Atmosphere**: the Calm and Dread profiles, and how much extra dread being off the trail adds.

**Seed** is on the generator. Same seed + same settings + same asset library = same forest; the inspector shows a
content hash to compare.

---

## 5. Replacing the models with your own (Forest Asset Library)

The generator never refers to a specific model. Everything it places comes from the **Forest Asset Library**
(`Settings/ForestAssetLibrary.asset`): a list of categories, each with weighted prefab variants.

Per category: enable switch, tag (zones scale categories by tag), kind (tree / undergrowth / ground cover / obstacle /
special), placement rule, density per 100 m², spacing, clustering, boundary density, max count, slope range, distance
from trails, clearings allowed, ground-surface preference, slope alignment, tilt, yaw, sink, **blocks monster**,
**colliders**, render mode, draw distance, shadows, LOD scale, canopy shade, ground mark.
Per variant: prefab, weight, scale range, height stretch, footprint override.

To use your model: drop its prefab into a category's variant list (or replace a placeholder), then press **Generate**.
Add as many variants as you like; add new categories for new kinds of things.

### What a prefab needs
- **Pivot at the base** (where it meets the ground), **+Y up**, **+Z forward**, real-world **metres**. A root rotated
  by -90 degrees (Blender export) is fine: the root's own rotation and scale are kept.
- **Meshes**: MeshFilter + MeshRenderer. An **LOD Group** on the root is used for the level-of-detail distances (screen
  sizes are turned into distances at a 90 degree field of view, then scaled by Quality Settings > LOD Bias and the
  category's LOD scale).
- **Materials**: tick **Enable GPU Instancing** on them, or the prefab is placed as GameObjects (one draw call each);
  the checks tell you. Any URP material works. Use **Havoc/Forest Vegetation** to get the canopy darkening, lamp light,
  wind and copy-to-copy colour variation (give it your colour texture; set Wind to 0 for rigid things).
- **Colliders**: put them on the prefab (Capsule for trunks, convex Mesh or primitives for rocks, Capsule along X for
  logs). The forest copies them for every instance as static collider-only objects. Categories with **Colliders** off
  get none. Ground cover should not have any.
- **Behaviour** (scripts, lights, particles, audio, animation, skinned meshes): such prefabs are automatically placed as
  real GameObjects instead of being instanced. Fine for a few (streetlights, landmarks), expensive for many.
- **Streetlights**: add a **Forest Lamp** component (point it at the glass renderers, the halo renderer and a light
  point). Keep the arm higher than the monster's overhead clearance; the checks warn if it is lower.
- **Footprint**: worked out from the colliders (the trunk capsule for trees) or the bounds; override per variant if
  spacing looks wrong.

The checks list flags: missing prefabs or meshes, floating or buried pivots, materials without instancing,
missing colliders on trees and obstacles, triangle counts over the Quest budget (trees 4,000, obstacles 1,500, ground
cover 300 at full detail), missing LOD Groups, heavy non-convex mesh colliders, alpha-clipped materials.

The placeholders are made by code (`Editor/ForestPlaceholderModels.cs`): low-poly, vertex-coloured, all one material.
**Tools > Havoc Forest > Rebuild Placeholder Ground Textures** repaints the placeholder textures and models.

---

## 6. Ground surfaces and shaders

### Ground Layers asset (`Settings/ForestGroundLayers.asset`)
Eight surfaces in fixed roles: Dirt, Gravel, Moss, Rock, Grass, Leaf Litter, Roots, Needles. Per surface: colour
texture, optional normal map (OpenGL style), optional height map (else the colour's alpha, else its brightness), tile
size in metres, tint, smoothness, normal strength.

After changing textures or the texture size press **Build Texture Arrays** (on the asset or the generator). Any size
or format is fine; each is resized into the arrays, imported as Texture2DArray with mipmaps, ASTC 6x6 (colour) and
5x5 (normals) on Android. Tile size, tint, smoothness and normal strength apply immediately.

### Shared vs per-object materials
- **One terrain material** for the whole forest floor (`Materials/ForestGround.mat`, `Havoc/Forest Ground`). The
  per-forest blend and light maps are set as global shader inputs by Forest World, so this one material serves any
  forest. (Only one forest per scene is supported for that reason.)
- **Objects keep their own materials.** The placeholders share `ForestVegetation.mat`; your models can use their own.
- `ForestLampGlass.mat` (emissive glass) and `ForestLampGlow.mat` (`Havoc/Forest Lamp Glow`, the additive halo).

### Shaders
| Shader | Notes |
|---|---|
| `Havoc/Forest Ground` | 8-surface texture-array blend, top-3 (top-2 on Quest 2), height blend, anti-tiling, baked AO + canopy light, per-vertex lamp light, built-in fog, Forward and Forward+, single-pass instanced stereo, debug views (surfaces / light map / strongest surface). |
| `Havoc/Forest Vegetation` | Vertex colour or texture, per-copy variation, wind, canopy darkening, lamp light, emission, optional alpha clip, GPU instancing, shadow caster. |
| `Havoc/Forest Lamp Glow` | Camera-facing additive halo; fades near the camera and into the fog (Unity fog, or the volumetric fog through `_ForestFogExtinction`). |

The Quest 2 "lite" mode is a global keyword (`_FOREST_LITE`, set by Forest World > Detail = Auto on Low-tier
headsets): 2 ground surfaces per pixel instead of 3, no ground normal maps, no anti-tiling sample.

### Surface textures on the models (bark, needles, leaves, stone...)
The placeholder models get their colour from vertex colours. **Surface textures** add the pattern on top, still with one
shared material and one draw per model.

- **How it works.** Every part of a model says which of 8 surfaces it is (Bark, Birch Bark, Needles, Leaves, Blades
  [grass and ferns], Stone, Iron, Wood) in its texture coordinates, `(u, v, surface)`; u and v count repeats. The shared
  `ForestVegetation.mat` samples one Texture2DArray (`Settings/ForestSurfaceLayers_Detail.png`, ASTC 6x6 on Quest, 8 x
  512 x 512 = about 1.2 MB) with a single extra texture read per pixel. Trunks and branches wrap their texture all the way
  round (a whole number of repeats, so there is no seam); rocks and crowns are projected flat onto each face in metres,
  so the pattern keeps one scale.
- **Only the pattern is used.** Each texture is divided by its own average colour when the array is built, so it adds
  grain, cracks and variation without changing the colours the models were designed with: a photo of grey bark on a brown
  trunk does not turn it grey. Bark, leaves and so on stay the colours of the vertex colours (and of the autumn variation
  between copies).
- **Menu: Tools > Havoc Forest > Surface Textures.** *Add To The Models* does everything (paints the placeholder
  textures into `Textures/Surfaces`, builds the array, switches it on, and rebuilds the placeholder model meshes and
  prefabs so they carry texture coordinates: same shapes, the ground is not touched). *Turn Off* / *Turn On* go back to
  plain vertex colours and forward again (the material's `_UseSurfaces`). *Repaint Placeholder Textures* repaints the
  placeholder PNGs. *Rebuild Placeholder Models* only rebuilds the models.
- **Your own textures.** `Settings/ForestSurfaceLayers.asset`: drop a seamlessly tiling texture into a slot (any size or
  format, colour; no normal map is used) and press **Build Texture Array**. Strength (0..1) and the on/off switch apply
  right away. How many metres one repeat covers is `ForestSurfaceLayers.TileMetres` (bark 1.0, birch 1.0, needles 0.7,
  leaves 1.0, blades 0.3, stone 1.2, iron 0.8, wood 0.8); changing them needs *Rebuild Placeholder Models*.
- **Safety.** Models without texture coordinates (made before this existed) would sample a single texel for every vertex,
  so the textures are only switched on when the placeholder models have them (the menu checks). Your own prefabs are not
  affected: they use their own materials, which keep the switch off.
- **Cost.** One extra array sample per pixel on the vegetation, 3 floats of texture coordinates per vertex. Not
  measured on a headset.

### Volumetric fog
Forest Atmosphere > **Fog Mode = Volumetric** (the default) uses the project's own `Volumetric Fog` component
(`VRFog`, ray-marched, the same fog as in the Loading scene) on a box over the whole forest: drifting, swirling mist,
light scattering towards the sun, and the streetlights' real-time lights glowing in it. The box is the **Volumetric
Fog** child of the Forest object; it is created automatically when missing and resized whenever you generate
(regenerating keeps your settings on it).

Who sets what:
- **From the Calm / Dread profiles** (so the fog still thickens deeper in and is opaque at the far plane): the fog's
  Density, Fog Color and Max Distance. The profile's Fog Density means "opaque at 2.15 / density metres" in both modes;
  the profile's Fog Colour is the colour the fog shows where it is thick (brighter towards the sun, darker away from it).
  Changing these three on the Volumetric Fog component has no effect.
- **Forest Atmosphere > Clear Zone** (0.2): how far around the player the fog stays thin, as a share of the fog
  distance. Real fog of the same visibility is already hazy a few metres away (0); 0.3 is close to Unity fog's
  clear-near, thick-far look (good for the day presets). At night 0.2 keeps the flashlight from lighting up a wall of
  fog right in front of the eyes, while its beam still shows further out.
- **On the Volumetric Fog component** (everything else): noise size/strength/coverage, wind, turbulence, Anisotropy
  (glow towards the sun), Light Intensity / Light Core Radius / Light Absorption (lamp glow), Height Falloff (ground
  mist; the far plane follows the thinner fog up high), Max Steps (editor/PC) and **Mobile Max Steps (Quest)**.

How it is drawn: the forest box is hundreds of metres wide, so its faces would be beyond the pulled-in far plane and
get clipped. Forest Atmosphere therefore keeps the box's own Mesh Renderer from drawing and draws the fog component's
material on a small box around each camera, with the forest box handed to the shader. Same fog, faces always on screen.
Untick **Fog In Scene View** to look at the forest from above while editing.

The Scene view draws the trees and rocks out to **Forest Instanced Renderer > Scene View Draw Distance** (1000 m)
whatever the fog, so the whole forest shows when you look at it from above or from outside it. Game cameras (and the
headset) only draw as far as the fog lets you see: about 70 m in the Calm night, less in the Dread. From inside the
forest the Scene view looks like the game, so a view 100 m away from everything is just fog.

Cost on Quest (not measured on a headset): the fog is ray-marched on every pixel of both eyes, with Mobile Max Steps
(12) samples per pixel, two 3D noise reads and the lamp lights per sample, and it switches the camera depth texture
on (an extra depth copy or prepass on the headset's tile GPU). Expect it to be one of the biggest GPU costs in the
scene. If the frame rate suffers: lower Mobile Max Steps (8 keeps the same look with a coarser dither), Max Lights 1 or 0
(no lamp glow in the fog), or switch Fog Mode to Unity Fog.

Known differences from Unity fog: no shadows inside the fog (no light shafts through the canopy; the sun / moon glow is
everywhere), a fine static dither where the fog is thick (the ray-step jitter; more steps make it finer), and the lamps'
fog glow comes only from the 1-2 real-time lamp lights near the player (the others keep their halo sprites). In the
daytime presets that glow is subtle; at night it is strong.

### Night, light sources and the flashlight
The forest is set at night. The only light comes from:
- **Natural light:** the moon (the scene's directional light, low in the +Z direction where the Cold Night skybox has
  its moon; dim and blue) and the night sky (the ambient colours). Both come from the Calm / Dread profiles.
- **The streetlights:** per-vertex lamp light on the forest, the glowing glass and halos, and 1-2 real-time lamp
  lights near the player (they also glow in the fog).
- **The flashlight:** a real-time spot light with the cookie in `Textures/FlashlightCookie.png`, switched with **A**
  on the right controller (F on the keyboard in the editor).

The daytime look is kept as `Settings/ForestAtmosphere_DayCalm` / `_DayDread`: drag them into Forest Atmosphere's Calm
and Dread slots to go back (and set Clear Zone to 0.3).

The fog hides the sky: it is opaque at the draw distance in every direction, so the skybox does not show through
(with the volumetric fog, the moon shows as a brighter glow in the fog in its direction). The camera clears to the
fog colour.

**Forest Flashlight** (on the Forest object; added automatically to forests made before it existed):
- **Switch**: Hand (right) and Button (Primary = A / X, Secondary = B / Y), keyboard key, Start On, haptic buzz, click
  sound (a built-in click when empty).
- **Light**: Cookie, colour, Intensity (24: URP spot lights fall off with distance squared), Range (30 m), Spot Angle
  (60) and Inner Angle. No real-time shadows (Quest).
- **Mount**: Head (default; the hands swing all the time in Gorilla locomotion, so a hand-held beam would sweep
  around), Right Hand or Left Hand, with a position and rotation offset. A hand mount probably needs its Rotation
  Offset adjusted so the beam points along the controller (not tested on a headset).
- **Hooks**: `SetOn(bool)`, `Toggle()`, `Flicker(seconds)` (stutters, for horror events), `IsOn`, `Toggled` event.

Cookies need **Light Cookies** on in the URP asset: it is now on in `Mobile_RPAsset` (Quest) and was already on in
`PC_RPAsset`. Tools > Quest > Apply Optimized Settings no longer switches it off. The forest shaders apply cookies to
the real-time lights; the volumetric fog does not (the beam in the fog is a smooth cone).

Cost: one more per-pixel real-time light on everything the beam's cone touches while it is on, the cookie lookup, and
one more light in the volumetric fog's loop (Max Lights 3).

---

## 7. The monster

Monster settings (in Forest Settings > Monster): height (4.8 m = 3 x a 1.6 m player), width (2 m), turning radius
(3.5 m), step height (0.6 m), max slope (35 degrees), side margin, overhead margin. They drive:
- **Trail widths**: monster-accessible trails are at least width + 2 x margin wide; nothing that blocks the monster
  (trunks, rocks, logs, boulders, landmarks) stands in that corridor. Tree crowns may overhang a share of it
  (Scatter > Canopy Overhang).
- **Clearings**: never smaller than turning radius + half width + margin, kept free of blocking objects.
- **Monster gaps**: monster-wide openings from the trails into the woods, ending in a hidden pocket (markers
  MonsterGap / MonsterLair), for it to step out of the trees or vanish into them.
- **Streetlights**: the pole stands outside the corridor; the checks warn if an arm is lower than height + overhead
  margin.
- **Clearance check**: after placing everything, every monster route is walked in 1 m steps and every clearing's
  turning circle is checked. Anything in the way is listed in the checks and drawn as red spheres (gizmo Monster
  Clearance).
- **NavMesh**: **Bake Monster NavMesh** creates or updates the "Forest Monster" agent type in Project Settings >
  Navigation (radius = width / 2, height, step height, slope), bakes a NavMesh Surface from the colliders (the hidden
  walls fence it in; the ceiling is left out), saves it next to the forest, and checks that every clearing, dead end,
  monster gap and lair is reachable from the trailhead. Use `NavMeshAgent` with Agent Type = Forest Monster.

Changing the monster's size needs a **Generate** and a re-bake, no code changes.

---

## 8. Containment (keeping players in)

With Gorilla locomotion players can climb any collider (walls, cliffs, trees), so containment is layered:
1. **The raised boundary**: the ground rises 14-26 m outside the playable area (cliffs, rocky inclines, dense banks),
   covered with rock formations, dense trees and deadfall, so the edge reads as natural terrain.
2. **Hidden walls**: a closed ring of 4 m thick box colliders a few metres into the slope, from below the ground up to
3. **an invisible ceiling** higher than the tallest treetop plus 10 m, so climbing walls or trees and jumping never
   gets anyone over. Together they make a closed box.
4. **Forest Boundary Guard**: if a player still ends up past the wall, under the ground or above the ceiling, they are
   put back on the nearest trail (Gorilla Player is reset so they are not flung). Its **On Returned** event is the
   place for a fade or sound.

Tested headlessly (see 11): rays fired outward from inside at 1.5, 8 and 25 m height and upward from above the trees
were all stopped; a player thrown 370 m outside was put back 33 m inside.

---

## 9. Horror systems: hooks for what comes next

- **Markers** (`ForestWorld.GetMarkers`, `TryGetNearestMarker`): Spawn, Clearing, Heart, Junction, DeadEnd, Landmark,
  RevealPoint (start of a long straight sightline: distant sightings), BlindCorner (sharp bend hidden by a thicket),
  AmbientSound (deep in the trees, away from trails), MonsterGap, MonsterLair, Streetlight. Each has position, yaw,
  radius and dread. Gizmo colours: see Forest Generator > Debug.
- **Dread**: `ForestWorld.DreadAt(position)` (0 at the trailhead, 1 at the heart, more off the trails);
  `ForestAtmosphere.CurrentDread` (smoothed at the camera) to drive audio intensity, events, spawn rates.
- **Atmosphere events**: `ForestAtmosphere.PushOverride(profile, weight, fadeSeconds)` / `ReleaseOverride(...)`;
  `dreadOverride` to force a dread level.
- **Lamp events**: `ForestLampManager.FlickerAll(seconds)`, `KillLights(center, radius, seconds)`,
  `SetAllStates(state)`, `Nearest(position)`; per lamp `ForestLamp.SetState`.
- **Guard**: `ForestBoundaryGuard.onReturned`.
- **Queries**: `GroundHeight`, `TryGetNearestTrailPoint`, `EdgeDistance`, `SpawnPose`.

---

## 10. Performance on Quest

### What was measured (editor, not a headset)
From the trailhead with the default forest, in play mode: about 52-58 instanced draw calls, 800 visible objects,
55-66k triangles for the placed objects (plus the terrain chunks in view). About 20,000 objects and 9,000 colliders in
total. Generation takes about 4-7 s in the editor.

### Recommended settings
- Keep the scene's fog dense enough that the opaque distance (2.15 / density) stays under about 80 m; the renderer and
  the camera stop there.
- Volumetric fog: Mobile Max Steps 12 (lower first if GPU-bound), Max Lights 2. Unity Fog mode is the cheap fallback.
- Forest World > Detail = **Auto** (lite ground on Quest 2).
- Forest Lamp Manager: real-time lights 2 (Quest 3), 1 (Quest 2), shadows off.
- Forest Instanced Renderer: Low Tier Distance Scale 0.85; raise or lower LOD Distance Scale to trade popping for cost.
- Ground maps at 1024 use about 13 MB of memory; 512 saves about 9 MB.
- Keep foliage opaque. If you must use alpha-cut leaf cards, keep them on few, near objects.
- Sun real-time shadows stay off on Quest 2 (QuestPerformance already sets that); the canopy shade is baked.
- Grass / ferns / branches have short draw distances (24-40 m); raise them only if the GPU has room.

### Performance test on the headset
1. Build with the Forest scene in the build list (and Development Build ticked if you want the profiler).
2. In the headset, start in the forest, hold **both grips + left menu** for 1.5 s to open the Performance Lab panel.
   It shows fps, worst frame, late frames, CPU and GPU ms, and a **forest** line: draw calls, objects, triangles,
   lamps lit, dread, detail level.
3. Test these spots and note the numbers (fps should hold 72 on Quest 2, 72 or 90 on Quest 3):
   - trailhead looking down the trail;
   - a **RevealPoint** (long straight view);
   - the densest zone (Thicket) looking across;
   - next to two working streetlights (real lights active);
   - the **Heart** (densest fog, Dread profile);
   - turning quickly on the spot (culling cone);
   - pressed against the boundary looking out.
4. If GPU-bound: first compare Fog Mode Volumetric against Unity Fog at the same spot (that difference is the
   volumetric fog's cost), lower its Mobile Max Steps; then lower Ground blend-map resolution, raise fog density, lower
   LOD Distance Scale, reduce ground-cover density, set real-time lamp lights to 0-1. If CPU-bound: fewer categories with colliders, larger renderer cells,
   fewer GameObject-mode props.
5. For deeper numbers use OVR Metrics Tool (GPU utilisation) or the Unity profiler on a development build.

---

## 11. Testing checklist

Done headlessly in a copy of the project (no headset):
- [x] compiles; generation completes with 0 checks issues, 0 monster-clearance violations
- [x] same seed gives the same content hash; another seed gives a different one
- [x] regenerating 3 times leaves no extra objects, meshes, textures or files
- [x] renders in Mobile (Forward) and PC (Forward+); lite and full ground; Calm and Dread atmospheres
- [x] play mode: rig stays at the trailhead on the ground, instanced renderer draws for the game camera, fog and
      far plane set, colliders built, no exceptions
- [x] containment rays (outward at 3 heights, upward) all stopped; guard puts an escaped player back on a trail
- [x] monster NavMesh: all clearings, dead ends, gaps and lairs reachable; trunks carved out; outside unreachable
- [x] Android (IL2CPP) build of the project with the Forest scene succeeds (all forest shaders compile for the headset;
      built before the volumetric fog was added)
- [x] night profiles, flashlight (SetOn / Toggle, cookie on the light, parented to the camera), cookie
      pattern on the forest shaders, beam in the fog: renders and play mode in the editor (no controller input tested)
- [x] volumetric fog: renders in Forward and Forward+ at 12 steps, stops at trees and ground (depth texture), opaque at
      the far plane, Calm and Dread; play mode: the rig camera gets the depth texture, far plane and draw counts are the
      same as with Unity fog; the real-time lamp lights reach the fog; survives regenerating; Unity Fog mode switches back

To do on the headset (not verified):
- [ ] frame rate and GPU / CPU time at the test spots above, Quest 2 and Quest 3
- [ ] both eyes render the forest correctly (single-pass instanced), halos and lamp light look right in stereo
- [ ] walking, climbing and jumping along the boundary: no way out; the guard never triggers during normal play
- [ ] LOD popping and the culling cone are not noticeable when turning the head quickly
- [ ] the scale of trails, trees and clearings feels right next to the monster
- [ ] fog density and darkness are readable but tense (tune the Calm / Dread profiles)
- [ ] volumetric fog: GPU cost (compare with Unity Fog mode), both eyes match, the step dither is not distracting
- [ ] night: readable enough to walk the trails by moon and lamp light, dark enough to need the flashlight
- [ ] flashlight: A switches it, the cookie pattern and the beam in the fog look right in both eyes, its GPU cost

---

## 12. Known limitations

- **Placeholders are placeholders.** Low-poly, vertex-coloured models and procedural textures: the layout, lighting and
  atmosphere are real, the look of your reference image needs real models and textures.
- **One forest per scene** (the ground maps are global shader inputs).
- **No Unity Terrain tools** on the generated ground; regenerate to change it.
- **LOD switches pop** (no cross-fade: dithered cross-fade costs extra on Quest). The fog hides most of it.
- **Your own URP/Lit materials** do not get the baked canopy darkening or the per-vertex lamp light; they are lit by the
  sun, ambient and the 1-2 pooled real lamp lights near the player. Use Havoc/Forest Vegetation for full integration.
- **Generation runs in one go** (a few seconds). Runtime generation (Generate On Start) works but blocks the frame; the
  NavMesh must then be baked at runtime too (`NavMeshSurface.BuildNavMesh`).
- **Floating-point determinism across platforms** is not guaranteed for runtime generation (the editor-baked forest is
  identical everywhere because it is saved data).
- **The streetlights' real lights** use per-object light lists on Forward; instanced batches get the lights nearest
  the batch, which is fine with the 1-2 pooled lights.
- The boundary rocks hide stretched terrain on cliffs well; on very steep custom settings some stretching can show.
- **Flashlight**: no real-time shadows (the beam lights trunks behind trunks), the cookie is not applied inside the
  volumetric fog, the A button press itself was not tested (no headset here), and hand mounts need their rotation
  offset tuned. It is local only: other players do not see your beam.
- **Volumetric fog**: no shadows or light shafts inside it, a fine static dither where it is thick, lamp glow only from
  the real-time lamp lights, and a full-screen GPU cost on Quest (see 6, Volumetric fog). With Fog Mode = Unity Fog the
  scene saves with Unity fog on; with Volumetric it saves with Unity fog off, so Unity fog variants may be stripped from
  builds: switch modes in the editor and save the scene, not at runtime.

---

## 13. Files

```
Assets/ForestGenerator/
  README.md
  Scripts/ForestGenerator.cs, ForestSaving.cs (editor-only part)
  Scripts/Core/          ForestRandom, ForestNoise, ForestGrid (deterministic helpers)
  Scripts/Settings/      ForestSettings, ForestMonsterProfile, ForestAtmosphereProfile, ForestGroundLayers, ForestAssetLibrary
  Scripts/Assets/        ForestPrefabInfo (reads prefabs: LODs, materials, colliders, footprint)
  Scripts/Generation/    ForestBuilder, ForestShape, ForestHeightField, ForestLayout, ForestLayoutBuilder,
                         ForestTrailRouter, ForestTrailField, ForestZoneMap, ForestMarkerBuilder,
                         ForestGroundPainter, ForestTerrainMesher, ForestScatter
  Scripts/Runtime/       ForestWorld, ForestData, ForestMarker, ForestInstancedRenderer, ForestColliders,
                         ForestAtmosphere, ForestFogVolume, ForestFlashlight, ForestLamp, ForestLampManager,
                         ForestBoundaryGuard, ForestDetail, ForestPerformanceStats
  Shaders/               ForestCommon.hlsl, ForestGround.shader, ForestVegetation.shader, ForestLampGlow.shader
  Editor/                ForestGeneratorEditor, ForestFogVolumeEditor, ForestSceneUpgrade, ForestValidator, ForestSetup,
                         ForestGameSetup (player + multiplayer), ForestTextureArrays,
                         ForestPlaceholderTextures, ForestPlaceholderModels, ForestMeshBuilder,
                         ForestNavMeshBaker, ForestUnpackNormal.shader
  Settings/              ForestSettings, ForestAssetLibrary, ForestGroundLayers (+ built arrays), atmosphere profiles
                         (Calm / Dread = night, DayCalm / DayDread = the old daytime look)
  Materials/             ForestGround, ForestVegetation, ForestLampGlass, ForestLampGlow
  Textures/              placeholder ground textures, macro noise, FlashlightCookie
  Placeholders/          placeholder prefabs + meshes
Assets/ForestGeneratorSaved/<Scene>_<id>/   ForestData.asset, Control0/1.png, LightMap.png, MonsterNavMesh.asset
Assets/Scenes/Forest.unity
```

Player / multiplayer change outside this folder: `Assets/Scenes/Loading.unity`, Loading Sequence > Main Scene =
Forest (was ClaudeTest).

Night / flashlight changes outside this folder: `Assets/Settings/Mobile_RPAsset.asset` has Light Cookies on, and
`Assets/Editor/Quest/QuestSetup.cs` no longer switches them off.

Outside this folder: `Assets/Scripts/Quest/PerformanceLab.cs` got a 3-line `ExtraStats` hook (the forest's line on the
panel). Project Settings > Navigation gets a "Forest Monster" agent type when you bake. The volumetric fog uses the
project's `Assets/Scripts/VolumetricFog` and `Assets/Shaders/VolumetricFog` unchanged (the forest only drives it).
