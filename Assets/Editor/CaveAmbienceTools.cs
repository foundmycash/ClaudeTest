using System.IO;
using Ambience;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor helpers for the cave ambience (Tools > Cave Ambience). The sounds are normally made in code at startup;
/// exporting them gives you real files to listen to in the Project window, or to use as a starting point for your own.
/// </summary>
public static class CaveAmbienceTools
{
    const string Folder = "Assets/Audio/CaveAmbience";
    const int ExportSampleRate = 44100;

    [MenuItem("Tools/Cave Ambience/Export The Five Sounds As WAV Files")]
    public static void ExportSounds()
    {
        // Same seed as the component in the open scene, if there is one, so the files are the sounds you would hear.
        var component = Object.FindFirstObjectByType<CaveAmbience>();
        int seed = component != null ? component.seed : CaveAmbience.DefaultSeed;

        var paths = new string[CaveSoundSynth.SoundCount];
        bool anyExists = false;
        for (int i = 0; i < paths.Length; i++)
        {
            paths[i] = $"{Folder}/CaveAmbience {i + 1} - {CaveSoundSynth.Names[i]}.wav";
            anyExists |= File.Exists(paths[i]);
        }

        if (anyExists && !Application.isBatchMode &&
            !EditorUtility.DisplayDialog("Cave Ambience", $"Files from an earlier export are already in {Folder}. Overwrite them?", "Overwrite", "Cancel"))
            return;

        Directory.CreateDirectory(Folder);
        for (int i = 0; i < paths.Length; i++)
        {
            float[] samples = CaveSoundSynth.Render(i, ExportSampleRate, seed);
            File.WriteAllBytes(paths[i], CaveSoundSynth.ToWav(samples, ExportSampleRate));
        }

        AssetDatabase.Refresh();
        Object folder = AssetDatabase.LoadAssetAtPath<Object>(Folder);
        if (folder != null)
            EditorGUIUtility.PingObject(folder);
        Debug.Log($"[CaveAmbience] Exported {paths.Length} sounds to {Folder} (44.1 kHz, mono, seed {seed}). " +
                  "To use one in the game, drag it into a slot of the Cave Ambience component's Custom Clips.");
    }

    [MenuItem("Tools/Cave Ambience/Play A Sound Now (Play Mode)")]
    static void PlayNow() => CaveAmbience.Instance.PlayNow();

    [MenuItem("Tools/Cave Ambience/Play A Sound Now (Play Mode)", true)]
    static bool CanPlayNow() => Application.isPlaying && CaveAmbience.Instance != null && CaveAmbience.Instance.SoundCount > 0;
}
