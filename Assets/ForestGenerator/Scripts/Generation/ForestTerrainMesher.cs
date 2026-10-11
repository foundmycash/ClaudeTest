using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>One terrain chunk's mesh data. Vertices are relative to <see cref="origin"/> (the chunk's local position).</summary>
    public sealed class ForestChunkMesh
    {
        public int chunkX, chunkZ;
        public Vector3 origin;
        public Vector3[] vertices;
        public Vector3[] normals;
        public Vector2[] uv;          // metres from the terrain's lower-left corner (ground texture tiling and blend-map lookup)
        public int[] triangles;
        public Bounds bounds;

        public string Name { get { return "Chunk_" + chunkX.ToString("00") + "_" + chunkZ.ToString("00"); } }
    }

    /// <summary>
    /// Cuts the height field into square chunks of grid cells and builds a mesh for each. Normals come from the whole height
    /// field, so there are no seams between chunks.
    /// </summary>
    public static class ForestTerrainMesher
    {
        public static List<ForestChunkMesh> Build(ForestHeightField hf, int chunkCells)
        {
            var chunks = new List<ForestChunkMesh>();
            int cc = Mathf.Max(4, chunkCells);
            int nx = Mathf.CeilToInt(hf.CellsX / (float)cc);
            int nz = Mathf.CeilToInt(hf.CellsZ / (float)cc);
            ForestGrid g = hf.grid;
            Vector2 min = hf.Min;

            for (int cz = 0; cz < nz; cz++)
            {
                for (int cx = 0; cx < nx; cx++)
                {
                    int x0 = cx * cc, z0 = cz * cc;
                    int x1 = Mathf.Min(x0 + cc, hf.CellsX), z1 = Mathf.Min(z0 + cc, hf.CellsZ);
                    int vw = x1 - x0 + 1, vh = z1 - z0 + 1;

                    var chunk = new ForestChunkMesh
                    {
                        chunkX = cx,
                        chunkZ = cz,
                        origin = new Vector3(min.x + x0 * g.cell, 0f, min.y + z0 * g.cell),
                        vertices = new Vector3[vw * vh],
                        normals = new Vector3[vw * vh],
                        uv = new Vector2[vw * vh],
                        triangles = new int[(vw - 1) * (vh - 1) * 6]
                    };

                    float yMin = float.MaxValue, yMax = float.MinValue;
                    for (int z = 0; z < vh; z++)
                    {
                        for (int x = 0; x < vw; x++)
                        {
                            int gx = x0 + x, gz = z0 + z;
                            float y = g[gx, gz];
                            int v = z * vw + x;
                            chunk.vertices[v] = new Vector3(x * g.cell, y, z * g.cell);
                            chunk.normals[v] = hf.NodeNormal(gx, gz);
                            chunk.uv[v] = new Vector2(gx * g.cell, gz * g.cell);
                            yMin = Mathf.Min(yMin, y);
                            yMax = Mathf.Max(yMax, y);
                        }
                    }

                    int t = 0;
                    for (int z = 0; z < vh - 1; z++)
                    {
                        for (int x = 0; x < vw - 1; x++)
                        {
                            int a = z * vw + x, b = a + 1, c = a + vw, d = c + 1;
                            if (!hf.flip[(z0 + z) * hf.CellsX + (x0 + x)])
                            {
                                chunk.triangles[t++] = a; chunk.triangles[t++] = c; chunk.triangles[t++] = d;
                                chunk.triangles[t++] = a; chunk.triangles[t++] = d; chunk.triangles[t++] = b;
                            }
                            else
                            {
                                chunk.triangles[t++] = a; chunk.triangles[t++] = c; chunk.triangles[t++] = b;
                                chunk.triangles[t++] = b; chunk.triangles[t++] = c; chunk.triangles[t++] = d;
                            }
                        }
                    }

                    var size = new Vector3((vw - 1) * g.cell, yMax - yMin, (vh - 1) * g.cell);
                    chunk.bounds = new Bounds(new Vector3(size.x * 0.5f, (yMin + yMax) * 0.5f, size.z * 0.5f), size + Vector3.up * 0.1f);
                    chunks.Add(chunk);
                }
            }
            return chunks;
        }
    }
}
