using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Small deterministic random number generator (PCG32). The same seed and stream give the same numbers on every
    /// platform, in the editor and in builds. Every generation step uses its own stream, so changing one step (for example
    /// the settings of one asset category) never reshuffles the others.
    /// </summary>
    public sealed class ForestRandom
    {
        const ulong Multiplier = 6364136223846793005UL;
        const ulong Increment = 1442695040888963407UL;

        ulong state;

        public ForestRandom(int seed, int stream)
        {
            state = 0UL;
            NextUInt();
            state += ((ulong)(uint)seed | ((ulong)(uint)stream << 32)) ^ 0x9E3779B97F4A7C15UL;
            NextUInt();
        }

        public ForestRandom(int seed, string stream) : this(seed, StableHash(stream)) { }

        public uint NextUInt()
        {
            unchecked
            {
                ulong old = state;
                state = old * Multiplier + Increment;
                uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
                int rot = (int)(old >> 59);
                return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
            }
        }

        /// <summary>A float in [0, 1).</summary>
        public float Value()
        {
            return (NextUInt() >> 8) * (1f / 16777216f);
        }

        public float Range(float min, float max)
        {
            return min + (max - min) * Value();
        }

        public float Range(Vector2 minMax)
        {
            return Range(minMax.x, minMax.y);
        }

        /// <summary>An int in [min, maxExclusive).</summary>
        public int Range(int min, int maxExclusive)
        {
            if (maxExclusive <= min) return min;
            return min + (int)(NextUInt() % (uint)(maxExclusive - min));
        }

        public bool Chance(float probability)
        {
            return Value() < probability;
        }

        /// <summary>A random point inside the unit circle, evenly spread.</summary>
        public Vector2 InsideCircle()
        {
            float a = Value() * Mathf.PI * 2f;
            float r = Mathf.Sqrt(Value());
            return new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }

        public float Angle()
        {
            return Value() * Mathf.PI * 2f;
        }

        /// <summary>Picks an index from a list of weights (negative weights count as zero). -1 if they are all zero.</summary>
        public int Pick(float[] weights, int count)
        {
            float total = 0f;
            for (int i = 0; i < count; i++) total += Mathf.Max(0f, weights[i]);
            if (total <= 0f) return -1;
            float r = Value() * total;
            for (int i = 0; i < count; i++)
            {
                float w = Mathf.Max(0f, weights[i]);
                if (r < w) return i;
                r -= w;
            }
            for (int i = count - 1; i >= 0; i--) if (weights[i] > 0f) return i;
            return -1;
        }

        /// <summary>FNV-1a hash of a string: the same on every platform (string.GetHashCode is not).</summary>
        public static int StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                if (s != null)
                    for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return (int)h;
            }
        }

        public static int Combine(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u;
                h ^= (uint)b + 0x7F4A7C15u + (h << 6) + (h >> 2);
                return (int)h;
            }
        }
    }
}
