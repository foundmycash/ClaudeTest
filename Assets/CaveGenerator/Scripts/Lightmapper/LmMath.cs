using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HavocCave.Lightmapping
{
    /// <summary>Small numeric helpers for the lightmapper. Nothing here touches the Unity engine, so it is safe on worker threads.</summary>
    internal static class LmMath
    {
        public const float Pi = 3.14159265f;
        public const float TwoPi = 6.28318531f;

        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits
        {
            [FieldOffset(0)] public float f;
            [FieldOffset(0)] public uint u;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lum(float r, float g, float b)
        {
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        /// <summary>Integer hash (Wang / lowbias32 style). Good enough to seed per-texel random streams.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        /// <summary>floor(log2(v)) for a positive, finite float, read straight from the exponent bits.</summary>
        static int FloorLog2(float v)
        {
            FloatBits b;
            b.u = 0;
            b.f = v;
            return (int)((b.u >> 23) & 0xFFu) - 127;
        }

        const float Rgb9e5Max = 65408f;     // (511 / 512) * 2^16

        /// <summary>Packs linear HDR colour into the shared-exponent format GPUs read as RGB9E5 (R in the low bits).</summary>
        public static uint PackRgb9e5(float r, float g, float b)
        {
            if (!(r > 0f)) r = 0f; else if (r > Rgb9e5Max) r = Rgb9e5Max;
            if (!(g > 0f)) g = 0f; else if (g > Rgb9e5Max) g = Rgb9e5Max;
            if (!(b > 0f)) b = 0f; else if (b > Rgb9e5Max) b = Rgb9e5Max;

            float maxc = Math.Max(r, Math.Max(g, b));
            int expShared = (maxc > 0f ? Math.Max(-16, FloorLog2(maxc)) : -16) + 16;
            float denom = (float)Math.Pow(2.0, expShared - 24);
            int maxm = (int)Math.Floor(maxc / denom + 0.5f);
            if (maxm == 512)
            {
                expShared++;
                denom *= 2f;
            }

            uint rm = (uint)Math.Floor(r / denom + 0.5f);
            uint gm = (uint)Math.Floor(g / denom + 0.5f);
            uint bm = (uint)Math.Floor(b / denom + 0.5f);
            return rm | (gm << 9) | (bm << 18) | ((uint)expShared << 27);
        }

        public static void UnpackRgb9e5(uint p, out float r, out float g, out float b)
        {
            int e = (int)(p >> 27);
            float scale = (float)Math.Pow(2.0, e - 24);
            r = (p & 0x1FFu) * scale;
            g = ((p >> 9) & 0x1FFu) * scale;
            b = ((p >> 18) & 0x1FFu) * scale;
        }

        /// <summary>float to IEEE half, round to nearest. Used when the GPU can't read RGB9E5.</summary>
        public static ushort FloatToHalf(float value)
        {
            FloatBits fb;
            fb.f = 0f;
            fb.u = 0;
            fb.f = value;
            uint x = fb.u;
            uint sign = (x >> 16) & 0x8000u;
            int exp = (int)((x >> 23) & 0xFFu);
            uint mant = x & 0x7FFFFFu;

            if (exp == 0xFF) return (ushort)(sign | 0x7C00u | (mant != 0 ? 0x200u : 0u));
            exp = exp - 127 + 15;
            if (exp >= 31) return (ushort)(sign | 0x7C00u);
            if (exp <= 0)
            {
                if (exp < -10) return (ushort)sign;
                mant |= 0x800000u;
                int shift = 14 - exp;
                uint half = mant >> shift;
                if (((mant >> (shift - 1)) & 1u) != 0) half++;
                return (ushort)(sign | half);
            }
            uint h = sign | ((uint)exp << 10) | (mant >> 13);
            if ((mant & 0x1000u) != 0) h++;
            return (ushort)h;
        }
    }

    /// <summary>Tiny counter-based random stream: cheap, allocation free and independent of thread scheduling.</summary>
    internal struct LmRng
    {
        uint state;

        public LmRng(uint seed)
        {
            state = LmMath.Hash(seed ^ 0x9E3779B9u);
        }

        public uint NextUInt()
        {
            state += 0x9E3779B9u;
            return LmMath.Hash(state);
        }

        /// <summary>Uniform in [0, 1).</summary>
        public float Next()
        {
            return (NextUInt() >> 8) * (1f / 16777216f);
        }
    }
}
