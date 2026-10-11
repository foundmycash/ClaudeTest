using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// A regular grid of float values over the forest's local XZ plane (x = local x, y = local z). Values sit on the grid
    /// nodes: node (i, j) is at origin + (i, j) * cell. Sampling between nodes is bilinear and clamps at the edges.
    /// </summary>
    public sealed class ForestGrid
    {
        public readonly int width;
        public readonly int height;
        public readonly float cell;
        public readonly Vector2 origin;
        public readonly float[] values;

        public ForestGrid(int width, int height, float cell, Vector2 origin)
        {
            this.width = Mathf.Max(2, width);
            this.height = Mathf.Max(2, height);
            this.cell = cell;
            this.origin = origin;
            values = new float[this.width * this.height];
        }

        /// <summary>A grid whose nodes cover the rectangle [min, min + size] with about the given spacing.</summary>
        public static ForestGrid Covering(Vector2 min, Vector2 size, float cell)
        {
            int w = Mathf.CeilToInt(size.x / cell) + 1;
            int h = Mathf.CeilToInt(size.y / cell) + 1;
            return new ForestGrid(w, h, cell, min);
        }

        public float this[int x, int y]
        {
            get { return values[y * width + x]; }
            set { values[y * width + x] = value; }
        }

        public Vector2 NodePosition(int x, int y)
        {
            return new Vector2(origin.x + x * cell, origin.y + y * cell);
        }

        public void Fill(float v)
        {
            for (int i = 0; i < values.Length; i++) values[i] = v;
        }

        public float Sample(Vector2 p)
        {
            float fx = (p.x - origin.x) / cell;
            float fy = (p.y - origin.y) / cell;
            fx = Mathf.Clamp(fx, 0f, width - 1.001f);
            fy = Mathf.Clamp(fy, 0f, height - 1.001f);
            int x = (int)fx, y = (int)fy;
            float tx = fx - x, ty = fy - y;
            int i = y * width + x;
            float a = values[i], b = values[i + 1], c = values[i + width], d = values[i + width + 1];
            return (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
        }

        public ForestGrid Clone()
        {
            var g = new ForestGrid(width, height, cell, origin);
            System.Array.Copy(values, g.values, values.Length);
            return g;
        }

        /// <summary>A blurred copy: three passes of a separable box filter, which is close to a Gaussian of about the given radius.</summary>
        public ForestGrid Blurred(float radiusMetres)
        {
            ForestGrid g = Clone();
            int r = Mathf.Max(1, Mathf.RoundToInt(radiusMetres / cell / 1.7f));
            var tmp = new float[values.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxH(g.values, tmp, width, height, r);
                BoxV(tmp, g.values, width, height, r);
            }
            return g;
        }

        static void BoxH(float[] src, float[] dst, int w, int h, int r)
        {
            float inv = 1f / (2 * r + 1);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                float sum = 0f;
                for (int k = -r; k <= r; k++) sum += src[row + Mathf.Clamp(k, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x] = sum * inv;
                    sum += src[row + Mathf.Min(x + r + 1, w - 1)] - src[row + Mathf.Max(x - r, 0)];
                }
            }
        }

        static void BoxV(float[] src, float[] dst, int w, int h, int r)
        {
            float inv = 1f / (2 * r + 1);
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int k = -r; k <= r; k++) sum += src[Mathf.Clamp(k, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = sum * inv;
                    sum += src[Mathf.Min(y + r + 1, h - 1) * w + x] - src[Mathf.Max(y - r, 0) * w + x];
                }
            }
        }
    }
}
