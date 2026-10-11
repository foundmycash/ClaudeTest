using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Marks the forest's Volumetric Fog box (created by Forest Atmosphere). Forest Atmosphere sizes the box to the forest
    /// and sets the fog's Density, Fog Color and Max Distance from the Calm / Dread profiles; everything else is tuned on
    /// the Volumetric Fog component next to this one.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public class ForestFogVolume : MonoBehaviour
    {
    }
}
