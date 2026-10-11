using UnityEditor;
using UnityEngine;

namespace HavocCave.EditorTools
{
    /// <summary>
    /// Scene view tools for the spawn cave. Select the Cave Generator and you can:
    ///   - drag the squares on the outline to change the room's width, depth and height,
    ///   - click a hole or a ledge to pick it, then drag its handles to move, aim or resize it,
    ///   - drag the green marker to move the spawn point.
    /// Everything is stored in the generator's Spawn Cave settings, so it is saved with the scene and undoable.
    /// </summary>
    public sealed class CaveSpawnHandles
    {
        const string PrefEdit = "HavocCave.EditSpawnCave";

        public int SelectedHole = -1;
        public int SelectedLedge = -1;

        public static bool Editing
        {
            get { return EditorPrefs.GetBool(PrefEdit, true); }
            set { EditorPrefs.SetBool(PrefEdit, value); }
        }

        static readonly Color RoomColor = new Color(0.35f, 0.85f, 1f);
        static readonly Color ExitColor = new Color(1f, 0.75f, 0.2f);
        static readonly Color NicheColor = new Color(0.75f, 0.75f, 0.8f);
        static readonly Color LedgeColor = new Color(0.5f, 1f, 0.5f);
        static readonly Color SelectedColor = new Color(1f, 0.35f, 0.7f);

        Transform t;

        Vector3 W(Vector3 local) { return t.TransformPoint(local); }
        Vector3 L(Vector3 world) { return t.InverseTransformPoint(world); }

        public void Draw(CaveGenerator gen)
        {
            CaveSpawnCave sp = gen.spawnCave;
            if (sp == null || !sp.enabled || !Editing) return;
            t = gen.transform;
            if (SelectedHole >= sp.holes.Count) SelectedHole = -1;
            if (SelectedLedge >= sp.ledges.Count) SelectedLedge = -1;

            DrawRoom(gen, sp);
            DrawSpawnPoint(gen, sp);
            DrawHoles(gen, sp);
            DrawLedges(gen, sp);
        }

        // ---------------------------------------------------------------- the room

        void DrawRoom(CaveGenerator gen, CaveSpawnCave sp)
        {
            float rx = sp.size.x, rz = sp.size.z, h = sp.size.y;
            Handles.color = RoomColor;

            // Outline: the floor, and a few rings up the dome.
            float[] heights = { 0f, 0.5f, 0.85f };
            foreach (float f in heights)
            {
                float y = h * f;
                float s = Mathf.Sqrt(Mathf.Max(0f, 1f - f * f));
                var ring = new Vector3[49];
                for (int i = 0; i <= 48; i++)
                {
                    float a = i / 48f * Mathf.PI * 2f;
                    ring[i] = W(new Vector3(Mathf.Cos(a) * rx * s, y, Mathf.Sin(a) * rz * s));
                }
                Handles.DrawPolyLine(ring);
            }
            DrawArc(rx, h, true);
            DrawArc(rx, h, false);
            DrawArc(rz, h, true, true);
            DrawArc(rz, h, false, true);

            // Size handles.
            Vector3 px = W(new Vector3(rx, 0f, 0f));
            Vector3 pz = W(new Vector3(0f, 0f, rz));
            Vector3 py = W(new Vector3(0f, h, 0f));
            EditorGUI.BeginChangeCheck();
            Vector3 nx = Handles.Slider(px, t.right, HandleUtility.GetHandleSize(px) * 0.08f, Handles.CubeHandleCap, 0f);
            Vector3 nz = Handles.Slider(pz, t.forward, HandleUtility.GetHandleSize(pz) * 0.08f, Handles.CubeHandleCap, 0f);
            Vector3 ny = Handles.Slider(py, t.up, HandleUtility.GetHandleSize(py) * 0.08f, Handles.CubeHandleCap, 0f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(gen, "Resize Spawn Cave");
                sp.size = new Vector3(Mathf.Max(2.5f, L(nx).x), Mathf.Max(3f, L(ny).y), Mathf.Max(2.5f, L(nz).z));
                Changed(gen);
            }
            Handles.Label(W(new Vector3(0f, h + 0.4f, 0f)), "Spawn cave  " + (rx * 2f).ToString("0.0") + " x " + (rz * 2f).ToString("0.0") + " x " + h.ToString("0.0") + " m");
        }

        void DrawArc(float radius, float h, bool positive, bool alongZ = false)
        {
            var pts = new Vector3[13];
            for (int i = 0; i <= 12; i++)
            {
                float a = i / 12f * Mathf.PI * 0.5f;
                float s = Mathf.Cos(a) * radius * (positive ? 1f : -1f);
                pts[i] = W(alongZ ? new Vector3(0f, Mathf.Sin(a) * h, s) : new Vector3(s, Mathf.Sin(a) * h, 0f));
            }
            Handles.DrawPolyLine(pts);
        }

        // ---------------------------------------------------------------- spawn point

        void DrawSpawnPoint(CaveGenerator gen, CaveSpawnCave sp)
        {
            Vector3 local = new Vector3(sp.spawnPoint.x, 0f, sp.spawnPoint.y);
            Vector3 p = W(local);
            Handles.color = new Color(0.3f, 0.95f, 0.5f);
            Handles.DrawWireDisc(p + t.up * 0.03f, t.up, 0.45f);
            Handles.DrawLine(p, p + t.TransformDirection(FirstExitDirection(sp)) * 1.2f);
            Handles.Label(p + t.up * 0.3f, "Spawn");

            EditorGUI.BeginChangeCheck();
            Vector3 np = Handles.Slider2D(p, t.up, t.right, t.forward, HandleUtility.GetHandleSize(p) * 0.1f, Handles.CylinderHandleCap, 0f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(gen, "Move Spawn Point");
                Vector3 l = L(np);
                sp.spawnPoint = new Vector2(l.x, l.z);
                Changed(gen);
            }
        }

        static Vector3 FirstExitDirection(CaveSpawnCave sp)
        {
            foreach (CaveSpawnHole h in sp.holes)
                if (h != null && h.leadsToCave)
                {
                    Vector3 d = h.Direction;
                    d.y = 0f;
                    return d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.forward;
                }
            return Vector3.forward;
        }

        // ---------------------------------------------------------------- holes

        void DrawHoles(CaveGenerator gen, CaveSpawnCave sp)
        {
            int exitNumber = 0;
            for (int i = 0; i < sp.holes.Count; i++)
            {
                CaveSpawnHole h = sp.holes[i];
                if (h == null) continue;
                bool selected = i == SelectedHole;
                if (h.leadsToCave) exitNumber++;

                Vector3 a = h.Start;
                Vector3 b = h.End;
                Vector3 dir = h.Direction;
                Vector3 right = Vector3.Cross(Vector3.up, dir);
                if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
                right.Normalize();
                Vector3 up = Vector3.Cross(dir, right).normalized;

                Handles.color = selected ? SelectedColor : (h.leadsToCave ? ExitColor : NicheColor);
                DrawRing(a, right, up, h.radius);
                DrawRing((a + b) * 0.5f, right, up, h.radius);
                DrawRing(b, right, up, h.radius);
                Handles.DrawLine(W(a + right * h.radius), W(b + right * h.radius));
                Handles.DrawLine(W(a - right * h.radius), W(b - right * h.radius));
                Handles.DrawLine(W(a + up * h.radius * 0.85f), W(b + up * h.radius * 0.85f));
                Handles.DrawLine(W(a - up * h.radius * 0.85f), W(b - up * h.radius * 0.85f));
                Handles.Label(W(a) + t.up * 0.4f, h.leadsToCave ? "Exit " + exitNumber : "Hole");

                float size = HandleUtility.GetHandleSize(W(a)) * 0.12f;
                if (Handles.Button(W(a), Quaternion.identity, size, size * 1.3f, Handles.SphereHandleCap))
                {
                    SelectedHole = i;
                    SelectedLedge = -1;
                }
                if (!selected) continue;

                // Move the whole hole.
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(W(a), t.rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(gen, "Move Spawn Cave Hole");
                    Vector3 d = L(moved) - a;
                    h.position.x += d.x;
                    h.position.z += d.z;
                    if (!h.snapToFloor) h.position.y += d.y;
                    Changed(gen);
                }

                // Aim it and set its length by dragging the far end.
                EditorGUI.BeginChangeCheck();
                Vector3 tip = Handles.PositionHandle(W(b), t.rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(gen, "Aim Spawn Cave Hole");
                    Vector3 v = L(tip) - a;
                    float len = v.magnitude;
                    if (len > 0.01f)
                    {
                        h.length = Mathf.Max(1f, len);
                        h.yaw = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
                        h.pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(v.y / len, -1f, 1f)) * Mathf.Rad2Deg, -90f, 90f);
                    }
                    Changed(gen);
                }

                // Width.
                Vector3 edge = W(b + right * h.radius);
                EditorGUI.BeginChangeCheck();
                Vector3 newEdge = Handles.Slider(edge, t.TransformDirection(right), HandleUtility.GetHandleSize(edge) * 0.08f, Handles.CubeHandleCap, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(gen, "Resize Spawn Cave Hole");
                    h.radius = Mathf.Max(0.8f, Vector3.Dot(L(newEdge) - b, right));
                    Changed(gen);
                }
            }
        }

        void DrawRing(Vector3 center, Vector3 right, Vector3 up, float radius)
        {
            var pts = new Vector3[25];
            for (int i = 0; i <= 24; i++)
            {
                float a = i / 24f * Mathf.PI * 2f;
                pts[i] = W(center + right * (Mathf.Cos(a) * radius) + up * (Mathf.Sin(a) * radius * 0.85f));
            }
            Handles.DrawPolyLine(pts);
        }

        // ---------------------------------------------------------------- ledges and steps

        void DrawLedges(CaveGenerator gen, CaveSpawnCave sp)
        {
            for (int i = 0; i < sp.ledges.Count; i++)
            {
                CaveSpawnLedge l = sp.ledges[i];
                if (l == null) continue;
                bool selected = i == SelectedLedge;

                int steps = Mathf.Max(1, l.steps);
                float w = Mathf.Max(0.4f, l.size.x);
                float d = Mathf.Max(0.4f, l.size.y);
                float h = Mathf.Max(0.1f, l.height);
                float tread = d / steps;

                Vector3 floorPos = new Vector3(l.position.x, 0f, l.position.y);
                Matrix4x4 m = t.localToWorldMatrix * Matrix4x4.TRS(floorPos, Quaternion.Euler(0f, l.yaw, 0f), Vector3.one);
                Matrix4x4 saved = Handles.matrix;
                Handles.matrix = m;
                Handles.color = selected ? SelectedColor : LedgeColor;
                for (int s = 0; s < steps; s++)
                {
                    float zMin = -d * 0.5f + s * tread;
                    float top = h * (s + 1);
                    Handles.DrawWireCube(new Vector3(0f, top * 0.5f, (zMin + d * 0.5f) * 0.5f), new Vector3(w, top, d * 0.5f - zMin));
                }
                Handles.DrawLine(new Vector3(0f, h * steps + 0.05f, d * 0.5f - 0.15f), new Vector3(0f, h * steps + 0.05f, d * 0.5f - 0.55f));   // arrow: which way it climbs
                Handles.matrix = saved;

                Vector3 topCenter = m.MultiplyPoint3x4(new Vector3(0f, h * steps, 0f));
                float size = HandleUtility.GetHandleSize(topCenter) * 0.12f;
                if (Handles.Button(topCenter, Quaternion.identity, size, size * 1.3f, Handles.SphereHandleCap))
                {
                    SelectedLedge = i;
                    SelectedHole = -1;
                }
                if (!selected) continue;

                // Move along the floor.
                Vector3 floorWorld = W(floorPos);
                EditorGUI.BeginChangeCheck();
                Vector3 np = Handles.Slider2D(floorWorld, t.up, t.right, t.forward, HandleUtility.GetHandleSize(floorWorld) * 0.12f, Handles.CylinderHandleCap, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(gen, "Move Spawn Cave Ledge");
                    Vector3 loc = L(np);
                    l.position = new Vector2(loc.x, loc.z);
                    Changed(gen);
                }

                // Turn it.
                Quaternion current = t.rotation * Quaternion.Euler(0f, l.yaw, 0f);
                EditorGUI.BeginChangeCheck();
                Quaternion turned = Handles.Disc(current, floorWorld, t.up, Mathf.Max(w, d) * 0.7f, false, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(gen, "Turn Spawn Cave Ledge");
                    l.yaw = Mathf.DeltaAngle(0f, (Quaternion.Inverse(t.rotation) * turned).eulerAngles.y);
                    Changed(gen);
                }

                // Width, depth and step height.
                Matrix4x4 inv = m.inverse;
                Vector3 wp = m.MultiplyPoint3x4(new Vector3(w * 0.5f, 0.1f, 0f));
                Vector3 dp = m.MultiplyPoint3x4(new Vector3(0f, 0.1f, d * 0.5f));
                Vector3 hp = m.MultiplyPoint3x4(new Vector3(0f, h * steps, d * 0.5f));
                EditorGUI.BeginChangeCheck();
                Vector3 nw = Handles.Slider(wp, m.MultiplyVector(Vector3.right).normalized, HandleUtility.GetHandleSize(wp) * 0.08f, Handles.CubeHandleCap, 0f);
                Vector3 nd = Handles.Slider(dp, m.MultiplyVector(Vector3.forward).normalized, HandleUtility.GetHandleSize(dp) * 0.08f, Handles.CubeHandleCap, 0f);
                Vector3 nh = Handles.Slider(hp, t.up, HandleUtility.GetHandleSize(hp) * 0.08f, Handles.CubeHandleCap, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(gen, "Resize Spawn Cave Ledge");
                    l.size = new Vector2(Mathf.Max(0.4f, 2f * Mathf.Abs(inv.MultiplyPoint3x4(nw).x)), Mathf.Max(0.4f, 2f * Mathf.Abs(inv.MultiplyPoint3x4(nd).z)));
                    l.height = Mathf.Max(0.1f, inv.MultiplyPoint3x4(nh).y / steps);
                    Changed(gen);
                }
            }
        }

        // ---------------------------------------------------------------- adding things

        /// <summary>Adds a hole on the wall facing the Scene view camera.</summary>
        public void AddHole(CaveGenerator gen)
        {
            CaveSpawnCave sp = gen.spawnCave;
            Undo.RecordObject(gen, "Add Spawn Cave Hole");
            float yaw = 90f;
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null)
            {
                Vector3 f = gen.transform.InverseTransformDirection(view.camera.transform.forward);
                f.y = 0f;
                if (f.sqrMagnitude > 1e-3f) yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            }
            float sin = Mathf.Sin(yaw * Mathf.Deg2Rad);
            float cos = Mathf.Cos(yaw * Mathf.Deg2Rad);
            float wall = 1f / Mathf.Sqrt(Mathf.Pow(sin / sp.size.x, 2f) + Mathf.Pow(cos / sp.size.z, 2f));
            float start = Mathf.Max(0.5f, wall - 1f);
            sp.holes.Add(new CaveSpawnHole { position = new Vector3(sin * start, 1.3f, cos * start), yaw = yaw, radius = 1.5f, length = 5.5f, leadsToCave = false });
            SelectedHole = sp.holes.Count - 1;
            SelectedLedge = -1;
            Changed(gen);
        }

        /// <summary>Adds a ledge (steps = 1) or a flight of steps near the middle of the room.</summary>
        public void AddLedge(CaveGenerator gen, int steps)
        {
            CaveSpawnCave sp = gen.spawnCave;
            Undo.RecordObject(gen, steps > 1 ? "Add Spawn Cave Steps" : "Add Spawn Cave Ledge");
            sp.ledges.Add(new CaveSpawnLedge
            {
                position = new Vector2(Mathf.Min(sp.size.x * 0.4f, 3f), Mathf.Min(sp.size.z * 0.4f, 3f)),
                yaw = 0f,
                size = steps > 1 ? new Vector2(2.2f, 0.7f * steps) : new Vector2(1.8f, 1.2f),
                height = 0.4f,
                steps = steps
            });
            SelectedLedge = sp.ledges.Count - 1;
            SelectedHole = -1;
            Changed(gen);
        }

        public void RemoveSelected(CaveGenerator gen)
        {
            CaveSpawnCave sp = gen.spawnCave;
            if (SelectedHole >= 0 && SelectedHole < sp.holes.Count)
            {
                Undo.RecordObject(gen, "Remove Spawn Cave Hole");
                sp.holes.RemoveAt(SelectedHole);
                SelectedHole = -1;
                Changed(gen);
            }
            else if (SelectedLedge >= 0 && SelectedLedge < sp.ledges.Count)
            {
                Undo.RecordObject(gen, "Remove Spawn Cave Ledge");
                sp.ledges.RemoveAt(SelectedLedge);
                SelectedLedge = -1;
                Changed(gen);
            }
        }

        static void Changed(CaveGenerator gen)
        {
            EditorUtility.SetDirty(gen);
            SceneView.RepaintAll();
        }
    }
}
