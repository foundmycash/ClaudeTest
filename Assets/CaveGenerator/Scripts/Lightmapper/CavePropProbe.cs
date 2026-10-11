using UnityEngine;
using UnityEngine.Rendering;

namespace HavocCave
{
    /// <summary>
    /// The baked light of one prop: a light probe the cave lightmapper measured at the prop. It keeps the 27 spherical harmonics
    /// numbers with the prop, so they are saved with the scene, and hands them to every mesh under the prop, where URP's lit shaders
    /// read them as ambient light. Added by the cave generator when Lightmapper > Light Props is on; there is nothing to set up.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class CavePropProbe : MonoBehaviour
    {
        const int Coefficients = 9;

        [SerializeField, HideInInspector] float[] coefficients;      // red 0..8, green 0..8, blue 0..8

        MaterialPropertyBlock block;
        readonly SphericalHarmonicsL2[] probe = new SphericalHarmonicsL2[1];

        public bool HasLight { get { return coefficients != null && coefficients.Length == 3 * Coefficients; } }

        /// <summary>The light that reaches the prop, in world space.</summary>
        public void SetLight(SphericalHarmonicsL2 sh)
        {
            if (!HasLight) coefficients = new float[3 * Coefficients];
            for (int c = 0; c < 3; c++)
                for (int i = 0; i < Coefficients; i++)
                    coefficients[c * Coefficients + i] = sh[c, i];
            Apply();
        }

        /// <summary>The light that reaches the prop, in world space. Black when none has been baked.</summary>
        public SphericalHarmonicsL2 GetLight()
        {
            var sh = new SphericalHarmonicsL2();
            if (HasLight)
                for (int c = 0; c < 3; c++)
                    for (int i = 0; i < Coefficients; i++)
                        sh[c, i] = coefficients[c * Coefficients + i];
            return sh;
        }

        void OnEnable()
        {
            // A MaterialPropertyBlock is not saved with the scene, so it is made again every time the prop wakes up.
            Apply();
        }

        void Apply()
        {
            if (!HasLight) return;

            probe[0] = GetLight();
            if (block == null) block = new MaterialPropertyBlock();
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                r.lightProbeUsage = LightProbeUsage.CustomProvided;
                r.GetPropertyBlock(block);                    // keep whatever else is already on the renderer
                block.CopySHCoefficientArraysFrom(probe);
                r.SetPropertyBlock(block);
            }
        }
    }
}
