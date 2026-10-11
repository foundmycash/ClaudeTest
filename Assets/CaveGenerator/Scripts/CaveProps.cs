using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HavocCave
{
    public enum CavePropPlacement
    {
        [Tooltip("Sits on the floor.")] Floor,
        [Tooltip("Stuck to a side wall, facing into the tunnel.")] Wall,
        [Tooltip("Hangs from the ceiling.")] Ceiling,
        [Tooltip("A frame that spans the whole tunnel, fitted to its width and height.")] Archway
    }

    public enum CavePlaceholder
    {
        Crate,
        Barrel,
        PalletWithTarp,
        TimberFrame,
        CeilingLamp,
        WallLantern
    }

    /// <summary>
    /// One kind of prop. Give it a prefab, or leave the prefab empty to get one of the built-in placeholders.
    /// </summary>
    [System.Serializable]
    public class CavePropEntry
    {
        public string label = "Prop";
        public bool enabled = true;

        [Tooltip("Your own prefab. Leave empty to use the built-in placeholder below.\n" +
                 "Floor: any pivot works, it is sat on the floor by its bounds. The prefab's own rotation is kept, so a model with a -90 degree root (Blender export) still stands up.\n" +
                 "Wall / Ceiling: the pivot is the attach point and +Z points out of the wall.\n" +
                 "Archway: a 1-piece frame that gets stretched to fit the tunnel.")]
        public GameObject prefab;

        [Tooltip("Built-in stand-in used when no prefab is set. Pick one that suits the placement.")]
        public CavePlaceholder placeholder = CavePlaceholder.Crate;

        public CavePropPlacement placement = CavePropPlacement.Floor;

        [Tooltip("Average number of spawn spots per 10 metres of tunnel.")]
        [Min(0f)] public float perTenMeters = 0.8f;

        [Tooltip("Hard cap on how many of this prop are made. 0 = no cap.")]
        [Min(0)] public int maxCount = 40;

        [Tooltip("Random size multiplier (min, max).")]
        public Vector2 scale = new Vector2(0.85f, 1.2f);

        [Tooltip("Floor props: 0 = anywhere across the floor, 1 = pushed against the walls.")]
        [Range(0f, 1f)] public float wallBias = 0.6f;

        [Tooltip("Floor props: how many spawn together as a pile (min, max).")]
        public Vector2Int groupSize = new Vector2Int(1, 1);

        [Tooltip("Floor props: how far from the first prop the rest of a pile can land.")]
        [Min(0f)] public float groupRadius = 1f;

        [Tooltip("Rough footprint radius in metres. Props never overlap each other.")]
        [Min(0f)] public float radius = 0.5f;

        [Tooltip("Spread spawn spots evenly along the tunnel instead of at random.")]
        public bool evenlySpaced;

        [Tooltip("Floor props: tilt to match the slope of the ground.")]
        public bool alignToSurface;

        [Tooltip("Floor props: spin randomly. Off = line up with the tunnel.")]
        public bool randomYaw = true;

        [Tooltip("Floor props: push into the ground by this many metres.")]
        public float sink;

        [Tooltip("Archway only: also put one at the mouth of every side tunnel.")]
        public bool atBranchEntrances;

        public static List<CavePropEntry> CreateDefaults()
        {
            return new List<CavePropEntry>
            {
                new CavePropEntry
                {
                    label = "Crates", placeholder = CavePlaceholder.Crate, placement = CavePropPlacement.Floor,
                    perTenMeters = 0.9f, maxCount = 40, scale = new Vector2(0.8f, 1.2f), wallBias = 0.7f,
                    groupSize = new Vector2Int(1, 3), groupRadius = 1.1f, radius = 0.5f
                },
                new CavePropEntry
                {
                    label = "Barrels", placeholder = CavePlaceholder.Barrel, placement = CavePropPlacement.Floor,
                    perTenMeters = 0.5f, maxCount = 25, scale = new Vector2(0.9f, 1.1f), wallBias = 0.8f,
                    groupSize = new Vector2Int(1, 2), groupRadius = 0.8f, radius = 0.35f
                },
                new CavePropEntry
                {
                    label = "Pallet With Tarp", placeholder = CavePlaceholder.PalletWithTarp, placement = CavePropPlacement.Floor,
                    perTenMeters = 0.25f, maxCount = 12, scale = new Vector2(0.9f, 1.15f), wallBias = 0.8f, radius = 0.8f
                },
                new CavePropEntry
                {
                    label = "Timber Supports", placeholder = CavePlaceholder.TimberFrame, placement = CavePropPlacement.Archway,
                    perTenMeters = 0.8f, maxCount = 0, scale = new Vector2(0.95f, 1.15f), radius = 1.2f,
                    evenlySpaced = true, atBranchEntrances = true
                },
                new CavePropEntry
                {
                    label = "Ceiling Lamps", placeholder = CavePlaceholder.CeilingLamp, placement = CavePropPlacement.Ceiling,
                    perTenMeters = 1.3f, maxCount = 0, scale = new Vector2(1f, 1f), radius = 1f, evenlySpaced = true
                },
                new CavePropEntry
                {
                    label = "Wall Lanterns", placeholder = CavePlaceholder.WallLantern, placement = CavePropPlacement.Wall,
                    perTenMeters = 0.25f, maxCount = 12, scale = new Vector2(1f, 1f), radius = 0.5f
                }
            };
        }
    }

    /// <summary>Places the props. Everything here runs on the main thread and talks to the cave only through the field.</summary>
    public sealed class CavePropSpawner
    {
        static readonly Color Wood = new Color(0.42f, 0.30f, 0.18f);
        static readonly Color DarkWood = new Color(0.24f, 0.16f, 0.10f);
        static readonly Color Rust = new Color(0.42f, 0.17f, 0.10f);
        static readonly Color Metal = new Color(0.14f, 0.14f, 0.15f);
        static readonly Color Tarp = new Color(0.17f, 0.24f, 0.17f);
        static readonly Color Strap = new Color(0.10f, 0.32f, 0.95f);

        readonly CaveGenerator owner;
        readonly CaveLayout layout;
        readonly CaveField field;
        readonly CaveField.Region all;
        readonly Transform parent;
        readonly System.Random rng;
        readonly System.Func<bool> overBudget;
        readonly HideFlags flags;
        readonly List<Vector4> placed = new List<Vector4>();   // xyz = position, w = radius
        readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        int lightCounter;
        readonly bool realtimeLights;       // false while the lightmapper bakes the lamps: the baked light is then the only light

        public int Count { get; private set; }

        /// <summary>Every built-in lamp and lantern placed. The cave bakes their light into its walls.</summary>
        public readonly List<CaveLamp> Lamps = new List<CaveLamp>();

        public CavePropSpawner(CaveGenerator owner, CaveLayout layout, CaveField field, Transform parent,
                               System.Random rng, HideFlags flags, System.Func<bool> overBudget, bool realtimeLights = true)
        {
            this.realtimeLights = realtimeLights;
            this.owner = owner;
            this.layout = layout;
            this.field = field;
            this.parent = parent;
            this.rng = rng;
            this.flags = flags;
            this.overBudget = overBudget;
            all = field.All();
        }

        float Rand(float a, float b)
        {
            return a + (float)rng.NextDouble() * (b - a);
        }

        static Vector3 Horizontal(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
        }

        // ---------------------------------------------------------------- main loop

        public IEnumerator Run(List<CavePropEntry> entries)
        {
            if (layout.totalLength < 1f) yield break;

            for (int ei = 0; ei < entries.Count; ei++)
            {
                CavePropEntry e = entries[ei];
                if (e == null || !e.enabled || e.perTenMeters <= 0f) continue;

                if (e.placement == CavePropPlacement.Archway)
                {
                    IEnumerator frames = RunArchways(e);
                    while (frames.MoveNext()) yield return null;
                    continue;
                }

                float expected = layout.totalLength / 10f * e.perTenMeters;
                int spots = Mathf.FloorToInt(expected) + (Rand(0f, 1f) < expected - Mathf.Floor(expected) ? 1 : 0);
                int made = 0;

                for (int i = 0; i < spots; i++)
                {
                    if (e.maxCount > 0 && made >= e.maxCount) break;

                    float u = e.evenlySpaced ? (i + 0.5f + Rand(-0.3f, 0.3f)) * layout.totalLength / spots
                                             : Rand(0f, layout.totalLength);
                    Vector3 c, tan;
                    float hw, hh;
                    Locate(u, out c, out tan, out hw, out hh);
                    Vector3 fwd = Horizontal(tan);
                    Vector3 right = Vector3.Cross(Vector3.up, fwd);

                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        int got = 0;
                        switch (e.placement)
                        {
                            case CavePropPlacement.Floor: got = PlaceFloor(e, c, fwd, right, hw, hh, e.maxCount > 0 ? e.maxCount - made : int.MaxValue); break;
                            case CavePropPlacement.Wall: got = PlaceWall(e, c, right, hw, hh) ? 1 : 0; break;
                            case CavePropPlacement.Ceiling: got = PlaceCeiling(e, c, fwd, hh) ? 1 : 0; break;
                        }
                        made += got;
                        if (got > 0) break;
                    }

                    if (overBudget()) yield return null;
                }

                IEnumerator inRooms = RunRooms(e);
                while (inRooms.MoveNext()) yield return null;
            }
        }

        /// <summary>The same props again, spread over the floors, walls and ceilings of the rooms.</summary>
        IEnumerator RunRooms(CavePropEntry e)
        {
            int cap = e.maxCount > 0 ? Mathf.Max(2, e.maxCount / 3) : int.MaxValue;
            int made = 0;
            for (int ri = 0; ri < layout.rooms.Length && made < cap; ri++)
            {
                CaveRoom room = layout.rooms[ri];
                if (room.kind == (int)CaveRoomKind.Chimney) continue;     // up in the roof: nobody can get there
                float area = Mathf.PI * room.radius * room.radius * room.stretch;
                float expected;
                switch (e.placement)
                {
                    case CavePropPlacement.Floor: expected = e.perTenMeters * area / 60f; break;
                    case CavePropPlacement.Wall: expected = e.perTenMeters * 2f * Mathf.PI * room.radius / 10f; break;
                    default: expected = Mathf.Max(1f, e.perTenMeters * area / 45f); break;
                }
                int spots = Mathf.FloorToInt(expected) + (Rand(0f, 1f) < expected - Mathf.Floor(expected) ? 1 : 0);
                Vector3 mid = room.floorCenter + Vector3.up * (room.height * 0.5f);

                for (int i = 0; i < spots && made < cap; i++)
                {
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        float turn = e.placement == CavePropPlacement.Ceiling && e.evenlySpaced ? (i + Rand(-0.15f, 0.15f)) / spots : Rand(0f, 1f);
                        float ang = turn * Mathf.PI * 2f;
                        Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                        Vector3 across = Vector3.Cross(Vector3.up, dir);

                        int got = 0;
                        switch (e.placement)
                        {
                            case CavePropPlacement.Floor:
                                got = PlaceFloor(e, mid, across, dir, room.radius, room.height * 0.5f, e.maxCount > 0 ? cap - made : int.MaxValue);
                                break;
                            case CavePropPlacement.Wall:
                                got = PlaceWall(e, mid, dir, room.radius * 1.3f, room.height * 0.5f) ? 1 : 0;
                                break;
                            case CavePropPlacement.Ceiling:
                                got = PlaceCeiling(e, room.floorCenter + dir * (room.radius * 0.45f) + Vector3.up * 1.5f, across, room.height * 0.8f) ? 1 : 0;
                                break;
                        }
                        made += got;
                        if (got > 0) break;
                    }
                    if (overBudget()) yield return null;
                }
            }
        }

        void Locate(float u, out Vector3 c, out Vector3 tan, out float hw, out float hh)
        {
            for (int i = 0; i < layout.paths.Count; i++)
            {
                CavePath p = layout.paths[i];
                if (u <= p.Length || i == layout.paths.Count - 1)
                {
                    p.Sample(Mathf.Min(u, p.Length), out c, out tan, out hw, out hh);
                    return;
                }
                u -= p.Length;
            }
            c = Vector3.zero; tan = Vector3.forward; hw = 1f; hh = 1f;
        }

        bool Overlaps(Vector3 p, float r)
        {
            for (int i = 0; i < placed.Count; i++)
            {
                Vector4 q = placed[i];
                float d = r + q.w;
                if ((new Vector3(q.x, q.y, q.z) - p).sqrMagnitude < d * d) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- floor

        bool FloorFrom(Vector3 start, float maxDown, out Vector3 hit, out Vector3 normal)
        {
            normal = Vector3.up;
            if (!field.RayMarch(all, start, Vector3.down, maxDown, out hit)) return false;
            normal = field.Normal(all, hit, 0.15f);
            return normal.y >= 0.7f;
        }

        int PlaceFloor(CavePropEntry e, Vector3 c, Vector3 fwd, Vector3 right, float hw, float hh, int allowed)
        {
            float f = Mathf.Pow(Rand(0f, 1f), Mathf.Lerp(1f, 0.25f, e.wallBias));
            float lat = f * hw * 0.85f * (rng.Next(2) == 0 ? -1f : 1f);

            Vector3 anchor, anchorNormal;
            if (!FloorFrom(c + right * lat, hh * 2.5f, out anchor, out anchorNormal)) return 0;

            int groupMin = Mathf.Max(1, e.groupSize.x);
            int groupMax = Mathf.Max(groupMin, e.groupSize.y);
            int group = rng.Next(groupMin, groupMax + 1);
            int made = 0;

            for (int g = 0; g < group && made < allowed; g++)
            {
                Vector3 hit = anchor;
                Vector3 normal = anchorNormal;
                if (g > 0)
                {
                    float ang = Rand(0f, Mathf.PI * 2f);
                    float rad = Rand(0.35f, 1f) * e.groupRadius;
                    Vector3 start = anchor + new Vector3(Mathf.Cos(ang) * rad, 0.7f, Mathf.Sin(ang) * rad);
                    if (!FloorFrom(start, 1.8f, out hit, out normal)) continue;
                    if (Mathf.Abs(hit.y - anchor.y) > 0.5f) continue;
                }

                float scale = Rand(e.scale.x, e.scale.y);
                float r = e.radius * scale;
                if (Overlaps(hit, r)) continue;

                float yaw = e.randomYaw ? Rand(0f, 360f) : Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
                if (e.alignToSurface) rot = Quaternion.FromToRotation(Vector3.up, normal) * rot;

                Create(e, scale, true, hit + Vector3.down * e.sink, rot);
                placed.Add(new Vector4(hit.x, hit.y, hit.z, r));
                made++;
            }
            return made;
        }

        // ---------------------------------------------------------------- wall & ceiling

        bool PlaceWall(CavePropEntry e, Vector3 c, Vector3 right, float hw, float hh)
        {
            float side = rng.Next(2) == 0 ? -1f : 1f;
            Vector3 start = c + Vector3.up * Rand(-hh * 0.5f, hh * 0.3f);
            Vector3 hit;
            if (!field.RayMarch(all, start, right * side, hw * 3f, out hit)) return false;
            Vector3 normal = field.Normal(all, hit, 0.15f);
            if (Mathf.Abs(normal.y) > 0.55f) return false;

            float scale = Rand(e.scale.x, e.scale.y);
            float r = e.radius * scale;
            if (Overlaps(hit, r)) return false;

            Vector3 facing = Horizontal(normal);
            Create(e, scale, false, hit + normal * 0.02f, Quaternion.LookRotation(facing, Vector3.up));
            if (e.prefab == null && e.placeholder == CavePlaceholder.WallLantern)
                Lamps.Add(new CaveLamp { position = hit + normal * 0.35f });                       // a glass lantern: shines all round (direction zero)
            placed.Add(new Vector4(hit.x, hit.y, hit.z, r));
            return true;
        }

        bool PlaceCeiling(CavePropEntry e, Vector3 c, Vector3 fwd, float hh)
        {
            Vector3 mid;
            if (!field.RayMarch(all, c, Vector3.up, hh * 3f, out mid)) return false;

            // A long fixture must clear the lowest part of the ceiling under it.
            float lowest = mid.y;
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 hit;
                if (field.RayMarch(all, c + fwd * (0.55f * s), Vector3.up, hh * 3f, out hit) && hit.y < lowest) lowest = hit.y;
            }

            float scale = Rand(e.scale.x, e.scale.y);
            float r = e.radius * scale;
            if (Overlaps(mid, r)) return false;

            Vector3 pos = new Vector3(mid.x, lowest - 0.02f, mid.z);
            float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg + (rng.Next(2) == 0 ? 0f : 180f);
            GameObject go = Create(e, scale, false, pos, Quaternion.Euler(0f, yaw, 0f));
            if (e.prefab == null && e.placeholder == CavePlaceholder.CeilingLamp)
            {
                AddStem(go.transform, mid.y - pos.y);
                Lamps.Add(new CaveLamp { position = pos + Vector3.down * 0.15f, direction = Vector3.down });   // a strip light hung from the ceiling: its housing blocks the light going up
            }

            placed.Add(new Vector4(mid.x, mid.y, mid.z, r));
            return true;
        }

        // ---------------------------------------------------------------- archways

        IEnumerator RunArchways(CavePropEntry e)
        {
            int made = 0;
            for (int pi = 0; pi < layout.paths.Count; pi++)
            {
                CavePath path = layout.paths[pi];
                float len = path.Length;
                int n = Mathf.RoundToInt(len / 10f * e.perTenMeters);
                for (int k = 0; k < n; k++)
                {
                    if (e.maxCount > 0 && made >= e.maxCount) yield break;
                    float s = (k + 0.5f + Rand(-0.2f, 0.2f)) * len / n;
                    if (path.isMain && s < 6f) continue;   // keep the spawn room open

                    Vector3 c, tan;
                    float hw, hh;
                    path.Sample(s, out c, out tan, out hw, out hh);
                    if (TryFrame(e, c, tan)) made++;
                    if (overBudget()) yield return null;
                }
            }

            if (!e.atBranchEntrances) yield break;
            for (int pi = 0; pi < layout.paths.Count; pi++)
            {
                CavePath path = layout.paths[pi];
                if (path.isMain || path.Length < 8f) continue;
                if (e.maxCount > 0 && made >= e.maxCount) yield break;

                Vector3 c, tan;
                float hw, hh;
                path.Sample(3.5f, out c, out tan, out hw, out hh);
                if (TryFrame(e, c, tan)) made++;
                if (overBudget()) yield return null;
            }
        }

        bool TryFrame(CavePropEntry e, Vector3 c, Vector3 tan)
        {
            Vector3 fwd = Horizontal(tan);
            Vector3 right = Vector3.Cross(Vector3.up, fwd);

            Vector3 hl, hr;
            if (!field.RayMarch(all, c, -right, 7f, out hl) || !field.RayMarch(all, c, right, 7f, out hr)) return false;
            float width = Vector3.Distance(hl, hr);
            if (width < 1.6f || width > 8f) return false;

            float sv = Rand(e.scale.x, e.scale.y);
            float postT = 0.22f * sv;
            Vector3 pl = hl + right * (postT * 0.5f - 0.03f);
            Vector3 pr = hr - right * (postT * 0.5f - 0.03f);

            Vector3 fl, fr, cl, cr;
            if (!field.RayMarch(all, pl, Vector3.down, 6f, out fl) || !field.RayMarch(all, pr, Vector3.down, 6f, out fr) ||
                !field.RayMarch(all, pl, Vector3.up, 6f, out cl) || !field.RayMarch(all, pr, Vector3.up, 6f, out cr)) return false;

            float beamH = 0.24f * sv;
            float topY = Mathf.Min(cl.y, cr.y) - 0.03f;
            float postTop = topY - beamH;
            float bottomL = fl.y - 0.06f;
            float bottomR = fr.y - 0.06f;
            if (postTop - Mathf.Max(bottomL, bottomR) < 1.0f) return false;

            Vector3 mid = (pl + pr) * 0.5f;
            if (Overlaps(mid, e.radius)) return false;

            float floorY = Mathf.Min(fl.y, fr.y);
            Vector3 rootPos = new Vector3(mid.x, floorY, mid.z);
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up);
            float span = Vector3.Distance(pl, pr);

            GameObject root;
            if (e.prefab != null)
            {
                root = new GameObject(e.prefab.name);
                root.transform.SetParent(parent, false);
                root.transform.localPosition = rootPos;
                root.transform.localRotation = rot;
                GameObject inst = Object.Instantiate(e.prefab, root.transform);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = e.prefab.transform.localRotation;     // see Create: a rotated prefab root must survive

                Bounds b;
                if (LocalBounds(root.transform, out b) && b.size.x > 0.01f && b.size.y > 0.01f)
                {
                    // Stretch the frame to the tunnel. The stretch is applied to a holder, so it runs along the tunnel's axes even when
                    // the prefab itself is turned. The bounds were measured at the prefab's own scale, so after scaling about the
                    // pivot its bottom-centre sits at (centre.x * sx, min.y * sy, centre.z * sz).
                    float sx = (span + postT) / b.size.x;
                    float sy = (topY - floorY) / b.size.y;
                    float sz = Mathf.Clamp((sx + sy) * 0.5f, 0.5f, 2f);
                    var stretch = new GameObject("Stretch");
                    stretch.transform.SetParent(root.transform, false);
                    inst.transform.SetParent(stretch.transform, false);
                    stretch.transform.localScale = new Vector3(sx, sy, sz);
                    stretch.transform.localPosition = new Vector3(-b.center.x * sx, -b.min.y * sy, -b.center.z * sz);
                    stretch.layer = owner.generatedLayer;
                }
                CaveUtil.Tag(root, -1, flags);
                root.layer = owner.generatedLayer;
            }
            else
            {
                root = new GameObject("TimberFrame");
                root.transform.SetParent(parent, false);
                root.transform.localPosition = rootPos;
                root.transform.localRotation = rot;

                Material wood = Mat("wood", Wood, 0.1f);
                Material dark = Mat("darkwood", DarkWood, 0.1f);
                Quaternion inv = Quaternion.Inverse(rot);
                Vector3 L(Vector3 world) { return inv * (world - rootPos); }

                float hL = postTop - bottomL;
                float hR = postTop - bottomR;
                Part(root.transform, "PostL", CaveUtil.CubeMesh, L(new Vector3(pl.x, bottomL + hL * 0.5f, pl.z)), new Vector3(postT, hL, postT * 1.1f), wood, Quaternion.identity, owner.propColliders);
                Part(root.transform, "PostR", CaveUtil.CubeMesh, L(new Vector3(pr.x, bottomR + hR * 0.5f, pr.z)), new Vector3(postT, hR, postT * 1.1f), wood, Quaternion.identity, owner.propColliders);
                Part(root.transform, "Beam", CaveUtil.CubeMesh, L(new Vector3(mid.x, topY - beamH * 0.5f, mid.z)), new Vector3(span + postT + 0.3f, beamH, 0.26f * sv), dark, Quaternion.identity, owner.propColliders);

                float braceY = postTop - floorY - 0.26f;
                float braceX = span * 0.5f - 0.3f;
                Part(root.transform, "BraceL", CaveUtil.CubeMesh, new Vector3(-braceX, braceY, 0f), new Vector3(0.6f, 0.09f, 0.12f), dark, Quaternion.Euler(0f, 0f, -45f), false);
                Part(root.transform, "BraceR", CaveUtil.CubeMesh, new Vector3(braceX, braceY, 0f), new Vector3(0.6f, 0.09f, 0.12f), dark, Quaternion.Euler(0f, 0f, 45f), false);
                CaveUtil.Tag(root, owner.generatedLayer, flags);
            }

            placed.Add(new Vector4(mid.x, mid.y, mid.z, e.radius));
            Count++;
            return true;
        }

        // ---------------------------------------------------------------- creating objects

        GameObject Create(CavePropEntry e, float scale, bool sitOnBounds, Vector3 pos, Quaternion rot)
        {
            GameObject root;
            if (e.prefab != null)
            {
                root = new GameObject(e.prefab.name);
                root.transform.SetParent(parent, false);
                root.transform.localPosition = pos;
                root.transform.localRotation = rot;

                GameObject inst = Object.Instantiate(e.prefab, root.transform);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = e.prefab.transform.localRotation;     // keep the prefab's own turn: a model exported Z-up (Blender) has a -90 degree root, and without it lies on its side
                inst.transform.localScale = e.prefab.transform.localScale * scale;

                Bounds b;
                if (sitOnBounds && LocalBounds(root.transform, out b))
                    inst.transform.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z);

                CaveUtil.Tag(root, -1, flags);
                root.layer = owner.generatedLayer;
            }
            else
            {
                root = BuildPlaceholder(e.placeholder, scale);
                root.transform.SetParent(parent, false);
                root.transform.localPosition = pos;
                root.transform.localRotation = rot;
                CaveUtil.Tag(root, owner.generatedLayer, flags);
            }
            Count++;
            return root;
        }

        /// <summary>Bounds of all meshes under space, measured in space's own local axes (so rotation never matters).</summary>
        static bool LocalBounds(Transform space, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            MeshFilter[] filters = space.GetComponentsInChildren<MeshFilter>();
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh mesh = filters[i].sharedMesh;
                if (mesh == null) continue;
                Bounds mb = mesh.bounds;
                for (int cx = -1; cx <= 1; cx += 2)
                for (int cy = -1; cy <= 1; cy += 2)
                for (int cz = -1; cz <= 1; cz += 2)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3(cx, cy, cz));
                    Vector3 q = space.InverseTransformPoint(filters[i].transform.TransformPoint(corner));
                    if (!any) { bounds = new Bounds(q, Vector3.zero); any = true; }
                    else bounds.Encapsulate(q);
                }
            }
            return any;
        }

        // ---------------------------------------------------------------- placeholders

        Material Mat(string key, Color color, float smoothness)
        {
            return Mat(key, color, smoothness, 0f);
        }

        Material Mat(string key, Color color, float smoothness, float emission)
        {
            Material m;
            if (materials.TryGetValue(key, out m) && m != null) return m;

            Material baseMat = owner.propMaterial;
            if (baseMat != null)
            {
                m = new Material(baseMat);
            }
            else
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh == null) sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                m = new Material(sh);
            }
            m.name = "CaveProp_" + key;
            m.color = color;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", color * emission);
            }
            materials[key] = m;
            return m;
        }

        static GameObject Part(Transform root, string name, Mesh mesh, Vector3 pos, Vector3 scale, Material mat, Quaternion rot, bool collider)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (collider) go.AddComponent<BoxCollider>();
            return go;
        }

        GameObject BuildPlaceholder(CavePlaceholder kind, float s)
        {
            switch (kind)
            {
                case CavePlaceholder.Barrel: return BuildBarrel(s);
                case CavePlaceholder.PalletWithTarp: return BuildPallet(s);
                case CavePlaceholder.CeilingLamp: return BuildLamp(s);
                case CavePlaceholder.WallLantern: return BuildLantern(s);
                default: return BuildCrate(s);
            }
        }

        GameObject BuildCrate(float s)
        {
            var root = new GameObject("Crate");
            Vector3 size = new Vector3(Rand(0.7f, 0.95f), Rand(0.5f, 0.7f), Rand(0.7f, 0.95f)) * s;
            Material wood = Mat("crate", Wood, 0.1f);
            Material dark = Mat("crateband", DarkWood, 0.1f);

            Part(root.transform, "Body", CaveUtil.CubeMesh, new Vector3(0f, size.y * 0.5f, 0f), size, wood, Quaternion.identity, false);
            float band = 0.07f * s;
            Part(root.transform, "BandLow", CaveUtil.CubeMesh, new Vector3(0f, size.y * 0.2f, 0f), new Vector3(size.x + 0.02f, band, size.z + 0.02f), dark, Quaternion.identity, false);
            Part(root.transform, "BandHigh", CaveUtil.CubeMesh, new Vector3(0f, size.y * 0.8f, 0f), new Vector3(size.x + 0.02f, band, size.z + 0.02f), dark, Quaternion.identity, false);

            if (owner.propColliders)
            {
                var box = root.AddComponent<BoxCollider>();
                box.center = new Vector3(0f, size.y * 0.5f, 0f);
                box.size = size;
            }
            return root;
        }

        GameObject BuildBarrel(float s)
        {
            var root = new GameObject("Barrel");
            float r = 0.28f * s;
            float h = 0.9f * s;
            Material body = Mat("barrel", Rust, 0.3f);
            Material ring = Mat("barrelring", Metal, 0.4f);

            Part(root.transform, "Body", CaveUtil.CylinderMesh, new Vector3(0f, h * 0.5f, 0f), new Vector3(r * 2f, h * 0.5f, r * 2f), body, Quaternion.identity, false);
            Part(root.transform, "RingLow", CaveUtil.CylinderMesh, new Vector3(0f, h * 0.2f, 0f), new Vector3(r * 2.08f, 0.03f * s, r * 2.08f), ring, Quaternion.identity, false);
            Part(root.transform, "RingHigh", CaveUtil.CylinderMesh, new Vector3(0f, h * 0.8f, 0f), new Vector3(r * 2.08f, 0.03f * s, r * 2.08f), ring, Quaternion.identity, false);

            if (owner.propColliders)
            {
                var cap = root.AddComponent<CapsuleCollider>();
                cap.center = new Vector3(0f, h * 0.5f, 0f);
                cap.radius = r;
                cap.height = h;
            }
            return root;
        }

        GameObject BuildPallet(float s)
        {
            var root = new GameObject("PalletWithTarp");
            Material wood = Mat("pallet", Wood, 0.1f);
            Material tarp = Mat("tarp", Tarp, 0.15f);
            Material strap = Mat("strap", Strap, 0.2f);

            Vector3 pallet = new Vector3(1.2f, 0.12f, 1f) * s;
            Part(root.transform, "RunnerA", CaveUtil.CubeMesh, new Vector3(0f, pallet.y * 0.37f, -0.38f * s), new Vector3(pallet.x, pallet.y * 0.75f, 0.1f * s), wood, Quaternion.identity, false);
            Part(root.transform, "RunnerB", CaveUtil.CubeMesh, new Vector3(0f, pallet.y * 0.37f, 0.38f * s), new Vector3(pallet.x, pallet.y * 0.75f, 0.1f * s), wood, Quaternion.identity, false);
            Part(root.transform, "Deck", CaveUtil.CubeMesh, new Vector3(0f, pallet.y * 0.875f, 0f), new Vector3(pallet.x, pallet.y * 0.25f, pallet.z), wood, Quaternion.identity, false);

            Vector3 cargo = new Vector3(1.1f, 0.85f, 0.9f) * s;
            float cargoY = pallet.y + cargo.y * 0.5f;
            Part(root.transform, "Tarp", CaveUtil.CubeMesh, new Vector3(0f, cargoY, 0f), cargo, tarp, Quaternion.identity, false);
            Part(root.transform, "Strap", CaveUtil.CubeMesh, new Vector3(0f, pallet.y + cargo.y * 0.62f, 0f), new Vector3(cargo.x + 0.02f, 0.05f * s, cargo.z + 0.02f), strap, Quaternion.identity, false);

            if (owner.propColliders)
            {
                var box = root.AddComponent<BoxCollider>();
                float total = pallet.y + cargo.y;
                box.center = new Vector3(0f, total * 0.5f, 0f);
                box.size = new Vector3(pallet.x, total, pallet.z);
            }
            return root;
        }

        GameObject BuildLamp(float s)
        {
            var root = new GameObject("CeilingLamp");
            Material glow = Mat("lamp", new Color(1f, 0.96f, 0.88f), 0.5f, 3f);
            Material housing = Mat("lamphousing", Metal, 0.3f);

            Part(root.transform, "Tube", CaveUtil.CubeMesh, Vector3.zero, new Vector3(1.1f * s, 0.05f, 0.1f * s), glow, Quaternion.identity, false);
            Part(root.transform, "Housing", CaveUtil.CubeMesh, new Vector3(0f, 0.04f, 0f), new Vector3(1.14f * s, 0.03f, 0.16f * s), housing, Quaternion.identity, false);
            AddLight(root.transform, new Vector3(0f, -0.25f, 0f));
            return root;
        }

        /// <summary>A short cable from the fixture up to the ceiling. Added after the lamp is placed so it knows how far up to reach.</summary>
        void AddStem(Transform lamp, float length)
        {
            length = Mathf.Max(0.05f, length);
            Material housing = Mat("lamphousing", Metal, 0.3f);
            GameObject stem = Part(lamp, "Cable", CaveUtil.CubeMesh, new Vector3(0f, length * 0.5f, 0f), new Vector3(0.025f, length, 0.025f), housing, Quaternion.identity, false);
            CaveUtil.Tag(stem, owner.generatedLayer, flags);
        }

        GameObject BuildLantern(float s)
        {
            var root = new GameObject("WallLantern");
            Material glow = Mat("lantern", new Color(1f, 0.7f, 0.35f), 0.4f, 2.5f);
            Material metal = Mat("lanternmetal", Metal, 0.3f);

            // +Z points out of the wall, so the bracket sits behind the glass.
            Part(root.transform, "Bracket", CaveUtil.CubeMesh, new Vector3(0f, 0f, 0.02f * s), new Vector3(0.12f * s, 0.34f * s, 0.04f * s), metal, Quaternion.identity, false);
            Part(root.transform, "Glass", CaveUtil.CubeMesh, new Vector3(0f, 0f, 0.14f * s), new Vector3(0.16f * s, 0.26f * s, 0.16f * s), glow, Quaternion.identity, false);
            AddLight(root.transform, new Vector3(0f, 0f, 0.35f));
            return root;
        }

        void AddLight(Transform root, Vector3 localPos)
        {
            if (!realtimeLights || owner.lampLightEvery <= 0) return;
            lightCounter++;
            if (lightCounter % owner.lampLightEvery != 0) return;

            var go = new GameObject("Light");
            go.transform.SetParent(root, false);
            go.transform.localPosition = localPos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = owner.lampLightRange;
            l.intensity = owner.lampLightIntensity;
            l.color = owner.lampLightColor;
            l.shadows = LightShadows.None;
        }
    }
}
