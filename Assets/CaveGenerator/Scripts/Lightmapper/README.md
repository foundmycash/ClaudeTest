# Cave Lightmapper

A path-tracing lightmapper for the generated cave, in the spirit of Bakery. Every time `CaveGenerator` builds a cave it
unwraps the walls, traces the light for every lightmap texel on background threads, and puts the result on the cave
material. Same seed + same settings gives the same lightmap.

## What it bakes

- **Direct light** from the cave's lamps and lanterns, with soft shadows (bulb size is `Lamp Radius`). The light falls off like a
  real lamp, with the square of the distance (the same curve URP uses for point lights), so there are bright pools round the
  lamps and dark stretches between them. The ceiling strip lights shine down only (their housing blocks the other side);
  glass lanterns shine all round. `Lamp Falloff > Soft` brings back the old flat fade that lights everything evenly.
- **Bounced light** (indirect) off the rock, 1 to 3 bounces, tinted by the rock and dirt colours.
- **Ambient occlusion**, if you give it an Ambient Color.
- **Shadows from props**: crates, barrels and timber frames block the lamps. A lamp's own fixture never shadows it.
- **Light on the props** (Light Props): each prop gets a light probe measured by the same bake, so a crate is bright on the side
  that faces a lamp, with the light bounced off the floor and walls round it. See *Props* below.
- A **dominant light direction** map, so the rock's normal map still reacts to the baked light.

## Using it

Nothing to do: `Lightmapper > Enabled` is on by default, so Generate (button, `Generate()`, `Generate(seed)`, or
Generate On Start) bakes. In the editor a progress bar with a Cancel button shows during the bake and the lightmap is
saved next to the cave meshes in `Assets/CaveGeneratorSaved`. In play mode the textures live in memory and are freed when
the cave is cleared or regenerated.

Cave material: use `Havoc/Cave Blend` (the default). Its **Baked Lightmap Strength** slider scales the baked light.
The old `Baked Lamp Strength` only applies when the lightmapper is off.

**No real-time lamp lights.** While the lightmapper bakes the lamps (Lightmapper > Enabled and Bake Lamp Light on), the
generator does not create the real-time point lights that `Lamp Light Every` would otherwise add: the baked light is
the only light from the lamps. Turn the lightmapper off and `Lamp Light Every` works as before.

Extra lights: add a **Cave Baked Light** component next to any Light (point, spot or directional), also inside your own
prop prefabs. Its Light's intensity and range are used as they are, so it matches what the same real-time light would look
like (use Bake Intensity Scale to adjust). With **Baked Only** on (default) its Light is switched off when the game starts, so it only exists as
baked light; the bake still reads its settings. (In the editor it stays on so you can see and tune it.)

From code, for a loading screen:

    gen.Generated += g => ...;          // fires after the bake
    gen.IsBaking, gen.BakeProgress, gen.BakeStage

## Settings (Cave Generator > Lightmapper)

| Setting | What it does |
|---|---|
| Enabled / Bake At Runtime | Turn the bake off entirely, or only in the editor. |
| Quality | Low (1 bounce), Medium (2), High (3), Custom. Mobile / Quest is forced to Low, at most 5 texels per metre and a 1024 map unless Custom. |
| Texels Per Metre / Max Size | Lightmap resolution. If the cave does not fit, the density is lowered until it does. |
| Lamp Falloff | Realistic (inverse square, default) or Soft (the old flat fade). |
| Lamp Brightness | Multiplies Lamp Light Intensity for the bake. 5 matches the overall brightness the old flat fade had at Intensity 1. |
| Ceiling Lamps Shine Down | The ceiling strip lights do not shine up into the ceiling. |
| Lamp Radius | Softness of lamp shadows. |
| Bounce Intensity | Scales indirect light. 0 = direct only. |
| Ambient Color / Range | Light from everywhere, dimmed in crevices. |
| Rock / Dirt Albedo | Colours the bounced light picks up. |
| Directional | Also bake the direction map (one extra texture). |
| Props Cast Shadows | Props block the lamps and cast shadows on the walls and floor. |
| Light Props | Props are lit by the baked light too (a light probe per prop). Off: props only get the scene's own lighting. |

## Props

Props are not part of the lightmap (they have no texels), so they are handled in two ways:

- **They cast shadows.** Every prop mesh is an occluder in the bake. A mesh with Read/Write off (the import default for models,
  so most of your own prefabs) cannot be read, so its bounding box is the shadow instead: exact for a crate, a bit too big for a
  barrel. Turn Read/Write on in the model's import settings for the exact shape.
- **They are lit by it.** At the middle of each prop the baker measures all the light that arrives: every lamp that can see the point
  (soft shadows, same falloff as the walls), plus rays over the whole sphere that pick up the light bounced off the floor, walls and
  other props, and the ambient colour. That becomes spherical harmonics, the same thing a light probe holds. A small `Cave Prop
  Probe` component on the prop keeps them (so they are saved with the scene) and gives them to every mesh under the prop, which
  URP's lit shaders read as ambient light. The scene's own ambient light is replaced by it for those meshes, the way the cave walls
  already ignore it.

Notes:

- It is a single probe per prop, so a long prop (a timber frame across the whole tunnel) gets one average, not a gradient.
- Meshes with their own light values cannot be batched by the SRP Batcher. For a few hundred props this is fine; if the draw
  calls matter on Quest, turn Light Props off.
- A prefab's own root rotation is kept when it is placed (a Z-up model exported with a -90 degree root stands upright).
- Lamps with no fixture round them (a Cave Baked Light that is not inside a prop) are shadowed by the cave like any other.

## How it works

1. `LmUnwrap` cuts the mesh into charts (a chart only grows while its triangles face about the same way and never folds
   over itself), rotates each to a tight rectangle and shelf-packs them into one atlas shared by every chunk.
2. `LmBvh` builds a bounding volume hierarchy over the cave and the props.
3. `LmAtlas` finds the triangle and barycentric position behind every texel.
4. `LmTracer` gathers the light per texel: shadow rays to each lamp, cosine-weighted bounce paths with next event
   estimation, and a short-range occlusion measure for ambient.
5. `LmTracerProbes` measures the light at each prop (lamps plus rays over the sphere, with the prop's own shell ignored).
6. `LmPost` smooths the indirect light with a cross-bilateral filter (never across charts or creases), spreads colour past
   chart edges, and packs it as RGB9E5 (4 bytes per texel).

The baking code touches no Unity API, so it runs on worker threads. Everything that needs the engine is in
`CaveLightmapUnity`.

## Cost

Cave of about 58k triangles and 400k texels, 16 core PC, plain C#: Low 1.3 s, Medium 2.7 s, High 11 s. The Unity editor's
Mono runtime is roughly three times slower; a phone-class CPU slower again, which is why Quest defaults to Low.
Memory while baking peaks at roughly 90 bytes per atlas texel (about 80 MB for a 1024 x 1024 map), then 8 bytes per texel for the two textures.
