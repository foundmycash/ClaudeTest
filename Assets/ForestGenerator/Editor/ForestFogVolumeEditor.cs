using UnityEditor;

namespace HavocForest.EditorTools
{
    [CustomEditor(typeof(ForestFogVolume))]
    class ForestFogVolumeEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "The forest's fog. Forest Atmosphere (on the Forest object) drives part of the Volumetric Fog below:\n" +
                "- Density, Fog Color and Max Distance come from the Calm / Dread atmosphere profiles (Fog Colour, Fog Density), " +
                "so the fog thickens deeper in and is opaque where the camera's far plane is. Changing them here has no effect.\n" +
                "- The box is sized to the forest whenever it is generated.\n\n" +
                "Tune everything else below: noise, wind and turbulence, Anisotropy, the lamp glow (Light Intensity, Light Core Radius, " +
                "Light Absorption), Height Falloff, and Max Steps / Mobile Max Steps, the main GPU cost on Quest.\n\n" +
                "The forest draws this fog itself on a small box around each camera (so this Mesh Renderer is kept from drawing): the box " +
                "faces would otherwise lie beyond the far plane. To use Unity's fog instead, set Fog Mode on Forest Atmosphere to Unity Fog; " +
                "to see the forest from above while editing, untick Fog In Scene View there.",
                MessageType.Info);
        }
    }
}
