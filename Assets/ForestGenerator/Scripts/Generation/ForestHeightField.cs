using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// The terrain heights on a regular grid of vertices, plus which way each grid cell is split into two triangles.
    /// SampleHeight interpolates exactly the way the terrain mesh is triangulated, so objects placed with it sit precisely on
    /// the rendered (and collided) ground.
    /// </summary>
    public sealed class ForestHeightField
    {
        public readonly ForestGrid grid;

        /// <summary>Per cell: false = split along the (x, z)-(x+1, z+1) diagonal, true = along (x+1, z)-(x, z+1).</summary>
        public bool[] flip;

        public ForestHeightField(Vector2 min, Vector2 size, float spacing)
        {
            grid = ForestGrid.Covering(min, size, spacing);
            flip = new bool[(grid.width - 1) * (grid.height - 1)];
        }

        public ForestHeightField(ForestGrid grid, bool[] flip)
        {
            this.grid = grid;
            this.flip = flip;
        }

        public int CellsX { get { return grid.width - 1; } }
        public int CellsZ { get { return grid.height - 1; } }
        public float Spacing { get { return grid.cell; } }
        public Vector2 Min { get { return grid.origin; } }
        public Vector2 Size { get { return new Vector2(CellsX * grid.cell, CellsZ * grid.cell); } }

        /// <summary>Splits every cell along the diagonal whose two corners are closest in height: smoother ridges and valleys.</summary>
        public void ComputeDiagonals()
        {
            int w = grid.width;
            float[] h = grid.values;
            for (int z = 0; z < CellsZ; z++)
            {
                for (int x = 0; x < CellsX; x++)
                {
                    int i = z * w + x;
                    float d1 = Mathf.Abs(h[i] - h[i + w + 1]);
                    float d2 = Mathf.Abs(h[i + 1] - h[i + w]);
                    flip[z * CellsX + x] = d2 < d1;
                }
            }
        }

        public float SampleHeight(Vector2 p)
        {
            float fx = (p.x - grid.origin.x) / grid.cell;
            float fz = (p.y - grid.origin.y) / grid.cell;
            fx = Mathf.Clamp(fx, 0f, CellsX - 0.0001f);
            fz = Mathf.Clamp(fz, 0f, CellsZ - 0.0001f);
            int x = (int)fx, z = (int)fz;
            float tx = fx - x, tz = fz - z;
            int w = grid.width;
            int i = z * w + x;
            float[] h = grid.values;
            float a = h[i], b = h[i + 1], c = h[i + w], d = h[i + w + 1];

            if (!flip[z * CellsX + x])
            {
                // diagonal a-d: triangles (a, c, d) and (a, d, b)
                if (tz > tx) return a + (d - c) * tx + (c - a) * tz;
                return a + (b - a) * tx + (d - b) * tz;
            }
            // diagonal b-c: triangles (a, c, b) and (b, c, d)
            if (tx + tz < 1f) return a + (b - a) * tx + (c - a) * tz;
            return d + (c - d) * (1f - tx) + (b - d) * (1f - tz);
        }

        /// <summary>Smooth normal at a grid vertex (central differences).</summary>
        public Vector3 NodeNormal(int x, int z)
        {
            int w = grid.width;
            int x0 = Mathf.Max(0, x - 1), x1 = Mathf.Min(grid.width - 1, x + 1);
            int z0 = Mathf.Max(0, z - 1), z1 = Mathf.Min(grid.height - 1, z + 1);
            float[] h = grid.values;
            float dx = (h[z * w + x1] - h[z * w + x0]) / ((x1 - x0) * grid.cell);
            float dz = (h[z1 * w + x] - h[z0 * w + x]) / ((z1 - z0) * grid.cell);
            return new Vector3(-dx, 1f, -dz).normalized;
        }

        /// <summary>Smooth normal anywhere (bilinear blend of the vertex normals).</summary>
        public Vector3 NormalAt(Vector2 p)
        {
            float fx = Mathf.Clamp((p.x - grid.origin.x) / grid.cell, 0f, CellsX - 0.0001f);
            float fz = Mathf.Clamp((p.y - grid.origin.y) / grid.cell, 0f, CellsZ - 0.0001f);
            int x = (int)fx, z = (int)fz;
            float tx = fx - x, tz = fz - z;
            Vector3 n = Vector3.Lerp(Vector3.Lerp(NodeNormal(x, z), NodeNormal(x + 1, z), tx),
                                     Vector3.Lerp(NodeNormal(x, z + 1), NodeNormal(x + 1, z + 1), tx), tz);
            return n.normalized;
        }

        /// <summary>Steepness of the ground in degrees.</summary>
        public float SlopeDegrees(Vector2 p)
        {
            return Mathf.Acos(Mathf.Clamp(NormalAt(p).y, -1f, 1f)) * Mathf.Rad2Deg;
        }
    }
}
